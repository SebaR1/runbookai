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

Errors are ProblemDetails (RFC 9457) with `title` and `detail` in Spanish.

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
