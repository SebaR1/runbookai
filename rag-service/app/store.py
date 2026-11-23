"""Chroma access: one collection per embedding model + dimension.

Vectors from different models are not comparable, so the collection name and
metadata carry the model. Changing the model = new collection + re-ingest.
"""

import re
from dataclasses import dataclass
from typing import Any
from urllib.parse import urlparse

import chromadb
from chromadb.api import ClientAPI
from chromadb.api.models.Collection import Collection

from app.settings import Settings


def collection_name(embedding_model: str, dimension: int) -> str:
    model = re.sub(r"[^a-zA-Z0-9_-]", "-", embedding_model.removesuffix(":latest"))
    return f"chunks_{model}_{dimension}"


@dataclass
class ChunkRecord:
    id: str
    text: str
    embedding: list[float]
    metadata: dict[str, Any]


@dataclass
class QueryHit:
    id: str
    text: str
    metadata: dict[str, Any]
    distance: float


class Store:
    def __init__(self, settings: Settings):
        url = urlparse(settings.chroma_url)
        self.client: ClientAPI = chromadb.HttpClient(
            host=url.hostname or "localhost",
            port=url.port or 8000,
            ssl=url.scheme == "https",
        )
        self.embedding_model = settings.embedding_model
        self.dimension = settings.embedding_dimension
        self.name = collection_name(self.embedding_model, self.dimension)
        self._collection: Collection | None = None

    @property
    def collection(self) -> Collection:
        if self._collection is None:
            collection = self.client.get_or_create_collection(
                self.name,
                configuration={"hnsw": {"space": "cosine"}},
                metadata={"embedding_model": self.embedding_model, "dimension": self.dimension},
                embedding_function=None,  # we always send precomputed vectors
            )
            meta = collection.metadata or {}
            if meta.get("embedding_model") != self.embedding_model or meta.get("dimension") != self.dimension:
                raise RuntimeError(
                    f"Collection '{self.name}' was created with {meta}; refusing to mix "
                    f"vectors from '{self.embedding_model}' ({self.dimension})"
                )
            self._collection = collection
        return self._collection

    def heartbeat(self) -> bool:
        try:
            self.client.heartbeat()
            return True
        except Exception:
            return False

    def replace_document(self, document_id: str, chunks: list[ChunkRecord]) -> None:
        """Idempotent ingest: drop the previous chunks of the document, then add."""
        self.delete_document(document_id)
        if not chunks:
            return
        self.collection.add(
            ids=[c.id for c in chunks],
            embeddings=[c.embedding for c in chunks],
            documents=[c.text for c in chunks],
            metadatas=[c.metadata for c in chunks],
        )

    def delete_document(self, document_id: str) -> None:
        self.collection.delete(where={"documentId": document_id})

    def query(self, embedding: list[float], namespace: str, k: int) -> list[QueryHit]:
        result = self.collection.query(
            query_embeddings=[embedding],
            n_results=k,
            where={"namespace": namespace},
            include=["documents", "metadatas", "distances"],
        )
        return [
            QueryHit(id=i, text=t or "", metadata=dict(m or {}), distance=d)
            for i, t, m, d in zip(
                result["ids"][0],
                result["documents"][0],
                result["metadatas"][0],
                result["distances"][0],
            )
        ]
