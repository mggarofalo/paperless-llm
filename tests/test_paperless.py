import json
import subprocess
from pathlib import Path
from unittest.mock import patch

import pytest

from paperless_llm.paperless import PaperlessClient, PaperlessError


def doc(number):
    return {"id": number, "title": "Synthetic document", "content": "private synthetic OCR", "tags": [2]}


def page(records, next=None):
    return {"count": len(records), "results": records, "next": next}


def response(value):
    return subprocess.CompletedProcess([], 0, json.dumps(value), "")


def test_discovery_resolves_name_and_ignores_next_url():
    replies = [page([{"id": 2, "name": "Needs Review"}]),
               page([doc(i) for i in range(1, 101)], "https://attacker.invalid/secret"),
               page([doc(i) for i in range(101, 201)])]
    with patch("paperless_llm.paperless.subprocess.run", side_effect=[response(x) for x in replies]) as run:
        docs = PaperlessClient().list_documents(limit=150)
    assert len(docs) == 150
    assert docs[-1]["id"] == 150
    assert "tags__id__all=2" in run.call_args_list[1].args[0]
    assert run.call_args_list[2].args[0][-5:] == ["100", "--ordering", "id", "--filter", "tags__id__all=2"]
    for call in run.call_args_list:
        assert not any("attacker" in arg for arg in call.args[0])
        assert call.kwargs["shell"] is False
        assert call.kwargs["timeout"] == 60


@pytest.mark.parametrize("value", [[], {}, {"id": True}, {"id": 2, "title": "x", "content": [], "tags": []}])
def test_get_rejects_unexpected_json(value):
    with patch("paperless_llm.paperless.subprocess.run", return_value=response(value)):
        with pytest.raises(PaperlessError):
            PaperlessClient().get_document(1)


def test_get_rejects_wrong_identity():
    with patch("paperless_llm.paperless.subprocess.run", return_value=response(doc(2))):
        with pytest.raises(PaperlessError, match="different document"):
            PaperlessClient().get_document(1)


@pytest.mark.parametrize("failure", [
    subprocess.TimeoutExpired("private command", 1, output="secret document"),
    OSError("secret key path"),
])
def test_subprocess_failures_sanitized(failure):
    with patch("paperless_llm.paperless.subprocess.run", side_effect=failure):
        with pytest.raises(PaperlessError) as raised:
            PaperlessClient().get_document(1)
    assert "secret" not in str(raised.value)
    assert raised.value.__suppress_context__


def test_cli_error_and_malformed_json_do_not_leak_content():
    for result in [subprocess.CompletedProcess([], 1, "private OCR", "secret token"),
                   subprocess.CompletedProcess([], 0, "private invalid JSON", "")]:
        with patch("paperless_llm.paperless.subprocess.run", return_value=result):
            with pytest.raises(PaperlessError) as raised:
                PaperlessClient().get_document(1)
        assert "private" not in str(raised.value)
        assert "secret" not in str(raised.value)


def test_missing_and_ambiguous_tag_fail_closed():
    for tags in [[], [{"id": 1, "name": "needs review"}, {"id": 2, "name": "Needs Review"}]]:
        with patch("paperless_llm.paperless.subprocess.run", return_value=response(page(tags))) as run:
            with pytest.raises(PaperlessError, match="ambiguous"):
                PaperlessClient().list_documents()
        assert run.call_count == 1


def test_pagination_repetition_and_empty_continuation_fail():
    for pages in [[page([{"id": 2, "name": "x"}], "next"), page([{"id": 2, "name": "x"}])],
                  [page([], "next")]]:
        with patch("paperless_llm.paperless.subprocess.run", side_effect=[response(p) for p in pages]):
            with pytest.raises(PaperlessError):
                PaperlessClient().taxonomy()


def test_pagination_is_bounded():
    client = PaperlessClient()
    client.MAX_PAGES = 2
    with patch.object(client, "_run", side_effect=[page([{"id": n, "name": "x"}], "next") for n in [1, 2]]):
        with pytest.raises(PaperlessError, match="safety limit"):
            client.taxonomy()


@pytest.mark.parametrize("value", [True, 0, -1, "1;whoami", 1.5])
def test_invalid_ids_never_execute(value):
    with patch("paperless_llm.paperless.subprocess.run") as run:
        with pytest.raises(ValueError):
            PaperlessClient().get_document(value)
    run.assert_not_called()


def test_download_preserves_existing_and_stages(tmp_path):
    destination = tmp_path / "scan.pdf"
    def download(*args):
        assert args[:4] == ("document", "download", "3", "--original")
        Path(args[-1]).write_bytes(b"synthetic original")
        return {"bytes": 18}
    client = PaperlessClient()
    with patch.object(client, "_run", side_effect=download) as run:
        assert client.download_original(3, destination) == destination
        with pytest.raises(FileExistsError):
            client.download_original(3, destination)
        assert run.call_count == 1
    assert destination.read_bytes() == b"synthetic original"
    assert list(tmp_path.iterdir()) == [destination]


def test_failed_download_cleans_partial(tmp_path):
    def download(*args):
        Path(args[-1]).write_bytes(b"partial")
        raise PaperlessError("timed out")
    with patch.object(PaperlessClient, "_run", side_effect=download):
        with pytest.raises(PaperlessError):
            PaperlessClient().download_original(3, tmp_path / "scan.pdf")
    assert list(tmp_path.iterdir()) == []


def test_snapshot_rejects_duplicate_ids_before_reading():
    with patch.object(PaperlessClient, "_run") as run:
        with pytest.raises(ValueError):
            PaperlessClient().collect_snapshot([1, 1])
    run.assert_not_called()


def test_taxonomy_and_explicit_snapshot():
    client = PaperlessClient()
    with patch.object(client, "_run", side_effect=[doc(1), page([]), page([]), page([])]) as run:
        snapshot = client.collect_snapshot([1])
    assert snapshot == {"documents": [doc(1)], "taxonomy": {"tags": [], "correspondents": [], "document_types": []}}
    assert all(call.args[0] in {"document", "tag", "correspondent", "document-type"} for call in run.call_args_list)
