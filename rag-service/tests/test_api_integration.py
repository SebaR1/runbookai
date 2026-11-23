"""End-to-end against the real Chroma and Ollama. Skipped if they are not up.

Run only these: uv run pytest -m integration
"""

import json
import uuid
from pathlib import Path

import pytest
from fastapi.testclient import TestClient

from app.main import app, get_store

pytestmark = pytest.mark.integration

SAMPLE = Path(__file__).parent.parent / "samples" / "runbook-pagos.md"


@pytest.fixture(scope="module")
def client():
    with TestClient(app) as c:
        if c.get("/health").status_code != 200:
            pytest.skip("Chroma / Ollama not available")
        yield c


@pytest.fixture(scope="module")
def ingested(client):
    """Ingest the sample into an isolated namespace; clean up afterwards."""
    document_id = str(uuid.uuid4())
    namespace = f"test-{uuid.uuid4().hex[:8]}"
    with SAMPLE.open("rb") as f:
        r = client.post(
            "/ingest",
            files={"file": (SAMPLE.name, f, "text/markdown")},
            data={"documentId": document_id, "namespace": namespace, "title": "Runbook pagos"},
        )
    assert r.status_code == 200, r.text
    yield document_id, namespace, r.json()
    get_store().delete_document(document_id)


def test_ingest_is_idempotent(client, ingested):
    document_id, namespace, first = ingested
    assert first["chunkCount"] > 1
    with SAMPLE.open("rb") as f:
        second = client.post(
            "/ingest",
            files={"file": (SAMPLE.name, f, "text/markdown")},
            data={"documentId": document_id, "namespace": namespace, "title": "Runbook pagos"},
        ).json()
    stored = get_store().collection.get(where={"documentId": document_id})
    assert len(stored["ids"]) == first["chunkCount"] == second["chunkCount"]


def test_ask_json_cites_the_ingested_document(client, ingested):
    document_id, namespace, _ = ingested
    r = client.post(
        "/ask?stream=false",
        json={"question": "¿Qué comando uso para reiniciar payments-api?", "namespace": namespace},
    )
    assert r.status_code == 200, r.text
    body = r.json()
    assert body["sources"] and all(s["documentId"] == document_id for s in body["sources"])
    assert "rollout restart" in body["answer"]
    assert "[" in body["answer"]  # cites sources like [1]


def test_ask_stream_emits_sources_tokens_and_done(client, ingested):
    _, namespace, _ = ingested
    events = []
    with client.stream("POST", "/ask", json={"question": "¿Cuál es el umbral de latencia?", "namespace": namespace}) as r:
        assert r.headers["content-type"].startswith("text/event-stream")
        event = None
        for line in r.iter_lines():
            if line.startswith("event: "):
                event = line.removeprefix("event: ")
            elif line.startswith("data: "):
                events.append((event, json.loads(line.removeprefix("data: "))))
    names = [e for e, _ in events]
    assert names[0] == "sources" and names[-1] == "done" and "token" in names
    answer = "".join(d["text"] for e, d in events if e == "token")
    assert "800" in answer


def test_other_namespace_sees_nothing(client, ingested):
    r = client.post("/ask?stream=false", json={"question": "reinicio de payments-api", "namespace": "does-not-exist"})
    assert r.json()["sources"] == []
