"""Prompt construction. Instructions are in Spanish because they shape the
user-facing answer; identifiers stay in English."""

from typing import Literal

from app.schemas import HistoryMessage, Source

PromptVersion = Literal["v1", "v2"]

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
}

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
) -> list[dict[str, str]]:
    messages: list[dict[str, str]] = [{"role": "system", "content": SYSTEM_PROMPTS[prompt_version]}]
    recent = history[-history_max_messages:] if history_max_messages > 0 else []
    messages += [{"role": m.role, "content": m.content} for m in recent]
    messages.append(
        {
            "role": "user",
            "content": f"CONTEXTO:\n\n{format_context(sources)}\n\nPREGUNTA: {question}",
        }
    )
    return messages
