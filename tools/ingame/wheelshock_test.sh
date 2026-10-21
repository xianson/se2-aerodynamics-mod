#!/usr/bin/env bash
# wheelshock_test.sh - THE WHEEL SHOCKS IN GAME (2026-10-20; Integration/AeroWheelShockComponent.cs, Dev/WheelShockDev.cs). One launch
# of an 'Orbital Test...' world holding the TU-134 BIG (blueprint "Tupolev Tu-134 Big": 56 t on four vanilla 1 m wheels) parked on
# flat ground, the Orbital Mod's harness driving it (testlib.sh / orb.sh); ~4-5 min; PASS / FAIL / SKIP from the aero mod's own
# probes (aeroget WheelShockDev.DevResult, AeroWheelShockComponent.Summary) and the game log - nothing judged by eye. Run it in the
# background (run_in_background + Monitor), never while someone plays.
#
# usage: tools/ingame/wheelshock_test.sh            the run. REFUSES while a game is running (it may be the user's): KILL_GAME=1
#                                                   closes it - only when nobody plays. REFUSES unless the launch world is an
#                                                   'Orbital Test...' world.
#        tools/ingame/wheelshock_test.sh --release  only the dev overrides back in the running game (after a killed run)
# env: HARNESS (the Orbital Mod's tools/harness), DEADLINE s (300), ARGS (the launch args file), NOLAUNCH=1 (the game already
#      running), GRID (the grid's name, "Tupolev Tu-134 Big"), NOSE (its nose along its local Z: 1 +Z - the blueprint's cockpit
#      faces +Z (tools/gridgen/bpinfo.py) - or -1), SINKS ("1 3 5"), FORWARDS ("0 45")
# needs: THIS branch's aero build deployed to the game's mod folder; ONE-TIME SETUP (the game free): the world 'Orbital Test
#      Wheels' - a copy of 'Orbital Test World' with the Tu-134 Big pasted on level ground (a runway's length clear ahead of its
#      nose for the 45 m/s drops: ~300 m a drop) and saved; -start: of ARGS naming it.
#
# TIMELINE (launch ~60-150 s, then ~2.5 min):
#  A  PARKED (Shocks: Airshock + oleo): the grid found; its four wheels resolved and rated (AeroWheelShockComponent.Summary: the
#     1 m wheels at 15 t); at rest every wheel inside its stroke (compression < 95 %) and on the ground.
#  B  DROPS (WheelShockDev: thrown up 1.2 m, held level at the sink rate until the first touch, then recorded per tick for 4 s):
#     for Shocks Off (0) then Airshock + oleo (2), forward 0 then 45 m/s, sink 1 / 3 / 5 m/s - per drop the touch speed, the deepest
#     compression of each wheel's stroke, wheels bottomed, hull contacts, the highest rebound speed, bounces, the settle time,
#     the strut's peak (rated weights), the damage (integrity lost, blocks lost). JUDGED (Shocks on, 1 and 3 m/s): touched within
#     0.5 m/s of the asked sink, no wheel bottomed, no hull contact, rebound under 30 % of the sink, no bounce, settled under 2 s,
#     no damage; 5 m/s and Shocks Off reported only (the baseline); and on vs off: the oleo bottoms no more wheels than vanilla.
#  C  the run's log: no exception in our code, no Havok assertion, no [AERO-SHOCK] fault.
#  release (also from the EXIT trap): Wheels.Shocks back to 2, the drop's dev values back.
set -u
SUITE=wheelshock_test
T0=$(date +%s)
MOD="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
HARNESS=${HARNESS:-$MOD/../Orbital Mod/tools/harness}
[ -f "$HARNESS/testlib.sh" ] || HARNESS="/c/Users/slob/Documents/SpaceEngineers2/Mods/Orbital Mod/tools/harness"
source "$HARNESS/testlib.sh"
DEADLINE=${DEADLINE:-300}
ARGS=${ARGS:-${SE2_LAUNCH_ARGS:+$(cygpath -u "$SE2_LAUNCH_ARGS")}}
ARGS=${ARGS:-${AERO_TOOLS:-$MOD/tools/shared}/se2-launch-args.txt}
GRID=${GRID:-Tupolev Tu-134 Big}; NOSE=${NOSE:-1}; SINKS=${SINKS:-1 3 5}; FORWARDS=${FORWARDS:-0 45}
RELEASED=""; notes=(); pass=0; fail=0; skipped=0; results=()

el() { echo $(( $(date +%s) - T0 )); }
budget() { [ $(( $(date +%s) - T0 + $1 )) -le "$DEADLINE" ]; }
note() { notes+=("$1"); echo "   $1"; }
poll() { local end=$(( $(date +%s) + $1 )); shift; while :; do "$@" >/dev/null 2>&1 && return 0; alive || return 1; [ "$(date +%s)" -ge "$end" ] && return 1; sleep 1; done; }
startof() { grep -ao '\-start:[^"]*' | head -1 | sed -E 's/^-start://; s/[[:space:]]+-[a-zA-Z]+(:.*|[[:space:]].*)?$//; s/[[:space:]]*$//'; }
set_() { ask "aeroset $1 $2" >/dev/null; }
lt() { awk -v a="$1" -v b="$2" 'BEGIN{exit !(a != "" && a + 0 < b + 0)}'; }
le() { awk -v a="$1" -v b="$2" 'BEGIN{exit !(a != "" && a + 0 <= b + 0)}'; }
near() { awk -v a="$1" -v b="$2" -v t="$3" 'BEGIN{exit !(a != "" && (a - b < t) && (b - a < t))}'; }
eq() { [ "$1" = "$2" ]; }
ge() { [ -n "$1" ] && [ "$1" -ge "$2" ] 2>/dev/null; }
all_of() { local a=(); while [ $# -gt 0 ]; do if [ "$1" = "--" ]; then "${a[@]}" || return 1; a=(); else a+=("$1"); fi; shift; done; [ ${#a[@]} -eq 0 ] || "${a[@]}"; }
# a field of the drop's result line ("... maxcomp 0.73 bottomed 0 hull 0 rebound 0.21 ...")
rf() { sed -n "s/.* $1 \(-\?[0-9.]*\).*/\1/p" <<< "$R" | head -1; }
dropdone() { local p r; p=$(val WheelShockDev.DevPhase); r=$(val WheelShockDev.DevResult); [ "$p" = "idle" ] && [[ "$r" == "drop $1:"* ]] && [[ "$r" != *running* ]]; }
shocks() { val AeroWheelShockComponent.Summary; }

release() {
    [ -n "$RELEASED" ] && return; RELEASED=1
    alive || { echo "== release: no game running"; return; }
    local r=("aeroset WheelShockSettings.Mode 2" "aeroset WheelShockDev.DevForward 0" "aeroset WheelShockDev.DevSink 3" "aeroset WheelShockDev.DevGrid -")
    echo "== release: ${#r[@]} overrides (Wheels.Shocks 2, the drop's dev values)"
    ask "${r[@]}" | paste -d'|' <(printf '%s\n' "${r[@]}") - | sed 's/^/   /; s/|/  =>  /' | cut -c1-140
}
summary_all() {
    echo; echo "== $SUITE: $(el) s (deadline $DEADLINE s)"
    [ ${#notes[@]} -gt 0 ] && { echo "-- numbers"; for n in "${notes[@]}"; do echo "  $n"; done; }
    summary
}
[ "${1:-}" = "--release" ] && { release; exit 0; }

# ---- guards ------------------------------------------------------------------------------------------------------------------
W=$(startof < "$ARGS" 2>/dev/null)
case "$W" in "Orbital Test"*) ;; *) echo "REFUSED: the launch world is '$W' (-start: in $ARGS) - not an 'Orbital Test...' world."; exit 2 ;; esac
if [ -z "${NOLAUNCH:-}" ] && alive && [ -z "${KILL_GAME:-}" ]; then echo "REFUSED: Space Engineers 2 is running - it may be the user's game. KILL_GAME=1 closes it."; exit 2; fi
trap 'release; summary_all' EXIT
trap 'exit 130' INT TERM

# ---- launch --------------------------------------------------------------------------------------------------------------------
if [ -z "${NOLAUNCH:-}" ]; then
    old=$(LOGF 2>/dev/null); mkdir -p "$(dirname "$ST")"; touch "$(dirname "$ST")/dev.flag"; rm -f "$(dirname "$ST")/cmd.txt"
    echo "== launch ($(el) s): '$W'"; launch
    for i in $(seq 1 150); do L=$(LOGF 2>/dev/null); [ -n "$L" ] && [ "$L" != "$old" ] && grep -aq 'beacon Root Entity\|Loading Failed' "$L" 2>/dev/null && break; sleep 1; done
fi
L=$(LOGF); MARK=0; [ -n "${NOLAUNCH:-}" ] && MARK=$(wc -l < "$L")
grep -aq 'Loading Failed' "$L" && { record "the world loaded" "Loading Failed in $L"; exit 1; }
poll 60 gridsup || { record "the harness answers" "findgrids unanswered 60 s after the load"; exit 1; }
[ "$(val WheelShockDev.DevPhase)" = "?" ] && { record "aero: the wheel shocks' dev statics (WheelShockDev.*)" "not in this build - deploy this branch"; exit 1; }
FG=$(ask "findgrids $GRID")
grep -aq '[0-9]=' <<< "$FG" || { record "the test grid '$GRID' in the world" "not found ($FG) - see the ONE-TIME SETUP in this script's header"; exit 1; }
record "loaded, the harness answers, this build has the wheel shocks, '$GRID' is here ($(el) s)" ""

# ---- A: parked -----------------------------------------------------------------------------------------------------------------
echo "== A parked ($(el) s)"
set_ WheelShockSettings.Mode 2; set_ WheelShockDev.DevGrid "${GRID// /_}"; set_ WheelShockDev.DevNose "$NOSE"; set_ WheelShockDev.DevRecordSeconds 4
hold 3
S=$(shocks); note "parked: $(echo "$S" | cut -c1-400)"
nw=$(grep -o ' | 1.0 m x' <<< "$S" | wc -l | tr -d ' ')
expect "A: four 1 m wheels resolved and rated" "$(echo "$S" | cut -c1-200)" ge "$nw" 4
worst=$(grep -o ' x [0-9.]*/[0-9.]*' <<< "$S" | awk -F'[ x/]+' '{ if ($3 > 0) { u = $2 / $3; if (u > m) m = u } } END { print m + 0 }')
grounded=$(grep -o ' ground' <<< "$S" | wc -l | tr -d ' ')
expect "A: at rest every wheel inside its stroke (deepest $worst of it) and on the ground ($grounded)" "$(echo "$S" | cut -c1-200)" all_of lt "$worst" 0.95 -- ge "$grounded" 4

# ---- B: the drops --------------------------------------------------------------------------------------------------------------
n=0
declare -A BOT
for mode in 0 2; do
    set_ WheelShockSettings.Mode $mode
    for fwd in $FORWARDS; do
        for v in $SINKS; do
            budget 25 || { skip "B: mode $mode forward $fwd sink $v" "deadline"; continue; }
            n=$((n + 1))
            set_ WheelShockDev.DevSink "$v"; set_ WheelShockDev.DevForward "$fwd"; set_ WheelShockDev.DevDrop "$n"
            if ! poll 20 dropdone "$n"; then record "B: drop $n (mode $mode, forward $fwd, sink $v) finished" "$(val WheelShockDev.DevPhase) | $(val WheelShockDev.DevResult | cut -c1-160)"; continue; fi
            R=$(val WheelShockDev.DevResult)
            note "mode $mode fwd $fwd sink $v: $(echo "$R" | cut -c1-330)"
            if [[ "$R" == *aborted* ]]; then record "B: drop $n (mode $mode, forward $fwd, sink $v)" "$R"; continue; fi
            tv=$(rf touch); mc=$(rf maxcomp); bo=$(rf bottomed); hu=$(rf hull); rb=$(rf rebound); bn=$(rf bounces); st=$(rf settle); dm=$(rf damage); lost=$(rf lost)
            BOT["$mode/$fwd/$v"]=$bo
            if [ "$mode" = 2 ] && [ "$v" != 5 ]; then
                expect "B: oleo, forward $fwd, $v m/s - touched at $v +- 0.5 m/s" "touch $tv" near "$tv" "$v" 0.5
                expect "B: oleo, forward $fwd, $v m/s - no wheel bottomed (deepest $mc), no hull contact" "bottomed $bo hull $hu" all_of eq "$bo" 0 -- eq "$hu" 0
                expect "B: oleo, forward $fwd, $v m/s - rebound under 30 % of the sink, no bounce" "rebound $rb bounces $bn" all_of lt "$rb" "$(awk -v v=$v 'BEGIN{print 0.3*v}')" -- eq "$bn" 0
                expect "B: oleo, forward $fwd, $v m/s - settled under 2 s, no damage" "settle $st damage $dm lost $lost" all_of le 0 "$st" -- lt "$st" 2 -- lt "$dm" 0.001 -- eq "$lost" 0
            fi
        done
    done
done
for fwd in $FORWARDS; do for v in $SINKS; do
    off=${BOT["0/$fwd/$v"]:-}; on=${BOT["2/$fwd/$v"]:-}
    [ -n "$off" ] && [ -n "$on" ] && expect "B: forward $fwd, $v m/s - the oleo bottoms no more wheels than vanilla ($on vs $off)" "on $on off $off" le "$on" "$off"
done; done

# ---- C: the log ----------------------------------------------------------------------------------------------------------------
echo "== C log ($(el) s)"
x=$(ourexc "$L" "$((MARK + 1))" | head -3); record "C: no exception in our code" "${x:+$x}"
b=$(bad); record "C: no Havok assertion, no fatal crash" "${b:+$b}"
f=$(val AeroWheelShockComponent.Faults); expect "C: no wheel shock faults" "faults $f ($(val AeroWheelShockComponent.LastFault))" eq "$f" 0
alive || record "C: the game alive at the end" "it died"
