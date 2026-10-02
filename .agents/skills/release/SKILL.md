---
name: release
description: Choose a semantic version, prepare, publish and verify paperless-llm Docker releases and latest/stable GHCR channels. Use for versioning and release requests in this repository.
---

# Release paperless-llm

Read [AGENTS.md](../../../AGENTS.md), the version in
[src/PaperlessLlm/PaperlessLlm.csproj](../../../src/PaperlessLlm/PaperlessLlm.csproj),
[CI](../../../.github/workflows/ci.yml), [release workflow](../../../.github/workflows/release.yml)
and [Compose](../../../compose.yaml). The product is a .NET 10 Linux container;
do not publish the retired Python prototype.

## Prepare

Inspect remote tags, published releases and changes since the highest stable
semantic version. Honor an explicit requested version;
if it already exists, do not reuse it or silently choose another. Never move a
published version tag. Prereleases need explicit workflow support first and must
never advance stable channels.

When no version is supplied, follow [SemVer](https://semver.org/): PATCH for
compatible fixes, MINOR for compatible user-facing capabilities, MAJOR for breaking
the public contract after 1.0. Before 1.0, use a new minor for deliberate breaking
changes or a substantial feature milestone, and patches for compatible fixes.
Treat CLI/configuration names, persisted job/journal semantics, auth-volume layout,
prompt output contract and Compose behavior as compatibility surfaces. Record the
reason and migration impact in release notes; a code-only refactor is normally a
patch unless bundled into an explicitly requested milestone.

Work on a branch and preserve unrelated edits. Update the project Version,
Compose image tag, Dockerfile default, CI's expected version and versioned documentation together.
Run `dotnet restore --locked-mode`, `dotnet test -c Release --no-restore --tl:off`,
`docker build -t ppllm:release .`, and the CI container smoke commands. Verify
Compose with `.env.example`. Synthetic tests must not access production documents
or require credentials. Check the README/auth/operations/review links and commands
against the executable. Run the complexity and Coverlet checks documented in
[quality](../../../docs/quality.md), the release-policy tests and relative-link
checks. Record scoped metrics and meaningful remaining coverage gaps; do not
exclude difficult code or add implementation-mirroring tests to inflate numbers.

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
It must not overwrite existing release assets or retarget an immutable version tag.
Only after verification, it promotes the same digest to `latest` and `stable` and
marks the GitHub release latest. Both names mean the highest published stable
version; neither is a nightly channel. Publication is serialized across versions.
[release-policy.mjs](../../../scripts/release-policy.mjs) compares all published
stable versions numerically, so an older delayed build cannot roll channels back.
Keep Compose pinned by default; users explicitly opt into a mutable channel.
For a diagnosed transient failure, inspect partial publication before retrying.
Fix source defects through another PR and a new version if a tag was already pushed.

If the versioned image/release succeeded but channel promotion failed, do not rebuild
or replace immutable artifacts. Inspect both channel digests and all published stable
versions. Resume promotion only for the highest verified stable release, using its
existing digest and the tested promotion policy; never promote an older version.
Channel writes are not atomic, so verify both digests afterward. If the failure needs
a code change, land it through a PR before retrying the channel-only action. Explicit
rollback to an older channel is a separate user-directed operation, not normal release
recovery. If publication state cannot be established, stop publication and report the
specific uncertainty while continuing non-publishing verification.

## Verify delivery

Check the release workflow completed for the tag SHA. Verify the GHCR package is
public: public source repositories do not necessarily make their packages public.
Use an anonymous Docker configuration for a digest pull; inspect the manifest for
both promised platforms and run `--version` from the pulled image. Verify the
Compose, environment and prompt release assets against SHA256SUMS, then run Compose config.
Verify `latest` and `stable` resolve to the release digest and can be pulled without
registry credentials. Check GitHub's latest release too. A clean versioned release
with broken mutable channels is only partial delivery; report it accurately.
Do not report a release as pullable until the anonymous pull works.

Report version, image reference, release URL and verification outcome. Clearly
separate synthetic verification from a live ChatGPT sign-in or a real Paperless
pilot. Keep credentials and private audit data out of release notes and artifacts.
