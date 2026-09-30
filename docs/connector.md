# Paperless connector

[Home](../README.md) · [Architecture](architecture.md) · [Authentication](authentication.md)

The .NET connector uses a dedicated Paperless API token mounted from a secret
file. It does not depend on the interactive `paperless` CLI or copy its credentials.
Create a view-only account with the [authentication guide](authentication.md).

All connector requests use GET. It lists documents and taxonomy, fetches document
metadata/OCR, and downloads originals. Pagination stays within the configured
instance and endpoint, with bounded page counts and response sizes. Automatic
redirects are disabled so credentials cannot follow a redirect to another service.
Document titles and filenames are never used as local output paths.

Defaults are a 60-second request timeout, 100 entries per page, 50 pages per
request sequence, 8 MiB JSON responses and 50 MiB originals. A missing or ambiguous
eligibility tag fails rather than selecting every document.

## Source evidence

PNG and JPEG originals are supported. PDFs are rendered with the bundled Poppler
utilities, with at most 10 pages, 2000 pixels per page dimension, 30 MiB total
rendered output and a 90-second rendering deadline. All PDF pages must render;
a document exceeding a limit is rejected rather than silently truncated.
Other original formats are not supported in this release.

The connector hashes the original bytes and relevant document metadata. The worker
rechecks the metadata revision after model inference. This detects concurrent
changes exposed by Paperless metadata; it is not a transactional lock on the
Paperless database. See [job management](operations.md#document-enrollment).

View-only credentials are the server-side enforcement boundary. The application's
GET-only interface adds another boundary, but cannot correct an overprivileged
account or make documents visible without the appropriate object permissions.
