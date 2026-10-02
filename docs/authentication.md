# Authentication

[Home](../README.md) · [Operation](operations.md) · [Architecture](architecture.md)

Two credentials are needed: a Paperless API token for reading and updating documents, and ChatGPT OAuth credentials for inference. They have separate storage and revocation.

## Paperless account

Create a dedicated non-admin account. Grant view access to documents, tags, correspondents and document types, plus **change access to documents**. Ensure object permissions cover the intended current and future documents. Do not grant document deletion or taxonomy creation. The existing `needs review` tag must be visible to this account.

The permissions checkboxes on the user-account screen grant global capabilities. They do not necessarily grant access to objects owned by another user. Share the existing review tag and other taxonomy with the service account (View), and share intended documents with it (View and Change). Configure upload/workflow permissions for future documents too. If `check` reports `review_tag_not_visible`, fix access to the existing tag instead of creating a second tag or making the service account a superuser.

Create its API token using Paperless and save only the token in `secrets/paperless_token.txt`, beside Compose. The container reads that file as a Docker secret. On a Linux Docker host, make it readable by the image's UID 1654:

```sh
sudo chown 1654:1654 secrets/paperless_token.txt
sudo chmod 600 secrets/paperless_token.txt
```

Set `PPLLM_PAPERLESS_URL` in `.env`, using your instance's HTTPS URL. The token is never sent to the model process.

## ChatGPT device login

Run on the machine hosting Docker:

```sh
docker compose pull
docker compose stop worker
docker compose --profile setup run --rm auth
```

The command prints an OpenAI device-approval URL and a short code. Open that URL on any computer or phone, sign in with ChatGPT and approve the code. Keep the command running until it confirms completion. If your account requires it, enable device-code authentication in ChatGPT security settings first.

No callback listener, published port, DNS entry, reverse proxy or SSH tunnel is needed. The pinned Pi SDK's `openai-codex` provider owns the device-code exchange, credential storage and refresh. This uses the Codex subscription authentication flow; the project does not register a separate “Sign in with ChatGPT” application or extract tokens into .NET. See [OpenAI's authentication documentation](https://developers.openai.com/codex/auth/) and the [Pi source](https://github.com/badlogic/pi-mono).

Credentials live under `/data/auth/pi` in Compose's persistent `auth` volume. Keep that volume private. Use one worker per auth volume; stop the worker before login or logout, and do not run cloned copies of rotating credentials concurrently.

## Verify the deployment

```sh
docker compose --profile setup run --rm auth auth status
docker compose --profile setup run --rm auth models
docker compose --profile setup run --rm auth probe
docker compose run --rm worker check
```

`auth status` checks for saved OAuth credentials. `models` lists the SDK catalog, which does not prove your account can use each model. `probe` makes a real request with a synthetic one-pixel image and validates the JSON reply; this checks transport and entitlement, not OCR quality. `check` verifies visible Paperless taxonomy, the review tag, document listing and saved auth. It does not write a document or prove change permission.

Run `docker compose run --rm worker probe organization` to check the configured text policy and taxonomy-name resolution using a synthetic receipt. See [prompt configuration](prompts.md). After these pass, start the worker and inspect a small initial batch and its Paperless history. Confirm OCR, metadata, preserved workflow tags and the review marker. Also verify operation after a token refresh and container restart. Live grant, refresh and model accuracy are deployment acceptance checks; they are not covered by the synthetic CI tests.

`worker` and `once` check saved authorization, catalog availability and review-tag visibility before initializing discovery. This does not substitute for the live `probe`. In v0.1.1 the probe image had a bad PNG checksum and inference errors were collapsed to `runner_request_failed`; upgrade to v0.1.3 before diagnosing that result. A previous `completed 0, failed 0` may only mean that a baseline was recorded without processing any documents.

Probe errors now preserve fixed categories: `runner_image_rejected`, `runner_model_access_denied`, `runner_access_denied`, `runner_transport_failed`, `runner_provider_unavailable` and `runner_request_rejected`. Unknown provider failures remain `runner_inference_failed`. Raw provider errors and credentials are never printed. Report the category if the corrected probe still fails; do not paste auth files.

## Renewal and revocation

The SDK refreshes credentials in the same volume when needed. Missing, expired or revoked authorization pauses processing without consuming job attempts. Repeat the device-login command, then restart the worker to resume.

```sh
docker compose stop worker
docker compose --profile setup run --rm auth auth logout
```

Logout removes this deployment's local credentials. Use ChatGPT account settings to revoke access remotely, and revoke the Paperless token separately in Paperless. Keep backups encrypted and avoid restoring an old credential copy while another worker is running.
