#!/usr/bin/env bash
set -euo pipefail
cd "$(dirname "$0")/../../frontend"
npm ci
npm ls @vue/test-utils js-beautify glob
npm test -- --maxWorkers=2
npm audit
npm run build
