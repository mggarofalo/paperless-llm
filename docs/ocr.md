# Paperless OCR configuration and tuning

[Index](index.md) · [Operations](operations.md) · [Evaluation](offline-evaluation.md)

Paperless LLM consumes **existing Paperless OCR**. Improve recognition at ingestion,
then evaluate organization separately. There is no evidence yet that one new OCR
setting improves every document; the following is a baseline and an experiment plan.

## Version and effective configuration first

This research targets **Paperless-ngx 2.20.15**. Newer online documentation may use
different names or modes. Check your installed version before copying settings.
The UI's OCR overrides take precedence over environment configuration. A `null`
value from `/api/config/` means inheritance, not proof that a default is active.
See the [version-pinned configuration reference](https://github.com/paperless-ngx/paperless-ngx/blob/v2.20.15/docs/configuration.md#ocr).

With the authenticated Paperless CLI:

```sh
paperless api GET status/
paperless api GET config/
```

Keep the results private. On the Paperless host, inspect only the relevant OCR
variables rather than dumping all environment settings (which may include secrets):

```sh
docker compose exec webserver sh -c 'env | sort | grep -E "^PAPERLESS_(OCR_|THREADS_PER_WORKER=|TASK_WORKERS=)"'
docker compose exec webserver tesseract --version
docker compose exec webserver ocrmypdf --version
```

Replace `webserver` with the actual Paperless service name. These commands belong
to the Paperless deployment, not the `paperless-llm` Compose project.

## Recommended starting point

For an English, mixed-document library, retain this conservative baseline unless
the current effective configuration or measured sample gives a reason to change it:

```yaml
environment:
  PAPERLESS_OCR_LANGUAGE: eng
  PAPERLESS_OCR_MODE: skip
  PAPERLESS_OCR_CLEAN: clean
  PAPERLESS_OCR_DESKEW: "true"
  PAPERLESS_OCR_ROTATE_PAGES: "true"
  PAPERLESS_OCR_ROTATE_PAGES_THRESHOLD: "12"
```

Leave the page limit unset, keep user arguments empty initially, and retain normal
archive output. Add languages only for documents that need them. Set image DPI only
when the source image lacks accurate DPI metadata and the scanner resolution is known.
These are mostly existing 2.20.15 defaults, not a demonstrated quality upgrade.

## Which knobs are worth testing?

| Problem | Candidate | What to check |
| --- | --- | --- |
| Scanner already embedded poor OCR | Targeted `redo` | Whether the old text layer was actually replaced |
| Skewed or noisy receipt | Deskew, compare `clean` and `none` | Lost decimal points, minus signs and thin digits |
| Narrow single-column receipt | `tesseract_pagesegmode: 6` versus baseline | Reading order and total/date accuracy |
| Sparse business card | `tesseract_pagesegmode: 11` versus baseline | Names and phone/address grouping |
| Low-resolution printed text | Better scan; test `oversample: 300` on a copy | Genuine character recovery versus merely larger output |
| Wrong rotations | Adjust threshold on a sample | False rotations as well as missed corrections |

These are **hypotheses**, not universal presets. Keep the full-page/default
segmentation for mixed layouts until a comparison supports changing it.
Tesseract's [quality guide](https://tesseract-ocr.github.io/tessdoc/ImproveQuality.html)
recommends adequate resolution (at least 300 DPI), suitable segmentation and clean,
correctly aligned input. Upscaling cannot reconstruct detail absent from a scan.

In this Paperless version, `redo` disables deskew and downgrades `clean-final`
to `clean`. User arguments are merged after normal options and can override them;
inspect compatibility before adding them. This is verified in the
[2.20.15 parser](https://github.com/paperless-ngx/paperless-ngx/blob/v2.20.15/src/paperless_tesseract/parsers.py).
For example, `{"tesseract_pagesegmode":6}` is an isolated trial, not a recommended
global `PAPERLESS_OCR_USER_ARGS` value.

Avoid global `force`, aggressive cleanup or lossy optimization as a quality fix.
OCRmyPDF's [cookbook](https://ocrmypdf.readthedocs.io/en/stable/cookbook.html#redo-existing-ocr)
explains how redo differs from rasterizing force mode and why image transformations
can affect output. Check the installed OCRmyPDF version before using newer CLI flags.
Handwriting and missing/clipped characters are particularly poor candidates for a
single generic tuning switch.

## A useful private experiment

1. Reserve a representative set: receipts/refunds, checks, business cards, clean
   digital PDFs, skewed scans and multi-document packets. Use copies of originals
   outside the live consumption directory. Record tool versions and effective knobs.
2. Label critical fields against the scan: issuer, primary date, amounts including
   signs/decimals, and identifiers. Freeze the labels and a held-out subset before tuning.
3. Compare baseline against one change at a time. Measure field-exact accuracy,
   character/word error, page completeness, runtime and failures. Do not select a
   winner solely from average text similarity.
4. Reject any setting that improves prose while corrupting amounts or dates. Inspect
   archive appearance too. Apply the selected setting only to the relevant document
   class if it regresses mixed documents.
5. Test organization using each OCR variant with the same frozen policy/model. Keep
   OCR quality and organization quality as separate measurements.

Changing settings does not prove historical text has improved. Reprocessing existing
documents is a separate, bounded operation with backups and before/after comparison.
Completed organizer jobs do not automatically rerun after OCR changes; explicitly
reprocess selected jobs when ready. No live OCR settings or documents were changed
as part of this research.
