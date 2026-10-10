#!/usr/bin/env bash
set -euo pipefail
# Run from a checkout with an already-built application image. No real credentials.
image="${1:?Usage: bash tests/container/run.sh IMAGE}"
fixture_dir="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
if command -v cygpath >/dev/null 2>&1; then fixture_dir="$(cygpath -m "$fixture_dir")"; export MSYS_NO_PATHCONV=1; fi
suffix="$$-$RANDOM"
network="ppllm-e2e-$suffix"
server="ppllm-fixture-$suffix"
worker="ppllm-worker-$suffix"
volume="ppllm-data-$suffix"
cleanup() {
  docker rm -f "$worker" >/dev/null 2>&1 || true
  docker rm -f "$server" >/dev/null 2>&1 || true
  docker network rm "$network" >/dev/null 2>&1 || true
  docker volume rm "$volume" >/dev/null 2>&1 || true
}
trap cleanup EXIT
docker network create --internal "$network" >/dev/null
docker volume create "$volume" >/dev/null
docker run --rm --user root --entrypoint sh -v "$volume:/data" "$image" -c 'mkdir -p /data/auth /data/state /data/audit; printf synthetic-only > /data/token; chown -R app:app /data; chmod 700 /data/auth /data/state /data/audit; chmod 600 /data/token'
docker run -d --name "$server" --network "$network" --network-alias paperless --entrypoint node -v "$fixture_dir:/fixtures:ro" "$image" /fixtures/fake-paperless.mjs >/dev/null
control() {
  docker exec "$server" node --input-type=module -e "$1"
}
for i in $(seq 1 30); do
  if control "await fetch('http://localhost:8080/test/ready').then(r=>{if(!r.ok)process.exit(1)})" >/dev/null 2>&1; then break; fi
  if [ "$i" = 30 ]; then docker logs "$server"; exit 1; fi
  sleep 1
done
run() {
  docker run --rm --network "$network" -v "$volume:/data" -v "$fixture_dir:/fixtures:ro" \
    -e PPLLM_PAPERLESS_URL=http://paperless:8080 -e PPLLM_PAPERLESS_TOKEN_FILE=/data/token \
    -e PPLLM_RUNNER_BRIDGE=/fixtures/fake-bridge.mjs -e PPLLM_BACKFILL_LIMIT=1 \
    -e PPLLM_DRY_RUN=false "$image" "$@"
}
# Drop the connection after accepting the write: journal recovery must avoid a second PATCH.
control "await fetch('http://localhost:8080/test/hide-tag')"
if run once; then echo 'Setup should fail with a hidden review tag' >&2; exit 1; fi
docker run --rm --entrypoint sh -v "$volume:/data" "$image" -c 'test ! -f /data/state/organizer.json && test ! -f /data/state/organizer-bootstrap.json'
control "await fetch('http://localhost:8080/test/show-tag')"
control "await fetch('http://localhost:8080/test/disconnect')"
control "await fetch('http://localhost:8080/test/disconnect-note')"
run once
run retry 1
run once
run retry 1
run once
control "const s=await fetch('http://localhost:8080/test/state').then(r=>r.json()); if(s.document.title!=='Synthetic receipt'||s.patches!==1||s.posts!==1||s.document.notes.length!==2||s.document.notes[0].note!=='Human note: keep for warranty.'||s.inferences!==1||JSON.stringify([...s.document.tags].sort())!=='[1,2,3]')throw Error(JSON.stringify(s));"
# New containers reuse state; neither restart nor manually clearing the review marker triggers work.
run once
control "await fetch('http://localhost:8080/test/clear')"
run once
control "const s=await fetch('http://localhost:8080/test/state').then(r=>r.json()); if(s.patches!==1||s.posts!==1||s.document.notes.length!==2||s.document.notes[0].note!=='Human note: keep for warranty.'||s.inferences!==1||s.document.tags.includes(2)||!s.document.tags.includes(1))throw Error(JSON.stringify(s));"
# Explicit reprocessing reads the existing generated note and is a true no-op.
run reprocess 1
run once
control "const s=await fetch('http://localhost:8080/test/state').then(r=>r.json()); if(s.posts!==1||s.patches!==1||s.inferences!==2||s.document.tags.includes(2))throw Error(JSON.stringify(s));"
run status
# Bulk preview is read-only; cancellation and resume work against a live worker.
preview="$(run reprocess --all --missing-summary --notes-only --preview --json)"
node -e 'const p=JSON.parse(process.argv[1]);if(p.Eligible!==0||p.Skipped!==1||p.RunId!==null)process.exit(1)' "$preview"
submitted="$(run reprocess --all --json)"
run_id="$(node -e 'console.log(JSON.parse(process.argv[1]).RunId)' "$submitted")"
run runs cancel "$run_id"
docker run -d --name "$worker" --network "$network" -v "$volume:/data" -v "$fixture_dir:/fixtures:ro" \
  -e PPLLM_PAPERLESS_URL=http://paperless:8080 -e PPLLM_PAPERLESS_TOKEN_FILE=/data/token \
  -e PPLLM_RUNNER_BRIDGE=/fixtures/fake-bridge.mjs -e PPLLM_DRY_RUN=false "$image" worker >/dev/null
for i in $(seq 1 30); do
  if docker exec "$worker" dotnet PaperlessLlm.dll health >/dev/null 2>&1; then break; fi
  if [ "$i" = 30 ]; then docker logs "$worker"; exit 1; fi
  sleep 1
done
docker exec "$worker" dotnet PaperlessLlm.dll runs resume "$run_id"
for i in $(seq 1 30); do
  status="$(docker exec "$worker" dotnet PaperlessLlm.dll status --run "$run_id" --json)"
  if node -e 'const s=JSON.parse(process.argv[1]);if(s.Phase!=="completed"||s.NoChange!==1)process.exit(1)' "$status"; then break; fi
  if [ "$i" = 30 ]; then docker logs "$worker"; exit 1; fi
  sleep 1
done
control "const s=await fetch('http://localhost:8080/test/state').then(r=>r.json()); if(s.posts!==1||s.patches!==1||s.inferences!==3||s.document.tags.includes(2))throw Error(JSON.stringify(s));"
# Submit through the live container: no worker lock or per-document containers.
docker exec "$worker" dotnet PaperlessLlm.dll reprocess --all --preview
control "await fetch('http://localhost:8080/test/prepare-notes-only')"
submitted="$(docker exec "$worker" dotnet PaperlessLlm.dll reprocess --all --missing-summary --notes-only --json)"
run_id="$(node -e 'console.log(JSON.parse(process.argv[1]).RunId)' "$submitted")"
for i in $(seq 1 30); do
  status="$(docker exec "$worker" dotnet PaperlessLlm.dll status --run "$run_id" --json)"
  if node -e 'const s=JSON.parse(process.argv[1]);if(s.Phase!=="completed"||s.Applied!==1)process.exit(1)' "$status"; then break; fi
  if [ "$i" = 30 ]; then docker logs "$worker"; exit 1; fi
  sleep 1
done
docker exec "$worker" dotnet PaperlessLlm.dll reprocess --all --missing-summary --notes-only --request-id "$run_id" --json
control "const s=await fetch('http://localhost:8080/test/state').then(r=>r.json()); if(s.document.title!=='Human title'||s.posts!==2||s.patches!==2||s.inferences!==4||s.document.notes.length!==2||s.document.notes[0].note!=='Human note: keep for warranty.'||JSON.stringify([...s.document.tags].sort())!=='[1,2,3]')throw Error(JSON.stringify(s));"
printf '%s\n' 'Synthetic container E2E passed: existing OCR, name resolution, proposal, validated PATCH and note POST, ambiguous-write recovery for both endpoints, restart deduplication, review-marker clearing. This does not test live ChatGPT authentication or model accuracy.'
