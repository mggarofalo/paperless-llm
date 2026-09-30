# Operations

[Home](../README.md) · [Authentication](authentication.md) · [Accuracy reports](review.md)

## Document enrollment

The worker polls Paperless; no webhook, message broker or Paperless workflow
change is required. The `needs review` tag selects candidates. Paperless does not
store job status, and the worker never adds or removes a tag to mark completion.

On its first run, the worker persists the highest visible document ID as its
baseline. With `PPLLM_BACKFILL_LIMIT=0`, documents at or below that baseline stay
out of scope. Newer eligible documents become durable local jobs. Discovery
revisits the eligible set, so a new document tagged after an earlier poll is still
found. Removing the tag before processing makes the job ineligible.

To review a small existing sample, set `PPLLM_BACKFILL_LIMIT` before the first run.
That many existing eligible document IDs are saved for backfill. Increasing this
value after enrollment does not silently expand the original baseline. Keep the
state volume: deleting it loses deduplication and changes enrollment behavior.

A job records the document ID, source revision and review-policy fingerprint.
Unchanged completed work is skipped. A changed source or model/policy can require
a fresh review for enrolled documents. The worker rechecks the source after
inference and rejects stale results. It does not promise exactly-once model
billing: a crash after an OpenAI response but before local completion can require
a repeated request.

## Observe jobs

```sh
docker compose logs --tail 100 -f worker
docker compose exec worker dotnet PaperlessLlm.dll status
```

Logs are structured JSON with document IDs, outcomes and retry/authentication
signals. They exclude source text, model responses and credentials. Compose keeps
three 10 MB log files. The status command reads saved state and reports job counts,
individual states and failure codes. It does not call Paperless or OpenAI.

`docker compose ps` also shows a health check based on a recent poll and the
absence of a global pause. This is a liveness signal, not a quality score:
individual failed jobs still need inspection through `status`.

The job lifecycle is `Pending → Processing → Ready`. `Ready` means a proposal is
saved for inspection, not that it is correct or applied. Other states are
`RetryWaiting`, `Failed`, `AuthPaused` and `NotEligible`. Interrupted processing is
recoverable. Transient failures use delayed retries; after three attempts a job
is failed instead of retried forever. Authentication failures pause model work
without consuming the normal retry budget.

After fixing a terminal failure, explicitly requeue that document locally:

```sh
docker compose stop worker
docker compose run --rm worker retry 123
docker compose up -d worker
```

Replace `123` with the document ID shown by status. This preserves the enrollment
baseline and does not edit Paperless. It only accepts jobs in `Failed` state.

The private [accuracy reports](review.md) contain the evidence behind each
completed or rejected proposal. Keep Docker logs for operational diagnosis and
reports for judging model quality.

## Configuration

Copy [.env.example](../.env.example) to `.env`. Recreate the service after changes
with `docker compose up -d worker`.

| Variable | Default | Meaning |
| --- | --- | --- |
| `PPLLM_PAPERLESS_URL` | required | Instance URL, including a deployment subpath if needed |
| `PPLLM_PAPERLESS_TOKEN_FILE` | `/run/secrets/paperless_token` in Compose | Dedicated view-only token file |
| `PPLLM_MODEL` | `gpt-6-luna` | Available model slug; `gpt-6-sol` is another supported choice |
| `PPLLM_TAG` | `needs review` | Eligibility tag; must exist and be visible |
| `PPLLM_BATCH_SIZE` | `5` | Maximum jobs processed per poll, 1–100 |
| `PPLLM_POLL_SECONDS` | `300` | Delay between polling cycles, 10–86400 |
| `PPLLM_BACKFILL_LIMIT` | `0` | Initial existing-document enrollment, 0–100 |
| `PPLLM_MAX_AUDIT_MIB` | `2048` | Audit storage cap; pause rather than delete evidence |
| `PPLLM_AUTH_DIRECTORY` | `/data/auth` | Protected renewable ChatGPT credentials |
| `PPLLM_STATE_DIRECTORY` | `/data/state` | Durable jobs and enrollment baseline |
| `PPLLM_AUDIT_DIRECTORY` | `/data/audit` | Private reports and retained source evidence |

The sample Compose exposes common settings. Add an optional variable to its
`environment` section to override it. Use one worker per state/auth volume;
horizontal scaling is not supported in this release.

## Storage, recovery and upgrades

The image runs as non-root with a read-only root filesystem. Only the auth, state
and audit volumes and temporary rendering directory are writable. It uses no
public web interface. Original documents and rendered pages remain in audit
storage so reports can be checked later.

The audit limit is checked before each attempt and can be exceeded by that final
attempt; leave free space for downloading, rendering and report copies. The local
queue is capped at 10,000 jobs in this release and pauses visibly at capacity.

Back up all three volumes together while the worker is stopped. Protect backups
as sensitive data. At the audit cap, archive or remove selected old audit folders
after review, or raise the cap. Reports are not automatically deleted. The state
volume remains necessary even when older reports are archived.

For a new release, update both image references through the shared `x-runtime`
image in [compose.yaml](../compose.yaml), then:

```sh
docker compose pull
docker compose up -d worker
docker compose exec worker dotnet PaperlessLlm.dll --version
```

Stable images use `ghcr.io/mggarofalo/paperless-llm:vX.Y.Z`. Each GitHub release
also includes `image-digest.txt` for an immutable image reference. Named volumes
survive normal recreation and `docker compose down`; **`down -v` deletes them**.

If authorization is rejected, stop the worker and repeat the
[sign-in procedure](authentication.md). For Paperless 401/403 errors, check the
dedicated token, global view permissions and object-level visibility. A running
container alone does not prove that jobs are progressing: inspect status and logs.
