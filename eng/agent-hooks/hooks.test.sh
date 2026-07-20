#!/usr/bin/env bash
# Regression test for shared agent PreToolUse hooks. Run from the repo root:
#   bash eng/agent-hooks/hooks.test.sh
set -u
cd "$(dirname "${BASH_SOURCE[0]}")"

if ! command -v jq >/dev/null 2>&1; then
  printf 'hooks.test.sh SKIPPED: jq is not installed, so the guards fail open here.\n'
  exit 0
fi

GEN="guard-generated-files.sh"
pass=0
fail=0

check() {
  local name="$1" expected="$2" script="$3" json="$4" got
  printf '%s' "$json" | bash "$script" >/dev/null 2>&1
  got=$?
  if [ "$got" -eq "$expected" ]; then
    printf '  ok   %-42s (exit %d)\n' "$name" "$got"
    pass=$((pass + 1))
  else
    printf '  FAIL %-42s expected %d, got %d\n' "$name" "$expected" "$got"
    fail=$((fail + 1))
  fi
}

j_write() { jq -cn --arg p "$1" '{tool_name:"Write",tool_input:{file_path:$p,content:"x"}}'; }
j_edit() { jq -cn --arg p "$1" '{tool_name:"Edit",tool_input:{file_path:$p,old_string:"a",new_string:"b"}}'; }

echo "== generated-files guard =="
check "write schema.d.ts blocked"        2 "$GEN" "$(j_write 'src/Cvoya.Spring.Web/src/lib/api/schema.d.ts')"
check "absolute schema.d.ts blocked"     2 "$GEN" "$(j_edit '/tmp/repo/src/Cvoya.Spring.Web/src/lib/api/schema.d.ts')"
check "edit normal source allowed"       0 "$GEN" "$(j_edit 'src/Cvoya.Spring.Web/src/app/page.tsx')"
check "write API source allowed"         0 "$GEN" "$(j_write 'src/Cvoya.Spring.Host.Api/Program.cs')"

printf '\n%d passed, %d failed\n' "$pass" "$fail"
[ "$fail" -eq 0 ]
