"""Prompt construction. Instructions are in Spanish because they shape the
user-facing answer; identifiers stay in English."""

from typing import Literal

from app.schemas import HistoryMessage, Source

PromptVersion = Literal["v1", "v2", "v3"]

# Fixed sentence for "the documents don't answer this", so abstention can be
# detected automatically (UI and evaluation).
NO_INFO_ANSWER = "No encontré esa información en los documentos."

SYSTEM_PROMPTS: dict[str, str] = {
    "v1": """Sos un asistente que responde preguntas sobre runbooks y documentación técnica.

Reglas:
- Respondé en español, de forma clara y concisa.
- Usá solo la información del CONTEXTO. Si el contexto no alcanza para responder, decilo.
- Citá las fuentes que uses con su número entre corchetes, por ejemplo [1] o [2][3].
- Si hay pasos o comandos, mantenelos exactos y en orden.""",
    "v2": f"""Sos un asistente que responde preguntas sobre runbooks y documentación técnica interna.

Reglas estrictas:
1. Respondé SOLO con información que aparezca en el CONTEXTO. No uses conocimiento propio, no completes con suposiciones y no relaciones secciones distintas si el texto no lo hace.
2. Si el CONTEXTO no contiene la respuesta, respondé exactamente: "{NO_INFO_ANSWER}" y nada más.
3. Citá cada afirmación con el número de su fuente entre corchetes, por ejemplo [1] o [2][3].
4. Copiá comandos, nombres y valores exactamente como aparecen, y mantené el orden de los pasos.
5. Respondé en español, aunque el contexto esté en otro idioma. Sé conciso.""",
    # v3 = v2 + the conversation as a second, limited source: what the user said
    # about themselves or the conversation itself can be used; technical facts still
    # need the CONTEXTO and a citation.
    "v3": f"""Sos un asistente que responde preguntas sobre runbooks y documentación técnica interna.

Tenés dos fuentes:
- El CONTEXTO: fragmentos numerados de los documentos.
- La conversación previa con el usuario.

Reglas estrictas:
1. Toda información técnica (procedimientos, comandos, valores, umbrales, responsables) tiene que salir SOLO del CONTEXTO. No uses conocimiento propio, no completes con suposiciones y no relaciones secciones distintas si el texto no lo hace.
2. Lo que el usuario dijo en la conversación (por ejemplo su nombre, su equipo o de qué servicio viene hablando) sí podés usarlo para responder y para entender a qué se refiere la pregunta. Eso no lleva cita.
3. Si ni el CONTEXTO ni la conversación contienen la respuesta, respondé exactamente: "{NO_INFO_ANSWER}" y nada más.
4. Citá cada afirmación que salga del CONTEXTO con el número de su fuente entre corchetes, por ejemplo [1] o [2][3].
5. Copiá comandos, nombres y valores exactamente como aparecen, y mantené el orden de los pasos.
6. Respondé en español, aunque el contexto esté en otro idioma. Sé conciso.""",
}

# Query rewriting ("condense question"): turns a follow-up into a standalone
# question, used only to embed and search. The answer still sees the original.
REWRITE_PROMPT = """Tu tarea es reescribir la última pregunta del usuario para que se entienda sola, sin la conversación.

Reglas:
- Reemplazá pronombres y referencias ("eso", "lo", "su", "y cuánto tarda", "el anterior") por lo que nombran en la conversación. Preferí el nombre del servicio, sistema o procedimiento (por ejemplo "payments-api", "el failover de orders-db") antes que un detalle de la respuesta.
- Mantené la persona: si el usuario habla de sí mismo ("me", "mi", "estoy"), la pregunta sigue en primera persona.
- Si la pregunta ya se entiende sola o cambia de tema, devolvela igual.
- No la respondas. No agregues explicaciones ni comillas.
- Mantené el idioma de la pregunta.

Devolvé solo la pregunta reescrita, en una línea."""

# A rewrite longer than this is not a question but a rambling answer: discard it.
REWRITE_MAX_CHARS = 400

# Back-compat alias used by the API when nothing is retrieved.
NO_CONTEXT_ANSWER = NO_INFO_ANSWER


def format_context(sources: list[Source]) -> str:
    blocks = [f"[{s.index}] (documento: {s.title})\n{s.text}" for s in sources]
    return "\n\n---\n\n".join(blocks)


def build_messages(
    question: str,
    sources: list[Source],
    history: list[HistoryMessage],
    history_max_messages: int,
    prompt_version: PromptVersion = "v1",
    search_query: str | None = None,
) -> list[dict[str, str]]:
    messages: list[dict[str, str]] = [{"role": "system", "content": SYSTEM_PROMPTS[prompt_version]}]
    recent = history[-history_max_messages:] if history_max_messages > 0 else []
    messages += [{"role": m.role, "content": m.content} for m in recent]
    content = f"CONTEXTO:\n\n{format_context(sources)}\n\nPREGUNTA: {question}"
    # The standalone rewrite resolves "eso" / "lo" for the answer too; the original
    # question stays, so a bad rewrite cannot replace what the user asked.
    if search_query and search_query != question:
        content += f"\n(Interpretación de la pregunta: {search_query})"
    messages.append({"role": "user", "content": content})
    return messages


def build_rewrite_messages(question: str, history: list[HistoryMessage], history_max_messages: int) -> list[dict[str, str]]:
    recent = history[-history_max_messages:] if history_max_messages > 0 else []
    conversation = "\n".join(f"{'Usuario' if m.role == 'user' else 'Asistente'}: {m.content}" for m in recent)
    return [
        {"role": "system", "content": REWRITE_PROMPT},
        {"role": "user", "content": f"CONVERSACIÓN:\n{conversation}\n\nÚLTIMA PREGUNTA: {question}"},
    ]


def clean_rewrite(output: str, question: str) -> str:
    """The model's rewrite, or the original question if the output is unusable."""
    lines = [line.strip() for line in output.strip().splitlines() if line.strip()]
    if not lines:
        return question
    rewritten = lines[0]
    for prefix in ("Pregunta reescrita:", "ÚLTIMA PREGUNTA:", "Pregunta:"):
        rewritten = rewritten.removeprefix(prefix)
    rewritten = rewritten.strip("\"'«»“” ")
    if not rewritten or len(rewritten) > REWRITE_MAX_CHARS:
        return question
    return rewritten
