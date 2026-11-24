# rag-service

RAG service in Python + FastAPI: ingest (chunking + embeddings), retrieval from Chroma, and answer generation through an OpenAI-compatible API (Ollama today).

## Run locally (Windows)

Requires the Compose stack (`docker compose up -d` from the repo root) and Ollama with `qwen3:8b` and `bge-m3`.

```powershell
cd rag-service
uv sync
uv run uvicorn app.main:app --reload --port 8000
```

- Swagger UI: http://localhost:8000/docs
- OpenAPI: http://localhost:8000/openapi.json

The configuration comes from environment variables or from `rag-service/.env` (see [app/settings.py](app/settings.py)). The defaults point to `localhost`.

## Endpoints

| Method | Path | Description |
|---|---|---|
| GET | `/health` | Checks Chroma, the LLM and that both models are available. Returns 503 if anything is missing. |
| POST | `/ingest` | Multipart: `file` (.md, .txt or .pdf), `documentId`, `namespace`, `title`. Idempotent: replaces the previous chunks of the document. |
| DELETE | `/documents/{documentId}` | Deletes every chunk of the document. Idempotent: returns `deletedChunkCount` (0 if there was nothing). |
| POST | `/ask` | JSON `{question, namespace, history, k}`. SSE by default (events `sources`, `token`, `done`, `error`); `?stream=false` returns JSON. |

## Examples (curl)

```bash
curl localhost:8000/ingest -F "file=@eval/corpus/runbook-pagos.md" \
  -F "documentId=11111111-1111-4111-8111-111111111111" -F "namespace=demo"

# Body from a UTF-8 file: on Windows, curl sends inline arguments in the
# console encoding, which breaks accents and "¿".
curl -N localhost:8000/ask -H "Content-Type: application/json" --data-binary @ask.json
```

## OpenAPI contract

The System API generates its C# client from a versioned snapshot of this contract. After changing an endpoint or a schema:

```bash
uv run python -m scripts.export_openapi   # writes system-api/src/RunbookAI.SystemApi/Rag/rag-service.openapi.json
```

`tests/test_openapi_snapshot.py` fails if the snapshot is out of date.

## Inspect Chroma

```bash
uv run python -m scripts.inspect_chroma --document-id <id> --limit 5
```

## Evaluation

```bash
uv run python -m scripts.run_eval                                  # current defaults vs eval/baseline.json
uv run python -m scripts.run_eval --strategy paragraph --k 3 --prompt v1 --tag my-test
uv run python -m scripts.make_pdfs                                 # regenerate the PDF corpus
```

Corpus, questions and results are described in [docs/evaluacion.md](../docs/evaluacion.md).

## Tests

```bash
uv run pytest                  # all (integration tests are skipped if Chroma/Ollama are down)
uv run pytest -m "not integration"
```
