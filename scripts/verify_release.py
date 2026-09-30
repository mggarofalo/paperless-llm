"""Verify a universal wheel, source archive, and their SHA256SUMS without extraction."""
from __future__ import annotations

import argparse
import ast
import base64
import csv
from email.parser import BytesParser
import hashlib
import io
from pathlib import Path, PurePosixPath
import re
import stat
import sys
import tarfile
import tomllib
import zipfile


SEMVER = r"(?:0|[1-9][0-9]*)\.(?:0|[1-9][0-9]*)\.(?:0|[1-9][0-9]*)"
MAX_MEMBERS = 1000
MAX_MEMBER_BYTES = 5 * 1024 * 1024
MAX_TOTAL_BYTES = 30 * 1024 * 1024
ROOT_FILES = {"README.md", "AGENTS.md", "LICENSE", "LICENSE.md", "LICENSE.txt",
              "pyproject.toml", "uv.lock", ".gitignore", "PKG-INFO"}


def _version(value: str) -> str:
    if not isinstance(value, str) or not re.fullmatch(SEMVER, value):
        raise ValueError("Version must be stable X.Y.Z without a v prefix")
    return value


def _source_version(source: bytes) -> str:
    """Read the literal package version; never import or execute release code."""
    tree = ast.parse(source.decode("utf-8"))
    versions = []
    for node in tree.body:
        if isinstance(node, ast.Assign) and any(isinstance(t, ast.Name) and t.id == "__version__" for t in node.targets):
            if not isinstance(node.value, ast.Constant) or not isinstance(node.value.value, str):
                raise ValueError("Package __version__ must be a literal string")
            versions.append(node.value.value)
        elif isinstance(node, ast.AnnAssign) and isinstance(node.target, ast.Name) and node.target.id == "__version__":
            if not isinstance(node.value, ast.Constant) or not isinstance(node.value.value, str):
                raise ValueError("Package __version__ must be a literal string")
            versions.append(node.value.value)
    if len(versions) != 1:
        raise ValueError("Package must declare exactly one literal __version__")
    return _version(versions[0])


def _member_path(name: str) -> tuple[str, ...]:
    if not name or "\\" in name or ":" in name or any(ord(c) < 32 for c in name):
        raise ValueError("Unsafe archive member path")
    parts = name.rstrip("/").split("/")
    if any(part in {"", ".", ".."} for part in parts) or PurePosixPath(name).is_absolute():
        raise ValueError("Unsafe archive member path")
    return tuple(parts)


def _allowed_source(parts: tuple[str, ...]) -> bool:
    if not parts:
        return False
    if len(parts) == 1:
        return parts[0] in ROOT_FILES
    if parts[:2] == ("src", "paperless_llm"):
        return len(parts) == 3 and parts[-1].endswith(".py")
    if parts[0] in {"tests", "scripts"}:
        return len(parts) == 2 and parts[-1].endswith(".py")
    if parts[0] == "docs":
        return len(parts) == 2 and parts[-1].endswith(".md")
    if parts[:2] == (".github", "workflows"):
        return len(parts) == 3 and parts[-1].endswith((".yml", ".yaml"))
    return parts == (".agents", "skills", "release", "SKILL.md")


def _allowed_wheel(parts: tuple[str, ...], version: str) -> bool:
    if len(parts) == 2 and parts[0] == "paperless_llm":
        return parts[1].endswith(".py")
    info = f"paperless_llm-{version}.dist-info"
    if len(parts) == 2 and parts[0] == info:
        return parts[1] in {"METADATA", "WHEEL", "RECORD", "entry_points.txt"}
    return len(parts) == 3 and parts[:2] == (info, "licenses") and parts[2] in {"LICENSE", "LICENSE.md", "LICENSE.txt"}


def _bounded_size(size: int, total: int) -> int:
    if size < 0 or size > MAX_MEMBER_BYTES or total + size > MAX_TOTAL_BYTES:
        raise ValueError("Archive exceeds verification size limits")
    return total + size


def _wheel(path: Path, version: str) -> dict[str, bytes]:
    payload = {}
    total = 0
    with zipfile.ZipFile(path) as archive:
        members = archive.infolist()
        if not 1 <= len(members) <= MAX_MEMBERS:
            raise ValueError("Invalid wheel member count")
        for member in members:
            parts = _member_path(member.filename)
            mode = member.external_attr >> 16
            if member.is_dir() or stat.S_IFMT(mode) not in {0, stat.S_IFREG}:
                raise ValueError("Wheel must contain only regular files")
            if member.filename in payload or not _allowed_wheel(parts, version):
                raise ValueError("Unexpected or duplicate wheel member")
            total = _bounded_size(member.file_size, total)
            payload[member.filename] = archive.read(member)  # Also verifies ZIP CRC.
    return payload


def _sdist(path: Path, version: str) -> dict[str, bytes]:
    payload = {}
    seen = set()
    directories = []
    total = 0
    with tarfile.open(path, "r:gz") as archive:
        for number, member in enumerate(archive, 1):
            if number > MAX_MEMBERS:
                raise ValueError("Invalid source archive member count")
            parts = _member_path(member.name)
            if parts[0] != f"paperless_llm-{version}" or member.name.rstrip("/") in seen:
                raise ValueError("Unexpected or duplicate source archive member")
            seen.add(member.name.rstrip("/"))
            if member.isdir():
                directories.append(parts)
                continue
            if not member.isfile() or not _allowed_source(parts[1:]):
                raise ValueError("Unexpected source archive file or link")
            total = _bounded_size(member.size, total)
            stream = archive.extractfile(member)
            if stream is None:
                raise ValueError("Missing source archive payload")
            with stream:
                data = stream.read(MAX_MEMBER_BYTES + 1)
            if len(data) != member.size:
                raise ValueError("Source archive payload size mismatch")
            payload["/".join(parts[1:])] = data
        for directory in directories:
            prefix = "/".join(directory[1:])
            if prefix and not any(name.startswith(prefix + "/") for name in payload):
                raise ValueError("Unexpected empty source archive directory")
    return payload


def _metadata(data: bytes, version: str) -> None:
    metadata = BytesParser().parsebytes(data)
    if metadata.get_all("Name") != ["paperless-llm"] or metadata.get_all("Version") != [version]:
        raise ValueError("Package metadata name or version mismatch")


def _verify_payloads(wheel: dict[str, bytes], source: dict[str, bytes], version: str) -> None:
    info = f"paperless_llm-{version}.dist-info"
    try:
        _metadata(wheel[f"{info}/METADATA"], version)
        _metadata(source["PKG-INFO"], version)
        if _source_version(wheel["paperless_llm/__init__.py"]) != version or _source_version(source["src/paperless_llm/__init__.py"]) != version:
            raise ValueError("Embedded package version mismatch")
        config = tomllib.loads(source["pyproject.toml"].decode("utf-8"))
        project = config["project"]
        if project.get("name") != "paperless-llm":
            raise ValueError("Source project name mismatch")
        if "version" in project:
            if project["version"] != version:
                raise ValueError("Source project version mismatch")
        elif "version" not in project.get("dynamic", []) or config.get("tool", {}).get("hatch", {}).get("version", {}).get("path") != "src/paperless_llm/__init__.py":
            raise ValueError("Unsupported dynamic source version configuration")
        wheel_info = BytesParser().parsebytes(wheel[f"{info}/WHEEL"])
        if wheel_info.get_all("Root-Is-Purelib") != ["true"] or wheel_info.get_all("Tag") != ["py3-none-any"]:
            raise ValueError("Wheel must be universal pure Python")
        # A filename alone does not establish integrity: verify every RECORD entry.
        record_name = f"{info}/RECORD"
        rows = list(csv.reader(io.StringIO(wheel[record_name].decode("utf-8"))))
        names = set()
        for row in rows:
            if len(row) != 3 or row[0] in names or row[0] not in wheel:
                raise ValueError("Invalid wheel RECORD")
            name, digest, size = row
            names.add(name)
            if name == record_name:
                if digest or size:
                    raise ValueError("Invalid self-reference in wheel RECORD")
            else:
                expected = "sha256=" + base64.urlsafe_b64encode(hashlib.sha256(wheel[name]).digest()).decode("ascii").rstrip("=")
                if digest != expected or size != str(len(wheel[name])):
                    raise ValueError("Wheel RECORD integrity mismatch")
        if names != set(wheel):
            raise ValueError("Wheel RECORD does not cover all files")
        package = {name: data for name, data in wheel.items() if name.startswith("paperless_llm/")}
        sources = {name.removeprefix("src/"): data for name, data in source.items() if name.startswith("src/paperless_llm/")}
        if package != sources:
            raise ValueError("Wheel and source package files differ")
    except (KeyError, TypeError, AttributeError):
        raise ValueError("Required release metadata or package file is missing") from None


def verify(directory: Path, version: str | None = None, *, write_checksums: bool = False) -> str:
    directory = Path(directory)
    if version is None:
        candidates = [p.name for p in directory.iterdir() if p.name.endswith(".whl")]
        if len(candidates) != 1:
            raise ValueError("Expected exactly one wheel to infer the version")
        match = re.fullmatch(rf"paperless_llm-(?P<version>{SEMVER})-py3-none-any\.whl", candidates[0])
        if match is None:
            raise ValueError("Unexpected wheel filename")
        version = match.group("version")
    version = _version(version)
    filenames = [f"paperless_llm-{version}-py3-none-any.whl", f"paperless_llm-{version}.tar.gz"]
    allowed = set(filenames) | {"SHA256SUMS"}
    actual = {p.name for p in directory.iterdir()}
    if not set(filenames) <= actual or actual - allowed:
        raise ValueError("Release directory must contain exactly the expected wheel, source archive, and SHA256SUMS")
    if any(p.is_symlink() or not p.is_file() for p in directory.iterdir()):
        raise ValueError("Release artifacts must be regular files")
    wheel = _wheel(directory / filenames[0], version)
    source = _sdist(directory / filenames[1], version)
    _verify_payloads(wheel, source, version)
    digests = {}
    for name in filenames:
        with (directory / name).open("rb") as stream:
            digests[name] = hashlib.file_digest(stream, "sha256").hexdigest()
    manifest = directory / "SHA256SUMS"
    if write_checksums and not manifest.exists():
        with manifest.open("x", encoding="utf-8", newline="\n") as stream:
            for name in sorted(digests):
                stream.write(f"{digests[name]}  {name}\n")
    if not manifest.exists():
        raise ValueError("SHA256SUMS is required; use --write-checksums when preparing a release")
    if manifest.stat().st_size > 4096:
        raise ValueError("Invalid SHA256SUMS size")
    recorded = {}
    for line in manifest.read_text(encoding="utf-8").splitlines():
        match = re.fullmatch(r"([a-f0-9]{64})  ([A-Za-z0-9_.-]+)", line)
        if match is None or match.group(2) in recorded:
            raise ValueError("Invalid or duplicate checksum entry")
        recorded[match.group(2)] = match.group(1)
    if recorded != digests:
        raise ValueError("Checksum mismatch or unexpected checksum entries")
    return version


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("directory", type=Path)
    parser.add_argument("--version", help="Stable version without v; defaults to the wheel filename")
    parser.add_argument("--write-checksums", action="store_true", help="Create SHA256SUMS if absent; never overwrite existing checksums")
    args = parser.parse_args(argv)
    try:
        version = verify(args.directory, args.version, write_checksums=args.write_checksums)
    except (ValueError, OSError, UnicodeError, SyntaxError, zipfile.BadZipFile, tarfile.TarError, EOFError):
        print("Release verification failed: check artifact names, metadata, archive contents, and SHA256SUMS", file=sys.stderr)
        return 1
    print(f"Verified paperless-llm {version}: wheel, source archive, and SHA256SUMS")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
