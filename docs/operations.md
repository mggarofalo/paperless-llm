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
| `PPLLM_IMAGE` | Version pinned in Compose | Compose-only image override; see [release channels](releases.md) |
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

Console JSON retains the rendered `Message` and typed `State` fields, but omits
the redundant `{OriginalFormat}` message template. Timestamps are UTC.

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

## Backfill notes on an existing installation

Run these Bash commands **on the Docker host, in your existing Compose directory**.
They assume the service is named `worker`, and use `curl`, `sha256sum` and `jq`.
Preserve your Compose project name, token and auth/state/audit volumes. Back up
Paperless and the worker volumes before upgrading; do not delete state or use
`docker compose down -v`.

### Upgrade and check permissions

Stop the worker and set `PPLLM_IMAGE=ghcr.io/mggarofalo/paperless-llm:v0.3.0`
in your existing `.env`. In Paperless, grant its dedicated service account
**Add Notes** and **View Notes**, keeping document View/Change access.

```sh
docker compose stop worker
umask 077
mkdir -p notes-release-0.3.0
for file in compose.yaml env.example organization.txt image-digest.txt SHA256SUMS; do
  curl -fL "https://github.com/mggarofalo/paperless-llm/releases/download/v0.3.0/$file" \
    -o "notes-release-0.3.0/$file" || break
done
(cd notes-release-0.3.0 && sha256sum --check SHA256SUMS)
```

Continue only if every checksum passes. Merge the release Compose asset with
your installation's overrides; do not replace your `.env` with `env.example`.
If you use the bundled prompt, pulling the image updates it. If you mount a custom
prompt, back it up and merge the release `organization.txt`, including its v2
template and long-term note instructions. An old prompt can still return v1
metadata-only proposals, producing no notes. Keep the worker stopped while editing.

```sh
docker compose pull worker
docker compose run --rm -T worker --version
docker compose run --rm -T worker check
docker compose run --rm -T worker probe organization
docker compose run --rm -T worker status > notes-status.json
```

Version must be `0.3.0`. `check` does not prove Add Notes permission, and the
synthetic probe does not measure note accuracy. Resolve any pending/failed old
writes before the backfill; do not discard their saved intents or journals.
This command lists non-completed jobs without OCR or document titles:

```sh
jq '.Jobs[] | select(.State != 3) | {DocumentId, State, ErrorCode, Outcome}' notes-status.json
```

### Queue a small pilot, then bounded batches

Take one snapshot of already-completed job IDs (`State == 3`) and inspect it:

```sh
jq -r '.Jobs | map(select(.State == 3)) | sort_by(.DocumentId) | .[].DocumentId' \
  notes-status.json > notes-remaining.ids
head -n 10 notes-remaining.ids
wc -l notes-remaining.ids
```

You may edit the file to select specific completed documents. These are
candidates, not a verified list of documents missing notes: existing generated
summaries will be skipped. Human notes do not prevent adding the first summary.
Do not regenerate this list between batches, or you will include documents
already processed by the backfill.

Define this helper in the same shell. It queues at most 50 IDs per invocation and
removes each successfully queued ID from the remaining list. The worker must
remain stopped while queueing. If interrupted or a command fails, inspect status
before continuing; never reprocess an unresolved write merely to get past it.

```sh
queue_notes_batch() {
  local count="$1" id i
  case "$count" in ''|*[!0-9]*) return 1 ;; esac
  [ "$count" -ge 1 ] && [ "$count" -le 50 ] || return 1
  for ((i=0; i<count; i++)); do
    IFS= read -r id < notes-remaining.ids || break
    case "$id" in ''|*[!0-9]*) echo 'Invalid document ID'; return 1 ;; esac
    docker compose run --rm -T worker reprocess "$id" || return 1
    tail -n +2 notes-remaining.ids > notes-remaining.ids.next || return 1
    mv notes-remaining.ids.next notes-remaining.ids || return 1
  done
}

queue_notes_batch 3
docker compose run --rm -T -e PPLLM_DRY_RUN=false -e PPLLM_BATCH_SIZE=3 worker once
docker compose run --rm -T worker status
```

`once` discovers new documents too, and selects eligible queued jobs; its batch
size is not a filter for just the three IDs. Verify the intended jobs completed,
then inspect their Notes and `needs review` entries in Paperless. Check amounts,
dates, warranty qualifications and factual wording against the document. A
`no_change` or skipped note can be a valid result. Reprocessing runs the normal
organizer and **can also change metadata**; v0.3.0 has no notes-only command.

When satisfied, queue up to 50 more and resume the normal worker. Ensure `.env`
has `PPLLM_DRY_RUN=false` for the background service; the one-off override above
does not persist. The default is five jobs per cycle, one cycle on startup and
then hourly. Leave those bounds unchanged initially and inspect results before
queueing another batch.

```sh
queue_notes_batch 50
docker compose up -d worker
docker compose logs -f worker
# Later, before queueing the next batch:
docker compose stop worker
docker compose run --rm -T worker status
# After reviewing the prior batch, repeat queue_notes_batch 50 and up -d.
```

`retry ID` reuses an old proposal and will not add notes to a saved v1 proposal;
`reprocess ID` requests fresh inference. Changing `PPLLM_BACKFILL_LIMIT` after
initialization does not enroll history. IDs absent from `status` are not enrolled
and cannot be queued with `reprocess` in v0.3.0; arbitrary historical enrollment
remains PPLLM-32. Do not reset the state volume to work around this limitation.
For a failed note POST, follow [note recovery](review.md#document-notes).

## Upgrade from v0.1.0

v0.1.0 generated read-only proposals. This release applies validated updates automatically. Stop the old worker and back up its volumes first. Update Compose and `.env`; the new `organizer-state` volume preserves the old state volume for reference. Legacy checkpoint files are rejected instead of silently reused.

Grant the dedicated Paperless account document-change permission and complete device login into the new Pi auth subdirectory. Run `check`, `probe`, and a bounded initial batch before leaving the worker unattended. Existing history is excluded by default. Keep old proposal evidence as long as you need it. Do not run `docker compose down -v` during an upgrade: it deletes deployment volumes.

For v0.3.0, see [upgrade and image channels](releases.md) and [editable prompts](prompts.md).
