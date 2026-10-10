# Quality gates and refactoring evidence

[Documentation index](index.md) · [Architecture](architecture.md) · [Offline evaluation](offline-evaluation.md)

The 0.2.0 refactor keeps the organizer behavior and on-disk job format. It separates discovery, inference, synchronization, retry/pause handling, CLI commands, pagination validation, image validation, and evaluation pipelines. These boundaries allow tests to exercise failures without accessing Paperless or signing in to a model provider.

## Scope and architecture cleanup

The supported CLI enters `OrganizerCli`, which uses `OrganizerWorker` and the isolated `PiRunner`. The older loopback OAuth/Responses client, review-only worker/checkpoint format, and HTML review generator had no path from that CLI. They and their dedicated tests were removed. Current private request/response evidence, synchronization journals, retrospective review tags, and protected workflow-tag rules remain. The obsolete JWT and HTTP-factory package references were also removed.

The worker still rejects a legacy review checkpoint instead of silently adopting it. No job-state migration, new model request, live document update, or auth-volume migration is part of this refactor.

## Complexity

[Lizard 1.17.31](../requirements-quality.txt) checks every maintained C# function in `src/` and JavaScript function in `runner/` and `scripts/`. Generated `bin/` and `obj/`, third-party `node_modules/`, and test fixtures are excluded. The maximum permitted cyclomatic complexity is **10**; there is no allowlist of complicated production functions.

```sh
python -m pip install -r requirements-quality.txt
python scripts/complexity.py
```

Alternatively, without installing into the active Python environment:

```sh
uv run --with lizard==1.17.31 python scripts/complexity.py
```

Before refactoring, 44 functions exceeded 10, with maximum 40. After refactoring, all measured functions are at or below 10. The bridge's former top-level dispatch/inference block was also separated into explicit functions so its logic is measured.

Lizard is a lightweight parser, not Roslyn semantic analysis. Its C# parser can miss the first method following a semicolon-terminated positional record. Records now have explicit empty bodies, and the baseline measurement applied that same syntax-only normalization before counting. This exposed the synchronization method that the original raw scan missed. The gate is a maintenance signal, not proof of correctness or a substitute for review.

## Coverage evidence

Both runs used Linux containers, .NET 10 Release builds, Node, Poppler, and Coverlet collector 6.0.4 with [the same coverage settings](../coverage.runsettings). The baseline is commit `c03ddbe` (0.1.3), with only the coverage collector/settings added. Generated regular-expression code and test assemblies are excluded; handwritten production and evaluation code are included. JavaScript and separate container smoke tests are not included in Coverlet percentages.

| Module | Before line | After line | Before branch | After branch |
| --- | ---: | ---: | ---: | ---: |
| Worker/runtime | 84.05% | **91.70%** | 67.69% | **78.85%** |
| Offline evaluator | 51.76% | **82.99%** | 49.38% | **75.72%** |
| Combined | 74.02% | **87.85%** | 61.79% | **77.46%** |

The final Linux run passed **234 .NET tests, zero skipped**. Removing the retired subsystem removed 78 legacy-only test cases; 73 new cases were added for maintained behavior. The runner JavaScript suite also passed 17 tests, including eight new bridge tests with a stub provider. Release-policy tests are an additional suite.

Coverage percentages have changed denominators because obsolete code was deleted and functions were split. The gains are not attributable solely to new tests. The before/after table reports the complete maintained assemblies at each revision rather than selectively excluding low-coverage files.

```sh
dotnet restore --locked-mode
dotnet test --no-restore --configuration Release --tl:off \
  --collect:"XPlat Code Coverage" --settings coverage.runsettings \
  --results-directory artifacts/coverage
python scripts/coverage.py artifacts/coverage
node --test runner/*.test.mjs scripts/*.test.mjs
```

Install `poppler-utils` and Node before running the suite on Linux. Use a fresh results directory: the coverage gate rejects multiple reports rather than choosing a potentially stale one. CI enforces at least 90% line / 77% branch for the worker and 82% line / 74% branch for the evaluator, leaving limited compiler/platform variation below the measured results. Retain the Cobertura and JSON outputs as CI artifacts; they are not committed.

## What the new tests establish

- A saved synchronization intent cannot be substituted under the same job identity; changed review markers and conflicting source revisions stop replay.
- Successful HTTP alone is insufficient: readback mismatch leaves the journal pending. A restarted dry run cannot apply an already pending write.
- Invalid credential files never reach the network. Caller cancellation stays distinct from an uncertain write outcome.
- Corrupt job identities/state fail closed. Cancelled atomic writes preserve the previous checkpoint and remove temporary files.
- Explicit reprocessing archives the previous evidence and creates a new job identity.
- Manual bulk submission uses one durable snapshot while the worker runs. Tests
  exercise overlapping submissions, bounded materialization, interrupted sync,
  cancellation/resume, historical pagination, capacity, provider pauses across
  restart, and wake-up without the normal hourly delay.
- Run diagnostics expose only categorical field dispositions and numeric provider
  usage; model text stays private. Contradictory decision/action pairs are rejected.
- Model subprocess output is bounded, malformed output is rejected, timeouts/cancellation release resources, and only fixed diagnostic codes reach ordinary logs.
- Device codes cannot redirect users to an untrusted URL. The bridge configures device-code OAuth, no model tools, and no provider-network catalog refresh; tool-call and incomplete responses are rejected.
- JPEG/PNG bounds reject truncated or oversized input; Linux tests render every page of a synthetic PDF and reject page-cap truncation.
- All seven evaluation pipelines run offline against synthetic responses, retain failed raw output, and preserve provenance. Fake child processes cover Unicode stdin, usage capture, stderr truncation, stdout limits, timeout, cancellation, and tool rejection.

## Document notes (2026-10-08 development change)

The Linux .NET 10 Release run passed **276 tests, zero skipped**, including note
validation, existing-note preservation, note-only changes, absent/truncated source
abstention, legacy journal compatibility, interrupted metadata/POST recovery,
no blind POST replay, source conflicts, dry runs and reprocessing. Worker coverage
was **92.35% line / 80.49% branch**; evaluator coverage remained **82.99% / 75.72%**.
All 22 JavaScript tests passed, and maximum production function complexity was 10.
The Linux container acceptance fixture drops responses after both metadata and
note writes and verifies a single summary survives restart and reprocessing.
These checks exercise deterministic behavior with synthetic data. They do not
establish summary quality on real OCR or permission compatibility with a live
Paperless deployment.

## Manual reprocessing (v0.4.0)

The Linux .NET 10 Release run passed **329 tests, zero skipped**, and all **23
JavaScript tests** passed. Worker coverage is **93.48% line / 83.73% branch**;
evaluator coverage is **82.99% line / 75.72% branch**. Maximum production function
complexity remains 10. Coverage includes durable submissions, overlapping
selections, checkpoint migration, cancellation/resumption, provider pauses and
notes-only preservation. Container acceptance additionally exercises these
commands in a live synthetic worker and verifies uncertain-write recovery without
duplicate notes. These checks do not establish real-document model accuracy or
live authentication renewal.

## Remaining limits

The largest uncovered runtime area is CLI host construction/setup dispatch (65 uncovered sequence-point lines in this run). The independent Docker smoke suite exercises actual container setup, organization, and crash recovery, but those subprocesses are outside this Coverlet run. Additional uncovered runtime branches include unusual filesystem/access failures, alternate OS paths, and race/error handling that is difficult to force deterministically.

Evaluator gaps are mainly full batch orchestration/export CLI, rich-region scoring edge cases, and less-used image-variant validation paths. The measured per-file uncovered line counts are 73 for `ExperimentRunner.cs`, 50 for evaluator `Program.cs`, 32 for `RegionScorer.cs`, and 15 for `ExperimentImages.cs`. Future coverage work should target concrete regressions in those areas; assertions that merely repeat getters, record equality, or every configuration constant are not useful progress.

Tests do not establish model accuracy or exercise real ChatGPT authorization/renewal. Keep model-quality evaluation and a deployment's synthetic probe separate from deterministic code tests. Nothing here requires private documents or credentials.
