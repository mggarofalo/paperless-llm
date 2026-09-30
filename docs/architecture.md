# Subscription inference decision (PPLLM-2)

Decision, 2026-09-30: use a dedicated Sign in with ChatGPT grant and a tool-free
Responses request for production document analysis. This milestone implements
only a synthetic Codex subscription probe. It does not send private scans or OCR
to a model, and it does not implement the dedicated OAuth grant yet.

## Verified locally

Codex CLI 0.159.2 reported `Logged in using ChatGPT`. Its refreshed model catalog
included `gpt-6-luna` and `gpt-6-sol` with text and image input. Catalog presence
alone was not treated as proof of access.

Both exact models completed a live synthetic image test on 2026-09-30. An image
printed `SYNTHETIC RECEIPT` and `TOTAL 12.34`; each returned the schema-constrained
JSON `{"total":"12.34"}`. Their event streams contained no tool calls. Approximate
wall times were 3.8 seconds for Luna and 6.0 seconds for Sol, one sample each;
these are connectivity evidence, not an OCR quality or performance benchmark.
No private document or credential was included. No separately billed API fallback
was used. Access and limits may change.

`CodexClient.probe(model)` repeats the test with a programmatically generated
bitmap saying `TOTAL 12.34`. It accepts no prompt or image path, requires an
explicit model, checks ChatGPT login, removes inherited API-key variables, uses
an ephemeral read-only run in a temporary directory, and rejects incomplete,
unexpected, incorrect, or tool-using transcripts. Raw child output is not exposed.
Unit tests never make model requests.

## Why Codex exec is not the document backend

The installed CLI supports images and structured output but not a verified
universal tool-denial configuration. A strict-config trial rejected
`tools.disable_defaults=true`. Disabling shell, apps, plugins, hooks, browser,
computer, image generation, and subagents reduces the synthetic probe's surface;
it is not proof that every tool or inherited policy is absent. A read-only
sandbox also does not by itself prevent information disclosure. Post-hoc event
inspection cannot undo a tool call.

Consequently there is deliberately no `analyze(prompt, images)` method here.
Never adapt the synthetic probe to accept document content. Treat all text on a
scan as untrusted data, including apparent instructions to invoke tools or alter
tags. Production model requests should offer no tools and return proposals only.

## Dedicated sign-in implementation still required

Use the official [registration flow](https://developers.openai.com/siwc/token-sharing-open-source/sign-in).
Persist a host identifier; generate fresh state, nonce, and PKCE S256 values.
Start a loopback callback listener before opening authorization. Register the app
by its actual name with `dynamic_agent_client`, then retain the issued client ID.
Exchange the code with that issued ID and the identical callback URI. Validate
the ID token cryptographically and confirm the granted plan-usage scope before
inference. Store and refresh tokens in protected per-account credentials; do not
extract or repurpose Codex's native credentials. Logout/revocation and renewed
consent must be explicit, with no API-key fallback.

The [models and inference guide](https://developers.openai.com/siwc/token-sharing-open-source/models-and-inference)
defines public `/v1/models` discovery and `/v1/responses` inference. Select an
account-visible exact model slug. Stream requests with storage disabled, omit
tools, and accept results only after completed inference. A stream that emits
text before reporting a usage limit, failure, or interruption must fail without
applying a proposal. The direct route has not been live-tested for this app.

Follow the [preview constraints](https://developers.openai.com/siwc/token-sharing-open-source/preview-limitations):
send array inputs and developer instructions, use image inputs where supported,
and omit unsupported generation parameters. Do not use private ChatGPT backend
endpoints. Before deploying the worker, test scope rejection, token refresh,
revocation, plan exhaustion, partial streams, model unavailability, and schema
rejection. Record stable model/prompt versions with proposals.

## Pipeline boundary

The authenticated Paperless CLI supplies immutable local snapshots. A future
renderer and tool-free model adapter produce candidate OCR and metadata. A
separate deterministic validator checks evidence, allowed taxonomy, and protected
status tags. Human review and a future conflict-aware apply/rollback step sit
outside the model. Receipt handoff and background mutation remain later milestones.
