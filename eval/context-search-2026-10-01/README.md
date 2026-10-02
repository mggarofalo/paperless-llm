# Context-management experiments

These are offline research recipes, not production presets. See the
[research report](../../docs/evaluations/2026-10-01-context-search.md) for results
and limitations, and the [evaluation guide](../../docs/offline-evaluation.md)
for the case and reference formats.

The [manifest](manifest.json) lists each recipe's parent and hypothesis.
[Aggregate screening results](screen-results.json) and
[final validation results](validation-results.json) exclude document identities,
source text, expected answers, and model outputs. Prompt files are deduplicated
by content hash after normalizing line endings and the final newline. All document material and reference labels remain private.
The `controls/` directory contains the deployed-prompt and prior-pilot recipes.
The additional `all-keep` control is deterministic: every field keeps its current
value and the tag-addition list is empty; it makes no model calls.

## Reproduce a recipe

Use an authenticated local Codex CLI and .NET 10. This harness invokes Codex;
the deployed worker uses the Pi provider bridge. It does not automatically reuse
the worker's authentication volume, and matching model names do not establish
deployment parity.

Place the case file, image mapping and images in a private directory outside the
repository. Each recipe's `imageVariants` path is a placeholder under
`/private/eval`; adjust a private copy of the recipe for your environment.

```sh
dotnet run --project src/PaperlessLlm.Eval -- experiment \
  --cases /private/eval/cases.jsonl --split train \
  --recipe eval/context-search-2026-10-01/recipes/r086.json \
  --out /private/eval/runs/r086 --concurrency 2 --timeout 240

dotnet run --project src/PaperlessLlm.Eval -- score \
  /private/eval/cases.jsonl /private/eval/runs/r086 \
  --split train --version r086 --model gpt-6-luna \
  --references /private/eval/references.json \
  --out /private/eval/reports/r086.json
```

The output directory must be absent or empty. Keep failed attempts and retries
separate. Do not overwrite successful outputs with a new sample and present that
as the original experiment.

## Image inputs

The original study compared full-page images, higher-resolution images, three
overlapping regions along the long axis, and full pages plus those regions.
Regions covered 40% of the long dimension, starting at 0%, 30%, and 60%.
PDF high-resolution rendering used a 4,000-pixel maximum dimension; image
originals retained their available resolution and EXIF orientation was applied.
Preprocessing changed geometry only, never document text.

Recipe r090 adds narrow edge views: crop the leftmost and rightmost 10% of each
high-resolution page, then supply both 90-degree and 270-degree rotations of
each strip. Its per-page view order is the original three regions, left90,
left270, right90, right270. Both orientations avoid assuming which direction
marginal text faces. These remain views of one original page, not extra pages.
Its final metadata call receives page drafts without images; code assembles OCR
from the independently validated page records.

The image mapping format is described in the evaluation guide. Prepare mappings
with the same view order and original page numbering. Do not substitute generated
or reconstructed document images.

## What a score means

The research composite combines selected OCR-reference quality, repair of known
OCR problems, preservation of correct metadata, and correction challenges. It is
not a full-transcription accuracy percentage. The report describes the formula,
the separate post-audit regression gate, transport failures, and selection bias.

The recipes preserve the strict production rule: an OCR replacement must cover
every supplied page completely. Neither a model's `complete: true` assertion nor
a passing JSON validator proves that its transcription is accurate.



The [frozen selection](selection.json) identifies the research candidate chosen before the holdout was opened. It is not a production approval.
