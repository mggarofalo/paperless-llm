"""Bounded synthetic subscription probe, deliberately not a document backend.

Codex is a coding agent. Its CLI has not demonstrated a complete tool-denial
boundary, so this module never accepts document text, user prompts, or images.
"""
from __future__ import annotations

import json
import math
import os
from pathlib import Path
import struct
import subprocess
import tempfile
import zlib


class CodexError(RuntimeError):
    """Safe diagnostic without raw child-process output or credentials."""


MODELS = ("gpt-6-luna", "gpt-6-sol")
_DISABLED = (
    "shell_tool", "plugins", "apps", "hooks", "browser_use", "computer_use",
    "image_generation", "multi_agent", "view_image", "in_app_browser",
)
_PROMPT = (
    "This is a synthetic image OCR connectivity check. Do not use tools. "
    "Return the total printed in the attached image as a decimal string."
)


def _synthetic_png() -> bytes:
    """Render the fixed text TOTAL 12.34 with a tiny bundled bitmap font."""
    glyphs = {
        "T": [31, 4, 4, 4, 4, 4, 4], "O": [14, 17, 17, 17, 17, 17, 14],
        "A": [14, 17, 17, 31, 17, 17, 17], "L": [16, 16, 16, 16, 16, 16, 31],
        " ": [0] * 7, "1": [4, 12, 4, 4, 4, 4, 14],
        "2": [14, 17, 1, 2, 4, 8, 31], "3": [30, 1, 1, 14, 1, 1, 30],
        "4": [2, 6, 10, 18, 31, 2, 2], ".": [0, 0, 0, 0, 0, 6, 6],
    }
    text, scale, margin = "TOTAL 12.34", 8, 24
    width, height = len(text) * 6 * scale + margin * 2, 7 * scale + margin * 2
    rows = bytearray()
    for y in range(height):
        rows.append(0)  # PNG filter: none.
        for x in range(width):
            gx, gy = x - margin, y - margin
            dark = False
            if gx >= 0 and 0 <= gy < 7 * scale:
                index, column = divmod(gx // scale, 6)
                if index < len(text) and column < 5:
                    dark = bool(glyphs[text[index]][gy // scale] & (1 << (4 - column)))
            rows.append(0 if dark else 255)

    def chunk(kind: bytes, data: bytes) -> bytes:
        return struct.pack(">I", len(data)) + kind + data + struct.pack(">I", zlib.crc32(kind + data))

    return (b"\x89PNG\r\n\x1a\n"
            + chunk(b"IHDR", struct.pack(">IIBBBBB", width, height, 8, 0, 0, 0, 0))
            + chunk(b"IDAT", zlib.compress(rows)) + chunk(b"IEND", b""))


class CodexClient:
    def __init__(self, executable: str = "codex", timeout: float = 120):
        if (isinstance(timeout, bool) or not isinstance(timeout, (int, float))
                or not math.isfinite(timeout) or timeout <= 0):
            raise ValueError("timeout must be a finite positive number")
        self.executable = executable
        self.timeout = timeout

    def _run(self, args: list[str], cwd: Path | None = None) -> subprocess.CompletedProcess:
        # Never let an inherited API credential select separately billed access.
        env = {k: v for k, v in os.environ.items() if k.upper() not in {
            "OPENAI_API_KEY", "CODEX_API_KEY", "OPENAI_BASE_URL", "OPENAI_API_BASE",
        }}
        try:
            return subprocess.run(
                [self.executable, *args], cwd=cwd, env=env, capture_output=True,
                text=True, encoding="utf-8", errors="replace", timeout=self.timeout,
                shell=False, creationflags=getattr(subprocess, "CREATE_NO_WINDOW", 0),
            )
        except subprocess.TimeoutExpired:
            raise CodexError("Codex timed out; no automatic retry was attempted.") from None
        except OSError:
            raise CodexError("Codex could not be started. Check its installation.") from None

    def status(self) -> dict:
        result = self._run(["login", "status"])
        # Discard raw status output: API-key mode may include a key fragment.
        authenticated = result.returncode == 0 and (
            (result.stdout + result.stderr).strip() == "Logged in using ChatGPT"
        )
        return {"chatgpt_authenticated": authenticated, "document_inference_enabled": False}

    def probe(self, model: str) -> dict:
        """Spend one small subscription request on a generated synthetic image."""
        if model not in MODELS:
            raise ValueError("Choose an explicit supported probe model: " + ", ".join(MODELS))
        if not self.status()["chatgpt_authenticated"]:
            raise CodexError("ChatGPT login is required; API-key fallback is disabled.")
        with tempfile.TemporaryDirectory(prefix="ppllm-probe-") as directory:
            root = Path(directory)
            image, schema = root / "synthetic.png", root / "schema.json"
            image.write_bytes(_synthetic_png())
            schema.write_text(json.dumps({
                "type": "object", "properties": {"total": {"type": "string"}},
                "required": ["total"], "additionalProperties": False,
            }), encoding="utf-8")
            args = [
                "exec", "--ignore-user-config", "--strict-config", "--ephemeral",
                "--skip-git-repo-check", "-s", "read-only", "-c", "project_doc_max_bytes=0",
                "-c", 'web_search="disabled"', "-c", 'model_provider="openai"',
            ]
            for feature in _DISABLED:
                args.extend(["--disable", feature])
            args.extend(["-m", model, "--json", "--output-schema", str(schema),
                         "--image", str(image), "--", _PROMPT])
            result = self._run(args, root)
        if result.returncode != 0:
            raise CodexError("Synthetic Codex probe failed; inspect login, model access, and plan limits.")
        return _validate_probe(result.stdout, model)


def _validate_probe(output: str, model: str) -> dict:
    try:
        events = [json.loads(line) for line in output.splitlines() if line.strip()]
        if not all(isinstance(event, dict) for event in events):
            raise ValueError
        allowed = {"thread.started", "turn.started", "turn.completed", "item.started", "item.completed"}
        if any(event.get("type") not in allowed for event in events):
            raise ValueError
        messages = []
        for event in events:
            if event.get("type", "").startswith("item."):
                item = event["item"]
                if item.get("type") not in {"agent_message", "reasoning"}:
                    raise ValueError  # Never label a probe involving tool execution successful.
                if event["type"] == "item.completed" and item["type"] == "agent_message":
                    messages.append(item["text"])
        if not events or events[-1].get("type") != "turn.completed" or len(messages) != 1:
            raise ValueError
        if json.loads(messages[0]) != {"total": "12.34"}:
            raise ValueError
    except (KeyError, TypeError, ValueError, AttributeError):
        raise CodexError("Probe did not complete with the expected structured OCR result and no tool calls.") from None
    return {"model": model, "chatgpt_authenticated": True, "synthetic_only": True,
            "image_ocr_verified": True, "structured_output_verified": True,
            "observed_total": "12.34", "document_inference_enabled": False}
