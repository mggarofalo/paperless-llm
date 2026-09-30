"""Create local, immutable review inputs without mutating Paperless."""

from __future__ import annotations

import hashlib
import json
import os
from datetime import datetime, timezone
from pathlib import Path
from tempfile import TemporaryDirectory
from typing import Any

from .paperless import PaperlessClient


class SnapshotError(RuntimeError):
    """A snapshot could not be completed consistently."""


def read_json(path: Path, *, max_bytes: int = 32 * 1024 * 1024) -> Any:
    """Read bounded local JSON; never resolve references from its contents."""
    with path.open("rb") as stream:
        raw = stream.read(max_bytes + 1)
    if len(raw) > max_bytes:
        raise SnapshotError("JSON input exceeds the configured size limit")
    try:
        return json.loads(raw.decode("utf-8-sig"))
    except (ValueError, UnicodeError) as exc:
        raise SnapshotError("Input is not valid UTF-8 JSON") from exc


def save_snapshot(
    client: PaperlessClient,
    destination: Path,
    *,
    document_ids: list[int] | None = None,
    tag: str = "needs review",
    limit: int = 10,
    download_originals: bool = False,
) -> dict[str, Any]:
    """Publish a new snapshot with its completion manifest written last."""
    if isinstance(limit, bool) or not isinstance(limit, int) or not 1 <= limit <= 100:
        raise SnapshotError("limit must be between 1 and 100")
    if document_ids is not None:
        if not document_ids or len(document_ids) > limit:
            raise SnapshotError("Explicit document IDs must contain between 1 and limit entries")
        if any(type(i) is not int or i <= 0 for i in document_ids):
            raise SnapshotError("Document IDs must be positive integers")
        if len(set(document_ids)) != len(document_ids):
            raise SnapshotError("Duplicate document IDs are not allowed")
    destination = destination.absolute()
    if destination.exists() or destination.is_symlink():
        raise SnapshotError("Snapshot destination already exists")
    destination.parent.mkdir(parents=True, exist_ok=True)
    docs = (
        [client.get_document(i) for i in document_ids]
        if document_ids is not None
        else client.list_documents(tag=tag, limit=limit)
    )
    if len(docs) > limit:
        raise SnapshotError("Connector returned more documents than requested")
    ids = [d.get("id") for d in docs]
    if any(type(i) is not int or i <= 0 for i in ids) or len(set(ids)) != len(ids):
        raise SnapshotError("Connector returned invalid or duplicate document IDs")
    if document_ids is not None and ids != document_ids:
        raise SnapshotError("Connector returned a different document than requested")
    taxonomy = client.taxonomy()
    manifest: dict[str, Any] = {
        "format_version": 1,
        "created_at": datetime.now(timezone.utc).isoformat(),
        "profile": client.profile,
        "selection": {"document_ids": document_ids, "tag": tag if document_ids is None else None, "limit": limit},
        "documents": docs,
        "taxonomy": taxonomy,
        "originals": [],
    }
    # The temporary directory is created by us under the destination's parent.
    # It is cleaned on failure, including any partially downloaded sensitive files.
    with TemporaryDirectory(prefix=".ppllm-", dir=destination.parent) as temp:
        stage = Path(temp) / "snapshot"
        stage.mkdir(mode=0o700)
        if download_originals:
            originals = stage / "originals"
            originals.mkdir(mode=0o700)
            for doc in docs:
                ident = doc["id"]
                path = originals / f"{ident}.original"
                client.download_original(ident, path)
                digest = hashlib.sha256()
                with path.open("rb") as stream:
                    for chunk in iter(lambda: stream.read(1024 * 1024), b""):
                        digest.update(chunk)
                if path.stat().st_size == 0:
                    raise SnapshotError("Downloaded original is empty")
                manifest["originals"].append({
                    "document_id": ident,
                    "path": f"originals/{ident}.original",
                    "sha256": digest.hexdigest(),
                    "bytes": path.stat().st_size,
                })
        for doc in docs:
            fresh = client.get_document(doc["id"])
            if type(fresh.get("id")) is not int or fresh["id"] != doc["id"]:
                raise SnapshotError("Connector returned a different document during verification")
            for key in ("modified", "content", "title", "created", "correspondent", "document_type", "tags", "original_file_name", "page_count", "owner", "custom_fields"):
                if fresh.get(key) != doc.get(key):
                    raise SnapshotError("A document changed while collecting the snapshot; retry")
        manifest_path = stage / "snapshot.json"
        with manifest_path.open("x", encoding="utf-8") as stream:
            json.dump(manifest, stream, ensure_ascii=False, indent=2)
            stream.write("\n")
            stream.flush()
            os.fsync(stream.fileno())
        # Reserving the final directory prevents replacement of an existing run.
        destination.mkdir(mode=0o700)
        try:
            for child in stage.iterdir():
                if child.name == "snapshot.json":
                    continue
                child.rename(destination / child.name)
            manifest_path.rename(destination / "snapshot.json")
        except OSError:
            # Leave a visible incomplete directory; do not delete a path another
            # process may have touched. Absence of snapshot.json means incomplete.
            raise SnapshotError("Could not publish snapshot; inspect the destination") from None
    return {"documents": len(docs), "originals": len(manifest["originals"]), "snapshot": str(destination / "snapshot.json")}
