"""OpenAI-compatible client for embeddings and chat.

Points to Ollama today. Switching to OpenAI / Azure OpenAI is configuration
(base URL, key, model names), not code.
"""

from collections.abc import Iterator
from dataclasses import dataclass
from typing import Any

from openai import OpenAI

from app.settings import Settings


@dataclass
class StreamEvent:
    text: str | None = None
    input_tokens: int | None = None
    output_tokens: int | None = None


class LLM:
    def __init__(self, settings: Settings):
        self.settings = settings
        self.client = OpenAI(base_url=settings.openai_base_url, api_key=settings.openai_api_key)

    def embed(self, texts: list[str]) -> list[list[float]]:
        size = self.settings.embedding_batch_size
        vectors: list[list[float]] = []
        for i in range(0, len(texts), size):
            response = self.client.embeddings.create(
                model=self.settings.embedding_model, input=texts[i : i + size]
            )
            vectors += [d.embedding for d in sorted(response.data, key=lambda d: d.index)]
        expected = self.settings.embedding_dimension
        if vectors and len(vectors[0]) != expected:
            raise ValueError(
                f"Embedding dimension mismatch: model '{self.settings.embedding_model}' "
                f"returned {len(vectors[0])}, configured {expected}"
            )
        return vectors

    def _chat_kwargs(self) -> dict[str, Any]:
        kwargs: dict[str, Any] = {
            "model": self.settings.llm_model,
            "temperature": self.settings.llm_temperature,
        }
        if self.settings.llm_reasoning_effort:
            kwargs["reasoning_effort"] = self.settings.llm_reasoning_effort
        return kwargs

    def chat(self, messages: list[dict[str, str]]) -> tuple[str, int | None, int | None]:
        response = self.client.chat.completions.create(messages=messages, **self._chat_kwargs())
        usage = response.usage
        return (
            response.choices[0].message.content or "",
            usage.prompt_tokens if usage else None,
            usage.completion_tokens if usage else None,
        )

    def stream_chat(self, messages: list[dict[str, str]]) -> Iterator[StreamEvent]:
        stream = self.client.chat.completions.create(
            messages=messages,
            stream=True,
            stream_options={"include_usage": True},
            **self._chat_kwargs(),
        )
        for chunk in stream:
            if chunk.choices and chunk.choices[0].delta.content:
                yield StreamEvent(text=chunk.choices[0].delta.content)
            if chunk.usage:
                yield StreamEvent(
                    input_tokens=chunk.usage.prompt_tokens,
                    output_tokens=chunk.usage.completion_tokens,
                )

    def missing_models(self) -> list[str]:
        available = {m.id for m in self.client.models.list()}
        # Ollama lists "bge-m3:latest" for "bge-m3".
        available |= {name.removesuffix(":latest") for name in available}
        wanted = [self.settings.llm_model, self.settings.embedding_model]
        return [m for m in wanted if m not in available]
