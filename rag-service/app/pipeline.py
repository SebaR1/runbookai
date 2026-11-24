"""Ingest and retrieval logic, shared by the HTTP API and the evaluation script.

Everything that can vary between experiments comes from Settings, so the eval
can run the same code with different configurations.
"""

import hashlib
import logging
import time
from dataclasses import dataclass

from app.chunking import build_chunks, chunk_id
from app.extraction import extract_pages, source_type
from app.llm import LLM
from app.prompting import build_messages, build_rewrite_messages, clean_rewrite
from app.schemas import AskRequest, Source
from app.settings import Settings
from app.store import ChunkRecord, Store

log = logging.getLogger("rag")

# A standalone question fits easily; the cap stops a rewrite that starts answering.
REWRITE_MAX_TOKENS = 100


class EmptyDocument(ValueError):
    pass


@dataclass
class IngestResult:
    chunk_count: int
    doc_hash: str
    title: str


def ingest_document(
    settings: Settings,
    llm: LLM,
    store: Store,
    *,
    filename: str,
    raw: bytes,
    document_id: str,
    namespace: str,
    title: str | None = None,
) -> IngestResult:
    started = time.perf_counter()
    kind = source_type(filename)
    pages = extract_pages(filename, raw)
    chunks = build_chunks(pages, kind, settings.chunk_strategy, settings.chunk_size, settings.chunk_overlap)
    if not chunks:
        raise EmptyDocument("Document has no extractable text")

    doc_hash = hashlib.sha256(raw).hexdigest()
    title = title or filename.rsplit(".", 1)[0]
    vectors = llm.embed([c.text for c in chunks])
    records = [
        ChunkRecord(
            id=chunk_id(document_id, order),
            text=chunk.text,
            embedding=vector,
            metadata={
                "documentId": document_id,
                "title": title,
                "page": chunk.page,
                "section": chunk.section,
                "order": order,
                "namespace": namespace,
                "docHash": doc_hash,
                "sourceType": kind,
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
            "sourceType": kind,
            "pages": len(pages),
            "chunkCount": len(records),
            "strategy": settings.chunk_strategy,
            "elapsedMs": round((time.perf_counter() - started) * 1000),
        }},
    )
    return IngestResult(chunk_count=len(records), doc_hash=doc_hash, title=title)


def search_query(settings: Settings, llm: LLM, request: AskRequest) -> tuple[str, int | None]:
    """Text to embed: the question, or a standalone rewrite when there is history.

    Returns (query, rewrite time in ms or None if no rewrite ran). A failed rewrite
    falls back to the original question: it must never break the turn.
    """
    if not settings.query_rewrite or not request.history:
        return request.question, None
    started = time.perf_counter()
    try:
        output, _, _ = llm.chat(
            build_rewrite_messages(request.question, request.history, settings.history_max_messages),
            max_tokens=REWRITE_MAX_TOKENS,
        )
        query = clean_rewrite(output, request.question)
    except Exception:
        log.warning("query rewrite failed, searching with the original question", exc_info=True)
        query = request.question
    return query, round((time.perf_counter() - started) * 1000)


@dataclass
class Retrieval:
    sources: list[Source]
    # What was embedded: the question, or its standalone rewrite.
    search_query: str


def retrieve(settings: Settings, llm: LLM, store: Store, request: AskRequest) -> Retrieval:
    started = time.perf_counter()
    k = request.k or settings.top_k
    query, rewrite_ms = search_query(settings, llm, request)
    [query_vector] = llm.embed([query])
    hits = store.query(query_vector, request.namespace, k)
    discarded = 0
    if settings.max_distance is not None:
        kept = [h for h in hits if h.distance <= settings.max_distance]
        discarded = len(hits) - len(kept)
        hits = kept
    sources = [
        Source(
            index=i,
            document_id=h.metadata.get("documentId", ""),
            chunk_id=h.id,
            title=h.metadata.get("title", ""),
            page=h.metadata.get("page", 1),
            section=h.metadata.get("section", ""),
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
            "searchQuery": query,
            "rewriteMs": rewrite_ms,
            "historyMessages": len(request.history),
            "namespace": request.namespace,
            "k": k,
            "maxDistance": settings.max_distance,
            "discarded": discarded,
            "elapsedMs": round((time.perf_counter() - started) * 1000),
            "chunks": [
                {"chunkId": s.chunk_id, "documentId": s.document_id, "page": s.page,
                 "section": s.section, "order": s.order, "distance": s.distance}
                for s in sources
            ],
        }},
    )
    return Retrieval(sources=sources, search_query=query)


def messages_for(settings: Settings, request: AskRequest, retrieval: Retrieval) -> list[dict[str, str]]:
    return build_messages(
        request.question, retrieval.sources, request.history, settings.history_max_messages,
        settings.prompt_version, search_query=retrieval.search_query,
    )
