# Releases and image channels

[Index](index.md) · [Operations](operations.md) · [Release skill](../.agents/skills/release/SKILL.md)

| Image reference | Meaning |
| --- | --- |
| `ghcr.io/mggarofalo/paperless-llm:v0.5.1` | Immutable release tag; default in the matching Compose asset |
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

## Upgrade to 0.5.1

This patch makes "sign in again, then restart the worker" resume immediately.
Previously a sign-in pause kept the paused jobs waiting until the next poll even
after a successful re-login and restart. Now `worker` and `once` startup, after
confirming the saved sign-in, make those jobs due and clear the sign-in pause.
Rate-limit and Paperless pauses, other retry timers and attempt counts are
unchanged. There is no configuration or state change; set
`PPLLM_IMAGE=ghcr.io/mggarofalo/paperless-llm:v0.5.1`, then run
`docker compose pull worker` and `docker compose up -d worker`. Upgrading from
v0.4.0 also needs the v0.5.0 steps below.

## Upgrade to 0.5.0

This minor release adds a second inference harness and credential alerts.
`PPLLM_HARNESS=claude` runs inference through the bundled, pinned Claude Code CLI
with your own Claude subscription; the default `codex` harness, its model and its
auth path are unchanged. Set `PPLLM_NTFY_URL` to be notified through
[ntfy](https://ntfy.sh) when missing, expired or revoked credentials pause the
worker. See [authentication](authentication.md) and
[credential alerts](operations.md#credential-alerts).

There is no state or checkpoint migration; existing jobs and ChatGPT credentials
carry forward, and rollback to v0.4.0 needs no restore. Set
`PPLLM_IMAGE=ghcr.io/mggarofalo/paperless-llm:v0.5.0` in your existing `.env`,
then run `docker compose pull worker` and `docker compose up -d worker`.
`PPLLM_MODEL` may now be empty to select the harness default. **Replace your
`compose.yaml` with this release's copy**; the v0.4.0 file does not pass
`PPLLM_HARNESS` or the ntfy settings into the container. If you maintain your own
Compose file, set `PPLLM_HARNESS` and `PPLLM_MODEL` in both the `worker` and `auth`
services: sign-in runs in `auth`, and without it there it falls back to ChatGPT.

To switch to Claude, stop the worker, set `PPLLM_HARNESS=claude` and **clear
`PPLLM_MODEL`** (an existing `gpt-6-sol` value is not a Claude model and fails
closed), then run the Claude login in [authentication](authentication.md#claude-harness-claude-code-login)
and the verification commands below it. The default Claude model is
`claude-sonnet-5-5`, which matched Sol 6 low on the private held-out evaluation
([evaluation note](evaluations/2026-10-31-claude-named-organization.md)).
That evaluation ran on Windows with the frozen experimental prompt and no images;
the Linux image, the current production prompt and live Claude credential renewal
still require deployment acceptance. Usage counts against your Claude plan's limits.

## Upgrade to 0.4.0

This minor release makes manual bulk reprocessing a supported operation. Submit
one durable selection while the worker runs, preview it, then track, cancel or
resume it by run ID. Selections support completed jobs, explicit ID ranges,
completion dates, missing summaries and bounded historical enrollment. Use
`--notes-only` to preserve metadata. Run progress includes fixed outcome reasons,
field dispositions and provider usage when available. Reprocessing stays manual.

**Stop the worker and back up its state and audit volumes before upgrading.**
Startup upgrades the organizer checkpoint to version 2. Older workers reject that
checkpoint; rollback requires restoring the pre-upgrade backup. Never edit the
checkpoint version downward. Keep the same Compose project and auth/state/audit
volumes; existing jobs and credentials carry forward.

Set `PPLLM_IMAGE=ghcr.io/mggarofalo/paperless-llm:v0.4.0` in your existing `.env`,
then run `docker compose pull worker` and `docker compose up -d worker`.
The bundled prompt updates with the image. If you mount a custom prompt, merge
this release's categorical field-decision instructions to get richer diagnostics;
older custom prompts remain accepted and report unknown decisions.

Preview with `docker compose exec worker dotnet PaperlessLlm.dll reprocess --all --preview`.
Remove `--preview` to submit. Once submission returns a run ID, the detached worker
continues without an SSH session. See [manual reprocessing](operations.md#manual-bulk-reprocessing)
for selections and run controls. There is no need to raise the batch size or
restart the worker after submitting a run.

Selections are limited to 10,000 documents and 1,000 retained runs; automatic
retention is not included. Usage reflects the latest saved inference, not the sum
of failed attempts. Synthetic tests verify recovery and protected fields; live
model accuracy and credential renewal still require deployment acceptance.

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
In v0.3.0, reprocessing can also update metadata; v0.4.0 adds `--notes-only`.

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
