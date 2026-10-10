# Operation

[Home](../README.md) · [Authentication](authentication.md) · [Review](review.md)

## Discovery and jobs

The worker runs one bounded cycle on startup. Normal discovery follows the configured interval; explicitly submitted manual runs drain in bounded batches between discovery polls. A filesystem lock allows one worker per state directory. Processing is serial.

The first cycle records the highest visible document ID as its baseline. Newer documents are enrolled regardless of tags. A rotating discovery scan catches documents above that baseline which become visible later. Documents at or below the baseline are only included by the optional bounded initial backfill. Changing backfill after initialization does not enroll more history.

Jobs are JSON files under `/data/state/jobs`, with durable status fields, attempts, source metadata, proposal, timing and outcome. Files are replaced atomically. A completed document is not automatically rerun after a metadata or policy change; use explicit `reprocess` when needed. Clearing `needs review` is never a discovery signal.

Once a valid proposal has been saved, sync retries reuse it. A crash before that save may repeat inference. The [operation journal](review.md) reconciles an ambiguous write before allowing another PATCH.

## Configuration

| Variable | Default | Meaning |
| --- | --- | --- |
| `PPLLM_PAPERLESS_URL` | required | Paperless instance URL |
| `PPLLM_IMAGE` | Version pinned in Compose | Compose-only image override; see [release channels](releases.md) |
| `PPLLM_MODEL` | `gpt-6-sol` | Explicit model; no fallback |
| `PPLLM_PROMPT_FILE` | `/app/prompts/organization.txt` | UTF-8 policy file, reloaded per inference; see [prompt configuration](prompts.md) |
| `PPLLM_TAG` | `needs review` | Existing marker added after actual changes |
| `PPLLM_POLL_SECONDS` | `3600` | Normal discovery interval and shared-service pause, 60–86400 seconds |
| `PPLLM_BATCH_SIZE` | `5` | Maximum jobs processed per cycle, at most 100 |
| `PPLLM_BACKFILL_LIMIT` | `0` | Oldest visible historical documents enrolled on first initialization, at most 100 |
| `PPLLM_DRY_RUN` | `false` | Validate proposals without writing Paperless |
| `PPLLM_MAX_STATE_MIB` | `2048` | Soft state-directory cap before another attempt starts |

Compose mounts `PPLLM_PAPERLESS_TOKEN_FILE` from its secret. Native deployments can also set `PPLLM_STATE_DIRECTORY`, `PPLLM_AUDIT_DIRECTORY`, `PPLLM_AUTH_DIRECTORY`, `PPLLM_RUNNER_HOME`, `PPLLM_RUNNER_BRIDGE` and `PPLLM_NODE`. The bridge path must identify the installed bridge and locked dependencies.

Dry-run jobs finish as completed. Use a separate state volume for a trial, or explicitly reprocess those documents when enabling writes. Switching `PPLLM_DRY_RUN` alone does not replay completed jobs.

## Observe and recover

```sh
docker compose logs -f worker
docker compose exec worker dotnet PaperlessLlm.dll status
docker compose ps
```

Structured logs contain document/job IDs, state transitions, fixed error codes and inference/sync durations, without OCR, document titles, prompts or credential values. `status` reads durable job counts and schedule timestamps without contacting external services. Health fails on a recorded authorization pause or when the last activity is older than the poll interval plus 15 minutes. An uninitialized worker is not healthy. The model request deadline is five minutes.

Console JSON retains the rendered `Message` and typed `State` fields, but omits
the redundant `{OriginalFormat}` message template. Timestamps are UTC.

Jobs get up to three attempts, with exponential delay serviced on subsequent cycles. Authorization failures and provider rate limits pause the batch without consuming an attempt. Rate limits are retried at the next poll. Source conflicts become failed jobs. Invalid output can trigger another bounded inference attempt.

Stop the worker before `retry` or `once` so they can acquire its state lock. Bulk reprocessing and run controls work while it runs:

```sh
docker compose stop worker
docker compose run --rm worker retry 123
docker compose run --rm worker once
docker compose up -d worker
```

`retry 123` reuses saved intent. Use `reprocess 123` for a completed enrolled document to submit fresh inference from current source state. Reprocessing is deliberate: it can propose changes to fields a person previously edited. Unresolved jobs must recover their existing intent rather than acquire a new identity.

Private state contains document metadata, prompts and results. Older versions may also have retained originals and rendered pages. The soft size cap may be exceeded by one in-flight attempt; the operation journal is in the separate audit volume. There is no automatic evidence-retention policy yet. Monitor both volumes and available disk space, keep encrypted backups, and do not delete pending jobs or journals. Docker console logs rotate at three 10 MiB files in the sample Compose.

This initial filesystem implementation is intended for a personal library. It retains at most 10,000 enrolled jobs; reaching that cap pauses processing until capacity is addressed in a later version. Status and polls load full job records, so memory use grows with retained OCR. Do not discard completed-job indexes to reclaim space: they prevent duplicate processing. Capacity errors are exposed through the health pause reason.

## Manual bulk reprocessing

These commands require v0.4.0 or newer. See the [upgrade guide](releases.md#upgrade-to-040) before upgrading an existing worker.

Leave the worker running. Preview and submit all completed enrolled documents:

```sh
docker compose exec worker dotnet PaperlessLlm.dll reprocess --all --preview
docker compose exec worker dotnet PaperlessLlm.dll reprocess --all
```

Submission uses one process and returns a durable run ID. You can disconnect SSH
as soon as it returns. The worker processes documents sequentially and drains the
manual backlog in bounded batches without the hourly delay between batches.
`PPLLM_BATCH_SIZE` limits each batch, not concurrency. Discovery still gets its
normal polling opportunity; normal and manual jobs alternate within batches.
Provider pauses and retry backoff remain in force, including after restart.
Reprocessing is always manual; there are no recurring reprocessing policies.

```sh
docker compose exec worker dotnet PaperlessLlm.dll status --run RUN_ID
docker compose exec worker dotnet PaperlessLlm.dll status --run RUN_ID --watch
docker compose exec worker dotnet PaperlessLlm.dll status --run RUN_ID --json
docker compose exec worker dotnet PaperlessLlm.dll runs
docker compose exec worker dotnet PaperlessLlm.dll runs cancel RUN_ID
docker compose exec worker dotnet PaperlessLlm.dll runs resume RUN_ID
```

Replace `RUN_ID` with the returned ID. Stopping `--watch` only stops observation;
it does not cancel processing. Cancellation prevents unstarted work from starting.
A job that already started retains its identity and saved proposal and finishes
or recovers normally. Resume restores cancelled unstarted work; it does not retry
failed jobs. Failure recovery still uses `retry ID` with the worker stopped.

Run status distinguishes queued, running, applied, unchanged, skipped, failed,
waiting and cancelled documents, with fixed reasons and the next retry time.
Submission success is not processing success. A completed run may contain failures;
inspect its counts. JSON includes safe IDs, timestamps, decision counts and optional
provider token usage. `UncertaintyCount > 0` means the proposal expressed uncertainty,
so `no_change` must not be interpreted as proof every field was confidently correct.
`Decisions.Fields` distinguishes model-declared `unchanged`, `uncertain`, `policy`,
`not_applicable` and `change` for each managed field. These are explanations, not
accuracy guarantees. Older/custom prompts without these diagnostics report null
rather than inventing confident reasons. In notes-only mode they describe the
model proposal before the metadata restriction. Full evidence remains private. Usage is the latest saved
inference's reported counts, not billing data or a total of failed attempts;
missing usage stays null. Global `status` adds queue depth, oldest queued timestamp,
phase, and last successful discovery/sync timestamps. Run status includes accepted
requests that have not yet been materialized into jobs.

At startup the new worker upgrades the checkpoint to version 2 before consuming
requests. Older workers reject that version, preventing a downgrade
from silently ignoring notes-only or cancellation semantics. Keep a pre-upgrade
backup for rollback; never edit the checkpoint version to bypass this guard.

Selections:

```sh
# Explicit IDs and inclusive ranges, deduplicated into a fixed snapshot:
docker compose exec worker dotnet PaperlessLlm.dll reprocess --ids 1,4-9 --preview
# UTC processing cutoff:
docker compose exec worker dotnet PaperlessLlm.dll reprocess --all --processed-before 2026-10-01T00:00:00Z
# Historical enrollment is a separate, explicitly bounded scope:
docker compose exec worker dotnet PaperlessLlm.dll reprocess --all --include-unenrolled --limit 1000 --preview
```

`--all` examines enrolled documents and skips non-completed jobs. Explicit IDs
that are not enrolled are reported as skipped. `--include-unenrolled` reads all
currently visible documents with pagination and requires `--all` and `--limit`.
The whole selection fails if it exceeds the limit; no partial request is submitted.
The maximum is 10,000 selected IDs and 1,000 retained runs. Run records and prior
jobs count toward the state cap; automatic compaction is not yet implemented.
Do not delete files or change Paperless tags to requeue work.

A preview does not save a request, call the model, or write Paperless. Executing
reselects at submission time; the submitted ID/previous-job snapshot is fixed.
If a selected job changes before materialization, the item is skipped with
`selection_changed`. Concurrent overlapping requests cannot regenerate the same
previous job twice. Pending, running, retrying and failed jobs are never replaced
by reprocessing. A deleted or newly invisible document can still fail when the
worker fetches it. Saved requests survive process/container restarts.

For scripts that may lose the submission response, supply `--request-id` with a
32-character hexadecimal GUID generated once by the caller. Repeating the same ID
and selection options acknowledges the original snapshot without another run;
reusing it with different options fails. Ordinary commands generate a fresh ID.
Use `runs` to recover an ID after losing a response, and `runs resume` to restore
cancelled work. Repeating a submission never clears cancellation.

## Backfill notes on an existing installation

Grant the dedicated Paperless account **View Notes** and **Add Notes**, keeping
its document View/Change access. Use the current bundled prompt, or merge the v2
note instructions into a custom prompt. A v1 metadata-only prompt produces no notes.

```sh
docker compose exec worker dotnet PaperlessLlm.dll reprocess --all --missing-summary --notes-only --preview
docker compose exec worker dotnet PaperlessLlm.dll reprocess --all --missing-summary --notes-only
```

`--missing-summary` reads current Paperless notes instead of trusting old saved
snapshots; unavailable notes are skipped. `--notes-only` validates the proposal,
then restricts application to an append-only summary and the usual `needs review`
marker when a note is actually added. It cannot change title, date, correspondent,
type, other tags, or OCR. Existing labeled summaries are preserved. Without
`--notes-only`, reprocessing can also update metadata, including human edits.
Try `--ids 1,2,3` for a small pilot and inspect the results in Paperless first.

Removing a note is not a queue signal. Changing `PPLLM_BACKFILL_LIMIT` after
initialization does not enroll history. For uncertain note writes, follow
[note recovery](review.md#document-notes); never discard pending intent or journals.

## Upgrade from v0.1.0

v0.1.0 generated read-only proposals. This release applies validated updates automatically. Stop the old worker and back up its volumes first. Update Compose and `.env`; the new `organizer-state` volume preserves the old state volume for reference. Legacy checkpoint files are rejected instead of silently reused.

Grant the dedicated Paperless account document-change permission and complete device login into the new Pi auth subdirectory. Run `check`, `probe`, and a bounded initial batch before leaving the worker unattended. Existing history is excluded by default. Keep old proposal evidence as long as you need it. Do not run `docker compose down -v` during an upgrade: it deletes deployment volumes.

For v0.3.0, see [upgrade and image channels](releases.md) and [editable prompts](prompts.md).
