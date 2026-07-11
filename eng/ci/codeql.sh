#!/usr/bin/env bash
# Run the same build-mode-none language matrix and default code-scanning query
# suites as .github/workflows/codeql.yml. Results stay local under artifacts/.

set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
OUTPUT_DIR="${CODEQL_OUTPUT_DIR:-$ROOT/artifacts/codeql}"
TEMP_SOURCE_ROOT=""
TEMP_DATABASE_ROOT=""

if [ "$#" -gt 0 ]; then
  LANGUAGES=("$@")
else
  LANGUAGES=(actions csharp javascript-typescript python)
fi

command -v codeql >/dev/null 2>&1 || {
  echo "CodeQL CLI is required: https://github.com/github/codeql-cli-binaries/releases" >&2
  exit 1
}
command -v rsync >/dev/null 2>&1 || {
  echo "rsync is required to create a clean CodeQL source copy" >&2
  exit 1
}

cleanup() {
  [ -z "$TEMP_SOURCE_ROOT" ] || rm -rf "$TEMP_SOURCE_ROOT"
  [ -z "$TEMP_DATABASE_ROOT" ] || rm -rf "$TEMP_DATABASE_ROOT"
}
trap cleanup EXIT

query_suite() {
  case "$1" in
    actions) echo "codeql/actions-queries:codeql-suites/actions-code-scanning.qls" ;;
    csharp) echo "codeql/csharp-queries:codeql-suites/csharp-code-scanning.qls" ;;
    javascript-typescript) echo "codeql/javascript-queries:codeql-suites/javascript-code-scanning.qls" ;;
    python) echo "codeql/python-queries:codeql-suites/python-code-scanning.qls" ;;
    *) echo "unsupported CodeQL language: $1" >&2; return 2 ;;
  esac
}

TEMP_SOURCE_ROOT="$(mktemp -d "${TMPDIR:-/tmp}/spring-voyage-codeql-source.XXXXXX")"
TEMP_DATABASE_ROOT="$(mktemp -d "${TMPDIR:-/tmp}/spring-voyage-codeql-db.XXXXXX")"
rsync -a --delete \
  --exclude='.git/' \
  --exclude='artifacts/' \
  --exclude='bin/' \
  --exclude='obj/' \
  --exclude='node_modules/' \
  --exclude='.next/' \
  --exclude='TestResults/' \
  "$ROOT/" "$TEMP_SOURCE_ROOT/"
mkdir -p "$OUTPUT_DIR"

for language in "${LANGUAGES[@]}"; do
  database="$TEMP_DATABASE_ROOT/$language"
  suite="$(query_suite "$language")"
  codeql pack download "$suite"
  codeql database create "$database" \
    --language="$language" \
    --build-mode=none \
    --source-root="$TEMP_SOURCE_ROOT" \
    --overwrite
  codeql database analyze "$database" "$suite" \
    --format=sarif-latest \
    --output="$OUTPUT_DIR/$language.sarif" \
    --sarif-category="/language:$language"
done
