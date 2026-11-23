"""Print what is stored in Chroma: collections, their metadata and chunks.

Usage (from rag-service/):
    uv run python -m scripts.inspect_chroma [--document-id ID] [--limit N]
"""

import argparse

from app.settings import get_settings
from app.store import Store


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("--document-id")
    parser.add_argument("--limit", type=int, default=20)
    args = parser.parse_args()

    store = Store(get_settings())
    print("Collections:")
    for col in store.client.list_collections():
        hnsw = (col.configuration_json or {}).get("hnsw") or {}
        print(f"  - {col.name}: count={col.count()} metadata={col.metadata} space={hnsw.get('space')}")

    where = {"documentId": args.document_id} if args.document_id else None
    result = store.collection.get(where=where, limit=args.limit, include=["documents", "metadatas", "embeddings"])
    print(f"\nChunks in '{store.name}' (showing up to {args.limit}):")
    rows = sorted(
        zip(result["ids"], result["documents"], result["metadatas"], result["embeddings"]),
        key=lambda r: (r[2]["documentId"], r[2]["order"]),
    )
    for chunk_id, text, meta, vector in rows:
        preview = text.replace("\n", " ")[:90]
        print(f"\n  id={chunk_id}\n  meta={meta}\n  vector=dim {len(vector)} [{vector[0]:.4f}, {vector[1]:.4f}, ...]\n  text={preview}...")


if __name__ == "__main__":
    main()
