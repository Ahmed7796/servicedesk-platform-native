#!/usr/bin/env bash
# Lints the OpenAPI contracts with Spectral. Fails on warnings as well as errors.
# Needs Node (npx). The package is fetched at run time; nothing is added to the repo.
set -euo pipefail
cd "$(dirname "$0")"
npx -y @stoplight/spectral-cli@6.17.0 lint openapi/*.yaml \
  --ruleset .spectral.yaml --fail-severity=warn
