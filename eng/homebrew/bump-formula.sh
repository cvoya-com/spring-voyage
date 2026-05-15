#!/usr/bin/env bash
# Render the Homebrew formula from the template, substituting version and SHAs
# extracted from a SHA256SUMS file produced by the release workflow.
#
# Usage: bump-formula.sh <version> <sha256sums-file> <output.rb>
#
# Arguments:
#   version        Semantic version string, e.g. 1.0.0
#   sha256sums-file  Path to the SHA256SUMS file attached to the GitHub release
#   output.rb      Destination path for the rendered formula
set -euo pipefail

VERSION="${1:?version required}"
SHA256SUMS="${2:?sha256sums-file required}"
OUTPUT="${3:?output.rb required}"

TEMPLATE="$(cd "$(dirname "$0")" && pwd)/spring-voyage.rb.tmpl"

if [[ ! -f "${TEMPLATE}" ]]; then
  echo "::error::formula template not found: ${TEMPLATE}" >&2
  exit 1
fi

if [[ ! -f "${SHA256SUMS}" ]]; then
  echo "::error::SHA256SUMS file not found: ${SHA256SUMS}" >&2
  exit 1
fi

SHA_ARM64=$(grep "spring-voyage-${VERSION}-osx-arm64\\.tar\\.gz" "${SHA256SUMS}" | awk '{print $1}')
SHA_X64=$(  grep "spring-voyage-${VERSION}-osx-x64\\.tar\\.gz"   "${SHA256SUMS}" | awk '{print $1}')

if [[ -z "${SHA_ARM64}" ]]; then
  echo "::error::SHA256 for osx-arm64 not found in ${SHA256SUMS}" >&2
  exit 1
fi

if [[ -z "${SHA_X64}" ]]; then
  echo "::error::SHA256 for osx-x64 not found in ${SHA256SUMS}" >&2
  exit 1
fi

sed \
  -e "s/{{VERSION}}/${VERSION}/g" \
  -e "s/{{SHA256_OSX_ARM64}}/${SHA_ARM64}/g" \
  -e "s/{{SHA256_OSX_X64}}/${SHA_X64}/g" \
  "${TEMPLATE}" > "${OUTPUT}"

echo "Generated ${OUTPUT}:"
cat "${OUTPUT}"
