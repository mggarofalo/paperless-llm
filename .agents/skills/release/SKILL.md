---
name: release
description: Prepare, publish, or verify a paperless-llm release from protected main using stable version tags and verified Python distributions on GitHub Releases. Use for release requests in this repository.
---

# Release paperless-llm

Read root `AGENTS.md`, `pyproject.toml`, `.github/workflows/ci.yml`,
`.github/workflows/release.yml`, and `scripts/verify_release.py` first.
This repository publishes a wheel, source archive and `SHA256SUMS` to GitHub
Releases. It does not publish to PyPI or bundle the external Paperless/Codex CLIs.

## Prepare the version

Accept an explicit stable version (`0.1.0` or `v0.1.0`) or a major/minor/patch
bump; default to patch when unspecified. Inspect remote tags and GitHub releases,
compare stable versions numerically, and use `0.0.0` as the bump base for the
first release. Honor an explicit version exactly and require it to be newer than
existing stable releases. Prereleases require a workflow change first.

Use a clean working tree or an isolated checkout; preserve unrelated user edits.
Prepare the release on a branch. Update `project.version` in `pyproject.toml`
and `__version__` in `src/paperless_llm/__init__.py` together, then run `uv lock`.
Run `uv sync --locked --extra test`, `uv run --locked --extra test pytest -q`,
and `uv build --no-create-gitignore --out-dir NEW_TEMP_DIRECTORY` (uv 0.11.32,
as pinned in CI). Verify that fresh directory with
`python scripts/verify_release.py NEW_TEMP_DIRECTORY --version VERSION --write-checksums`.

Open a PR and require all four **Tests (OS, Python VERSION)** jobs and
**Release packaging** to pass. Merge through normal branch protection; never
bypass checks or push directly to main. Use existing user authorization: a
request to release authorizes the necessary version PR, merge, tag and publication.
A request only to prepare a release stops at a ready PR with concrete version and
notes; do not merge, tag, or publish unless those actions are separately authorized.

Summarize changes since the previous release (all commits for the first), including
user-visible changes, limitations and breaking changes. Do not imply production
OCR, automatic tagging or receipt imports exist merely because foundational code
is present. Keep private document data and credentials out of notes.

## Tag and publish

Fetch main and tags. From a clean main checkout, require `HEAD == origin/main`
and successful CI on that exact commit. Check both the remote tag and release for
the chosen version. If either already exists, inspect its target and state; never
move/delete a published tag or silently overwrite release assets.

Create and push an annotated `vMAJOR.MINOR.PATCH` tag on that checked commit:

```sh
git tag -a vMAJOR.MINOR.PATCH -m "Release vMAJOR.MINOR.PATCH"
git push origin refs/tags/vMAJOR.MINOR.PATCH
```

Use the authenticated user's git credentials. A tag push triggers `release.yml`;
a tag created using another workflow's default `GITHUB_TOKEN` will not trigger
this pipeline. The workflow validates tag/version/main ancestry, reruns the full
CI workflow, verifies distributions, publishes release assets, and downloads them
again for checksum/content validation. No release is requested merely by invoking
this skill for guidance or by adding release infrastructure.

## Verify completion

Find the release run using `gh run list --workflow release.yml --branch vVERSION
--event push`; confirm its head SHA matches the tag and wait for successful completion.
Then use `gh release view vVERSION --json url,isDraft,isPrerelease,assets`.
Expect exactly `paperless_llm-VERSION.tar.gz`,
`paperless_llm-VERSION-py3-none-any.whl`, and `SHA256SUMS`.

Download all three assets to a new temporary directory and run:

```sh
python scripts/verify_release.py TEMP_DIRECTORY --version VERSION
uv run --isolated --no-project --with TEMP_DIRECTORY/paperless_llm-VERSION-py3-none-any.whl ppllm --version
```

Require the installed CLI to report the release version. Unit tests and release
verification must not run live model probes or require production credentials.
Curated release notes can be applied with `gh release edit --notes-file` using a
temporary file outside the repository.

On failure, inspect logs and any partial release/assets before retrying. Re-run
unchanged code only for a diagnosed transient failure. If assets already exist,
the workflow retains them and requires downloaded checksums to match the rebuilt
distributions; drafts, prereleases, missing assets or mismatches fail for inspection.
It never repairs a partial release by replacing assets automatically. Fix code through a PR; do
not retarget an already-pushed tag. Report incomplete publication accurately and
resolve the next version with the user if the authorized version cannot be retained.
Report the release URL, version, source commit and verification outcome.
