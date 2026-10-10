# Documentation

Paperless LLM is a scheduled .NET/Docker organizer for Paperless-ngx. Start with
the task you need to perform; the reference pages describe the current release.

| Task | Read |
| --- | --- |
| Install and sign in with ChatGPT | [README](../README.md), then [authentication](authentication.md) |
| Set Paperless permissions | [Authentication](authentication.md) |
| Configure schedule, backlog, dry-run and storage | [Operations](operations.md) |
| Backfill notes for completed documents | [Existing-installation procedure](operations.md#backfill-notes-on-an-existing-installation) |
| Edit organization instructions without rebuilding | [Prompts](prompts.md) |
| Inspect changes, no-change results and failures | [Review](review.md), [operations](operations.md#observe-and-recover) |
| Improve the OCR supplied by Paperless | [OCR research and tuning](ocr.md) |
| Upgrade or choose a mutable image channel | [Releases](releases.md) |
| Change implementation | [AGENTS.md](../AGENTS.md), [architecture](architecture.md), [quality](quality.md) |
| Run offline model evaluations | [Offline evaluation](offline-evaluation.md) |
| Prepare and publish a release | [Release agent skill](../.agents/skills/release/SKILL.md) |
| Understand future features | [Roadmap](roadmap.md); detailed work is tracked in Plane PPLLM |

Evaluation reports under `evaluations/` are dated evidence, not current setup
instructions. Their model configurations and historical limitations are preserved
so experiments remain interpretable. See the [documentation strategy](documentation-strategy.md)
for ownership and maintenance rules.
