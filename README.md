# Paperless LLM

A .NET 10 worker that reads Paperless-ngx documents, asks a ChatGPT model to review
OCR and metadata, and saves private reports for you to inspect. It never applies
changes to Paperless. Your receipt-tracking and review tags remain under your control.

The Docker image includes the worker and PDF renderer. It uses your authorized
ChatGPT plan through Sign in with ChatGPT; there is no separately billed API-key
fallback. Documents selected for review, their OCR and taxonomy are sent to OpenAI.

## Quick start

Requires Docker Engine with Compose v2, an accessible Paperless instance, and a
ChatGPT account that can authorize plan usage for this app.

1. Download [compose.yaml](compose.yaml) and [.env.example](.env.example) into an
   empty deployment directory. Rename `.env.example` to `.env` and set
   `PPLLM_PAPERLESS_URL` (for example, `https://paperless.example.com`).
2. Follow [authentication](docs/authentication.md) to create a **view-only
   Paperless token**, save it as `secrets/paperless_token.txt`, and sign in with ChatGPT.
3. Start the worker:

   ```sh
   docker compose pull
   docker compose up -d worker
   docker compose logs -f worker
   ```

The first run records a baseline. By default, only subsequently added documents
with the `needs review` tag are considered. To try a few existing documents, set
`PPLLM_BACKFILL_LIMIT=3` **before the first worker run**. See
[configuration and enrollment](docs/operations.md#document-enrollment).

Reports contain the source OCR, proposed changes, visual evidence, and validation
results. See [reviewing accuracy](docs/review.md) for copying and inspecting them.
No proposed title, OCR, correspondent, document type or tag is written back.

## Documentation

- [Authentication](docs/authentication.md): Paperless permissions, ChatGPT sign-in,
  remote-server setup, refresh and revocation.
- [Operations](docs/operations.md): settings, enrollment, logs, backups and upgrades.
- [Reviewing accuracy](docs/review.md): private audit reports and their limitations.
- [Architecture and development](docs/architecture.md): code map, tests and data flow.
- [Paperless connector](docs/connector.md): supported inputs and read-only boundaries.
- [Release procedure](.agents/skills/release/SKILL.md): protected main, CI and GHCR.

## Release scope

This is an initial read-only release. Human review is required to judge suggestions.
Model confidence is not a measured accuracy score. Unsupported or oversized
inputs are reported as failures; the worker does not silently review a truncated
scan. There is no automatic writeback, duplicate deletion, receipt import or
background modification of your existing Paperless workflows.

ChatGPT plan authorization and available models depend on your account and the
[Sign in with ChatGPT preview](https://developers.openai.com/siwc/token-sharing-open-source/preview-limitations).
Revocation or an expired refresh session requires sign-in again.
