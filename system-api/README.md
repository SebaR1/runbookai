# system-api

System API in ASP.NET Core (.NET 10, Minimal APIs). Owns the original documents (Azurite / Blob), the document registry (MongoDB) and, from Phase 5, the conversations. Orchestrates the RAG service.

## Run with Docker

The service is part of the Compose stack (`docker compose up -d --build` from the repo root, in a terminal at `C:\dev\RunbookAI`). Compose passes the internal addresses (`mongo:27017`, `azurite:10000`, `rag-service:8000`) in `environment:`.

- Swagger UI: http://localhost:5080/swagger
- Logs: `docker compose logs -f system-api`
- Rebuild after a change: `docker compose up -d --build system-api`

## Run locally (Windows)

For debugging or hot reload. Stop the container first (`docker compose stop system-api`): both use port 5080.

Requires the Compose stack (`docker compose up -d` from the repo root) and the RAG service on `localhost:8000`.

```powershell
cd system-api/src/RunbookAI.SystemApi
dotnet run --launch-profile http
```

- Swagger UI: http://localhost:5080/swagger
- OpenAPI: http://localhost:5080/openapi/v1.json

## Configuration

Options pattern, validated at startup. The defaults point to `localhost`; override them with environment variables (`Section__Key`).

| Key | Default | Notes |
|---|---|---|
| `Mongo__ConnectionString` | `mongodb://localhost:27017` | No credentials in the URL. |
| `Mongo__Database` | `runbookai` | |
| `Mongo__Username` / `Mongo__Password` | `MONGO_ROOT_USER` / `MONGO_ROOT_PASSWORD` | Taken from the `.env` variables if empty. |
| `Blob__ConnectionString` | `UseDevelopmentStorage=true` | Azurite outside Docker. Inside Compose: full string with `azurite:10000`. |
| `Blob__ServiceUri` | — | Real Azure Blob: leave `ConnectionString` empty; uses `DefaultAzureCredential`. |
| `Blob__ContainerName` | `documents` | |
| `Rag__BaseUrl` | `http://localhost:8000` | |
| `Rag__Timeout` | `00:05:00` | Ingesting a large PDF takes time. |
| `Rag__AskTimeout` | `00:03:00` | Limit for a whole streamed answer (the streaming client has no `HttpClient.Timeout`). |
| `Conversations__HistoryMaxMessages` | `8` | Messages of history sent to the RAG (whole turns only). |
| `Conversations__MaxMessageLength` | `4000` | |
| `Documents__MaxFileSizeBytes` | `20971520` (20 MB) | |

## Endpoints

| Method | Path | Description |
|---|---|---|
| GET | `/documents?namespace=&includeDeleted=false` | List, newest first. |
| GET | `/documents/{id}` | One document (poll it to follow the indexing). |
| POST | `/documents` | Multipart: `file` (.md, .txt, .pdf), `namespace` (default `default`), `title`. Returns `202` in `pending`. Same content in the same namespace: `409` with `existingDocumentId`. |
| PUT | `/documents/{id}/content` | New version: same id, `version + 1`, blob `{id}/v{n}.ext`. Only from `indexed` or `error`. |
| POST | `/documents/{id}/reindex` | Back to `pending`. Only from `indexed` or `error`. |
| DELETE | `/documents/{id}` | Chunks (RAG) → blobs → `deleted`. Idempotent. `409` while `indexing`, `502` if the RAG service fails (nothing is marked). |

| POST | `/conversations` | JSON `{namespace?, title?}`. `201`. The namespace is fixed for the conversation; without a title, the first question becomes the title. |
| GET | `/conversations?namespace=` | List, most recent activity (`updatedAt`) first. |
| GET | `/conversations/{id}` | One conversation. |
| GET | `/conversations/{id}/messages` | Messages in chronological order. |
| POST | `/conversations/{id}/messages` | JSON `{content}`. Answers with SSE (see below). `404` / `400` before the stream starts. |

Errors are ProblemDetails (RFC 9457) with `title` and `detail` in Spanish.

### Chat streaming

`POST /conversations/{id}/messages` saves the question, loads the history, calls the RAG `/ask` with a raw `HttpClient` (`ResponseHeadersRead` + `SseParser`, in `Rag/RagStreamClient.cs`) and forwards the answer as its own SSE events:

| Event | Data |
|---|---|
| `start` | `{conversationId, userMessageId, assistantMessageId}` |
| `sources` | `[{index, documentId, chunkId, title, page, section, distance}]` |
| `token` | `{text}` (many) |
| `done` | `{messageId, model, inputTokens, outputTokens}` |
| `error` | `{messageId, message}` (message in Spanish, for the user) |

The assistant message is inserted once, when the stream ends, with `status`:

- `completed`: the RAG sent `done`. Sources, model and tokens are saved.
- `error`: the RAG sent `error`, was down, or timed out. The partial text and `errorDetail` are saved; the client gets `error`.
- `cancelled`: the client disconnected. The call to the RAG is cancelled too (Ollama stops generating) and the partial text is saved.

Only complete turns (`user` answered by a `completed` assistant message) go into the history, the last `HistoryMaxMessages`, in chronological order. Two messages sent at the same time to the same conversation are not serialized (no users yet).

With curl (the body in a UTF-8 file, so accents survive on Windows; `-N` disables buffering so the tokens show up as they arrive):

```powershell
'{"namespace":"default"}' | Out-File -Encoding ascii conv.json
curl.exe -s -H "Content-Type: application/json" --data-binary "@conv.json" http://127.0.0.1:5080/conversations
# q.json: {"content":"¿Cómo fuerzo un failover manual de orders-db?"}  (saved as UTF-8)
curl.exe -N -H "Content-Type: application/json" --data-binary "@q.json" http://127.0.0.1:5080/conversations/<id>/messages
```

Behind a proxy buffering must be off for this route, or the tokens arrive all at once. The response carries `X-Accel-Buffering: no` (honored by nginx and ingress-nginx), and the front's nginx also sets `proxy_buffering off` for it.

## OpenAPI and the front's client

The document is emitted as OpenAPI 3.0 (`/openapi/v1.json`), not the default 3.1: NSwag, which generates the front's TypeScript client, only partly supports 3.1. Two transformers in `Infrastructure/` adapt what NSwag misreads: multipart forms flattened into one object with the file as `format: binary`, and nullable enums without `null` among their values. Numbers are serialized strictly (no `"12"` accepted), so integers are typed as `integer` only.

The front builds its client from a versioned copy, `frontend/src/app/api/system-api.openapi.json`. The test `OpenApiSnapshotTests` (integration: the app needs Mongo and Azurite to start) fails if it is out of date. To refresh it:

```powershell
$env:UPDATE_OPENAPI_SNAPSHOT = "1"; dotnet test -- --filter-method "*Front_snapshot*"; Remove-Item env:UPDATE_OPENAPI_SNAPSHOT
cd ../frontend; npm run api:generate
```

### Indexing

`POST` stores the blob and the record and enqueues the id in an in-process `Channel` (`IIndexingQueue`). `IndexingWorker` (a `BackgroundService`, single consumer) moves the document `pending → indexing → indexed | error`, with conditional updates on the expected status and version. On startup it re-enqueues whatever is left in `pending` or `indexing`. This works with a single instance; several replicas need a real queue (Phase 8).

## RAG service client (NSwag)

Generated at build time by `NSwag.ApiDescription.Client` (`<OpenApiReference>` in the `.csproj`) into `obj/`, not versioned. The source is the versioned snapshot [Rag/rag-service.openapi.json](src/RunbookAI.SystemApi/Rag/rag-service.openapi.json). After changing the RAG contract:

```powershell
cd rag-service
uv run python -m scripts.export_openapi
```

The snapshot is written as OpenAPI 3.0: NSwag only partly supports the 3.1 that FastAPI emits. The RAG test `test_openapi_snapshot.py` fails if the snapshot is out of date.

## Tests

xUnit v3 on Microsoft.Testing.Platform (`global.json`). The integration tests use the real Mongo, Azurite and RAG service, with their own database and blob container per run (dropped at the end). They are skipped if a dependency is down or `MONGO_ROOT_USER` / `MONGO_ROOT_PASSWORD` are not set.

```powershell
cd system-api
dotnet test                                                  # all
dotnet test -- --filter-not-trait "Category=Integration"     # unit only
```
