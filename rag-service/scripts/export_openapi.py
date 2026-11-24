"""Write the RAG service OpenAPI contract to the System API snapshot.

The System API generates its C# client (NSwag) from this versioned file, so the
contract change shows up in the diff. tests/test_openapi_snapshot.py fails if the
snapshot is out of date. No Chroma or Ollama needed: it does not start the server.

Usage (from rag-service/):
    uv run python -m scripts.export_openapi
"""

import json
from pathlib import Path
from typing import Any

from app.main import app

SNAPSHOT = (
    Path(__file__).resolve().parents[2]
    / "system-api" / "src" / "RunbookAI.SystemApi" / "Rag" / "rag-service.openapi.json"
)

NULL = {"type": "null"}


def to_openapi_30(node: Any) -> Any:
    """Rewrite the OpenAPI 3.1 constructs FastAPI emits into their 3.0 form.

    NSwag (NJsonSchema) only partly understands 3.1: `anyOf: [X, {type: null}]`
    becomes a wrapper class per field, and `contentMediaType` is not seen as a
    file upload. Same contract, older dialect:
      anyOf [X, null]                -> X + nullable: true
      contentMediaType octet-stream  -> format: binary
    """
    if isinstance(node, list):
        return [to_openapi_30(n) for n in node]
    if not isinstance(node, dict):
        return node
    node = {k: to_openapi_30(v) for k, v in node.items()}
    any_of = node.get("anyOf")
    if isinstance(any_of, list) and NULL in any_of:
        rest = [s for s in any_of if s != NULL]
        del node["anyOf"]
        if len(rest) == 1:
            node = {**rest[0], **node}
        else:
            node["anyOf"] = rest
        node["nullable"] = True
    if node.get("contentMediaType") == "application/octet-stream":
        del node["contentMediaType"]
        node["format"] = "binary"
    return node


def render() -> str:
    spec = to_openapi_30(app.openapi())
    spec["openapi"] = "3.0.3"
    return json.dumps(spec, indent=2, ensure_ascii=False) + "\n"


def main() -> None:
    SNAPSHOT.parent.mkdir(parents=True, exist_ok=True)
    SNAPSHOT.write_text(render(), encoding="utf-8", newline="\n")
    print(f"Wrote {SNAPSHOT}")


if __name__ == "__main__":
    main()
