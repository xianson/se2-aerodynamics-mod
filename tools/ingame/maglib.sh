#!/usr/bin/env bash
# maglib.sh - the magnetic gear's in-game scripts' shared helpers (sourced by maggear_test.sh and maggear_reload.sh): the Orbital
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
mag() { sv AeroMagGearComponent.Summary; }
gear() { sv AeroGearComponent.Summary; }
rec() { val AeroMagGearComponent.DevRecordResult; }
count() { grep -o -- "$2" <<< "$1" | wc -l | tr -d ' '; }
# the n-th gear's field from the recorder line ("drift 0.004 m 0.10 deg tilt ..."): recf FIELD -> the largest over the gears
recf() { rec | tr '|' '\n' | sed -n "s/.* $1 \([-0-9.]*\).*/\1/p" | sort -g | tail -1; }
recmin() { rec | tr '|' '\n' | sed -n "s/.* $1 \([-0-9.]*\).*/\1/p" | sort -g | head -1; }
lt() { awk -v a="$1" -v b="$2" 'BEGIN{exit !(a != "" && a + 0 < b + 0)}'; }
near() { awk -v a="$1" -v b="$2" -v t="$3" 'BEGIN{exit !(a != "" && (a - b < t) && (b - a < t))}'; }
joined_n() { count "$(mag)" "| Joined"; }
locked_n() { count "$(mag)" " LOCKED"; }
# predicates for expect / poll (run in this shell: no bash -c)
joined_ge() { [ "$(joined_n)" -ge "$1" ]; }
locked_ge() { [ "$(locked_n)" -ge "$1" ]; }
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
    local r=("aeroset AeroMagGearComponent.DevRecord false" "aeroset AeroMagGearComponent.DevHop 0" "aeroset AeroMagGearComponent.DevCommand 3"
             "aeroset AeroMagGearComponent.DevCommand 0" "aeroset AeroGearComponent.DevCommand 1" "aeroset AeroGearComponent.DevCommand 0" "gen clear")
    echo "== release: ${#r[@]} overrides (dev probes off, Auto-Lock on, gears down, the test grids removed)"
    ask "${r[@]}" | paste -d'|' <(printf '%s\n' "${r[@]}") - | sed 's/^/   /; s/|/  =>  /' | cut -c1-140
}
summary_all() {
    echo; echo "== ${SUITE:-maggear}: $(el) s (deadline $DEADLINE s)"
    [ ${#notes[@]} -gt 0 ] && { echo "-- numbers"; for n in "${notes[@]}"; do echo "  $n"; done; }
    summary
}
