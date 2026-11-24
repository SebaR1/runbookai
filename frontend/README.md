# frontend

Angular 22 SPA (standalone components, signals, zoneless). It only presents and calls the System API: documents (upload, status with polling, reindex, delete) and chat (streaming, sources, cancel). UI texts in Spanish.

Requires Node `^22.22.3 || ^24.15.0 || >=26` (verified with 26.13.1). The Angular CLI is the project's own (`npx ng`), not the global one.

## Run with Docker

Part of the Compose stack: http://localhost:8080. nginx serves the SPA and proxies `/api/*` to the System API ([nginx/default.conf](nginx/default.conf)):

- `/api` prefix, stripped before the System API: SPA routes (`/documents`) and API routes (`/documents`) would collide on a reload otherwise.
- `/api/conversations/{id}/messages`: `proxy_buffering off` and `gzip off`, or the tokens arrive all at once. The System API also sends `X-Accel-Buffering: no` (nginx consumes it, so it does not reach the browser).
- `client_max_body_size 21m` (nginx default: 1 MB). Above it, nginx answers 413 as ProblemDetails.
- `resolver 127.0.0.11`: follows the System API when its container is recreated (new IP).
- Fallback to `index.html` for the Angular routes; hashed files cached for a year, `index.html` revalidated.

Rebuild after a change: `docker compose up -d --build front`.

## Run locally

```powershell
cd frontend
npm install
npm start          # http://localhost:4200
```

`ng serve` proxies `/api` to the System API on `127.0.0.1:5080` ([proxy.conf.json](proxy.conf.json)), so the Compose stack must be up. It can run alongside the `front` container (different ports).

## System API client (NSwag)

| File | Versioned | Written by |
|---|---|---|
| [src/app/api/system-api.openapi.json](src/app/api/system-api.openapi.json) | yes | the System API test `OpenApiSnapshotTests` |
| [src/app/api/system-api.client.ts](src/app/api/system-api.client.ts) | yes | `npm run api:generate` ([nswag.json](nswag.json)) |

The generated client is versioned because NSwag needs the .NET runtime, and the Docker build is Node only. After changing the System API contract:

```powershell
cd system-api
$env:UPDATE_OPENAPI_SNAPSHOT = "1"; dotnet test -- --filter-method "*Front_snapshot*"; Remove-Item env:UPDATE_OPENAPI_SNAPSHOT
cd ../frontend
npm run api:generate
npm run api:check  # regenerates and fails if the versioned client differs
```

The System API emits OpenAPI 3.0 (NSwag only partly supports 3.1), with two transformers so NSwag reads it well: multipart forms flattened (the file parameter) and nullable enums without `null` among their values.

## Chat streaming

`POST /conversations/{id}/messages` answers SSE, and `EventSource` only does GET, so the stream is read with `fetch` and parsed in [core/sse-parser.ts](src/app/core/sse-parser.ts) (WHATWG format: lines split between chunks, CRLF, multi-line data, comments; an event without its closing empty line is discarded). Not a library: `@microsoft/fetch-event-source` retries on its own, and retrying a POST would send the question again.

- [core/chat-stream.ts](src/app/core/chat-stream.ts): the request and the typed events (`start`, `sources`, `token`, `done`, `error`). A rejection before the stream (400, 404, 502) is a `ChatRequestError`: nothing was saved and the question goes back to the box.
- [core/chat-reducer.ts](src/app/core/chat-reducer.ts): pure state of the answer. "Detener" aborts the `fetch` (`AbortController`): the System API sees the disconnect and saves the answer as `cancelled`. A stream that ends without `done` or `error` is shown as an error.
- [core/message-text.ts](src/app/core/message-text.ts): splits the answer into text, citations and code. Only `[n]` with a source of that `index` is a citation (`[conversación previa]` or `[7]` without a source stay as text). Inline code and ``` blocks are rendered as code. No `innerHTML`: the text comes from an LLM.

## Tests

Vitest (`npm test`): SSE parser, stream reading (accents split at byte level), reducer, citations and code, document rules, and the message component.
