"""Read-only access through the user's authenticated Paperless CLI.

Responses contain private document data. Do not log them or exception causes.
"""
from __future__ import annotations

import json
import math
import os
from pathlib import Path
import shutil
import subprocess
import tempfile
from typing import Any


class PaperlessError(RuntimeError):
    """A sanitized connector failure, safe to display without document content."""


def _positive_int(value: Any, name: str) -> int:
    if type(value) is not int or value < 1:
        raise ValueError(f"{name} must be a positive integer")
    return value


class PaperlessClient:
    """No credentials are read and no Paperless mutation commands are exposed."""

    PAGE_SIZE = 100
    MAX_PAGES = 50
    MAX_DOCUMENTS = 1000

    def __init__(self, profile: str = "default", timeout: float = 60):
        if not isinstance(profile, str) or not profile.strip() or "\x00" in profile:
            raise ValueError("profile must be a nonempty name")
        if isinstance(timeout, bool) or not isinstance(timeout, (float, int)) or not math.isfinite(timeout) or timeout <= 0:
            raise ValueError("timeout must be finite and positive")
        self.profile = profile
        self.timeout = timeout

    def _run(self, *args: str) -> Any:
        command = ["paperless", "--profile", self.profile, "--output", "json",
                   "--timeout", f"{self.timeout:g}s", *args]
        options: dict[str, Any] = {}
        if os.name == "nt":
            options["creationflags"] = subprocess.CREATE_NO_WINDOW
        try:
            result = subprocess.run(command, capture_output=True, text=True,
                                    encoding="utf-8", timeout=self.timeout,
                                    check=False, shell=False, **options)
        except subprocess.TimeoutExpired:
            raise PaperlessError("Paperless CLI timed out") from None
        except (OSError, UnicodeError):
            raise PaperlessError("Paperless CLI could not be executed") from None
        if result.returncode:
            raise PaperlessError("Paperless CLI failed; check authentication and connectivity")
        try:
            return json.loads(result.stdout)
        except (ValueError, TypeError):
            raise PaperlessError("Paperless CLI returned invalid JSON") from None

    @staticmethod
    def _record(value: Any, resource: str) -> dict:
        if not isinstance(value, dict) or type(value.get("id")) is not int or value["id"] <= 0:
            raise PaperlessError("Paperless returned an invalid resource")
        if resource != "document" and not isinstance(value.get("name"), str):
            raise PaperlessError("Paperless returned an invalid taxonomy name")
        if resource == "document":
            if not isinstance(value.get("title"), str) or not isinstance(value.get("content"), str):
                raise PaperlessError("Paperless returned invalid document text")
            if not isinstance(value.get("tags"), list) or any(type(t) is not int or t <= 0 for t in value["tags"]):
                raise PaperlessError("Paperless returned invalid document tags")
        return value

    def _list(self, resource: str, limit: int | None = None, filters: tuple[str, ...] = ()) -> list[dict]:
        records: list[dict] = []
        seen: set[int] = set()
        for page in range(1, self.MAX_PAGES + 1):
            page_size = min(self.PAGE_SIZE, limit) if limit else self.PAGE_SIZE
            args = [resource, "list", "--page", str(page), "--page-size", str(page_size), "--ordering", "id"]
            for value in filters:
                args.extend(["--filter", value])
            data = self._run(*args)
            if not isinstance(data, dict) or not isinstance(data.get("results"), list):
                raise PaperlessError("Paperless returned an invalid page")
            count = data.get("count")
            if type(count) is not int or count < 0 or "next" not in data:
                raise PaperlessError("Paperless returned invalid pagination metadata")
            if data["next"] is not None and (not isinstance(data["next"], str) or not data["next"]):
                raise PaperlessError("Paperless returned invalid pagination metadata")
            if len(data["results"]) > page_size:
                raise PaperlessError("Paperless exceeded the requested page size")
            for raw in data["results"]:
                record = self._record(raw, resource)
                if record["id"] in seen:
                    raise PaperlessError("Paperless pagination repeated a resource")
                seen.add(record["id"])
                records.append(record)
            if limit is not None and len(records) >= limit:
                return records[:limit]
            if data["next"] is None:
                return records
            if not data["results"]:
                raise PaperlessError("Paperless pagination made no progress")
            # Never follow a URL provided by a response. Request the next numbered page.
        raise PaperlessError("Paperless exceeded the pagination safety limit")

    def list_documents(self, tag: str = "needs review", limit: int = 10) -> list[dict]:
        _positive_int(limit, "limit")
        if limit > self.MAX_DOCUMENTS:
            raise ValueError(f"limit must not exceed {self.MAX_DOCUMENTS}")
        if not isinstance(tag, str) or not tag.strip():
            raise ValueError("tag must be a nonempty name")
        matches = [t for t in self._list("tag") if t["name"].casefold() == tag.casefold()]
        if len(matches) != 1:
            raise PaperlessError("Review tag was not found or its name is ambiguous")
        return self._list("document", limit, (f"tags__id__all={matches[0]['id']}",))

    def get_document(self, document_id: int) -> dict:
        _positive_int(document_id, "document_id")
        record = self._record(self._run("document", "get", str(document_id)), "document")
        if record["id"] != document_id:
            raise PaperlessError("Paperless returned a different document")
        return record

    def taxonomy(self) -> dict[str, list[dict]]:
        return {"tags": self._list("tag"), "correspondents": self._list("correspondent"),
                "document_types": self._list("document-type")}

    def collect_snapshot(self, document_ids: list[int] | None = None, *, tag: str = "needs review", limit: int = 10) -> dict:
        if document_ids is not None:
            if not isinstance(document_ids, list) or not document_ids or len(document_ids) > self.MAX_DOCUMENTS:
                raise ValueError("document_ids must be a nonempty bounded list")
            for document_id in document_ids:
                _positive_int(document_id, "document_id")
            if len(set(document_ids)) != len(document_ids):
                raise ValueError("document_ids must be unique")
            documents = [self.get_document(document_id) for document_id in document_ids]
        else:
            documents = self.list_documents(tag, limit)
        return {"documents": documents, "taxonomy": self.taxonomy()}

    def download_original(self, document_id: int, dest: str | Path) -> Path:
        _positive_int(document_id, "document_id")
        destination = Path(dest).absolute()
        if destination.exists() or destination.is_symlink():
            raise FileExistsError("Download destination already exists")
        # Stage separately so a killed CLI cannot leave a partial final document.
        # Neither filenames nor download URLs from document metadata are used.
        with tempfile.TemporaryDirectory(prefix="ppllm-", dir=destination.parent) as folder:
            staged = Path(folder) / "original"
            self._run("document", "download", str(document_id), "--original", "--file", str(staged))
            if not staged.is_file() or staged.stat().st_size == 0:
                raise PaperlessError("Paperless did not produce a nonempty original")
            created = False
            try:
                fd = os.open(destination, os.O_WRONLY | os.O_CREAT | os.O_EXCL, 0o600)
                created = True
                with os.fdopen(fd, "wb") as target, staged.open("rb") as source:
                    shutil.copyfileobj(source, target)
            except BaseException:
                if created:
                    destination.unlink(missing_ok=True)
                raise
        return destination
