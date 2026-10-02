# Documentation strategy

[Index](index.md) · [Agent instructions](../AGENTS.md)

The docs serve agents that need precise contracts and people who need a working
deployment. Each guide starts with the outcome, gives a runnable example, then
explains boundaries and recovery. Agent-first means explicit commands, paths,
defaults and invariants—not a separate unreadable manual.

## Sources of truth

| Information | Authority | Update trigger |
| --- | --- | --- |
| Current capabilities and entrypoint | README | User-visible behavior changes |
| Repository rules and task routing | AGENTS.md | Development workflow changes |
| Authentication and secret handling | authentication.md | Provider/auth changes |
| Runtime settings and job semantics | operations.md | CLI/options/state changes |
| Prompt contract and mount behavior | prompts.md | Prompt/context changes |
| Sync guarantees and review procedure | review.md | Validator/journal changes |
| Internal design | architecture.md | Boundary/module changes |
| Complexity, coverage and test policy | quality.md | Quality tooling/refactor changes |
| Version and channel semantics | releases.md and release skill | Release workflow changes |
| Measured model evidence | Dated evaluations | A completed experiment |
| Planned features and acceptance criteria | roadmap.md and Plane PPLLM | Scope/status changes |

Keep commands beside their use case. Cross-link configuration and authentication
instead of copying entire tables. Preserve historical reports; add a pointer to
current guidance when their setup becomes obsolete. Keep live document evidence,
credentials and deployment-specific private context out of published docs.

## Structure and discoverability

Use standard Markdown with descriptive headings, relative links and searchable
terms such as Paperless-ngx, AI document organization, Docker Compose, .NET,
Sign In With ChatGPT and device-code authentication where they explain the product.
Do not promise unimplemented features, guaranteed accuracy or provider endorsement.
The index maps common tasks to authoritative pages; AGENTS.md links only the
references relevant to implementation and release work.

For each behavioral PR, check commands against the executable, update affected
defaults, verify relative links, and identify migration implications. A release
links version-pinned documentation. Keep future local inference and UI/SQLite work
in the roadmap until implementation, tests and deployment guidance exist.
