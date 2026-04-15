#!/usr/bin/env bash
# Master runner: executes every scenario under ./scenarios and aggregates the
# pass/fail count. Usage: ./run.sh [scenario-glob]
set -u
HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
glob="${1:-*.sh}"

total_pass=0
total_fail=0
failures=()

shopt -s nullglob
for script in "${HERE}/scenarios/"${glob}; do
    [[ -f "${script}" ]] || continue
    name="$(basename "${script}" .sh)"
    printf '\n===== %s =====\n' "${name}"
    if bash "${script}"; then
        total_pass=$((total_pass+1))
    else
        total_fail=$((total_fail+1))
        failures+=("${name}")
    fi
done

printf '\n===== SUMMARY =====\n'
printf '%d scenarios passed, %d failed\n' "${total_pass}" "${total_fail}"
if (( total_fail > 0 )); then
    printf 'Failed scenarios:\n'
    for f in "${failures[@]}"; do printf '  - %s\n' "${f}"; done
    exit 1
fi
exit 0
