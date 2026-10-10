# Releases and image channels

[Index](index.md) · [Operations](operations.md) · [Release skill](../.agents/skills/release/SKILL.md)

| Image reference | Meaning |
| --- | --- |
| `ghcr.io/mggarofalo/paperless-llm:v0.3.1` | Immutable release tag; default in the matching Compose asset |
| `ghcr.io/mggarofalo/paperless-llm@sha256:…` | Exact immutable image index; digest is included in release assets |
| `ghcr.io/mggarofalo/paperless-llm:stable` | Highest successfully published stable semantic version |
| `ghcr.io/mggarofalo/paperless-llm:latest` | Same stable channel; not a nightly or development build |

Both platforms (`linux/amd64`, `linux/arm64`) share an image index. Channels move
only after the versioned image, executable check and deployment assets verify.
The release workflow serializes publication and compares versions numerically;
an older release cannot automatically move channels backward. Prereleases do not
participate. Channel updates are separate registry operations, so a failure may
temporarily leave them different; the release skill defines repair steps.

Pin a version or digest for reproducible upgrades. To follow a mutable channel,
set `PPLLM_IMAGE=ghcr.io/mggarofalo/paperless-llm:stable` in `.env`. This setting
is consumed by Compose, not the worker. Pulling a tag does not update a running
container until Compose recreates it.

## Upgrade to 0.3.1

This patch removes the redundant `{OriginalFormat}` template from console JSON
logs while preserving rendered messages and typed event fields. Log timestamps
are explicitly UTC. Log consumers using `{OriginalFormat}` should use `Message`
and structured `State` fields instead.

Set `PPLLM_IMAGE=ghcr.io/mggarofalo/paperless-llm:v0.3.1` in your existing `.env`,
then run `docker compose pull worker` and `docker compose up -d worker`.
Keep the existing project and volumes. Queued reprocessing jobs survive the
container replacement and continue normally; no job, auth, prompt or configuration
migration is required from v0.3.0.

## Upgrade to 0.3.0 (notes feature)

This minor release adds optional OCR-grounded document summary notes. It adds a
version-2 prompt contract and journal fields while preserving saved version-1
metadata jobs. Existing human notes are preserved, generated summaries are not
duplicated, and ambiguous note POSTs require reconciliation rather than blind replay.
See [note recovery](review.md#document-notes).

Keep the same Compose project and auth, organizer-state and audit volumes. Pin
`PPLLM_IMAGE=ghcr.io/mggarofalo/paperless-llm:v0.3.0` in `.env` if it overrides
Compose. Grant the dedicated account Add Notes and View Notes, retaining document
View/Change permissions. Update any mounted prompt from the release's
`organization.txt`; the bundled prompt updates with the image. Merge custom policy
carefully so it uses schema version 2 and the new note instructions.

Stop the worker, pull the image, run `check` and `probe organization`, then inspect
a small batch before resuming. Neither the upgrade nor a changed backfill limit
reprocesses completed documents. Follow the [existing-installation notes backfill
procedure](operations.md#backfill-notes-on-an-existing-installation) to queue them.
Reprocessing can also update metadata; there is no dedicated notes-only CLI mode.

The remaining quality limitation is model interpretation: literal OCR quotations
are checked, but summary factual accuracy needs review on representative documents.
No authentication or volume migration is required from v0.2.0.

## Upgrade to 0.2.0 (historical)

Keep your project name and auth, organizer-state and audit volumes. Download the
new Compose asset, preserve your actual Paperless URL, token file and model setting,
and merge any prompt-directory override. Sol 6 low remains the default; the tested
prompt and the organization contract are unchanged. This release removes obsolete
internal prototype paths, adds quality checks and introduces mutable channels.
Local models and a web UI are still roadmap items.

```sh
docker compose stop worker
docker compose pull
docker compose run --rm worker check
docker compose run --rm worker probe organization
docker compose run --rm -e PPLLM_BATCH_SIZE=1 worker once
docker compose up -d worker
```

The one-document run may write validated updates. Inspect its outcome and
Paperless history. Existing device-code credentials normally carry forward;
reauthenticate only if the runner reports that sign-in is required.
Prompt-file changes affect new inference attempts; they do not replay completed
jobs or replace a saved intent on retry. See [prompt migration](prompts.md).

Release artifacts include Compose, an environment example, the bundled prompt,
image digest and SHA256SUMS. Verify checksums before use. Maintainers follow the
[semantic release skill](../.agents/skills/release/SKILL.md); version tags and
published assets are never overwritten.
