#!/usr/bin/env bash
# wheelshock_test.sh - THE WHEEL SHOCKS IN GAME (2026-10-20; Integration/AeroWheelShockComponent.cs, Dev/WheelShockDev.cs). One launch
# of an 'Orbital Test...' world (flat-ish voxel ground in front of the player), the Orbital Mod's harness driving it (testlib.sh /
# orb.sh, `gen spawn`); <= 8 min; PASS / FAIL / SKIP from the aero mod's own probes (aeroget WheelShockDev.DevResult / DevParked,
# AeroWheelShockComponent.Faults) and the game log - nothing judged by eye. Run it in the background (run_in_background + Monitor),
# never while someone plays.
#
# THE GRIDS (tools/ingame/build_wheelbrick.py writes them; the user's design, 2026-10-20): HEAVY ARMOUR BRICKS on four vanilla 1 m
# suspensions at their bottom corners, no cockpit - WSBRICK ~56 t (14 t a wheel: 93 % of the 1 m wheel's 15 t rating, the Tu-134
# Big's share), WSLIGHT ~16 t (27 %), WSOVER ~123 t (204 %) - and WSDECK, a static 60 x 100 m armour deck (grid-to-grid contact).
# The wheels are a parameter: WHEEL="..." is passed to build_wheelbrick.py (e.g. the pad-gear variants of feat/a-maggear:
#   WHEEL="--wheel custom --wheel-guid <server prefab> --wheel-box x0,y0,z0,x1,y1,z1 --wheel-mass KG --mount bottom").
#
# usage: tools/ingame/wheelshock_test.sh            the run. REFUSES while a game is running (it may be the user's): KILL_GAME=1
#                                                   closes it - only when nobody plays. REFUSES unless the launch world is an
#                                                   'Orbital Test...' world (it spawns test grids into the world it loads).
#        tools/ingame/wheelshock_test.sh --release  only the dev overrides back in the running game (after a killed run)
# env: HARNESS (the Orbital Mod's tools/harness), DEADLINE s (480), ARGS (the launch args file), NOLAUNCH=1 (the game already
#      running), PART (all | voxel | deck - two launches if one runs long), SINKS ("1 3 5"), WHEEL (build_wheelbrick.py's args)
# needs: THIS branch's aero build deployed to the game's mod folder.
#
# TIMELINE (launch ~60-150 s, then ~4-5 min):
#  A  VOXEL GROUND: WSBRICK, WSLIGHT, WSOVER spawned 40 m ahead, 4 m up, side by side; `gen whole` (every block joined - the
#     suspensions' mount face is unverified: build_wheelbrick.py's header); parked (Shocks: Airshock + oleo): four wheels rated,
#     on the ground, inside the stroke (WSOVER: reported).
#  B  DROPS (WheelShockDev: thrown up 1.2 m, held level at the sink rate until the first touch, then recorded per tick for 4 s;
#     forward runs alternate +/- so a brick stays near its spot): WSBRICK - Shocks Off (0) and Airshock + oleo (2) x forward 0 and
#     45 m/s x sink 1 / 3 / 5; WSLIGHT and WSOVER - Airshock + oleo, forward 0, sink 1 / 3 / 5. Per drop: the touch speed, the
#     deepest compression of each wheel's stroke, wheels bottomed, hull (brick) contacts, the highest rebound, bounces, the settle
#     time, the strut's peak (rated weights), the damage (integrity lost, blocks lost).
#  C  THE DECK: the voxel grids cleared; WSDECK (static) 8 m up, a WSBRICK copy (WSDECKBRICK) over it; parked; the WSBRICK matrix
#     again with forward 0 and 10 m/s (the deck's length); voxel vs deck listed side by side.
#  JUDGED (Shocks on; WSBRICK at 1 and 3 m/s, WSLIGHT at 1 / 3 / 5): touched within 0.5 m/s of the asked sink, no wheel bottomed, no
#     hull contact, rebound under 30 % of the sink, no bounce, settled under 2 s, no damage; WSBRICK 5 m/s, WSOVER and Shocks Off
#     reported only; on vs off: the oleo bottoms no more wheels than vanilla.
#  D  the run's log: no exception in our code, no Havok assertion, no wheel shock fault.
#  release (also from the EXIT trap): Wheels.Shocks back to 2, the drop's dev values back, the test grids cleared (`gen clear`).
set -u
SUITE=wheelshock_test
T0=$(date +%s)
MOD="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
HARNESS=${HARNESS:-$MOD/../Orbital Mod/tools/harness}
[ -f "$HARNESS/testlib.sh" ] || HARNESS="/c/Users/slob/Documents/SpaceEngineers2/Mods/Orbital Mod/tools/harness"
source "$HARNESS/testlib.sh"
DEADLINE=${DEADLINE:-480}
ARGS=${ARGS:-${SE2_LAUNCH_ARGS:+$(cygpath -u "$SE2_LAUNCH_ARGS")}}
ARGS=${ARGS:-${AERO_TOOLS:-$MOD/tools/shared}/se2-launch-args.txt}
PART=${PART:-all}; SINKS=${SINKS:-1 3 5}; WHEEL=${WHEEL:-}
GEN=/c/Users/slob/AppData/Local/Temp/OrbitalMod/gen
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
nosplit() { ! grep -q "split off" <<< "$1"; }
all_of() { local a=(); while [ $# -gt 0 ]; do if [ "$1" = "--" ]; then "${a[@]}" || return 1; a=(); else a+=("$1"); fi; shift; done; [ ${#a[@]} -eq 0 ] || "${a[@]}"; }
# a field of the drop's result line ("... maxcomp 0.73 bottomed 0 hull 0 rebound 0.21 ...") / of the parked line
rf() { sed -n "s/.* $1 \(-\?[0-9.]*\).*/\1/p" <<< "$R" | head -1; }
pf() { sed -n "s/.* $1 \(-\?[0-9.]*\).*/\1/p" <<< "$P" | head -1; }
dropdone() { local p r; p=$(val WheelShockDev.DevPhase); r=$(val WheelShockDev.DevResult); [ "$p" = "idle" ] && [[ "$r" == "drop $1:"* ]] && [[ "$r" != *running* ]]; }
# a server-built read (the first read says "asked" - read again)
parked() { local a i; for i in $(seq 1 20); do a=$(val WheelShockDev.DevParked); case "$a" in asked*|"?") sleep 0.5 ;; *) echo "$a"; return 0 ;; esac; done; echo "${a:-no answer}"; }
spawned() { "$ORB" 'gen status' | grep -aq "spawned '$1'"; }

release() {
    [ -n "$RELEASED" ] && return; RELEASED=1
    alive || { echo "== release: no game running"; return; }
    local r=("aeroset WheelShockSettings.Mode 2" "aeroset WheelShockDev.DevForward 0" "aeroset WheelShockDev.DevSink 3" "aeroset WheelShockDev.DevGrid -" "gen clear")
    echo "== release: ${#r[@]} overrides (Wheels.Shocks 2, the drop's dev values, the test grids cleared)"
    ask "${r[@]}" | paste -d'|' <(printf '%s\n' "${r[@]}") - | sed 's/^/   /; s/|/  =>  /' | cut -c1-140
}
summary_all() {
    echo; echo "== $SUITE: $(el) s (deadline $DEADLINE s)"
    [ ${#notes[@]} -gt 0 ] && { echo "-- numbers"; for x in "${notes[@]}"; do echo "  $x"; done; }
    summary
}
[ "${1:-}" = "--release" ] && { release; exit 0; }

# ---- guards ------------------------------------------------------------------------------------------------------------------
W=$(startof < "$ARGS" 2>/dev/null)
case "$W" in "Orbital Test"*) ;; *) echo "REFUSED: the launch world is '$W' (-start: in $ARGS) - not an 'Orbital Test...' world."; exit 2 ;; esac
if [ -z "${NOLAUNCH:-}" ] && alive && [ -z "${KILL_GAME:-}" ]; then echo "REFUSED: Space Engineers 2 is running - it may be the user's game. KILL_GAME=1 closes it."; exit 2; fi
mkdir -p "$GEN"
# shellcheck disable=SC2086
python "$MOD/tools/ingame/build_wheelbrick.py" --out "$(cygpath -w "$GEN")" $WHEEL | sed 's/^/   /'
[ -f "$GEN/WSBRICK.txt" ] && [ -f "$GEN/WSDECK.txt" ] || { echo "build_wheelbrick.py wrote no specs into $GEN"; exit 2; }
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
record "loaded, the harness answers, this build has the wheel shocks ($(el) s)" ""
set_ WheelShockDev.DevNose 1; set_ WheelShockDev.DevRecordSeconds 4

n=0; flip=1
declare -A BOT RES
# spawn NAME SPEC [overrides...]: spawned, then whole (a split grid is the spec's fault - build_wheelbrick.py)
spawn() {
    local name=$1 spec=$2 w; shift 2
    ask "gen spawn $spec name=$name $*" >/dev/null
    poll 25 spawned "$name" || { record "spawned $name" "$("$ORB" 'gen status' | tail -1 | cut -c1-140)"; return 1; }
    hold 2
    # ("WHOLE|NOT WHOLE 'NAME' grid ID: HAVE of WANT blocks[; split off: ...] || ..." - judged per grid: every spec block on it,
    #  nothing split off; HAVE may count more than the spec if a suspension's wheel is its child - never fewer)
    w=$(ask "gen whole" | sed 's/ || /\n/g' | grep -a "'$name'" | head -1 | cut -c1-200)
    local have want
    have=$(sed -n 's/.*: \([0-9]*\) of \([0-9]*\) blocks.*/\1/p' <<< "$w"); want=$(sed -n 's/.*: \([0-9]*\) of \([0-9]*\) blocks.*/\2/p' <<< "$w")
    expect "$name spawned whole (every block joined, nothing split off)" "${w:-no gen whole line for $name}" all_of ge "$have" "${want:-1}" -- nosplit "$w"
}
# park NAME SURFACE judge|report: at rest with the shocks on - four rated wheels on the ground, inside the stroke
park() {
    set_ WheelShockSettings.Mode 2; set_ WheelShockDev.DevGrid "$1"; hold 4
    P=$(parked); note "$1 parked on $2: $P"
    local u g w; u=$(pf maxcomp); g=$(pf ground); w=$(pf wheels)
    [ "$3" = judge ] && expect "$1 on $2: four wheels rated, on the ground, inside the stroke (deepest $u)" "$P" all_of ge "$w" 4 -- ge "$g" 4 -- lt "$u" 0.95
}
# drop NAME SURFACE MODE FWD SINK judge|report
drop() {
    local name=$1 surf=$2 mode=$3 fwd=$4 v=$5 judge=$6 f
    budget 25 || { skip "$name on $surf: mode $mode forward $fwd sink $v" "deadline"; return; }
    n=$((n + 1)); f=$fwd; [ "$fwd" != 0 ] && { f=$((fwd * flip)); flip=$((-flip)); }
    set_ WheelShockSettings.Mode "$mode"; set_ WheelShockDev.DevGrid "$name"
    set_ WheelShockDev.DevSink "$v"; set_ WheelShockDev.DevForward "$f"; set_ WheelShockDev.DevDrop "$n"
    if ! poll 20 dropdone "$n"; then record "$name on $surf: drop $n (mode $mode, forward $f, sink $v) finished" "$(val WheelShockDev.DevPhase) | $(val WheelShockDev.DevResult | cut -c1-160)"; return; fi
    R=$(val WheelShockDev.DevResult)
    note "$surf $name mode $mode fwd $f sink $v: $(echo "$R" | cut -c1-340)"
    if [[ "$R" == *aborted* ]]; then record "$name on $surf: drop $n (mode $mode, forward $f, sink $v)" "$R"; return; fi
    local tv mc bo hu rb bn st dm lost k
    tv=$(rf touch); mc=$(rf maxcomp); bo=$(rf bottomed); hu=$(rf hull); rb=$(rf rebound); bn=$(rf bounces); st=$(rf settle); dm=$(rf damage); lost=$(rf lost)
    BOT["$surf/$name/$mode/$fwd/$v"]=$bo; RES["$surf/$name/$mode/$fwd/$v"]="comp $mc bottomed $bo hull $hu rebound $rb bounces $bn settle $st"
    [ "$mode" = 2 ] && [ "$judge" = judge ] || return 0
    k="$name on $surf, forward $fwd, $v m/s"
    expect "$k - touched at $v +- 0.5 m/s" "touch $tv" near "$tv" "$v" 0.5
    expect "$k - no wheel bottomed (deepest $mc), no hull contact" "bottomed $bo hull $hu" all_of eq "$bo" 0 -- eq "$hu" 0
    expect "$k - rebound under 30 % of the sink, no bounce" "rebound $rb bounces $bn" all_of lt "$rb" "$(awk -v v="$v" 'BEGIN{print 0.3*v}')" -- eq "$bn" 0
    expect "$k - settled under 2 s, no damage" "settle $st damage $dm lost $lost" all_of le 0 "$st" -- lt "$st" 2 -- lt "$dm" 0.001 -- eq "$lost" 0
}
# matrix NAME SURFACE "FORWARDS": Shocks off and on x forwards x sinks (judged at 1 and 3 m/s), then on vs off
matrix() {
    local fwd mode v off on
    for fwd in $3; do for mode in 0 2; do for v in $SINKS; do
        drop "$1" "$2" "$mode" "$fwd" "$v" "$([ "$v" = 5 ] && echo report || echo judge)"
    done; done; done
    for fwd in $3; do for v in $SINKS; do
        off=${BOT["$2/$1/0/$fwd/$v"]:-}; on=${BOT["$2/$1/2/$fwd/$v"]:-}
        [ -n "$off" ] && [ -n "$on" ] && expect "$1 on $2, forward $fwd, $v m/s - the oleo bottoms no more wheels than vanilla ($on vs $off)" "on $on off $off" le "$on" "$off"
    done; done
}

# ---- A + B: voxel ground ---------------------------------------------------------------------------------------------------------
if [ "$PART" != deck ]; then
    echo "== A voxel: spawn ($(el) s)"
    spawn WSBRICK WSBRICK.txt ahead=40 up=4; spawn WSLIGHT WSLIGHT.txt ahead=40 up=4 right=14; spawn WSOVER WSOVER.txt ahead=40 up=4 right=-16
    hold 4
    park WSBRICK voxel judge; park WSLIGHT voxel judge; park WSOVER voxel report
    echo "== B voxel: drops ($(el) s)"
    matrix WSBRICK voxel "0 45"
    for v in $SINKS; do drop WSLIGHT voxel 2 0 "$v" judge; done
    for v in $SINKS; do drop WSOVER voxel 2 0 "$v" report; done
    ask "gen clear" >/dev/null; hold 2
fi

# ---- C: the static deck ----------------------------------------------------------------------------------------------------------
if [ "$PART" != voxel ]; then
    echo "== C deck ($(el) s)"
    spawn WSDECK WSDECK.txt ahead=40 up=8
    spawn WSDECKBRICK WSBRICK.txt ahead=40 up=11.5
    hold 4
    park WSDECKBRICK deck judge
    matrix WSDECKBRICK deck "0 10"
    if [ "$PART" = all ]; then
        for mode in 0 2; do for v in $SINKS; do
            note "voxel vs deck, mode $mode, $v m/s - voxel: ${RES["voxel/WSBRICK/$mode/0/$v"]:-none} | deck: ${RES["deck/WSDECKBRICK/$mode/0/$v"]:-none}"
        done; done
    fi
fi

# ---- D: the log ------------------------------------------------------------------------------------------------------------------
echo "== D log ($(el) s)"
x=$(ourexc "$L" "$((MARK + 1))" | head -3); record "D: no exception in our code" "${x:+$x}"
b=$(bad); record "D: no Havok assertion, no fatal crash" "${b:+$b}"
f=$(val AeroWheelShockComponent.Faults); expect "D: no wheel shock faults" "faults $f ($(val AeroWheelShockComponent.LastFault))" eq "$f" 0
alive || record "D: the game alive at the end" "it died"
