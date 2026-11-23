from functools import lru_cache
from typing import Literal

from pydantic_settings import BaseSettings, SettingsConfigDict


class Settings(BaseSettings):
    """Configuration from environment variables (or rag-service/.env).

    Defaults target running the service directly on Windows against the
    Compose stack. Inside Compose, the env vars point to the container names.
    """

    model_config = SettingsConfigDict(env_file=".env", extra="ignore")

    # OpenAI-compatible provider (Ollama today; OpenAI / Azure OpenAI later).
    openai_base_url: str = "http://localhost:11434/v1"
    openai_api_key: str = "ollama"

    llm_model: str = "qwen3:8b"
    llm_temperature: float = 0.2
    # "none" disables Qwen3 thinking through the OpenAI API. Empty = don't send it.
    llm_reasoning_effort: str | None = "none"

    embedding_model: str = "bge-m3"
    embedding_dimension: int = 1024
    embedding_batch_size: int = 32

    chroma_url: str = "http://localhost:8001"

    # Defaults chosen with the Phase 3 evaluation (docs/evaluacion.md).
    chunk_strategy: Literal["paragraph", "heading"] = "heading"
    chunk_size: int = 1000
    chunk_overlap: int = 150
    top_k: int = 5
    # Chunks farther than this (cosine distance) are discarded before the LLM. None = keep all.
    max_distance: float | None = None
    prompt_version: Literal["v1", "v2"] = "v2"
    history_max_messages: int = 8

    log_level: str = "INFO"


@lru_cache
def get_settings() -> Settings:
    return Settings()
