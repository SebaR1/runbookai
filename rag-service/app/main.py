import hashlib
import json
import logging
import sys
import time
from collections.abc import Iterator
from functools import lru_cache
from typing import Annotated, Any

from fastapi import Depends, FastAPI, File, Form, HTTPException, Query, UploadFile
from fastapi.responses import JSONResponse, StreamingResponse

from app.chunking import chunk_id, split_markdown
from app.llm import LLM
from app.prompting import NO_CONTEXT_ANSWER, build_messages
from app.schemas import AskRequest, AskResponse, HealthResponse, IngestResponse, Source
from app.settings import Settings, get_settings
from app.store import ChunkRecord, Store


class JsonFormatter(logging.Formatter):
    def format(self, record: logging.LogRecord) -> str:
        payload = {"ts": self.formatTime(record), "level": record.levelname, "msg": record.getMessage()}
        payload.update(getattr(record, "data", {}))
        return json.dumps(payload, ensure_ascii=False)


log = logging.getLogger("rag")
# On Windows the console defaults to cp1252; force UTF-8 so logs keep accents and "¿".
if hasattr(sys.stderr, "reconfigure"):
    sys.stderr.reconfigure(encoding="utf-8")
_handler = logging.StreamHandler()
_handler.setFormatter(JsonFormatter())
log.addHandler(_handler)
log.setLevel(get_settings().log_level)
log.propagate = False

ALLOWED_EXTENSIONS = (".md", ".markdown", ".txt")

app = FastAPI(
    title="RunbookAI RAG service",
    version="0.1.0",
    description="Ingest, retrieval and answer generation. Only component that talks to Chroma and the LLM.",
)


@lru_cache
def get_llm() -> LLM:
    return LLM(get_settings())


@lru_cache
def get_store() -> Store:
    return Store(get_settings())


SettingsDep = Annotated[Settings, Depends(get_settings)]
LLMDep = Annotated[LLM, Depends(get_llm)]
StoreDep = Annotated[Store, Depends(get_store)]


@app.get("/health", response_model=HealthResponse)
def health(llm: LLMDep, store: StoreDep) -> JSONResponse:
    chroma_ok = store.heartbeat()
    try:
        missing = llm.missing_models()
        llm_ok = True
    except Exception:
        missing, llm_ok = [], False
    ok = chroma_ok and llm_ok and not missing
    body = HealthResponse(
        status="ok" if ok else "degraded",
        chroma=chroma_ok,
        llm=llm_ok,
        missing_models=missing,
        collection=store.name,
    )
    return JSONResponse(body.model_dump(by_alias=True), status_code=200 if ok else 503)


@app.post("/ingest", response_model=IngestResponse)
def ingest(
    settings: SettingsDep,
    llm: LLMDep,
    store: StoreDep,
    file: Annotated[UploadFile, File(description="Markdown file")],
    document_id: Annotated[str, Form(alias="documentId")],
    namespace: Annotated[str, Form()] = "default",
    title: Annotated[str | None, Form()] = None,
) -> IngestResponse:
    filename = file.filename or "document.md"
    if not filename.lower().endswith(ALLOWED_EXTENSIONS):
        raise HTTPException(415, f"Unsupported file type: {filename}. Allowed: {ALLOWED_EXTENSIONS}")

    raw = file.file.read()
    try:
        text = raw.decode("utf-8-sig")
    except UnicodeDecodeError:
        raise HTTPException(422, "File must be UTF-8 text")

    chunks = split_markdown(text, settings.chunk_size, settings.chunk_overlap)
    if not chunks:
        raise HTTPException(422, "Document has no text")

    started = time.perf_counter()
    doc_hash = hashlib.sha256(raw).hexdigest()
    title = title or filename.rsplit(".", 1)[0]
    vectors = llm.embed(chunks)
    records = [
        ChunkRecord(
            id=chunk_id(document_id, order),
            text=chunk,
            embedding=vector,
            metadata={
                "documentId": document_id,
                "title": title,
                "page": 1,  # markdown has no pages; PDF support arrives in Phase 3
                "order": order,
                "namespace": namespace,
                "docHash": doc_hash,
            },
        )
        for order, (chunk, vector) in enumerate(zip(chunks, vectors))
    ]
    store.replace_document(document_id, records)

    log.info(
        "ingest",
        extra={"data": {
            "documentId": document_id,
            "namespace": namespace,
            "title": title,
            "chunkCount": len(records),
            "elapsedMs": round((time.perf_counter() - started) * 1000),
        }},
    )
    return IngestResponse(
        document_id=document_id,
        namespace=namespace,
        chunk_count=len(records),
        doc_hash=doc_hash,
        collection=store.name,
        embedding_model=settings.embedding_model,
    )


def _retrieve(request: AskRequest, settings: Settings, llm: LLM, store: Store) -> list[Source]:
    started = time.perf_counter()
    k = request.k or settings.top_k
    [query_vector] = llm.embed([request.question])
    hits = store.query(query_vector, request.namespace, k)
    sources = [
        Source(
            index=i,
            document_id=h.metadata.get("documentId", ""),
            chunk_id=h.id,
            title=h.metadata.get("title", ""),
            page=h.metadata.get("page", 1),
            order=h.metadata.get("order", 0),
            distance=round(h.distance, 4),
            text=h.text,
        )
        for i, h in enumerate(hits, start=1)
    ]
    log.info(
        "retrieval",
        extra={"data": {
            "question": request.question,
            "namespace": request.namespace,
            "k": k,
            "elapsedMs": round((time.perf_counter() - started) * 1000),
            "chunks": [
                {"chunkId": s.chunk_id, "documentId": s.document_id, "order": s.order, "distance": s.distance}
                for s in sources
            ],
        }},
    )
    return sources


def _sse(event: str, data: Any) -> str:
    return f"event: {event}\ndata: {json.dumps(data, ensure_ascii=False)}\n\n"


@app.post(
    "/ask",
    response_model=AskResponse,
    responses={200: {"content": {"text/event-stream": {}},
                     "description": "SSE (default): events `sources`, `token`, `done`, `error`. JSON when stream=false."}},
)
def ask(
    request: AskRequest,
    settings: SettingsDep,
    llm: LLMDep,
    store: StoreDep,
    stream: Annotated[bool, Query(description="Stream the answer as Server-Sent Events")] = True,
):
    sources = _retrieve(request, settings, llm, store)
    messages = build_messages(request.question, sources, request.history, settings.history_max_messages)

    if not stream:
        started = time.perf_counter()
        if sources:
            answer, tokens_in, tokens_out = llm.chat(messages)
        else:
            answer, tokens_in, tokens_out = NO_CONTEXT_ANSWER, None, None
        _log_generation(settings, started, tokens_in, tokens_out)
        return AskResponse(
            answer=answer,
            sources=sources,
            model=settings.llm_model,
            input_tokens=tokens_in,
            output_tokens=tokens_out,
        )

    def events() -> Iterator[str]:
        started = time.perf_counter()
        yield _sse("sources", [s.model_dump(by_alias=True) for s in sources])
        tokens_in = tokens_out = None
        try:
            if not sources:
                yield _sse("token", {"text": NO_CONTEXT_ANSWER})
            else:
                for event in llm.stream_chat(messages):
                    if event.text:
                        yield _sse("token", {"text": event.text})
                    if event.output_tokens is not None:
                        tokens_in, tokens_out = event.input_tokens, event.output_tokens
        except Exception as exc:  # the HTTP status is already sent; report in-band
            log.exception("generation failed")
            yield _sse("error", {"message": str(exc)})
            return
        _log_generation(settings, started, tokens_in, tokens_out)
        yield _sse("done", {"model": settings.llm_model, "inputTokens": tokens_in, "outputTokens": tokens_out})

    return StreamingResponse(events(), media_type="text/event-stream", headers={"Cache-Control": "no-cache"})


def _log_generation(settings: Settings, started: float, tokens_in: int | None, tokens_out: int | None) -> None:
    log.info(
        "generation",
        extra={"data": {
            "model": settings.llm_model,
            "elapsedMs": round((time.perf_counter() - started) * 1000),
            "inputTokens": tokens_in,
            "outputTokens": tokens_out,
        }},
    )
