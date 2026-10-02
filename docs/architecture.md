# Architecture

[Home](../README.md) · [Operation](operations.md) · [Authentication](authentication.md) · [Connector](connector.md)

```mermaid
flowchart LR
  P[Paperless API] --> D[Scheduled discovery]
  D --> J[Durable filesystem jobs]
  J --> C[Existing OCR and current taxonomy]
  C --> L[Pi provider SDK / Sol 6 low]
  L --> V[.NET intent validation]
  V --> S[Journal and minimal PATCH]
  S --> R[Readback verification]
  R --> H[Paperless needs review and history]
```

## Boundaries

.NET owns scheduling, enrollment, durable state, prompt loading, context construction, validation, retries, Paperless writes and operational records. The inference process receives only its dedicated OAuth home, model request and a minimal environment. It does not receive the Paperless token or access to the parent process's personal configuration.

The small JavaScript bridge uses **Pi 0.99.2 ModelRuntime**, `openai-codex`, `gpt-6-sol`, low reasoning and a 16,000-token output bound. It invokes one completion with an empty tool list. It does not instantiate an AgentSession, tool dispatcher or extension loader. Provider output is parsed and strictly validated locally; this is not a claim of provider-enforced Structured Outputs.

Pi's provider layer was selected over running a general coding-agent session because this task needs a bounded text-to-JSON request, with no file editing or command tools. Disabling a coding harness's shell alone does not necessarily disable its independent patch tool. OAuth and renewal remain SDK responsibilities, while the maintainable application code stays in .NET.

The current runner uses the provider catalog to validate the configured model and fails rather than silently choosing another model or a separately billed API. Catalog presence does not prove account entitlement. Upgrade the pinned SDK deliberately and retest device login, renewal, image transport and isolation.

The [prompt file](prompts.md) reloads before each inference. Exact taxonomy names are resolved to IDs in .NET before the existing validator and synchronizer. The worker does not render scans or replace OCR.

## Durable flow

A whole-worker filesystem lock serializes processing. Atomic JSON replacements record job state and checkpoints. The saved intent separates model work from sync retries; the write-ahead operation journal handles uncertain PATCH responses. Completed IDs remain complete until explicit reprocessing. See [discovery semantics](operations.md#discovery-and-jobs).

Prompt fingerprints cover instructions, schema and inference settings. Jobs retain source revision, taxonomy context, prompt text and hash and proposal. Validation checks the current taxonomy before sync and before replay. The final GET/PATCH race remains a documented [limitation](review.md#history-and-journals).

## Code map and development

| Location | Responsibility |
| --- | --- |
| `src/PaperlessLlm/Organizer` | Discovery, files, jobs, schedule and retries |
| `src/PaperlessLlm/Intent` | Desired-state schema, prompt and validation |
| `src/PaperlessLlm/Runner`, `runner/` | Isolated subprocess and pinned provider SDK bridge |
| `src/PaperlessLlm/Sync` | Minimal writes, conflict checks and reconciliation |
| `src/PaperlessLlm/Paperless` | Bounded reads and downloads |
| `src/PaperlessLlm/OrganizerCli.cs` | Current CLI wiring |
| `tests/container` | Isolated synthetic end-to-end acceptance |

The old `Auth`, `Inference`, `Review` and `Worker` foundations remain for regression coverage and shared components. The current CLI does not invoke the old loopback authentication flow or read-only worker.

```sh
dotnet restore --locked-mode
dotnet test --configuration Release --no-restore
npm ci --prefix runner --ignore-scripts
node runner/repair-shrinkwrap.mjs
dotnet run --project src/PaperlessLlm -- --help
docker build -t ppllm:local .
bash tests/container/run.sh ppllm:local
```

Native development needs Node 24, the installed bridge and a dedicated auth home. The supported deployment is a Linux Docker container, which bundles these plus Poppler. CI runs .NET tests on Linux and container smoke and synthetic end-to-end tests on native amd64 and arm64 Linux runners. Release builds publish both architectures; follow the [release procedure](../.agents/skills/release/SKILL.md).

The repair command replaces Pi's shrinkwrapped `brace-expansion` with the exact integrity-locked patched version already installed at the root. It runs offline, verifies versions and paths, and is required after each `npm ci`; upstream shrinkwrap otherwise overrides the nested lock entry. Docker includes this step.
