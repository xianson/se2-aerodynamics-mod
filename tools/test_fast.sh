#!/usr/bin/env bash
# test_fast.sh - the aero mod's fast gate (no game): the mod's scripts compiled exactly as the game compiles them
# (ModCheck), then every offline suite in Tests/. Exit 0 only if everything passes.
cd "$(dirname "$0")/.."
MODCHECK=/d/aero/tools/ModCheck/ModCheck.csproj
rc=0; t0=$(date +%s)

s=$(date +%s)
out=$(dotnet build "$MODCHECK" -nologo -v q "-p:ModDir=$(cygpath -w "$PWD")" 2>&1)
if echo "$out" | grep -q "Build succeeded"; then echo "PASS  compile (ModCheck)  $(( $(date +%s)-s ))s"
else rc=1; echo "FAIL  compile (ModCheck)"; echo "$out" | grep -E " error " | sort -u | head -20; fi

for d in Tests/*/; do
    [ -n "$(ls "$d"*.csproj 2>/dev/null)" ] || continue
    s=$(date +%s)
    args=""; [ "$(basename "$d")" = "AeroBench" ] && args="-- gate"   # (the full bench - every ship - by hand)
    out=$(dotnet run --project "$d" -c Release $args 2>&1); r=$?
    line=$(echo "$out" | grep -a -i "passed" | tail -1 | sed 's/^ *//')
    if [ $r -eq 0 ]; then echo "PASS  $(basename "$d")  $(( $(date +%s)-s ))s  $line"
    else rc=1; echo "FAIL  $(basename "$d")  $line"; echo "$out" | grep -a "FAIL\|error" | head -10 | sed 's/^/      /'; fi
done
echo "== aero fast gate: $([ $rc -eq 0 ] && echo PASSED || echo FAILED) in $(( $(date +%s)-t0 ))s"
exit $rc
