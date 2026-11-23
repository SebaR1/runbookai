"""Generate the PDF documents of the eval corpus from eval/corpus-src/*.md.

Each "##" section starts on a new page so the PDFs have several pages and the
`page` metadata can be checked. Usage: uv run python -m scripts.make_pdfs
"""

import re
from datetime import datetime, timezone
from pathlib import Path

from fpdf import FPDF

ROOT = Path(__file__).resolve().parent.parent
SRC = ROOT / "eval" / "corpus-src"
OUT = ROOT / "eval" / "corpus"


def plain(text: str) -> str:
    return re.sub(r"[*`]", "", text)


def render(md_path: Path, pdf_path: Path) -> int:
    pdf = FPDF(format="A4")
    pdf.set_margins(20, 20, 20)
    pdf.set_creator("RunbookAI eval corpus")
    pdf.set_creation_date(datetime(2026, 1, 1, tzinfo=timezone.utc))  # deterministic output
    first_section = True
    for line in md_path.read_text(encoding="utf-8").splitlines():
        line = line.rstrip()
        if line.startswith("# "):
            pdf.add_page()
            pdf.set_font("Helvetica", "B", 18)
            pdf.multi_cell(0, 10, plain(line[2:]), new_x="LMARGIN", new_y="NEXT")
            pdf.ln(4)
        elif line.startswith("## "):
            if not first_section:
                pdf.add_page()
            first_section = False
            pdf.set_font("Helvetica", "B", 14)
            pdf.multi_cell(0, 8, plain(line[3:]), new_x="LMARGIN", new_y="NEXT")
            pdf.ln(2)
        elif line:
            pdf.set_font("Helvetica", "", 11)
            pdf.multi_cell(0, 6, plain(line), new_x="LMARGIN", new_y="NEXT")
            pdf.ln(2)
    pdf.output(str(pdf_path))
    return pdf.pages_count


def main() -> None:
    for md in sorted(SRC.glob("*.md")):
        target = OUT / f"{md.stem}.pdf"
        pages = render(md, target)
        print(f"{target.relative_to(ROOT)}: {pages} pages")


if __name__ == "__main__":
    main()
