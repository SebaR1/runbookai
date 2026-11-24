"""The System API builds its client from a versioned copy of this contract."""

from scripts.export_openapi import SNAPSHOT, render


def test_openapi_snapshot_is_up_to_date():
    assert SNAPSHOT.exists(), "Run: uv run python -m scripts.export_openapi"
    assert SNAPSHOT.read_text(encoding="utf-8") == render(), (
        "The RAG contract changed. Run: uv run python -m scripts.export_openapi"
    )
