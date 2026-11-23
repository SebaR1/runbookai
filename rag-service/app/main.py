import json
import logging
import sys
import time
from collections.abc import Iterator
from functools import lru_cache
from typing import Annotated, Any

from fastapi import Depends, FastAPI, File, Form, HTTPException, Query, UploadFile
from fastapi.responses import JSONResponse, StreamingResponse

from app.extraction import UnsupportedDocument
from app.llm import LLM
from app.pipeline import EmptyDocument, ingest_document, messages_for, retrieve
from app.prompting import NO_CONTEXT_ANSWER
from app.schemas import AskRequest, AskResponse, HealthResponse, IngestResponse
from app.settings import Settings, get_settings
from app.store import Store


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
    file: Annotated[UploadFile, File(description="Markdown (.md, .txt) or PDF file")],
    document_id: Annotated[str, Form(alias="documentId")],
    namespace: Annotated[str, Form()] = "default",
    title: Annotated[str | None, Form()] = None,
) -> IngestResponse:
    filename = file.filename or "document.md"
    try:
        result = ingest_document(
            settings, llm, store,
            filename=filename, raw=file.file.read(),
            document_id=document_id, namespace=namespace, title=title,
        )
    except UnsupportedDocument as exc:
        raise HTTPException(415, str(exc))
    except EmptyDocument as exc:
        raise HTTPException(422, str(exc))
    return IngestResponse(
        document_id=document_id,
        namespace=namespace,
        chunk_count=result.chunk_count,
        doc_hash=result.doc_hash,
        collection=store.name,
        embedding_model=settings.embedding_model,
    )


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
    sources = retrieve(settings, llm, store, request)
    messages = messages_for(settings, request, sources)

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
