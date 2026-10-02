#!/usr/bin/env bash
set -euo pipefail
# Run from a checkout with an already-built application image. No real credentials.
image="${1:?Usage: bash tests/container/run.sh IMAGE}"
fixture_dir="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
if command -v cygpath >/dev/null 2>&1; then fixture_dir="$(cygpath -m "$fixture_dir")"; export MSYS_NO_PATHCONV=1; fi
suffix="$$-$RANDOM"
network="ppllm-e2e-$suffix"
server="ppllm-fixture-$suffix"
volume="ppllm-data-$suffix"
cleanup() {
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
run once
run retry 1
run once
control "const s=await fetch('http://localhost:8080/test/state').then(r=>r.json()); if(s.document.title!=='Synthetic receipt'||s.patches!==1||s.inferences!==1||JSON.stringify([...s.document.tags].sort())!=='[1,2,3]')throw Error(JSON.stringify(s));"
# New containers reuse state; neither restart nor manually clearing the review marker triggers work.
run once
control "await fetch('http://localhost:8080/test/clear')"
run once
control "const s=await fetch('http://localhost:8080/test/state').then(r=>r.json()); if(s.patches!==1||s.inferences!==1||s.document.tags.includes(2)||!s.document.tags.includes(1))throw Error(JSON.stringify(s));"
run status
printf '%s\n' 'Synthetic container E2E passed: existing OCR, name resolution, proposal, validated PATCH, ambiguous-write recovery, restart deduplication, review-marker clearing. This does not test live ChatGPT authentication or model accuracy.'
