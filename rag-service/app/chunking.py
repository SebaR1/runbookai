"""Chunking strategies.

- paragraph: paragraphs packed up to a max size, with overlap (Phase 2 baseline).
- heading: split markdown by headings; each chunk carries its section path
  ("Doc > Section > Subsection") as a prefix so the embedding knows the context.
"""

import re
import uuid
from dataclasses import dataclass
from typing import Literal

from app.extraction import Page

ChunkStrategy = Literal["paragraph", "heading"]


@dataclass
class Chunk:
    text: str
    page: int
    section: str

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


HEADING = re.compile(r"^(#{1,6})\s+(.+?)\s*#*\s*$")


def split_by_headings(text: str, size: int = 1000, overlap: int = 150) -> list[tuple[str, str]]:
    """Return (section_path, chunk_text) pairs. chunk_text starts with the path."""
    sections: list[tuple[str, str]] = []
    stack: list[tuple[int, str]] = []
    body: list[str] = []
    in_fence = False

    def flush() -> None:
        content = "\n".join(body).strip()
        body.clear()
        if not content:
            return
        path = " > ".join(title for _, title in stack)
        budget = max(size - len(path) - 2, size // 2)
        for piece in split_markdown(content, budget, min(overlap, budget // 2)):
            sections.append((path, f"{path}\n\n{piece}" if path else piece))

    for line in text.splitlines():
        if line.lstrip().startswith("```"):
            in_fence = not in_fence
        match = None if in_fence else HEADING.match(line)
        if match:
            flush()
            level = len(match.group(1))
            while stack and stack[-1][0] >= level:
                stack.pop()
            stack.append((level, match.group(2).strip()))
        else:
            body.append(line)
    flush()
    return sections


def build_chunks(
    pages: list[Page], source_type: str, strategy: ChunkStrategy, size: int, overlap: int
) -> list[Chunk]:
    chunks: list[Chunk] = []
    for page in pages:
        # PDF text has no markdown headings, so it always uses the paragraph strategy.
        if strategy == "heading" and source_type == "markdown":
            chunks += [Chunk(text, page.number, section) for section, text in split_by_headings(page.text, size, overlap)]
        else:
            chunks += [Chunk(text, page.number, "") for text in split_markdown(page.text, size, overlap)]
    return chunks
