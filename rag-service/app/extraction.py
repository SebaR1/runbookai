"""Text extraction per page. Markdown/text is a single page; PDFs keep real page numbers."""

import io
from dataclasses import dataclass

from pypdf import PdfReader

MARKDOWN_EXTENSIONS = (".md", ".markdown", ".txt")
PDF_EXTENSIONS = (".pdf",)
ALLOWED_EXTENSIONS = MARKDOWN_EXTENSIONS + PDF_EXTENSIONS


class UnsupportedDocument(ValueError):
    pass


@dataclass
class Page:
    number: int
    text: str


def source_type(filename: str) -> str:
    name = filename.lower()
    if name.endswith(PDF_EXTENSIONS):
        return "pdf"
    if name.endswith(MARKDOWN_EXTENSIONS):
        return "markdown"
    raise UnsupportedDocument(f"Unsupported file type: {filename}. Allowed: {ALLOWED_EXTENSIONS}")


def extract_pages(filename: str, raw: bytes) -> list[Page]:
    if source_type(filename) == "pdf":
        try:
            reader = PdfReader(io.BytesIO(raw))
        except Exception as exc:
            raise UnsupportedDocument(f"Invalid PDF: {exc}") from exc
        pages = [Page(i, (p.extract_text() or "").strip()) for i, p in enumerate(reader.pages, start=1)]
        return [p for p in pages if p.text]
    try:
        return [Page(1, raw.decode("utf-8-sig"))]
    except UnicodeDecodeError as exc:
        raise UnsupportedDocument("Text files must be UTF-8") from exc
