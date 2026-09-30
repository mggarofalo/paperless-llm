import copy
import hashlib
import json
from pathlib import Path

import pytest

from paperless_llm.snapshots import SnapshotError, read_json, save_snapshot


class ReadOnlyClient:
    profile = "synthetic"

    def __init__(self):
        self.document = {"id": 7, "title": "Synthetic", "content": "Private fixture", "tags": [2],
                         "modified": "2026-01-01", "created": "2025-12-31"}
        self.calls = []
        self.change = None
        self.download_failure = False

    def list_documents(self, *, tag, limit):
        self.calls.append(("list", tag, limit))
        return [copy.deepcopy(self.document)]

    def get_document(self, ident):
        self.calls.append(("get", ident))
        doc = copy.deepcopy(self.document)
        if self.change:
            doc.update(self.change)
        return doc

    def taxonomy(self):
        self.calls.append(("taxonomy",))
        return {"tags": [{"id": 2, "name": "needs review"}], "correspondents": [], "document_types": []}

    def download_original(self, ident, path):
        self.calls.append(("download", ident))
        path.write_bytes(b"synthetic original")
        if self.download_failure:
            raise RuntimeError("Download failed")


def test_snapshot_completes_with_hashes_and_only_reads(tmp_path):
    client = ReadOnlyClient()
    destination = tmp_path / "run"
    result = save_snapshot(client, destination, limit=3, download_originals=True)
    manifest = read_json(destination / "snapshot.json")
    assert result["documents"] == result["originals"] == 1
    assert manifest["originals"] == [{"document_id": 7, "path": "originals/7.original",
                                      "sha256": hashlib.sha256(b"synthetic original").hexdigest(), "bytes": 18}]
    assert (destination / "originals/7.original").read_bytes() == b"synthetic original"
    assert client.calls == [("list", "needs review", 3), ("taxonomy",), ("download", 7), ("get", 7)]
    assert list(tmp_path.iterdir()) == [destination]


@pytest.mark.parametrize("kwargs", [{"limit": 0}, {"limit": 101}, {"limit": True},
                                    {"document_ids": []}, {"document_ids": [True]},
                                    {"document_ids": [7, 7]}, {"document_ids": [7, 8], "limit": 1}])
def test_invalid_selection_rejected_before_connector_calls(tmp_path, kwargs):
    client = ReadOnlyClient()
    with pytest.raises(SnapshotError):
        save_snapshot(client, tmp_path / "run", **kwargs)
    assert client.calls == []
    assert not list(tmp_path.iterdir())


@pytest.mark.parametrize("change", [{"title": "Changed"}, {"content": "Changed"}, {"tags": []},
                                    {"modified": "2026-02-01"}, {"id": 8}])
def test_concurrent_change_rejects_and_cleans_staged_private_data(tmp_path, change):
    client = ReadOnlyClient()
    client.change = change
    with pytest.raises(SnapshotError):
        save_snapshot(client, tmp_path / "run", download_originals=True)
    assert not list(tmp_path.iterdir())


def test_partial_download_cleanup(tmp_path):
    client = ReadOnlyClient()
    client.download_failure = True
    with pytest.raises(RuntimeError):
        save_snapshot(client, tmp_path / "run", download_originals=True)
    assert not list(tmp_path.iterdir())


def test_snapshot_never_overwrites(tmp_path):
    client = ReadOnlyClient()
    destination = tmp_path / "run"
    destination.mkdir()
    sentinel = destination / "snapshot.json"
    sentinel.write_text("keep")
    with pytest.raises(SnapshotError, match="already exists"):
        save_snapshot(client, destination)
    assert sentinel.read_text() == "keep"
    assert client.calls == []


def test_empty_queue_creates_complete_snapshot(tmp_path):
    client = ReadOnlyClient()
    client.list_documents = lambda **kwargs: []
    result = save_snapshot(client, tmp_path / "empty")
    assert result["documents"] == 0
    assert read_json(tmp_path / "empty/snapshot.json")["documents"] == []


def test_connector_cannot_overrun_requested_limit(tmp_path):
    client = ReadOnlyClient()
    client.list_documents = lambda **kwargs: [dict(client.document, id=i) for i in (7, 8)]
    with pytest.raises(SnapshotError, match="more documents"):
        save_snapshot(client, tmp_path / "run", limit=1)
    assert not (tmp_path / "run").exists()


def test_manifest_is_published_last(tmp_path, monkeypatch):
    client = ReadOnlyClient()
    destination = tmp_path / "run"
    rename = Path.rename
    seen = []

    def observe(path, target):
        if path.name in {"originals", "snapshot.json"}:
            seen.append(path.name)
            assert not (destination / "snapshot.json").exists()
        return rename(path, target)

    monkeypatch.setattr(Path, "rename", observe)
    save_snapshot(client, destination, download_originals=True)
    assert seen == ["originals", "snapshot.json"]


@pytest.mark.parametrize("content", [b"not-json", b"\xff", b'{"truncated":'])
def test_read_json_rejects_malformed_input(tmp_path, content):
    path = tmp_path / "bad.json"
    path.write_bytes(content)
    with pytest.raises(SnapshotError, match="valid UTF-8 JSON"):
        read_json(path)


def test_read_json_bounds_size_and_accepts_bom(tmp_path):
    path = tmp_path / "input.json"
    path.write_bytes(b'\xef\xbb\xbf{"ok":true}')
    assert read_json(path) == {"ok": True}
    with pytest.raises(SnapshotError, match="size limit"):
        read_json(path, max_bytes=2)
