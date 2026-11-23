"""Simple chunking: paragraphs packed up to a max size, with overlap.

Phase 3 replaces this with heading-aware chunking; this version is the baseline
to compare against.
"""

import re
import uuid

# Fixed namespace so chunk ids are deterministic: same document + order -> same id.
CHUNK_ID_NAMESPACE = uuid.UUID("6f1c1d1e-3b4a-4c7e-9a43-52d2f0b7a001")


def chunk_id(document_id: str, order: int) -> str:
    return str(uuid.uuid5(CHUNK_ID_NAMESPACE, f"{document_id}:{order}"))


def _split_long(text: str, size: int) -> list[str]:
    """Split a paragraph longer than `size` at whitespace boundaries."""
    parts: list[str] = []
    while len(text) > size:
        cut = text.rfind(" ", 0, size)
        if cut <= 0:
            cut = size
        parts.append(text[:cut].strip())
        text = text[cut:].strip()
    if text:
        parts.append(text)
    return parts


def _tail(text: str, overlap: int) -> str:
    """Last ~`overlap` chars of text, starting at a word boundary."""
    if overlap <= 0 or len(text) <= overlap:
        return text if overlap > 0 else ""
    tail = text[-overlap:]
    space = tail.find(" ")
    return tail[space + 1 :] if space != -1 else tail


def split_markdown(text: str, size: int = 1000, overlap: int = 150) -> list[str]:
    if overlap >= size:
        raise ValueError("overlap must be smaller than size")

    paragraphs = [p.strip() for p in re.split(r"\n\s*\n", text) if p.strip()]
    pieces = [piece for p in paragraphs for piece in _split_long(p, size)]

    chunks: list[str] = []
    current = ""
    for piece in pieces:
        candidate = f"{current}\n\n{piece}" if current else piece
        if len(candidate) <= size:
            current = candidate
            continue
        chunks.append(current)
        carry = _tail(current, overlap)
        current = f"{carry}\n\n{piece}" if carry and len(carry) + len(piece) + 2 <= size else piece
    if current:
        chunks.append(current)
    return chunks
