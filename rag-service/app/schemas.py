"""API contract (exposed through OpenAPI).

Python uses snake_case; JSON uses camelCase. The conversion is configured once
in ApiModel, never per field.
"""

from typing import Literal

from pydantic import BaseModel, ConfigDict, Field
from pydantic.alias_generators import to_camel


class ApiModel(BaseModel):
    model_config = ConfigDict(alias_generator=to_camel, populate_by_name=True)


class IngestResponse(ApiModel):
    document_id: str
    namespace: str
    chunk_count: int
    doc_hash: str
    collection: str
    embedding_model: str


class HistoryMessage(ApiModel):
    role: Literal["user", "assistant"]
    content: str


class AskRequest(ApiModel):
    question: str = Field(min_length=1)
    namespace: str = "default"
    history: list[HistoryMessage] = []
    k: int | None = Field(default=None, ge=1, le=20)


class Source(ApiModel):
    index: int = Field(description="Number used to cite this source in the answer, e.g. [1]")
    document_id: str
    chunk_id: str
    title: str
    page: int
    section: str = ""
    order: int
    distance: float
    text: str


class AskResponse(ApiModel):
    answer: str
    sources: list[Source]
    model: str
    input_tokens: int | None
    output_tokens: int | None


class HealthResponse(ApiModel):
    status: Literal["ok", "degraded"]
    chroma: bool
    llm: bool
    missing_models: list[str]
    collection: str
