# Paperless connector

[Home](../README.md) · [Architecture](architecture.md) · [Authentication](authentication.md)

The .NET connector uses a dedicated Paperless API token mounted from a secret file. It does not depend on the interactive `paperless` CLI or copy its credentials. The account needs document view/change access and taxonomy view access as described in [authentication](authentication.md).

`PaperlessClient` is read-only: it lists documents and taxonomy, fetches metadata/OCR, and downloads originals. `PaperlessWriter` is a separate narrow PATCH client allowing only title, created date, correspondent, document type, tags and content. The synchronizer constructs that patch after validation; model output is never passed directly to the API.

Pagination stays within the configured instance and endpoint, with bounded page counts and response sizes. Automatic redirects are disabled so credentials cannot follow a redirect to another service. Document titles and filenames are never used as local output paths. Defaults are a 60-second request timeout, 100 entries per page, 50 pages per request sequence, 8 MiB JSON responses and 50 MiB originals.

Cursor discovery uses count-guided binary search over ascending ID pages, followed by a bounded batch. It does not scan every historical page or depend on an unsupported `id__gt` filter. Pagination is not snapshot-isolated; changing visibility can require a retry or the next rotating discovery pass.

## Source evidence

PNG and JPEG originals are supported. PDFs are rendered with bundled Poppler utilities, at most 10 pages, 2000 pixels per page dimension, 30 MiB total rendered output and a 90-second rendering deadline. All PDF pages must render; a document exceeding a limit fails instead of being truncated. Other original formats are unsupported in this release.

Downloads retain a hash of the original bytes. The worker separately rechecks the metadata revision after inference. This detects concurrent changes exposed by Paperless metadata, but is not a database lock or atomic conditional write. See [review and recovery](review.md).
