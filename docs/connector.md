# Read-only Paperless connector

`PaperlessClient` runs the installed, authenticated `paperless` executable with
argument arrays, JSON output, a selected profile, and a per-command timeout.
It never reads credential files or OS keyring entries. Authenticate the CLI
separately. The CLI owns origin validation and network authentication.

```python
from paperless_llm.paperless import PaperlessClient

client = PaperlessClient(profile="default", timeout=60)
documents = client.list_documents(tag="needs review", limit=10)
document = client.get_document(123)
taxonomy = client.taxonomy()  # tags, correspondents, document_types
snapshot = client.collect_snapshot([123])
# Optional: explicitly choose a PRIVATE destination outside the repository.
# client.download_original(123, private_directory / "123.original")
```

Discovery resolves the tag by exact, case-insensitive name and rejects absent or
ambiguous names. It filters by the resolved numeric ID and orders documents by ID.
Pagination uses numeric page arguments, never server-provided URLs, with at most
50 pages per resource and 1,000 documents per discovery. Oversized, repeated,
malformed, and stalled pages fail closed. A snapshot contains `documents` and
`taxonomy`; it is an observation, not an atomic server transaction.

Document metadata, OCR, and original bytes are sensitive. Nothing logs response
bodies or CLI stderr; connector errors use fixed messages. Callers are responsible
for private storage and must not commit snapshots. Original download filenames
come from the caller, never document metadata. Downloads stage in a temporary
directory beside the destination, clean up on error, and create the final file
exclusively so existing files are preserved. Python uses restrictive creation
permissions on platforms that support them; Windows storage inherits the parent
directory ACL. Use a private directory.

The connector exposes no update, delete, upload, or raw API methods. It does not
OCR files, execute document instructions, verify subscription access, or send data
to an LLM. Concurrent server changes can still cause pagination to skip records;
run discovery again to refresh. The timeout is per CLI command, not a whole-run
deadline. Output byte size depends on server document content.
