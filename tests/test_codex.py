import json
import subprocess

import pytest

from paperless_llm.codex import CodexClient, CodexError, _validate_probe


def transcript(answer='{"total":"12.34"}', extra=()):
    return "\n".join(json.dumps(e) for e in [
        {"type": "thread.started"}, {"type": "turn.started"}, *extra,
        {"type": "item.completed", "item": {"type": "agent_message", "text": answer}},
        {"type": "turn.completed"},
    ])


def test_synthetic_probe_uses_no_external_input_or_api_key(monkeypatch):
    monkeypatch.setenv("OPENAI_API_KEY", "secret-never-forward")
    monkeypatch.setenv("CODEX_API_KEY", "secret-never-forward")
    calls = []

    def run(args, **kwargs):
        calls.append(args)
        assert "OPENAI_API_KEY" not in kwargs["env"]
        assert "CODEX_API_KEY" not in kwargs["env"]
        assert kwargs["shell"] is False
        if args[1:] == ["login", "status"]:
            return subprocess.CompletedProcess(args, 0, "", "Logged in using ChatGPT\n")
        assert "--ignore-user-config" in args and "--strict-config" in args
        assert "--ephemeral" in args and "read-only" in args
        assert args[args.index("--image") + 2] == "--"
        assert (kwargs["cwd"] / "synthetic.png").read_bytes().startswith(b"\x89PNG\r\n\x1a\n")
        return subprocess.CompletedProcess(args, 0, transcript(), "")

    monkeypatch.setattr(subprocess, "run", run)
    result = CodexClient().probe("gpt-6-luna")
    assert result["image_ocr_verified"]
    assert result["document_inference_enabled"] is False
    assert len(calls) == 2


def test_api_key_login_does_not_run_inference_or_expose_fragment(monkeypatch):
    calls = []

    def run(args, **kwargs):
        calls.append(args)
        return subprocess.CompletedProcess(args, 0, "", "Logged in using an API key: sk-secret")

    monkeypatch.setattr(subprocess, "run", run)
    with pytest.raises(CodexError, match="ChatGPT login") as error:
        CodexClient().probe("gpt-6-sol")
    assert "sk-secret" not in str(error.value)
    assert len(calls) == 1


@pytest.mark.parametrize("output", [
    "not json", transcript('{"total":"1234"}'),
    transcript().rsplit("\n", 1)[0],
    transcript(extra=[{"type": "item.completed", "item": {"type": "command_execution"}}]),
    transcript(extra=[{"type": "error", "message": "secret"}]),
    transcript('{"total":"12.34","extra":true}'),
    "[]",
])
def test_partial_wrong_or_tool_using_probe_is_rejected(output):
    with pytest.raises(CodexError):
        _validate_probe(output, "gpt-6-luna")


def test_timeout_is_bounded_and_redacted(monkeypatch):
    def run(*args, **kwargs):
        raise subprocess.TimeoutExpired("secret-command", 1, output="secret-output")

    monkeypatch.setattr(subprocess, "run", run)
    with pytest.raises(CodexError, match="timed out") as error:
        CodexClient(timeout=1).status()
    assert "secret" not in str(error.value)


def test_explicit_known_model_required_before_any_process(monkeypatch):
    def run(*args, **kwargs):
        pytest.fail("must reject before spawning")

    monkeypatch.setattr(subprocess, "run", run)
    with pytest.raises(ValueError):
        CodexClient().probe("default")


@pytest.mark.parametrize("timeout", [True, False, 0, -1, float("nan"), float("inf"), "10"])
def test_timeout_must_be_finite_positive_number(timeout):
    with pytest.raises(ValueError, match="finite positive"):
        CodexClient(timeout=timeout)
