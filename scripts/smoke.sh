#!/usr/bin/env bash
set -uo pipefail

BASE="${1:-http://127.0.0.1:5700}"
KEY="${NEADOCS_SMOKE_KEY:-}"

pass=0
fail=0

check() {
    local name="$1" expected="$2" actual="$3"
    if [[ "$expected" == "$actual" ]]; then
        printf '  ok    %-52s %s\n' "$name" "$actual"
        pass=$((pass + 1))
    else
        printf '  FAIL  %-52s expected %s, got %s\n' "$name" "$expected" "$actual"
        fail=$((fail + 1))
    fi
}

contains() {
    local name="$1" needle="$2" haystack="$3"
    if [[ "$haystack" == *"$needle"* ]]; then
        printf '  ok    %-52s\n' "$name"
        pass=$((pass + 1))
    else
        printf '  FAIL  %-52s missing %q\n' "$name" "$needle"
        fail=$((fail + 1))
    fi
}

status() { curl -s -o /dev/null -w '%{http_code}' "$@"; }

echo "neadocs smoke against ${BASE}"

check "GET /health"                   200 "$(status "${BASE}/health")"
check "GET /ready"                    200 "$(status "${BASE}/ready")"
check "GET /metrics"                  200 "$(status "${BASE}/metrics")"
check "GET /api/v1/collections (401)" 401 "$(status "${BASE}/api/v1/collections")"

contains "health body reports ok" '"status":"ok"' "$(curl -s "${BASE}/health")"
contains "ready body reports ready" '"status":"ready"' "$(curl -s "${BASE}/ready")"

contains "401 is problem+json" 'application/problem+json' \
    "$(curl -s -D- -o /dev/null "${BASE}/api/v1/collections")"
contains "401 carries a correlation id" '"correlationId"' \
    "$(curl -s "${BASE}/api/v1/collections")"

contains "correlation id is echoed" 'smoke-correlation-1' \
    "$(curl -s -D- -o /dev/null -H 'X-Correlation-Id: smoke-correlation-1' "${BASE}/health")"
contains "a generated correlation id is returned" 'X-Correlation-Id' \
    "$(curl -s -D- -o /dev/null "${BASE}/health")"

contains "security headers are set" 'X-Content-Type-Options: nosniff' \
    "$(curl -s -D- -o /dev/null "${BASE}/health")"

contains "prometheus exposes the engine meter" 'neadocs' \
    "$(curl -s "${BASE}/metrics")"

if [[ -n "${KEY}" ]]; then
    code="$(status -H "X-Project-Key: ${KEY}" "${BASE}/api/v1/collections")"
    if [[ "$code" == "401" ]]; then
        printf '  FAIL  %-52s valid key was rejected\n' "project key is accepted"
        fail=$((fail + 1))
    else
        printf '  ok    %-52s %s\n' "project key is accepted" "$code"
        pass=$((pass + 1))
    fi
    check "an unknown project key is rejected" 401 \
        "$(status -H 'X-Project-Key: definitely-not-valid' "${BASE}/api/v1/collections")"
else
    echo "  skip  project key checks (set NEADOCS_SMOKE_KEY to enable)"
fi

echo
echo "${pass} passed, ${fail} failed"
[[ "$fail" -eq 0 ]]
