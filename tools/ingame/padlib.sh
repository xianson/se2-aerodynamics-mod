#!/usr/bin/env bash
# padlib.sh - the pad-foot gears' in-game scripts' shared helpers (sourced by padgear_test.sh and padgear_reload.sh): the Orbital
# Mod's harness library (HARNESS), the aero probes' reads (server-built summaries read until answered), the predicates the checks
# use (run in the calling shell - no bash -c), and the release of every dev override.
set -u
T0=$(date +%s)
MOD="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
HARNESS=${HARNESS:-$MOD/../Orbital Mod/tools/harness}
[ -f "$HARNESS/testlib.sh" ] || HARNESS="/c/Users/slob/Documents/SpaceEngineers2/Mods/Orbital Mod/tools/harness"
source "$HARNESS/testlib.sh"
SP="$HARNESS"
DEADLINE=${DEADLINE:-420}
ARGS=${ARGS:-${SE2_LAUNCH_ARGS:+$(cygpath -u "$SE2_LAUNCH_ARGS")}}
ARGS=${ARGS:-${AERO_TOOLS:-$MOD/tools/shared}/se2-launch-args.txt}
GEN=/c/Users/slob/AppData/Local/Temp/OrbitalMod/gen
RELEASED=""; notes=()

el() { echo $(( $(date +%s) - T0 )); }
budget() { [ $(( $(date +%s) - T0 + $1 )) -le "$DEADLINE" ]; }
note() { notes+=("$1"); echo "   $1"; }
poll() { local end=$(( $(date +%s) + $1 )); shift; while :; do "$@" >/dev/null 2>&1 && return 0; alive || return 1; [ "$(date +%s)" -ge "$end" ] && return 1; sleep 1; done; }
num() { awk -v x="$1" 'BEGIN{exit !(x ~ /^-?[0-9.]+([eE][-+]?[0-9]+)?$/)}'; }
startof() { grep -ao '\-start:[^"]*' | head -1 | sed -E 's/^-start://; s/[[:space:]]+-[a-zA-Z]+(:.*|[[:space:]].*)?$//; s/[[:space:]]*$//'; }
set_() { ask "aeroset $1 $2" >/dev/null; }
# a server-built summary (ServerDevReads: the first read says "asked..." - read again)
sv() { local a i; for i in $(seq 1 20); do a=$(val "$1"); case "$a" in asked*|"?") sleep 0.5 ;; *) echo "$a"; return 0 ;; esac; done; echo "${a:-no answer}"; }
gear() { sv AeroGearComponent.Summary; }
rec() { val AeroGearComponent.PadRecordResult; }
count() { grep -o -- "$2" <<< "$1" | wc -l | tr -d ' '; }
# a recorder field over the pad gears ("tilt 0.12 spin 0.003 ..."): recf FIELD -> the largest, recmin FIELD -> the smallest
recf() { rec | tr '|' '\n' | sed -n "s/.* $1 \([-0-9.]*\).*/\1/p" | sort -g | tail -1; }
recmin() { rec | tr '|' '\n' | sed -n "s/.* $1 \([-0-9.]*\).*/\1/p" | sort -g | head -1; }
lt() { awk -v a="$1" -v b="$2" 'BEGIN{exit !(a != "" && a + 0 < b + 0)}'; }
le() { awk -v a="$1" -v b="$2" 'BEGIN{exit !(a != "" && a + 0 <= b + 0)}'; }
near() { awk -v a="$1" -v b="$2" -v t="$3" 'BEGIN{exit !(a != "" && (a - b < t) && (b - a < t))}'; }
slid_n() { count "$(gear)" "PAD slider"; }
alone_n() { count "$(gear)" "PAD slider alone"; }
boxed_n() { count "$(gear)" " box tilt"; }
# predicates for expect / poll (run in this shell: no bash -c)
slid_ge() { [ "$(slid_n)" -ge "$1" ]; }
alone_ge() { [ "$(alone_n)" -ge "$1" ]; }
spawned() { "$ORB" 'gen status' | grep -aq "spawned '$1'"; }
whole() { "$ORB" 'gen whole' | grep -aq ' WHOLE'; }
ge() { [ -n "$1" ] && [ "$1" -ge "$2" ] 2>/dev/null; }
eq() { [ "$1" = "$2" ]; }
both_lt() { lt "$1" "$2" && lt "$3" "$4"; }
# all_of PRED ARGS -- PRED ARGS ...: every one holds
all_of() { local a=(); while [ $# -gt 0 ]; do if [ "$1" = "--" ]; then "${a[@]}" || return 1; a=(); else a+=("$1"); fi; shift; done; [ ${#a[@]} -eq 0 ] || "${a[@]}"; }

release() {
    [ -n "$RELEASED" ] && return; RELEASED=1
    alive || { echo "== release: no game running"; return; }
    local r=("aeroset AeroGearComponent.PadRecord false" "aeroset AeroGearComponent.PadHop 0" "aeroset AeroGearComponent.PadPush 0" "aeroset AeroGearComponent.PadTraceTicks 0"
             "aeroset AeroGearComponent.DevCommand 1" "aeroset AeroGearComponent.DevCommand 0"
             "aeroset WheelShockDev.DevApproach false" "aeroset WheelShockDev.DevNoAero false" "aeroset WheelShockSettings.Mode 2"
             "aeroset WheelShockDev.DevForward 0" "aeroset WheelShockDev.DevSink 3" "aeroset WheelShockDev.DevRecordSeconds 4"
             ${MIDAIR_D0:+"aerosetting Structure.Damage $MIDAIR_D0"} ${GENCLEAR:+"gen clear"})
    echo "== release: ${#r[@]} overrides (dev probes off, the midair drop's values back, gears down${GENCLEAR:+, the test grids removed})"
    ask "${r[@]}" | paste -d'|' <(printf '%s\n' "${r[@]}") - | sed 's/^/   /; s/|/  =>  /' | cut -c1-140
}
summary_all() {
    echo; echo "== ${SUITE:-padgear}: $(el) s (deadline $DEADLINE s)"
    [ ${#notes[@]} -gt 0 ] && { echo "-- numbers"; for n in "${notes[@]}"; do echo "  $n"; done; }
    summary
}

