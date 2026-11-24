# RAG service: FastAPI + uvicorn. No PyTorch: embeddings and LLM go through the
# OpenAI-compatible API (Ollama on the Windows host today).
FROM python:3.13.18-slim

# uv as a static binary, same version that created the local venv / uv.lock.
COPY --from=ghcr.io/astral-sh/uv:0.13.9 /uv /usr/local/bin/uv

ENV UV_COMPILE_BYTECODE=1 \
    UV_LINK_MODE=copy \
    UV_PYTHON_DOWNLOADS=never \
    PYTHONUNBUFFERED=1 \
    PATH="/app/.venv/bin:$PATH"

WORKDIR /app

# Dependencies first, in their own layer: changing the code does not reinstall them.
COPY pyproject.toml uv.lock .python-version ./
RUN uv sync --frozen --no-dev

COPY app ./app
COPY scripts ./scripts

RUN useradd --system --uid 10001 rag && chown -R rag /app
USER rag

EXPOSE 8000
CMD ["uvicorn", "app.main:app", "--host", "0.0.0.0", "--port", "8000"]
