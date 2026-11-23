"""Run the evaluation set against a given RAG configuration.

Ingests eval/corpus/ into an isolated namespace, asks every question in
eval/questions.jsonl, computes deterministic metrics and compares them with
eval/baseline.json.

Usage (from rag-service/):
    uv run python -m scripts.run_eval                       # current defaults
    uv run python -m scripts.run_eval --save-baseline       # store as baseline
    uv run python -m scripts.run_eval --strategy heading --chunk-size 500 --k 3 --prompt v2
"""

import argparse
import json
import logging
import re
import statistics
import time
import unicodedata
import uuid
from datetime import datetime
from pathlib import Path

from app.llm import LLM
from app.pipeline import ingest_document, messages_for, retrieve
from app.prompting import NO_INFO_ANSWER
from app.schemas import AskRequest
from app.settings import get_settings
from app.store import Store

ROOT = Path(__file__).resolve().parent.parent
EVAL = ROOT / "eval"
CORPUS = EVAL / "corpus"
BASELINE = EVAL / "baseline.json"
DOC_ID_NAMESPACE = uuid.UUID("0b8f3c52-7d1e-4f0a-9a7e-6c2b1d4e5f60")

# Phrases that count as "the model declined to answer". v2 uses NO_INFO_ANSWER
# exactly; the others catch free-form refusals (mostly with prompt v1).
ABSTENTION_PHRASES = [
    NO_INFO_ANSWER, "no encontre", "no hay informacion", "no se menciona", "no tengo informacion",
    "no se especifica", "no contiene informacion", "no se proporciona", "no aparece",
    "no incluye informacion", "no es posible responder", "no es posible determinar",
]
CITATION = re.compile(r"\[\d+\]")

METRICS = [
    ("retrievalHit", "Retrieval hit@k (evidencia en los chunks)"),
    ("mrr", "MRR (posición de la evidencia)"),
    ("factCoverage", "Cobertura de datos clave"),
    ("answerAccuracy", "Respuestas correctas (todos los datos, sin abstenerse)"),
    ("citationRate", "Respuestas con citas"),
    ("abstentionAccuracy", "Abstención correcta (sin respuesta en el corpus)"),
    ("abstentionExact", "Abstención con la frase fija (detectable por la UI)"),
    ("falseAbstention", "Abstención indebida (sí había respuesta)"),
    ("avgLatencyMs", "Latencia media (ms)"),
    ("avgInputTokens", "Tokens de entrada (media)"),
]
LOWER_IS_BETTER = {"falseAbstention", "avgLatencyMs", "avgInputTokens"}


def normalize(text: str) -> str:
    text = unicodedata.normalize("NFKD", text.lower())
    text = "".join(c for c in text if not unicodedata.combining(c))
    return re.sub(r"\s+", " ", text.replace("`", "")).strip()


def has_fact(answer: str, fact: str | list[str]) -> bool:
    options = fact if isinstance(fact, list) else [fact]
    return any(normalize(o) in normalize(answer) for o in options)


def is_abstention(answer: str) -> bool:
    return any(normalize(p) in normalize(answer) for p in ABSTENTION_PHRASES)


def evaluate(args: argparse.Namespace) -> dict:
    overrides = {
        "chunk_strategy": args.strategy,
        "chunk_size": args.chunk_size,
        "chunk_overlap": args.overlap,
        "top_k": args.k,
        "prompt_version": args.prompt,
        "max_distance": args.max_distance,
        "llm_temperature": 0.0,  # as deterministic as possible, so runs are comparable
    }
    base = get_settings()
    settings = base.model_copy(update={k: v for k, v in overrides.items() if v is not None or k == "max_distance"})
    config = {k: getattr(settings, k) for k in overrides} | {
        "llm_model": settings.llm_model, "embedding_model": settings.embedding_model,
    }

    llm, store = LLM(settings), Store(settings)
    namespace = f"eval-{uuid.uuid4().hex[:8]}"
    questions = [json.loads(line) for line in (EVAL / "questions.jsonl").read_text(encoding="utf-8").splitlines() if line.strip()]
    doc_ids: dict[str, str] = {}

    try:
        for path in sorted(CORPUS.iterdir()):
            doc_id = str(uuid.uuid5(DOC_ID_NAMESPACE, path.name))
            doc_ids[path.name] = doc_id
            ingest_document(settings, llm, store, filename=path.name, raw=path.read_bytes(),
                            document_id=doc_id, namespace=namespace, title=path.stem)

        rows = []
        for q in questions:
            started = time.perf_counter()
            request = AskRequest(question=q["question"], namespace=namespace)
            sources = retrieve(settings, llm, store, request)
            if sources:
                answer, tokens_in, tokens_out = llm.chat(messages_for(settings, request, sources))
            else:
                answer, tokens_in, tokens_out = NO_INFO_ANSWER, 0, 0
            latency = round((time.perf_counter() - started) * 1000)

            answerable = q["type"] != "unanswerable"
            rank = None
            if answerable:
                evidence = normalize(q["evidence"])
                rank = next((s.index for s in sources if evidence in normalize(s.text)), None)
            facts = q.get("mustInclude", [])
            found = [has_fact(answer, f) for f in facts]
            abstained = is_abstention(answer)
            rows.append({
                "id": q["id"], "type": q["type"], "question": q["question"],
                "answer": answer, "abstained": abstained,
                "evidenceRank": rank,
                "expectedDocRetrieved": answerable and any(s.document_id == doc_ids.get(q["document"]) for s in sources),
                "factsFound": sum(found), "factsTotal": len(facts),
                "cited": bool(CITATION.search(answer)),
                "latencyMs": latency, "inputTokens": tokens_in, "outputTokens": tokens_out,
                "sources": [{"index": s.index, "title": s.title, "page": s.page, "section": s.section,
                             "distance": s.distance} for s in sources],
            })
            mark = "ok " if (not answerable and abstained) or (answerable and all(found) and not abstained) else "ERR"
            print(f"  {mark} {q['id']} [{q['type']}] rank={rank} facts={sum(found)}/{len(facts)} {latency} ms")
    finally:
        store.collection.delete(where={"namespace": namespace})

    answerable_rows = [r for r in rows if r["type"] != "unanswerable"]
    unanswerable_rows = [r for r in rows if r["type"] == "unanswerable"]
    metrics = {
        "retrievalHit": statistics.mean(r["evidenceRank"] is not None for r in answerable_rows),
        "mrr": statistics.mean(1 / r["evidenceRank"] if r["evidenceRank"] else 0 for r in answerable_rows),
        "factCoverage": statistics.mean(r["factsFound"] / r["factsTotal"] for r in answerable_rows),
        "answerAccuracy": statistics.mean(r["factsFound"] == r["factsTotal"] and not r["abstained"] for r in answerable_rows),
        "citationRate": statistics.mean(r["cited"] for r in answerable_rows),
        "abstentionAccuracy": statistics.mean(r["abstained"] for r in unanswerable_rows),
        "abstentionExact": statistics.mean(r["answer"].strip() == NO_INFO_ANSWER for r in unanswerable_rows),
        "falseAbstention": statistics.mean(r["abstained"] for r in answerable_rows),
        "avgLatencyMs": statistics.mean(r["latencyMs"] for r in rows),
        "avgInputTokens": statistics.mean(r["inputTokens"] or 0 for r in rows),
    }
    return {"tag": args.tag, "createdAt": datetime.now().isoformat(timespec="seconds"),
            "config": config, "metrics": metrics, "questions": rows}


def print_report(result: dict, baseline: dict | None) -> None:
    print(f"\nConfig: {result['config']}")
    header = f"{'Métrica':<52} {'Valor':>8}" + (f" {'Base':>8} {'Delta':>8}" if baseline else "")
    print(header + "\n" + "-" * len(header))
    for key, label in METRICS:
        value = result["metrics"][key]
        fmt = (lambda v: f"{v:8.0f}") if key.startswith("avg") else (lambda v: f"{v:8.2f}")
        line = f"{label:<52} {fmt(value)}"
        if baseline:
            base = baseline["metrics"].get(key)
            if base is None:  # metric added after the baseline was saved
                print(line)
                continue
            delta = value - base
            better = (delta < 0) if key in LOWER_IS_BETTER else (delta > 0)
            sign = "" if abs(delta) < 1e-9 else (" ▲" if better else " ▼")
            line += f" {fmt(base)} {fmt(delta).strip():>8}{sign}"
        print(line)


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("--strategy", choices=["paragraph", "heading"])
    parser.add_argument("--chunk-size", type=int)
    parser.add_argument("--overlap", type=int)
    parser.add_argument("--k", type=int)
    parser.add_argument("--prompt", choices=["v1", "v2"])
    parser.add_argument("--max-distance", type=float)
    parser.add_argument("--tag", default="run")
    parser.add_argument("--save-baseline", action="store_true")
    args = parser.parse_args()

    logging.getLogger("rag").setLevel(logging.WARNING)
    result = evaluate(args)

    (EVAL / "results").mkdir(exist_ok=True)
    out = EVAL / "results" / f"{datetime.now():%Y%m%d-%H%M%S}-{args.tag}.json"
    out.write_text(json.dumps(result, ensure_ascii=False, indent=2), encoding="utf-8")

    baseline = None if args.save_baseline or not BASELINE.exists() else json.loads(BASELINE.read_text(encoding="utf-8"))
    print_report(result, baseline)
    if args.save_baseline:
        BASELINE.write_text(json.dumps(result, ensure_ascii=False, indent=2), encoding="utf-8")
        print(f"\nBaseline saved to {BASELINE.relative_to(ROOT)}")
    print(f"Details: {out.relative_to(ROOT)}")


if __name__ == "__main__":
    main()
