from app.prompting import SYSTEM_PROMPTS, build_messages
from app.schemas import AskRequest, HistoryMessage, Source
from app.store import collection_name


def _source(index: int, text: str) -> Source:
    return Source(index=index, document_id="d1", chunk_id=f"c{index}", title="Runbook", page=1,
                  order=index, distance=0.1, text=text)


def test_messages_have_system_history_and_question_with_numbered_context():
    history = [HistoryMessage(role="user", content=f"q{i}") for i in range(10)]
    messages = build_messages("¿Cómo reinicio?", [_source(1, "paso A"), _source(2, "paso B")], history, 4)

    assert messages[0] == {"role": "system", "content": SYSTEM_PROMPTS["v1"]}
    assert [m["content"] for m in messages[1:5]] == ["q6", "q7", "q8", "q9"]  # only the last N
    last = messages[-1]
    assert last["role"] == "user"
    assert "[1] (documento: Runbook)\npaso A" in last["content"]
    assert "[2] (documento: Runbook)\npaso B" in last["content"]
    assert last["content"].endswith("PREGUNTA: ¿Cómo reinicio?")


def test_json_contract_is_camel_case_and_accepts_both_casings():
    dumped = _source(1, "x").model_dump(by_alias=True)
    assert {"documentId", "chunkId"} <= dumped.keys()
    assert "document_id" not in dumped

    assert AskRequest.model_validate({"question": "q", "history": [{"role": "user", "content": "hi"}]}).namespace == "default"


def test_collection_name_carries_model_and_dimension():
    assert collection_name("bge-m3", 1024) == "chunks_bge-m3_1024"
    assert collection_name("bge-m3:latest", 1024) == "chunks_bge-m3_1024"
    assert collection_name("text-embedding-3-small", 1536) == "chunks_text-embedding-3-small_1536"
