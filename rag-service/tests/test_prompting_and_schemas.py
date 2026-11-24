from app.prompting import (
    NO_INFO_ANSWER,
    REWRITE_MAX_CHARS,
    REWRITE_PROMPT,
    SYSTEM_PROMPTS,
    build_messages,
    build_rewrite_messages,
    clean_rewrite,
)
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


def test_rewrite_messages_carry_recent_history_and_the_question():
    history = [HistoryMessage(role="user", content="¿Cómo hago el failover de orders-db?"),
               HistoryMessage(role="assistant", content="Ejecutá az postgres ... [1]")]
    messages = build_rewrite_messages("¿Y cuánto tarda?", history, 8)

    assert messages[0] == {"role": "system", "content": REWRITE_PROMPT}
    content = messages[1]["content"]
    assert "Usuario: ¿Cómo hago el failover de orders-db?" in content
    assert "Asistente: Ejecutá az postgres ... [1]" in content
    assert content.endswith("ÚLTIMA PREGUNTA: ¿Y cuánto tarda?")


def test_clean_rewrite_strips_noise_and_falls_back_to_the_question():
    original = "¿Y cuánto tarda?"
    assert clean_rewrite('Pregunta reescrita: "¿Cuánto tarda el failover?"\nExplicación...', original) == "¿Cuánto tarda el failover?"
    assert clean_rewrite("  \n ", original) == original
    assert clean_rewrite("x" * (REWRITE_MAX_CHARS + 1), original) == original


def test_prompt_v3_allows_the_conversation_but_keeps_the_fixed_abstention():
    assert NO_INFO_ANSWER in SYSTEM_PROMPTS["v3"]
    assert "conversación" in SYSTEM_PROMPTS["v3"]


def test_answer_prompt_adds_the_rewrite_as_interpretation_only_when_it_differs():
    sources = [_source(1, "x")]
    with_rewrite = build_messages("¿Y eso cuánto tarda?", sources, [], 8, "v3",
                                  search_query="¿Cuánto tarda el failover de orders-db?")[-1]["content"]
    assert "PREGUNTA: ¿Y eso cuánto tarda?" in with_rewrite
    assert with_rewrite.endswith("(Interpretación de la pregunta: ¿Cuánto tarda el failover de orders-db?)")

    same = build_messages("¿Cuánto dura?", sources, [], 8, "v3", search_query="¿Cuánto dura?")[-1]["content"]
    assert "Interpretación" not in same
