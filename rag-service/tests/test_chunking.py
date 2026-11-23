import pytest

from app.chunking import chunk_id, split_markdown


def test_short_text_is_a_single_chunk():
    assert split_markdown("# Title\n\nHello world.", size=1000, overlap=100) == ["# Title\n\nHello world."]


def test_empty_text_has_no_chunks():
    assert split_markdown("  \n\n  ", size=1000, overlap=100) == []


def test_chunks_respect_max_size():
    text = "\n\n".join(f"Paragraph {i} " + "word " * 40 for i in range(30))
    chunks = split_markdown(text, size=500, overlap=80)
    assert len(chunks) > 1
    assert all(len(c) <= 500 for c in chunks)


def test_long_paragraph_is_split_at_word_boundaries():
    text = "palabra " * 300
    chunks = split_markdown(text, size=200, overlap=0)
    assert all(len(c) <= 200 for c in chunks)
    assert all(not c.startswith("alabra") for c in chunks)


def test_consecutive_chunks_overlap():
    text = "\n\n".join(f"Sentence number {i} with some filler text." for i in range(40))
    chunks = split_markdown(text, size=300, overlap=60)
    first_tail_words = chunks[0].split()[-3:]
    assert " ".join(first_tail_words) in chunks[1]


def test_overlap_must_be_smaller_than_size():
    with pytest.raises(ValueError):
        split_markdown("text", size=100, overlap=100)


def test_chunk_id_is_deterministic_uuid():
    a = chunk_id("doc-1", 0)
    assert a == chunk_id("doc-1", 0)
    assert a != chunk_id("doc-1", 1)
    assert a != chunk_id("doc-2", 0)
    assert len(a) == 36
