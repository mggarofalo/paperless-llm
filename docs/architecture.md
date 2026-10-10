# Architecture

[Home](../README.md) · [Operation](operations.md) · [Authentication](authentication.md) · [Connector](connector.md)

```mermaid
flowchart LR
  P[Paperless API] --> D[Scheduled discovery]
  D --> J[Durable filesystem jobs]
  J --> C[Existing OCR and current taxonomy]
  C --> L[Pi provider SDK / Sol 6 low]
  L --> V[.NET intent validation]
  V --> S[Journal, minimal PATCH and optional note POST]
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

A whole-worker filesystem lock serializes processing. Atomic JSON replacements record job state and checkpoints. The saved intent separates model work from sync retries; the write-ahead operation journal handles uncertain PATCH responses and separate note POST attempts. Completed IDs remain complete until explicit reprocessing. See [discovery semantics](operations.md#discovery-and-jobs).

Manual reprocessing uses immutable `runs/<run-id>.json` requests on the private
state volume. Each selected item carries the previous and planned next job IDs.
Submission needs only a short submission lock, never the inference/worker lock.
Under the worker lock, materialization archives the completed job before atomically
replacing its index. Replay recognizes the planned identity or its archived result.
The worker first upgrades the checkpoint to version 2 so older binaries cannot
silently ignore the new job restrictions after a downgrade.
Only the worker writes skip progress; cancellation/resume use a separate atomic
control file and a short per-run lock shared with job start. This permits live
submission and cancellation without racing a model call or unresolved write.
Run read models reconcile current jobs and archived jobs without exposing their
document contents. Small-library limits remain explicit; there is no recurring
reprocessing scheduler or automatic history deletion.

Prompt fingerprints cover instructions, schema and inference settings. Jobs retain source revision, taxonomy context, prompt text and hash and proposal. Validation checks the current taxonomy before sync and before replay. The final GET/PATCH race remains a documented [limitation](review.md#history-and-journals).

Notes use a separate append-only write, after metadata and the review marker.
The operation journal adds optional `Note` and `NoteAttempted` fields and a
`metadata_verified` phase. Persisting the attempt before POST prevents blind
replay of a non-idempotent endpoint; an absent attempted note stops for inspection.
Document notes are compared separately from the existing metadata revision hash,
so pre-notes saved jobs retain their hash identity. Missing notes context is not
assumed to be an empty collection. The model receives presence flags, not human
note text. See [recovery and concurrency limitations](review.md#document-notes).

## Code map and development

| Location | Responsibility |
| --- | --- |
| `src/PaperlessLlm/Organizer` | Discovery, files, jobs, schedule and retries |
| `src/PaperlessLlm/Intent` | Desired-state schema, prompt and validation |
| `src/PaperlessLlm/Runner`, `runner/` | Isolated subprocess and pinned provider SDK bridge |
| `src/PaperlessLlm/Sync` | Minimal writes, conflict checks and reconciliation |
| `src/PaperlessLlm/Paperless` | Bounded reads and downloads |
| `src/PaperlessLlm/OrganizerCli.cs` | Current CLI wiring |
| `src/PaperlessLlm.Eval` | Offline experiments, scoring and isolated experiment processes |
| `tests/container` | Isolated synthetic end-to-end acceptance |

The retired loopback authentication, direct inference client and read-only worker
were removed in 0.2.0. Small shared exception types, protected-tag rules and the
private audit writer remain where the current organizer uses them. Evaluation
code separates orchestration, case execution, image preparation and process
isolation so experiments do not expand the production worker's permissions.
See [quality and testing](quality.md) for scope, measured complexity, coverage and
the failure modes exercised by tests.

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
