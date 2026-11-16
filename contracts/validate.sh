#!/usr/bin/env bash
# Validates every event example against its schema.
#   examples/<type>.json                    must be valid
#   examples/invalid/<type>.bad-<fmt>.json  must be invalid, and fail on format "<fmt>"
# The second rule proves formats are enforced, not silently ignored.
# Needs Node (npx). Packages are fetched at run time; nothing is added to the repo.
set -uo pipefail

cd "$(dirname "$0")/events"
AJV=(npx -y -p ajv-cli@5.0.0 -p ajv-formats@3.0.1 ajv validate
     --spec=draft2020 --strict=true --all-errors -c ajv-formats
     -r envelope.schema.json -r defs.schema.json)
failures=0

for schema in request.*.schema.json; do
  type="${schema%.schema.json}"
  example="examples/${type}.json"
  if [[ ! -f "$example" ]]; then
    echo "FAIL  $type: no example at $example"; failures=$((failures + 1)); continue
  fi
  if out=$("${AJV[@]}" -s "$schema" -d "$example" 2>&1); then
    echo "PASS  $example"
  else
    echo "FAIL  $example should be valid"; echo "$out" | grep -v '^npm '; failures=$((failures + 1))
  fi
done

for example in examples/invalid/*.json; do
  name="$(basename "$example" .json)"   # e.g. request.submitted.bad-uuid
  type="${name%.bad-*}"
  format="${name##*.bad-}"
  if out=$("${AJV[@]}" -s "${type}.schema.json" -d "$example" 2>&1); then
    echo "FAIL  $example should be invalid but passed"; failures=$((failures + 1))
  elif grep -q "\"format\": \"${format}\"\|format \"${format}\"" <<<"$out"; then
    echo "PASS  $example rejected on format \"$format\""
  else
    echo "FAIL  $example rejected, but not on format \"$format\""; echo "$out" | grep -v '^npm '; failures=$((failures + 1))
  fi
done

if (( failures > 0 )); then echo "$failures check(s) failed"; exit 1; fi
echo "All checks passed"
