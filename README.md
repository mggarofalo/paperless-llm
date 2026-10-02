# Paperless LLM

A .NET 10 worker that checks Paperless for new documents, asks Sol 6 low to infer organization from existing OCR, and applies validated changes automatically. Changed documents receive `needs review` so you can inspect them later in Paperless. There is no approval queue before updates.

.NET owns discovery, durable jobs, validation and sync. A small JavaScript bridge uses the pinned Pi provider SDK for ChatGPT device login and inference, without an agent session or tool executor. Existing OCR, document metadata and visible taxonomy are sent to OpenAI through your ChatGPT subscription; there is no API-key fallback.

## Quick start

Download `compose.yaml` and `env.example` from the [release](https://github.com/mggarofalo/paperless-llm/releases), save the latter as `.env`, and set your Paperless URL. Follow [authentication](docs/authentication.md) to create the scoped Paperless token and approve the ChatGPT device code.

```sh
docker compose run --rm worker check
docker compose --profile setup run --rm auth probe
docker compose up -d worker
docker compose logs -f worker
```

The default interval is one hour; set `PPLLM_POLL_SECONDS=10800` for three hours. The first run records a baseline and processes newer documents. Existing documents require a bounded initial backfill. **Updates are enabled by default**; see [configuration and migration](docs/operations.md) for dry-run mode and upgrading from v0.1.0.

The worker can change titles, document dates, existing correspondents and types, add descriptive tags, while keeping searchable OCR unchanged. It preserves originals, permissions, ownership and workflow tags such as receipt-tracker `inbox`. It cannot delete documents, remove tags, create taxonomy or import receipts.

## Guides

- [Authentication](docs/authentication.md): Paperless permissions, device login, persistent credentials and renewal.
- [Prompts](docs/prompts.md): editable policy files, audit hashes and v0.1.3 upgrade instructions.
- [Operation](docs/operations.md): discovery, jobs, retries, logs, health, storage and migration.
- [Review and recovery](docs/review.md): `needs review`, Paperless history and sync records.
- [Architecture](docs/architecture.md): boundaries, runner choice and contributor commands.
- [Paperless connector](docs/connector.md): API and rendering limits.
- [Offline evaluation](docs/offline-evaluation.md): private corpora, prompt experiments, and measured accuracy limitations.
- [Release procedure](.agents/skills/release/SKILL.md): protected main, CI and GHCR publication.

Synthetic tests cover validation, retries and sync recovery. They do not establish live ChatGPT entitlement, renewal or OCR accuracy. Those require the deployment acceptance checks in the authentication guide. Sync rechecks the source immediately before writing, but the GET and PATCH are not atomic; a human edit in that interval can race with the update.
