# Organization prompts

[Home](../README.md) · [Operation](operations.md) · [Evaluation results](evaluations/2026-10-02-named-organization.md)

The worker defaults to **Sol 6 low**, one request per document, using existing
Paperless OCR. The bundled [organization.txt](../src/PaperlessLlm/prompts/organization.txt)
is the selected evaluation policy, preserved verbatim (including its experiment
wording). It keeps titles and OCR unchanged and proposes dates, correspondents,
document types and additive subject tags. Accuracy depends on the OCR and the
organization conventions; the evaluation does not establish perfect accuracy.

## Edit without rebuilding

Download `organization.txt` from the same GitHub release as your image and save it
as `prompts/organization.txt` beside your Compose file. Add `compose.override.yaml`:

```yaml
services:
  worker:
    environment:
      PPLLM_PROMPT_FILE: /etc/ppllm/prompts/organization.txt
    volumes:
      - type: bind
        source: ./prompts
        target: /etc/ppllm/prompts
        read_only: true
        bind:
          create_host_path: false
```

Create the directory and file before starting Compose. The container's non-root
user must be able to read them. Compose loads this override automatically.
Mount the **directory**, so editors that atomically replace files work correctly.
If you maintain multiple override files, merge this service fragment into yours.

After adding the mount, run:

```sh
docker compose stop worker
docker compose run --rm worker check
docker compose run --rm worker probe organization
docker compose up -d worker
```

`probe organization` uses your configured prompt and model through the real runner
on a synthetic receipt. It makes no Paperless requests or writes. Its expected
receipt fields may not match intentionally customized policies; it is a smoke
check, not an accuracy benchmark.

Subsequent **prompt edits need neither a new image nor a restart**. Write a complete
UTF-8 file to `prompts/organization.txt.new`, then rename it over
`prompts/organization.txt` in the same directory. Avoid editing the live file in
place while the worker reads it. Keep prompt revisions in your own version control.
Restore an earlier file to roll back guidance.

## Loading and audit behavior

- `PPLLM_PROMPT_FILE` is a container path. If unset, the packaged file at
  `/app/prompts/organization.txt` is used. There is no remote prompt download.
- Each new inference reads one snapshot. The file must be nonempty UTF-8 without
  NUL characters and at most 64 KiB. Missing, unreadable or invalid files fail
  setup; changes that become invalid while running pause the batch without
  consuming job attempts. Correct the file; the next poll retries.
- An in-flight request keeps its snapshot. Saved intents retain their original
  policy across sync retries. Completed jobs are not automatically reprocessed.
- Logs record `PromptSha256` and the full policy fingerprint. Private
  `/data/state/evidence/.../request.json` records the exact instructions, schema,
  source and taxonomy; `response.txt` preserves raw model output before name
  resolution. Do not publish these files.

The file contains instructions only. Document context and JSON schema are attached
by code; no template variables or commands are executed. Model output must select
exact, unique existing taxonomy names. Unknown or ambiguous names, protected or
duplicate tag additions, invalid evidence and OCR replacement are rejected.
Changing the prompt cannot enable deletion, tag removal, taxonomy creation,
ownership/permission changes or OCR writes. Title changes remain allowed by the
schema, but the default policy keeps them unchanged.

## Upgrade from v0.1.2

Use the v0.1.3 Compose asset and set `PPLLM_MODEL=gpt-6-sol` in your existing `.env`
(an existing Luna setting overrides the new default). Keep the same Compose project
name and auth, organizer-state and audit volumes; no new sign-in is normally needed.
Pull the image, run `check` and `probe organization`, then start a small batch.
See [operations](operations.md) for status and retries.

Saved metadata-only intents remain resumable. Saved legacy OCR replacements stop
as `sync_conflict`: inspect their private operation journal and Paperless history
before using `reprocess ID`, especially if an older write may already have applied.
No upgrade automatically replays completed documents or discards audit records.
