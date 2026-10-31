# ChatGPT, Claude and Paperless authentication

[Home](../README.md) · [Operation](operations.md) · [Architecture](architecture.md)

Two credentials are needed: a Paperless API token for reading and updating documents, and subscription credentials for inference. `PPLLM_HARNESS` chooses the inference harness:

| `PPLLM_HARNESS` | Account | Sign-in | Credential owner | Auth volume path |
| --- | --- | --- | --- | --- |
| `codex` (default) | ChatGPT | OpenAI device code | Pinned Pi SDK `openai-codex` provider | `/data/auth/pi` |
| `claude` | Claude Pro, Max, Ultra, Team or Enterprise | Claude Code link and pasted code | Pinned, unmodified Claude Code CLI | `/data/auth/claude` |

Each harness keeps its own credentials, so switching back does not lose the other login. Paperless and inference credentials have separate storage and revocation.

## Paperless account

Create a dedicated non-admin account. Grant view access to documents, tags, correspondents and document types, plus **change access to documents**. Ensure object permissions cover the intended current and future documents. Do not grant document deletion or taxonomy creation. The existing `needs review` tag must be visible to this account.

The permissions checkboxes on the user-account screen grant global capabilities. They do not necessarily grant access to objects owned by another user. Share the existing review tag and other taxonomy with the service account (View), and share intended documents with it (View and Change). Configure upload/workflow permissions for future documents too. If `check` reports `review_tag_not_visible`, fix access to the existing tag instead of creating a second tag or making the service account a superuser.

Create its API token using Paperless and save only the token in `secrets/paperless_token.txt`, beside Compose. The container reads that file as a Docker secret. On a Linux Docker host, make it readable by the image's UID 1654:

```sh
sudo chown 1654:1654 secrets/paperless_token.txt
sudo chmod 600 secrets/paperless_token.txt
```

Set `PPLLM_PAPERLESS_URL` in `.env`, using your instance's HTTPS URL. The token is never sent to the model process.

For the new document-notes feature, also grant **Add Notes** and **View Notes**
global permissions. Keep document View/Change object permissions. Note deletion
and editing are not required. Paperless enforces `documents.add_note` on the
[Notes POST endpoint](https://github.com/paperless-ngx/paperless-ngx/blob/v2.20.13/src/documents/permissions.py).
`check` is read-only and does not prove note creation permission. A denied note
write can leave the metadata/review marker applied; inspect the private journal
and use the [note recovery procedure](review.md#document-notes) after fixing permissions.

## Codex harness: Sign In With ChatGPT device-code login

Run on the machine hosting Docker:

```sh
docker compose pull
docker compose stop worker
docker compose --profile setup run --rm auth
```

The command prints an OpenAI device-approval URL and a short code. Open that URL on any computer or phone, sign in with ChatGPT and approve the code. Keep the command running until it confirms completion. If your account requires it, enable device-code authentication in ChatGPT security settings first.

No callback listener, published port, DNS entry, reverse proxy or SSH tunnel is needed. The pinned Pi SDK's `openai-codex` provider owns the device-code exchange, credential storage and refresh. This uses the Codex subscription authentication flow; the project does not register a separate “Sign in with ChatGPT” application or extract tokens into .NET. See [OpenAI's headless authentication documentation](https://learn.chatgpt.com/docs/auth#login-on-headless-devices) and the [Pi source](https://github.com/badlogic/pi-mono).

Credentials live under `/data/auth/pi` in Compose's persistent `auth` volume. Keep that volume private. Use one worker per auth volume; stop the worker before login or logout, and do not run cloned copies of rotating credentials concurrently.

## Claude harness: Claude Code login

Set `PPLLM_HARNESS=claude` in `.env`, then run on the machine hosting Docker:

```sh
docker compose stop worker
docker compose --profile setup run --rm auth
```

Claude Code prints a claude.com sign-in link. Open it on any computer or phone, sign in with your Claude account and approve. Paste the code the browser shows into the waiting terminal. `docker compose run` provides the interactive terminal this needs.

The project never handles the login exchange or tokens. The bundled Claude Code CLI performs its own subscription login (`claude auth login --claudeai`) and stores and refreshes its credentials under `/data/auth/claude`. Anthropic's [legal and compliance terms](https://code.claude.com/docs/en/legal-and-compliance) permit an end user signing in to the unmodified Claude Code binary with their own subscription. They do not permit subscription credentials in third-party clients or the Agent SDK, which is why this harness runs the published CLI instead of Pi's Anthropic provider.

The bridge accepts only a Claude subscription login. `auth status` reports `SignedIn: false` for an API-key Console login, and provider API keys never reach the CLI. Usage counts against your plan's limits, which Anthropic sizes for ordinary individual use. Keep batches small; a rate or usage limit pauses the worker without consuming job attempts.

Each inference is one `claude -p` run with no built-in tools, MCP servers, settings sources, skills or saved sessions. The prompt arrives on stdin, and the output must match the intent schema through Claude Code's structured output. The bridge rejects the run if the CLI exposes any other tool, an MCP server, a different or fallback model, or no structured result. See [architecture](architecture.md#boundaries).

## Verify the deployment

```sh
docker compose --profile setup run --rm auth auth status
docker compose --profile setup run --rm auth models
docker compose --profile setup run --rm auth probe
docker compose run --rm worker check
```

`auth status` checks for saved subscription credentials for the configured harness. `models` lists the harness catalog (Pi's for codex, a list pinned with the Claude Code version for claude), which does not prove your account can use each model. `probe` makes a real request with a synthetic one-pixel image and validates the JSON reply; this checks transport and entitlement, not OCR quality. `check` verifies visible Paperless taxonomy, the review tag, document listing and saved auth. It does not write a document or prove change permission.

Run `docker compose run --rm worker probe organization` to check the configured text policy and taxonomy-name resolution using a synthetic receipt. See [prompt configuration](prompts.md). After these pass, start the worker and inspect a small initial batch and its Paperless history. Confirm OCR, metadata, preserved workflow tags and the review marker. Also verify operation after a token refresh and container restart. Live grant, refresh and model accuracy are deployment acceptance checks; they are not covered by the synthetic CI tests.

`worker` and `once` check saved authorization, catalog availability and review-tag visibility before initializing discovery. This does not substitute for the live `probe`. In v0.1.1 the probe image had a bad PNG checksum and inference errors were collapsed to `runner_request_failed`; upgrade to [v0.2.0](releases.md) before diagnosing that result. A previous `completed 0, failed 0` may only mean that a baseline was recorded without processing any documents.

Probe errors now preserve fixed categories: `runner_image_rejected`, `runner_model_access_denied`, `runner_access_denied`, `runner_transport_failed`, `runner_provider_unavailable` and `runner_request_rejected`. Unknown provider failures remain `runner_inference_failed`. Raw provider errors and credentials are never printed. Report the category if the corrected probe still fails; do not paste auth files.

## Renewal and revocation

The harness (Pi or Claude Code) refreshes credentials in the same volume when needed. Missing, expired or revoked authorization pauses processing without consuming job attempts. Repeat the device-login command, then restart the worker to resume.

```sh
docker compose stop worker
docker compose --profile setup run --rm auth auth logout
```

Logout removes this deployment's local credentials for the configured harness. Use ChatGPT or Claude account settings to revoke access remotely, and revoke the Paperless token separately in Paperless. Keep backups encrypted and avoid restoring an old credential copy while another worker is running.
