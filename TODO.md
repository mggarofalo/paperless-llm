# v0.3.0 delivery checklist

- [x] PPLLM-33: append useful OCR-grounded summary notes with durable POST recovery,
  human-note preservation, duplicate prevention, documentation, synthetic tests,
  Linux acceptance and passing PR CI. Merged in PR #16; not deployed.
- [x] PPLLM-33 implementation and local validation: 276 Linux .NET tests, 22
  JavaScript tests, complexity at most 10, coverage gates and Linux container acceptance.
- [ ] PPLLM-34: publish v0.3.0; verify versioned image, latest/stable channels,
  anonymous pulls, both architectures and checksummed assets.
- [x] PPLLM-34: document upgrade and bounded backfill queueing for completed jobs.
- [ ] Deployment acceptance (PPLLM-8/PPLLM-20): evaluate real summary quality
  and service-account Notes permissions before live backfill.

# v0.2.0 delivery checklist

Source of work status: existing Plane project **PPLLM**, parent **PPLLM-21**.
Keep this checklist and Plane aligned with verified outcomes.

- [x] PPLLM-22: inspect installed Paperless OCR configuration, research official knobs, recommend baseline and targeted experiments.
- [x] PPLLM-23: delegate refactoring; measure and enforce per-function complexity at most 10 across maintained source.
- [x] PPLLM-24: run Coverlet, improve meaningful behavioral tests, report line/branch coverage and remaining gaps.
- [x] PPLLM-25: add verified `latest` and `stable` mutable image channels without retargeting version tags or allowing backward promotion.
- [x] PPLLM-25: update the repository release agent skill for semantic versions and recovery rules.
- [x] PPLLM-25: publish stable v0.2.0 after CI, verify anonymous multiarch pulls and release assets.
- [x] PPLLM-26: define an agent-first, human-approachable documentation strategy and refresh linked guides.
- [x] PPLLM-26: improve README discoverability; explain Sign In With ChatGPT and device-code authentication accurately.
- [x] PPLLM-21: reconcile existing project statuses and backlog against shipped behavior and remaining acceptance work.
- [x] PPLLM-27: document local-model/OpenAI-compatible endpoint roadmap and acceptance criteria (implementation is future work).
- [x] PPLLM-28: document run-review UI and SQLite-backed settings/tuning roadmap (implementation is future work).

Production documents and deployment settings are not changed by this release/research task.

Delivery verified on 2026-10-02: [v0.2.0 release](https://github.com/mggarofalo/paperless-llm/releases/tag/v0.2.0),
[release workflow](https://github.com/mggarofalo/paperless-llm/actions/runs/37012952251).
Version, `latest` and `stable` share image index
`sha256:f537f56197636092493bd97d354ae15953b4d15428fa707e9d4eb41f19ef039a`.
Anonymous digest/channel pulls, both platform manifests, executable version and
release-file checksums were verified. The homelab deployment was not upgraded.
Future roadmap implementations and live renewal/accuracy acceptance remain open in Plane.
