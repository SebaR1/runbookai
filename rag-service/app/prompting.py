"""Prompt construction. Instructions are in Spanish because they shape the
user-facing answer; identifiers stay in English."""

from app.schemas import HistoryMessage, Source

SYSTEM_PROMPT = """Sos un asistente que responde preguntas sobre runbooks y documentación técnica.

Reglas:
- Respondé en español, de forma clara y concisa.
- Usá solo la información del CONTEXTO. Si el contexto no alcanza para responder, decilo.
- Citá las fuentes que uses con su número entre corchetes, por ejemplo [1] o [2][3].
- Si hay pasos o comandos, mantenelos exactos y en orden."""

NO_CONTEXT_ANSWER = "No encontré información relevante en los documentos para responder esa pregunta."


def format_context(sources: list[Source]) -> str:
    blocks = [f"[{s.index}] (documento: {s.title})\n{s.text}" for s in sources]
    return "\n\n---\n\n".join(blocks)


def build_messages(
    question: str,
    sources: list[Source],
    history: list[HistoryMessage],
    history_max_messages: int,
) -> list[dict[str, str]]:
    messages: list[dict[str, str]] = [{"role": "system", "content": SYSTEM_PROMPT}]
    recent = history[-history_max_messages:] if history_max_messages > 0 else []
    messages += [{"role": m.role, "content": m.content} for m in recent]
    messages.append(
        {
            "role": "user",
            "content": f"CONTEXTO:\n\n{format_context(sources)}\n\nPREGUNTA: {question}",
        }
    )
    return messages
