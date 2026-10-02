---
name: release
description: Prepare, publish, or verify a paperless-llm .NET Docker release from protected main to GHCR, with versioned Compose assets on GitHub Releases. Use for release requests in this repository.
---

# Release paperless-llm

Read [AGENTS.md](../../../AGENTS.md), the version in
[src/PaperlessLlm/PaperlessLlm.csproj](../../../src/PaperlessLlm/PaperlessLlm.csproj),
[CI](../../../.github/workflows/ci.yml), [release workflow](../../../.github/workflows/release.yml)
and [Compose](../../../compose.yaml). The product is a .NET 10 Linux container;
do not publish the retired Python prototype.

## Prepare

Inspect remote tags and published releases. Honor an explicit stable version;
otherwise use the next patch (base 0.0.0 when no stable tag exists). Initial
product version 0.1.0 is reserved by the current project. Never reuse or move a
published tag. Prereleases need explicit workflow support first.

Work on a branch and preserve unrelated edits. Update the project Version,
Compose image tag, CI's expected version and versioned documentation together.
Run `dotnet restore --locked-mode`, `dotnet test -c Release --no-restore --tl:off`,
`docker build -t ppllm:release .`, and the CI container smoke commands. Verify
Compose with `.env.example`. Synthetic tests must not access production documents
or require credentials. Check the README/auth/operations/review links and commands
against the executable.

Open or update a PR around the final behavior. Required checks are
`Tests (ubuntu-latest, .NET 10)`, `Container smoke test` and `Container smoke test (arm64)`.
Linux containers are the deployment target; do not add macOS or Windows CI jobs
without a corresponding supported deployment requirement.
Merge through normal branch protection after they pass; never bypass protection.
A user request to release authorizes the necessary PR merge, tag and publication.
A request only to prepare stops at the ready PR.

## Publish

Fetch main and tags. Require clean `HEAD == origin/main` and passing CI for that
exact commit. Check that the intended tag, GitHub release and GHCR version are
absent. Create an annotated `vMAJOR.MINOR.PATCH` tag and push it with the user's git
credentials. The tag triggers `.github/workflows/release.yml`; a workflow's default
GITHUB_TOKEN cannot trigger a separate workflow this way.

The pipeline validates version/main ancestry, reruns CI, publishes linux/amd64 and
linux/arm64 images, verifies a digest pull and executable version, and attaches
`compose.yaml`, `env.example`, `organization.txt`, `image-digest.txt` and `SHA256SUMS` to the release.
It must not overwrite existing release assets or retarget a published image tag.
For a diagnosed transient failure, inspect partial publication before retrying.
Fix source defects through another PR and a new version if a tag was already pushed.

## Verify delivery

Check the release workflow completed for the tag SHA. Verify the GHCR package is
public: public source repositories do not necessarily make their packages public.
Use an anonymous Docker configuration for a digest pull; inspect the manifest for
both promised platforms and run `--version` from the pulled image. Verify the
Compose and environment release assets against SHA256SUMS, then run Compose config.
Do not report a release as pullable until the anonymous pull works.

Report version, image reference, release URL and verification outcome. Clearly
separate synthetic verification from a live ChatGPT sign-in or a real Paperless
pilot. Keep credentials and private audit data out of release notes and artifacts.
