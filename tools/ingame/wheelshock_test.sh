#!/usr/bin/env bash
# wheelshock_test.sh - THE WHEEL SHOCKS IN GAME, MIDAIR (2026-10-21; Integration/AeroWheelShockComponent.cs, Dev/WheelShockDev.cs,
# tools/ingame/midair.sh). One launch of an 'Orbital Test...' world on a planet - NO flat ground needed (the user: "spawn it midair and
# with relative velocities, measure change in momentum"; "make sure there's no aero interference"); <= 8 min; PASS / FAIL / SKIP from
# the aero mod's own probes (aeroget WheelShockDev.DevResult / DevImpact, AeroWheelShockComponent.Faults) and the game log. Run it in
# the background (run_in_background + Monitor), never while someone plays.
#
# THE RIG: 2 km over the terrain a STATIC 27.5 x 180 m armour deck (WSDECK), the player seated on it; the HEAVY ARMOUR BRICKS on four
# vanilla 1 m suspensions (tools/ingame/build_wheelbrick.py; WSBRICK ~56 t: 93 % of the 1 m wheel's 15 t rating, WSLIGHT ~16 t: 27 %,
# WSOVER ~123 t: 204 %) spawned over it one at a time, levelled, dampeners off. The wheels are a parameter: WHEEL="..." is passed to
# build_wheelbrick.py (e.g. a pad-gear variant: "--wheel custom --wheel-guid <server prefab> --wheel-box ... --mount bottom").
# EACH DROP (WheelShockDev, DevApproach + DevNoAero): lifted clear of the deck, 12 free ticks (the gravity the bodies feel, measured),
# the approach - the last half metre at exactly the sink and forward speed asked, level - then free and recorded: the touch speed,
# per wheel the deepest compression of its stroke and the ticks at its limit, hull contacts, the rebound, bounces, the settle time,
# the strut's peak; and THE MOMENTUM LEDGER: the rig's P = sum m v (the grid and its wheel bodies), the grid's I w, the contact force
# dP/dt - M g - F_aero, the impact (its normal force's first lobe over 10 % of the peak): its duration, peak force, dP along the
# normal against M (v_in + v_out), the gravity's impulse M g T, the contact impulse, the tangential impulse, the rig's own contact
# impulses against the deck (ContactImpulseData, an independent check); the air's force on the rig every tick (must be 0).
#
# usage: tools/ingame/wheelshock_test.sh            the run. REFUSES while a game is running (it may be the user's): KILL_GAME=1
#                                                   closes it - only when nobody plays. REFUSES unless the launch world is an
#                                                   'Orbital Test...' world (it spawns test grids into the world it loads).
#        tools/ingame/wheelshock_test.sh --release  only the dev overrides back in the running game (after a killed run)
# env: HARNESS (the Orbital Mod's tools/harness), DEADLINE s (470), ARGS (the launch args file), NOLAUNCH=1 (the game already
#      running), PART (brick | others | all), SINKS ("1 3 5"), WHEEL (build_wheelbrick.py's args), MIDAIR_UP (2000 m)
# needs: THIS build deployed to the game's mod folder.
#
#  A  midair: the deck spawned 2 km up, the player seated on it.
#  B  (PART brick | all) WSBRICK: Shocks Off (0) and Airshock + oleo (2) x forward 0 / 45 m/s x sink 1 / 3 / 5.
#  C  (PART others | all) WSLIGHT and WSOVER, Shocks on, forward 0, sink 1 / 3 / 5; WSBRICK forward 10 (mode 0 and 2, sink 3).
#  JUDGED (Shocks on; WSBRICK 1 and 3 m/s, WSLIGHT 1 / 3 / 5): touched within 0.5 m/s of the sink asked, no wheel bottomed, no hull
#     contact, rebound under 30 % of the sink, no bounce, settled under 2 s (records of 4 s only), no damage; on vs off: the oleo
#     bottoms no more wheels than vanilla. EVERY drop: no air on the rig (the aero force 0), the ledger closes (the rig's own contact impulses against the deck within 10 %
#     of Jn = dPn + M g T, from the momentum). WSBRICK 5 m/s and WSOVER reported only.
#  D  the run's log: no exception in our code, no Havok assertion, no wheel shock fault.
#  release (also from the EXIT trap): the drop's dev values back, Wheels.Shocks 2, the test grids cleared (`gen clear`).
set -u
SUITE=wheelshock_test
T0=$(date +%s)
MOD="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
HARNESS=${HARNESS:-$MOD/../Orbital Mod/tools/harness}
[ -f "$HARNESS/testlib.sh" ] || HARNESS="/c/Users/slob/Documents/SpaceEngineers2/Mods/Orbital Mod/tools/harness"
source "$HARNESS/testlib.sh"
DEADLINE=${DEADLINE:-470}
ARGS=${ARGS:-${SE2_LAUNCH_ARGS:+$(cygpath -u "$SE2_LAUNCH_ARGS")}}
ARGS=${ARGS:-${AERO_TOOLS:-$MOD/tools/shared}/se2-launch-args.txt}
PART=${PART:-all}; SINKS=${SINKS:-1 3 5}; WHEEL=${WHEEL:-}
GEN=/c/Users/slob/AppData/Local/Temp/OrbitalMod/gen
RELEASED=""; notes=()

el() { echo $(( $(date +%s) - T0 )); }
budget() { [ $(( $(date +%s) - T0 + $1 )) -le "$DEADLINE" ]; }
note() { notes+=("$1"); echo "   $1"; }
poll() { local end=$(( $(date +%s) + $1 )); shift; while :; do "$@" >/dev/null 2>&1 && return 0; alive || return 1; [ "$(date +%s)" -ge "$end" ] && return 1; sleep 1; done; }
startof() { grep -ao '\-start:[^"]*' | head -1 | sed -E 's/^-start://; s/[[:space:]]+-[a-zA-Z]+(:.*|[[:space:]].*)?$//; s/[[:space:]]*$//'; }
lt() { awk -v a="$1" -v b="$2" 'BEGIN{exit !(a != "" && a + 0 < b + 0)}'; }
le() { awk -v a="$1" -v b="$2" 'BEGIN{exit !(a != "" && a + 0 <= b + 0)}'; }
near() { awk -v a="$1" -v b="$2" -v t="$3" 'BEGIN{exit !(a != "" && (a - b < t) && (b - a < t))}'; }
eq() { [ "$1" = "$2" ]; }
ge() { [ -n "$1" ] && [ "$1" -ge "$2" ] 2>/dev/null; }
all_of() { local a=(); while [ $# -gt 0 ]; do if [ "$1" = "--" ]; then "${a[@]}" || return 1; a=(); else a+=("$1"); fi; shift; done; [ ${#a[@]} -eq 0 ] || "${a[@]}"; }
spawned() { "$ORB" 'gen status' | grep -aq "spawned '$1'"; }
# the ledger closes: |dPn - M(vin+vout)| within 10 % (or 0.5 kN s)
closes() { awk -v a="$1" -v b="$2" 'BEGIN{d=a-b; if (d<0) d=-d; m=(a<0?-a:a); exit !(a != "" && b != "" && (d <= 0.10*m || d <= 0.5))}'; }
source "$MOD/tools/ingame/midair.sh"

release() {
    [ -n "$RELEASED" ] && return; RELEASED=1
    alive || { echo "== release: no game running"; return; }
    echo "== release: the drop's dev values back (approach / no-air off, Wheels.Shocks 2), the test grids cleared"
    midair_release; ask "gen clear" | sed 's/^/   /'
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
[ "$(val WheelShockDev.DevImpact)" = "?" ] && { record "aero: the midair drop's ledger (WheelShockDev.DevImpact)" "not in this build - deploy it"; exit 1; }
record "loaded, the harness answers, this build has the midair ledger ($(el) s)" ""
ask "encon off" "aeroset WheelShockDev.DevNose 1" >/dev/null

declare -A BOT RES
# drop NAME MODE FWD SINK SECONDS judge|report
drop() {
    local name=$1 mode=$2 fwd=$3 v=$4 secs=$5 judge=$6 key
    budget 20 || { skip "$name: mode $mode forward $fwd sink $v" "deadline"; return; }
    dropm "$name" "$mode" "$fwd" "$v" "$secs" || { record "$name: mode $mode forward $fwd sink $v - the drop ran" "$R"; return; }
    note "$name mode $mode fwd $fwd sink $v: $(echo "$R" | cut -c1-300)"
    note "   ledger: $(echo "$I" | cut -c1-520)"
    if [[ "$R" == *aborted* ]]; then record "$name: mode $mode forward $fwd sink $v - the drop ran" "$R"; return; fi
    local tv mc bo hu rb bn st dm lost aero dpn mv
    tv=$(rf touch); mc=$(rf maxcomp); bo=$(rf bottomed); hu=$(rf hull); rb=$(rf rebound); bn=$(rf bounces); st=$(rf settle); dm=$(rf damage); lost=$(rf lost)
    aero=$(imf aero); dpn=$(imf dPn); mv=$(imf "M(vin+vout)")
    key="$name/$mode/$fwd/$v"; BOT[$key]=$bo
    RES[$key]="touch $tv comp $mc bot $bo hull $hu reb $rb bnc $bn settle $st | T $(imf impact) peak $(imf peakFn) kN dPn $dpn M(vi+vo) $mv G $(imf G) Jn $(imf Jn) Jcid $(imf Jcid) Jt $(imf Jt) Lmax $(imf Lmax) aero $aero"
    expect "$name mode $mode fwd $fwd sink $v - no air on the rig (aero force $aero N)" "aero $aero N" lt "${aero:-1}" 1
    local jc jn; jc=$(imf Jcid); jn=$(imf Jn)
    expect "$name mode $mode fwd $fwd sink $v - the ledger closes: the deck's own contact impulse Jcid $jc vs Jn $jn = dPn + M g T (kN s)" "Jcid $jc Jn $jn" closes "$jc" "$jn"
    [ "$mode" = 2 ] && [ "$judge" = judge ] || return 0
    local k="$name fwd $fwd, $v m/s"
    expect "$k - touched at $v +- 0.5 m/s" "touch $tv" near "$tv" "$v" 0.5
    expect "$k - no wheel bottomed (deepest $mc), no hull contact" "bottomed $bo hull $hu" all_of eq "$bo" 0 -- eq "$hu" 0
    expect "$k - rebound under 30 % of the sink, no bounce" "rebound $rb bounces $bn" all_of lt "$rb" "$(awk -v v="$v" 'BEGIN{print 0.3*v}')" -- eq "$bn" 0
    if [ "${secs%.*}" -ge 3 ]; then expect "$k - settled under 2 s, no damage" "settle $st damage $dm lost $lost" all_of le 0 "$st" -- lt "$st" 2 -- lt "$dm" 0.001 -- eq "$lost" 0
    else expect "$k - no damage" "damage $dm lost $lost" all_of lt "$dm" 0.001 -- eq "$lost" 0; fi
}
secs_for() { case "$1" in 0) echo 4 ;; 10|-10) echo 3 ;; *) echo 1.2 ;; esac; }

echo "== A midair ($(el) s)"
midair_setup || exit 1
flip=1
if [ "$PART" != others ]; then
    echo "== B WSBRICK ($(el) s)"
    if rig WSBRICK WSBRICK.txt 2; then
        for fwd in 0 45; do for mode in 0 2; do for v in $SINKS; do
            f=$fwd; [ "$fwd" != 0 ] && { f=$((fwd * flip)); flip=$((-flip)); }
            drop WSBRICK "$mode" "$f" "$v" "$(secs_for "$fwd")" "$([ "$v" = 5 ] && echo report || echo judge)"
        done; done; done
        for fwd in 0 45; do for v in $SINKS; do
            off=""; on=""
            for k in "${!BOT[@]}"; do case "$k" in WSBRICK/0/$fwd/$v|WSBRICK/0/-$fwd/$v) off=${BOT[$k]} ;; WSBRICK/2/$fwd/$v|WSBRICK/2/-$fwd/$v) on=${BOT[$k]} ;; esac; done
            [ -n "$off" ] && [ -n "$on" ] && expect "WSBRICK fwd $fwd, $v m/s - the oleo bottoms no more wheels than vanilla ($on vs $off)" "on $on off $off" le "$on" "$off"
        done; done
        rig_del "$RIG"
    fi
fi
if [ "$PART" != brick ]; then
    echo "== C WSLIGHT, WSOVER, forward 10 ($(el) s)"
    if rig WSLIGHT WSLIGHT.txt 2; then for v in $SINKS; do drop WSLIGHT 2 0 "$v" 4 judge; done; rig_del "$RIG"; fi
    if rig WSOVER WSOVER.txt 3; then for v in $SINKS; do drop WSOVER 2 0 "$v" 4 report; done; rig_del "$RIG"; fi
    if [ "$PART" = others ] && rig WSBRICK WSBRICK.txt 2; then
        for mode in 0 2; do f=$((10 * flip)); flip=$((-flip)); drop WSBRICK "$mode" "$f" 3 3 report; done
        rig_del "$RIG"
    fi
fi
echo "-- the table (key: rig/mode/forward/sink)"
for k in $(printf '%s\n' "${!RES[@]}" | sort -t/ -k1,1 -k3,3n -k2,2n -k4,4n); do note "$k: ${RES[$k]}"; done

# ---- D: the log ------------------------------------------------------------------------------------------------------------------
echo "== D log ($(el) s)"
x=$(ourexc "$L" "$((MARK + 1))" | head -3); record "D: no exception in our code" "${x:+$x}"
b=$(bad); record "D: no Havok assertion, no fatal crash" "${b:+$b}"
f=$(val AeroWheelShockComponent.Faults); expect "D: no wheel shock faults" "faults $f ($(val AeroWheelShockComponent.LastFault))" eq "$f" 0
alive || record "D: the game alive at the end" "it died"
