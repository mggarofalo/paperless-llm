# Operation

[Home](../README.md) · [Authentication](authentication.md) · [Review](review.md)

## Discovery and jobs

The worker runs one bounded cycle on startup, then waits the configured interval after each cycle. A filesystem lock allows one worker per state directory. Processing is serial in this release.

The first cycle records the highest visible document ID as its baseline. Newer documents are enrolled regardless of tags. A rotating discovery scan catches documents above that baseline which become visible later. Documents at or below the baseline are only included by the optional bounded initial backfill. Changing backfill after initialization does not enroll more history.

Jobs are JSON files under `/data/state/jobs`, with durable status fields, attempts, source metadata, proposal, timing and outcome. Files are replaced atomically. A completed document is not automatically rerun after a metadata or policy change; use explicit `reprocess` when needed. Clearing `needs review` is never a discovery signal.

Once a valid proposal has been saved, sync retries reuse it. A crash before that save may repeat inference. The [operation journal](review.md) reconciles an ambiguous write before allowing another PATCH.

## Configuration

| Variable | Default | Meaning |
| --- | --- | --- |
| `PPLLM_PAPERLESS_URL` | required | Paperless instance URL |
| `PPLLM_MODEL` | `gpt-6-sol` | Explicit model; no fallback |
| `PPLLM_PROMPT_FILE` | `/app/prompts/organization.txt` | UTF-8 policy file, reloaded per inference; see [prompt configuration](prompts.md) |
| `PPLLM_TAG` | `needs review` | Existing marker added after actual changes |
| `PPLLM_POLL_SECONDS` | `3600` | Delay between cycles, 60–86400 seconds |
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

Jobs get up to three attempts, with exponential delay serviced on subsequent cycles. Authorization failures and provider rate limits pause the batch without consuming an attempt. Rate limits are retried at the next poll. Source conflicts become failed jobs. Invalid output can trigger another bounded inference attempt.

Stop the worker before manual commands so they can acquire its state lock:

```sh
docker compose stop worker
docker compose run --rm worker retry 123
docker compose run --rm worker once
docker compose up -d worker
```

`retry 123` reuses saved intent. Use `reprocess 123` instead to create a new job identity and infer from current source state. Reprocessing is deliberate: it can propose changes to fields a person previously edited. Both commands require an already enrolled document.

Private state contains document metadata, prompts and results. Older versions may also have retained originals and rendered pages. The soft size cap may be exceeded by one in-flight attempt; the operation journal is in the separate audit volume. There is no automatic evidence-retention policy yet. Monitor both volumes and available disk space, keep encrypted backups, and do not delete pending jobs or journals. Docker console logs rotate at three 10 MiB files in the sample Compose.

This initial filesystem implementation is intended for a personal library. It retains at most 10,000 enrolled jobs; reaching that cap pauses processing until capacity is addressed in a later version. Status and polls load full job records, so memory use grows with retained OCR. Do not discard completed-job indexes to reclaim space: they prevent duplicate processing. Capacity errors are exposed through the health pause reason.

## Upgrade from v0.1.0

v0.1.0 generated read-only proposals. This release applies validated updates automatically. Stop the old worker and back up its volumes first. Update Compose and `.env`; the new `organizer-state` volume preserves the old state volume for reference. Legacy checkpoint files are rejected instead of silently reused.

Grant the dedicated Paperless account document-change permission and complete device login into the new Pi auth subdirectory. Run `check`, `probe`, and a bounded initial batch before leaving the worker unattended. Existing history is excluded by default. Keep old proposal evidence as long as you need it. Do not run `docker compose down -v` during an upgrade: it deletes deployment volumes.

For v0.1.3, see the [upgrade and editable prompt guide](prompts.md#upgrade-from-v012).