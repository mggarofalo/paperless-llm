import json

import pytest

from paperless_llm import cli


@pytest.fixture
def local_validation(tmp_path, monkeypatch):
    def no_remote(*args, **kwargs):
        pytest.fail("Local validation must never construct a remote connector")

    monkeypatch.setattr(cli, "PaperlessClient", no_remote)
    snapshot = {"format_version": 1, "documents": [{"id": 1, "tags": [2], "content": "original"}],
                "taxonomy": {"tags": [{"id": 2, "name": "needs review"}],
                             "correspondents": [], "document_types": []},
                "originals": [{"document_id": 1, "path": "https://untrusted.invalid/file"}]}
    proposal = {"title": "Synthetic title", "date": None, "correspondent": None,
                "document_type": None, "add_tags": [], "ocr_text": None,
                "evidence": ["Synthetic fixture title"], "uncertainty": []}
    snapshot_path, proposal_path = tmp_path / "snapshot.json", tmp_path / "proposal.json"
    snapshot_path.write_text(json.dumps(snapshot))
    proposal_path.write_text(json.dumps(proposal))
    args = ["validate", "--snapshot", str(snapshot_path), "--document-id", "1", "--proposal", str(proposal_path)]
    return args, snapshot_path, proposal_path, snapshot, proposal


def test_validate_is_local_read_only(local_validation, capsys):
    args, snapshot_path, proposal_path, _, _ = local_validation
    before = (snapshot_path.read_bytes(), proposal_path.read_bytes())
    assert cli.main(args) == 0
    assert json.loads(capsys.readouterr().out) == {"valid": True, "document_id": 1, "applied": False}
    assert (snapshot_path.read_bytes(), proposal_path.read_bytes()) == before


@pytest.mark.parametrize("mutation", [
    lambda snapshot: snapshot.update(format_version=True),
    lambda snapshot: snapshot.update(taxonomy=None),
    lambda snapshot: snapshot.pop("taxonomy"),
    lambda snapshot: snapshot.update(documents="bad"),
    lambda snapshot: snapshot["documents"].append(snapshot["documents"][0].copy()),
    lambda snapshot: snapshot["documents"][0].update(id=True),
])
def test_malformed_snapshot_reports_error_without_traceback(local_validation, capsys, mutation):
    args, snapshot_path, _, snapshot, _ = local_validation
    mutation(snapshot)
    snapshot_path.write_text(json.dumps(snapshot))
    assert cli.main(args) == 1
    output = capsys.readouterr()
    assert output.out == ""
    assert output.err.startswith("ppllm:")


def test_download_metadata_does_not_authorize_ocr(local_validation, capsys):
    args, _, proposal_path, _, proposal = local_validation
    proposal["ocr_text"] = "A proposed transcription"
    proposal_path.write_text(json.dumps(proposal))
    assert cli.main(args) == 1
    assert "require supplied document scans" in capsys.readouterr().err


@pytest.mark.parametrize("raw", ["null", "[]", "{bad", '{"owner": 1}'])
def test_malformed_proposal_reports_error(local_validation, capsys, raw):
    args, _, proposal_path, _, _ = local_validation
    proposal_path.write_text(raw)
    assert cli.main(args) == 1
    assert capsys.readouterr().err.startswith("ppllm:")


def test_missing_local_file_reports_sanitized_error(local_validation, capsys):
    args, _, proposal_path, _, _ = local_validation
    proposal_path.unlink()
    assert cli.main(args) == 1
    output = capsys.readouterr()
    assert str(proposal_path) not in output.err
    assert "local file operation failed" in output.err


def test_discover_bounded_and_omits_ocr_and_permissions(monkeypatch, capsys):
    calls = []

    class FakeClient:
        def __init__(self, *, profile):
            calls.append(profile)

        def list_documents(self, *, tag, limit):
            calls.append((tag, limit))
            return [{"id": 1, "title": "Synthetic", "created": "2026-01-01", "content": "PRIVATE OCR", "owner": 99}]

    monkeypatch.setattr(cli, "PaperlessClient", FakeClient)
    assert cli.main(["--profile", "test", "discover", "--limit", "3"]) == 0
    output = capsys.readouterr().out
    assert calls == ["test", ("needs review", 3)]
    assert "PRIVATE OCR" not in output
    assert "owner" not in output
    assert json.loads(output)["count"] == 1


def test_cli_rejects_limit_before_remote_connection(monkeypatch, capsys):
    monkeypatch.setattr(cli, "PaperlessClient", lambda **kwargs: pytest.fail("Unexpected connection"))
    assert cli.main(["discover", "--limit", "101"]) == 1
    assert "limit" in capsys.readouterr().err


@pytest.mark.parametrize("args", [["--profile", " ", "discover"], ["discover", "--tag", " "]])
def test_blank_profile_or_tag_rejected_without_connection(monkeypatch, capsys, args):
    monkeypatch.setattr(cli, "PaperlessClient", lambda **kwargs: pytest.fail("Unexpected connection"))
    assert cli.main(args) == 1
    assert "nonempty name" in capsys.readouterr().err
