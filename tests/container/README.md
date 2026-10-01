# Synthetic container acceptance

From the repository root, after building the current application image:

```sh
docker build -t paperless-llm:test .
bash tests/container/run.sh paperless-llm:test
```

The script runs the actual .NET entrypoint, renderer, runner subprocess transport,
validation, sync journal and filesystem jobs. A fake Paperless HTTP service and
fake inference bridge provide a synthetic one-page document. It verifies a
validated title/tag update, preservation of `inbox`, addition of `needs review`,
reconciliation after the server commits a PATCH then disconnects, restart
idempotence, and immunity to manually clearing `needs review`.

Both containers use the image under test. An internal Docker network prevents
external calls; no ports are published. Only disposable test volumes and fake
credentials are used. Cleanup runs on exit. This is not live ChatGPT auth,
provider compatibility, OCR quality, or a real Paperless deployment test.

Run the script on each target architecture to verify that architecture's runtime.
