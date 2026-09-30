"""Release verification using tiny synthetic archives, never personal data."""
import base64
import csv
import hashlib
import importlib.util
import io
from pathlib import Path
import tarfile
import zipfile

import pytest


spec = importlib.util.spec_from_file_location("verify_release", Path(__file__).resolve().parents[1] / "scripts" / "verify_release.py")
release = importlib.util.module_from_spec(spec)
spec.loader.exec_module(release)


def make_release(directory, *, wheel_version="1.2.3", source_version="1.2.3", project_version="1.2.3",
                 metadata_version="1.2.3", wheel_extra=None, source_extra=None, malicious_init=None,
                 record_bad=False, symlink=False):
    version = "1.2.3"
    info = f"paperless_llm-{version}.dist-info"
    init = malicious_init if malicious_init is not None else f'__version__ = "{wheel_version}"\n'.encode()
    package = {"paperless_llm/__init__.py": init, "paperless_llm/cli.py": b"def main(): pass\n"}
    wheel = {**package,
             f"{info}/METADATA": f"Metadata-Version: 2.4\nName: paperless-llm\nVersion: {metadata_version}\n".encode(),
             f"{info}/WHEEL": b"Wheel-Version: 1.0\nRoot-Is-Purelib: true\nTag: py3-none-any\n",
             f"{info}/entry_points.txt": b"[console_scripts]\nppllm = paperless_llm.cli:main\n"}
    wheel.update(wheel_extra or {})
    rows = []
    for name, payload in wheel.items():
        digest = base64.urlsafe_b64encode(hashlib.sha256(payload).digest()).decode().rstrip("=")
        rows.append([name, "sha256=" + digest, str(len(payload))])
    if record_bad:
        rows[0][1] = "sha256=invalid"
    rows.append([f"{info}/RECORD", "", ""])
    record = io.StringIO()
    csv.writer(record, lineterminator="\n").writerows(rows)
    wheel[f"{info}/RECORD"] = record.getvalue().encode()
    wheel_path = directory / f"paperless_llm-{version}-py3-none-any.whl"
    with zipfile.ZipFile(wheel_path, "w") as archive:
        for name, payload in wheel.items():
            member = zipfile.ZipInfo(name)
            if symlink and name.endswith("cli.py"):
                member.create_system = 3
                member.external_attr = 0o120777 << 16
            archive.writestr(member, payload)
    source = {"src/" + name: data for name, data in package.items()}
    source["src/paperless_llm/__init__.py"] = f'__version__ = "{source_version}"\n'.encode()
    source.update({"PKG-INFO": wheel[f"{info}/METADATA"],
                   "pyproject.toml": f'[project]\nname = "paperless-llm"\nversion = "{project_version}"\n'.encode()})
    source.update(source_extra or {})
    tar_path = directory / f"paperless_llm-{version}.tar.gz"
    with tarfile.open(tar_path, "w:gz") as archive:
        for name, payload in source.items():
            member = tarfile.TarInfo(f"paperless_llm-{version}/{name}")
            member.size = len(payload)
            archive.addfile(member, io.BytesIO(payload))
    return wheel_path, tar_path


def test_complete_release_infers_version_and_preserves_manifest(tmp_path):
    make_release(tmp_path)
    assert release.verify(tmp_path, write_checksums=True) == "1.2.3"
    original = (tmp_path / "SHA256SUMS").read_bytes()
    assert release.verify(tmp_path, "1.2.3") == "1.2.3"
    release.verify(tmp_path, write_checksums=True)
    assert (tmp_path / "SHA256SUMS").read_bytes() == original
    assert len(original.splitlines()) == 2


def test_default_requires_checksums(tmp_path):
    make_release(tmp_path)
    with pytest.raises(ValueError, match="required"):
        release.verify(tmp_path)
    assert not (tmp_path / "SHA256SUMS").exists()


@pytest.mark.parametrize("version", ["v1.2.3", "1.2.3rc1", "1.2", "01.2.3", "1.2.3+build", "../../x"])
def test_unstable_versions_rejected(tmp_path, version):
    make_release(tmp_path)
    with pytest.raises(ValueError, match="stable"):
        release.verify(tmp_path, version, write_checksums=True)


@pytest.mark.parametrize("kwargs", [{"wheel_version": "1.2.4"}, {"source_version": "1.2.4"},
                                  {"project_version": "1.2.4"}, {"metadata_version": "1.2.4"}])
def test_every_embedded_version_must_agree(tmp_path, kwargs):
    make_release(tmp_path, **kwargs)
    with pytest.raises(ValueError, match="mismatch"):
        release.verify(tmp_path, write_checksums=True)
    assert not (tmp_path / "SHA256SUMS").exists()


@pytest.mark.parametrize("extra", ["receipt.pdf", "snapshot.json", ".env", "paperless-token.txt", "subdirectory"])
def test_release_directory_rejects_unexpected_artifacts(tmp_path, extra):
    make_release(tmp_path)
    (tmp_path / extra).write_text("synthetic sensitive data")
    with pytest.raises(ValueError, match="exactly"):
        release.verify(tmp_path, write_checksums=True)


@pytest.mark.parametrize("name", ["../escape.py", "/root.txt", "paperless_llm/private.json", "paperless_llm/token.txt", "paperless_llm/../../x.py", "paperless_llm\\x.py"])
def test_wheel_rejects_private_and_unsafe_members(tmp_path, name):
    make_release(tmp_path, wheel_extra={name: b"synthetic"})
    with pytest.raises(ValueError):
        release.verify(tmp_path, write_checksums=True)


@pytest.mark.parametrize("name", ["../escape.py", ".env", "snapshot.json", "scans/receipt.pdf", "src/paperless_llm/token.txt"])
def test_sdist_rejects_private_and_unsafe_members(tmp_path, name):
    make_release(tmp_path, source_extra={name: b"synthetic"})
    with pytest.raises(ValueError):
        release.verify(tmp_path, write_checksums=True)


def test_version_expression_never_executes(tmp_path):
    target = tmp_path / "should-not-exist"
    expression = f'__version__ = __import__("pathlib").Path({str(target)!r}).write_text("executed")\n'.encode()
    make_release(tmp_path, malicious_init=expression)
    with pytest.raises(ValueError, match="literal"):
        release.verify(tmp_path, write_checksums=True)
    assert not target.exists()


@pytest.mark.parametrize("kwargs", [{"record_bad": True}, {"symlink": True}])
def test_wheel_integrity_and_links_rejected(tmp_path, kwargs):
    make_release(tmp_path, **kwargs)
    with pytest.raises(ValueError):
        release.verify(tmp_path, write_checksums=True)


def test_archive_corruption_rejected(tmp_path):
    wheel, _ = make_release(tmp_path)
    wheel.write_bytes(b"not a zip file")
    with pytest.raises(zipfile.BadZipFile):
        release.verify(tmp_path, write_checksums=True)


def test_sdist_link_rejected_without_extracting(tmp_path):
    _, archive_path = make_release(tmp_path)
    with tarfile.open(archive_path, "w:gz") as archive:
        member = tarfile.TarInfo("paperless_llm-1.2.3/src/paperless_llm/__init__.py")
        member.type = tarfile.SYMTYPE
        member.linkname = "../../outside"
        archive.addfile(member)
    with pytest.raises(ValueError, match="link"):
        release.verify(tmp_path, write_checksums=True)


def test_duplicate_archive_member_rejected(tmp_path):
    wheel, _ = make_release(tmp_path)
    with zipfile.ZipFile(wheel, "a") as archive:
        with pytest.warns(UserWarning, match="Duplicate name"):
            archive.writestr("paperless_llm/cli.py", b"duplicate")
    with pytest.raises(ValueError, match="duplicate"):
        release.verify(tmp_path, write_checksums=True)


def test_oversized_member_rejected(tmp_path, monkeypatch):
    make_release(tmp_path)
    monkeypatch.setattr(release, "MAX_MEMBER_BYTES", 10)
    with pytest.raises(ValueError, match="size limits"):
        release.verify(tmp_path, write_checksums=True)


def test_checksum_tampering_never_overwritten(tmp_path):
    make_release(tmp_path)
    release.verify(tmp_path, write_checksums=True)
    manifest = tmp_path / "SHA256SUMS"
    corrupt = "0" * 64 + manifest.read_text()[64:]
    manifest.write_text(corrupt)
    with pytest.raises(ValueError, match="Checksum mismatch"):
        release.verify(tmp_path, write_checksums=True)
    assert manifest.read_text() == corrupt


def test_duplicate_manifest_lines_rejected(tmp_path):
    make_release(tmp_path)
    release.verify(tmp_path, write_checksums=True)
    manifest = tmp_path / "SHA256SUMS"
    manifest.write_text(manifest.read_text() + manifest.read_text().splitlines()[0] + "\n")
    with pytest.raises(ValueError, match="duplicate"):
        release.verify(tmp_path)


def test_source_package_must_match_wheel(tmp_path):
    make_release(tmp_path, source_extra={"src/paperless_llm/cli.py": b"different source\n"})
    with pytest.raises(ValueError, match="files differ"):
        release.verify(tmp_path, write_checksums=True)


def test_cli_failure_is_sanitized(tmp_path, capsys):
    make_release(tmp_path)
    (tmp_path / "secret-personal-filename.pdf").write_bytes(b"synthetic")
    assert release.main([str(tmp_path)]) == 1
    captured = capsys.readouterr()
    assert "secret-personal" not in captured.err
    assert "Traceback" not in captured.err
