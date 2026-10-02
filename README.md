# Paperless LLM — AI document organization for Paperless-ngx

A self-hosted **.NET worker for Paperless-ngx** that uses existing OCR to organize document dates, correspondents, document types and tags. Run it with Docker Compose on Linux, sign in with ChatGPT, and let it process new documents on a schedule. Validated changes apply automatically; changed documents receive `needs review` for retrospective inspection in Paperless.

## Sign In With ChatGPT

Paperless LLM implements **Sign In With ChatGPT using a device-code flow** through the pinned Pi SDK's `openai-codex` provider. Start login on your server, open the printed link on any browser, and approve the code. Credentials persist in a Docker volume. No localhost callback, published port or SSH tunnel is needed.

This is an independent integration, not an OpenAI product or certification. Model access and limits depend on your account. There is no silent API-key billing fallback. See [authentication and data flow](docs/authentication.md).

## Quick start

Download `compose.yaml` and `env.example` from the [latest stable release](https://github.com/mggarofalo/paperless-llm/releases/latest). Save `env.example` as `.env`, set your Paperless URL, and put a dedicated Paperless view/change token in `secrets/paperless_token.txt`. The [setup guide](docs/authentication.md) explains global and object permissions.

```sh
docker compose --profile setup run --rm auth login
docker compose run --rm worker check
docker compose run --rm worker probe organization
docker compose up -d worker
docker compose logs -f worker
```

The default is **Sol 6 low**, one call per document and five jobs per hourly cycle. The first run records a baseline; historical backfill is opt-in and bounded. **Writes are enabled by default.** Use the [dry-run and upgrade guide](docs/operations.md) to control enrollment and rollout. Linux `amd64` and `arm64` images are available.

## What it does

- Uses Paperless OCR as evidence; the worker does not replace OCR or scan files.
- Selects exact existing taxonomy names; .NET resolves IDs and validates changes.
- Preserves workflow tags, ownership, permissions and original files. It cannot delete documents, remove tags, create taxonomy or import receipts.
- Loads an [editable prompt file](docs/prompts.md) before each new inference; policy changes require no image rebuild or restart. The default keeps titles.
- Keeps durable jobs, prompt hashes and private before/after journals for [review and recovery](docs/review.md). Saved proposals survive sync retries.

OCR, document metadata and visible taxonomy are sent to the selected model through your ChatGPT subscription. The runner receives no Paperless token or write tools. Validation checks structure and permitted operations; it cannot prove factual accuracy. Read the [evaluation findings](docs/evaluations/2026-10-02-named-organization.md) and inspect early results in your own library.

## Documentation

Start at the [documentation index](docs/index.md). Operators get setup, configuration, OCR tuning and troubleshooting guides; coding agents get [AGENTS.md](AGENTS.md), architecture, quality commands and the [release skill](.agents/skills/release/SKILL.md).

- [Image versions and `latest` / `stable` channels](docs/releases.md)
- [Paperless OCR configuration recommendations](docs/ocr.md)
- [Architecture and contributing](docs/architecture.md)
- [Roadmap](docs/roadmap.md): local OpenAI-compatible models and a run-review UI with SQLite-backed settings are planned, not shipped.
- [Current delivery checklist](TODO.md) and [documentation strategy](docs/documentation-strategy.md)
