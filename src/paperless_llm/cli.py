"""Bounded read-only commands. No production inference or apply command."""

from __future__ import annotations

import argparse
import json
import sys
from pathlib import Path

from . import __version__
from .codex import CodexClient, CodexError, MODELS
from .paperless import PaperlessClient, PaperlessError
from .proposals import ProposalValidationError, validate_proposal
from .snapshots import SnapshotError, read_json, save_snapshot


def positive(value: str) -> int:
    try:
        number = int(value)
    except ValueError as exc:
        raise argparse.ArgumentTypeError("must be a positive integer") from exc
    if number < 1:
        raise argparse.ArgumentTypeError("must be a positive integer")
    return number


def parser() -> argparse.ArgumentParser:
    result = argparse.ArgumentParser(description="Read-only Paperless discovery and local proposal validation")
    result.add_argument("--version", action="version", version=__version__)
    result.add_argument("--profile", default="default", help="Authenticated paperless CLI profile")
    commands = result.add_subparsers(dest="command", required=True)
    commands.add_parser("auth-status", help="Check existing Codex ChatGPT login without revealing credentials")
    probe = commands.add_parser("probe", help="Use one subscription request to read a generated synthetic image")
    probe.add_argument("--model", required=True, choices=MODELS)
    discover = commands.add_parser("discover", help="List a bounded review queue; no OCR text printed")
    discover.add_argument("--tag", default="needs review")
    discover.add_argument("--limit", type=positive, default=10)
    snapshot = commands.add_parser("snapshot", help="Create a new local snapshot; may contain sensitive document data")
    snapshot.add_argument("--output", type=Path, required=True)
    selection = snapshot.add_mutually_exclusive_group()
    selection.add_argument("--document-id", type=positive, action="append", dest="document_ids")
    selection.add_argument("--tag", default="needs review")
    snapshot.add_argument("--limit", type=positive, default=10)
    snapshot.add_argument("--download-originals", action="store_true")
    validate = commands.add_parser("validate", help="Validate a local proposal against a snapshot; never applies it")
    validate.add_argument("--snapshot", type=Path, required=True)
    validate.add_argument("--document-id", type=positive, required=True)
    validate.add_argument("--proposal", type=Path, required=True)
    return result


def main(argv: list[str] | None = None) -> int:
    args = parser().parse_args(argv)
    try:
        if args.command == "validate":
            snapshot = read_json(args.snapshot)
            if not isinstance(snapshot, dict) or type(snapshot.get("format_version")) is not int or snapshot["format_version"] != 1:
                raise SnapshotError("Unsupported snapshot format")
            docs = snapshot.get("documents")
            if not isinstance(docs, list):
                raise SnapshotError("Snapshot documents must be an array")
            matches = [d for d in docs if isinstance(d, dict) and type(d.get("id")) is int and d["id"] == args.document_id]
            if len(matches) != 1:
                raise SnapshotError("Document must occur exactly once in snapshot")
            proposal = read_json(args.proposal, max_bytes=4 * 1024 * 1024)
            validate_proposal(proposal, matches[0], snapshot.get("taxonomy"))
            output = {"valid": True, "document_id": args.document_id, "applied": False}
        elif args.command == "auth-status":
            output = CodexClient().status()
        elif args.command == "probe":
            output = CodexClient().probe(args.model)
        else:
            if args.limit > 100:
                raise SnapshotError("limit must be between 1 and 100")
            if not args.profile.strip() or "\x00" in args.profile:
                raise SnapshotError("profile must be a nonempty name")
            if not (args.tag or "").strip():
                raise SnapshotError("tag must be a nonempty name")
            client = PaperlessClient(profile=args.profile)
            if args.command == "discover":
                docs = client.list_documents(tag=args.tag, limit=args.limit)
                output = {"count": len(docs), "documents": [{"id": d["id"], "title": d.get("title"), "created": d.get("created")} for d in docs]}
            else:
                output = save_snapshot(client, args.output, document_ids=args.document_ids,
                                       tag=args.tag or "needs review", limit=args.limit,
                                       download_originals=args.download_originals)
        print(json.dumps(output, ensure_ascii=False, indent=2))
        return 0
    except (PaperlessError, ProposalValidationError, SnapshotError, CodexError) as exc:
        print(f"ppllm: {exc}", file=sys.stderr)
        return 1
    except OSError:
        print("ppllm: local file operation failed; check paths and permissions", file=sys.stderr)
        return 1


if __name__ == "__main__":
    raise SystemExit(main())
