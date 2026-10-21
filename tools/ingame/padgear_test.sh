#!/usr/bin/env bash
# padgear_test.sh - THE PAD-FOOT GEARS IN GAME, MIDAIR (2026-10-20; midair since 2026-10-21 - tools/ingame/midair.sh; Integration/
# AeroGearComponent.PadFoot.cs, Dev/WheelShockDev.cs). One launch of an 'Orbital Test...' world on a planet (no flat ground needed: the
# user, "spawn it midair and with relative velocities, measure change in momentum"; "no aero interference"), the Orbital Mod's harness
# driving it; <= 8 min; PASS / FAIL / SKIP from the aero mod's own probes (aeroget AeroGearComponent.Summary / PadRecordResult,
# WheelShockDev.DevResult / DevImpact) and the game log. Run it in the background; never while someone plays.
#
# usage: tools/ingame/padgear_test.sh           the run. REFUSES while a game is running (it may be the user's): KILL_GAME=1 closes
#                                              it - only when nobody plays. REFUSES unless the launch world is an 'Orbital Test...'
#                                              world (it spawns test grids into the world it loads).
#        tools/ingame/padgear_test.sh --release only the dev overrides back in the running game (after a killed run)
# env: HARNESS, DEADLINE s (420), ARGS (the launch args file), NOLAUNCH=1, AXES (the lock axes swept: "3 4 5"), PART (rig | drops | all),
#      MIDAIR_UP (2000 m)
# needs: THIS build deployed to the game's mod folder (scripts + the content build: tools/shared/build_mod_content.py)
#
#  M  midair: a STATIC armour deck 2 km up (WSDECK, build_wheelbrick.py), the player seated on it (midair.sh).
#  A  RIG (PADRIG: one of each size under a STATIC platform, in the air beside the deck): four feet with their box collider and their
#     orientation lock; still 5 s - each foot within 1 deg of its block's up, turning < 0.02 rad/s; retract - four up, level in the
#     well (< 2 deg); extend - four down, locks still there.
#  B  THE LOCK AXIS SWEEP (AXES; AeroGearComponent.PadLockAxis: the 6-DOF axis the foot's lock fixes - 3 AngularX by default): for each,
#     a fresh PADTEST (33 t on three 1.5 m pads; the lock is made as the foot's body is) spawned over the deck, levelled, dampeners off,
#     and dropped (WheelShockDev midair: no air on it, the momentum ledger): sink 1 and 3 m/s, then a SLIDE (forward 10 m/s at sink 1).
#     Per drop: the touch, the rebound, bottoming, and per foot its tilt off its spawn attitude and its turn rate against the ship about
#     its own X (the axle) / Y / Z. JUDGED: the default axis - touch within 0.6 m/s, rebound under 40 %, no bottom-out, no hull contact,
#     every foot level (tilt < 3 deg) and not turning (each rate < 0.3 rad/s), the locks kept; the others reported (which axis holds).
#  C  the run's log: no gear fault, no exception in our code, no Havok assertion; the test grids removed at the end.
#  release (also from the EXIT trap): PadRecord off, PadHop / PadPush 0, PadLockAxis back, gears down, the drop's dev values, gen clear.
SUITE=padgear_test; GENCLEAR=1; source "$(dirname "$0")/padlib.sh"
AXES=${AXES:-3 4 5}; PART=${PART:-all}; AXIS0=$(grep -o 'PadLockAxis = [0-9]' "$MOD/Scripts/Simulation/Integration/AeroGearComponent.PadFoot.cs" | grep -o '[0-9]$')
[ "${1:-}" = "--release" ] && { release; exit 0; }
W=$(startof < "$ARGS" 2>/dev/null)
case "$W" in "Orbital Test"*) ;; *) echo "REFUSED: the launch world is '$W' (-start: in $ARGS) - not an 'Orbital Test...' world."; exit 2 ;; esac
if [ -z "${NOLAUNCH:-}" ] && alive && [ -z "${KILL_GAME:-}" ]; then echo "REFUSED: Space Engineers 2 is running - it may be the user's game. KILL_GAME=1 closes it."; exit 2; fi
trap 'release; summary_all' EXIT
trap 'exit 130' INT TERM
mkdir -p "$GEN"; cp "$MOD/tools/ingame/PADTEST.txt" "$MOD/tools/ingame/PADRIG.txt" "$GEN/"
python "$MOD/tools/ingame/build_wheelbrick.py" --out "$(cygpath -w "$GEN")" >/dev/null   # (the midair deck: WSDECK)

# ---- launch --------------------------------------------------------------------------------------------------------------------
if [ -z "${NOLAUNCH:-}" ]; then
    old=$(LOGF 2>/dev/null); mkdir -p "$(dirname "$ST")"; touch "$(dirname "$ST")/dev.flag"; rm -f "$(dirname "$ST")/cmd.txt"
    echo "== launch ($(el) s): '$W'"; launch
    for i in $(seq 1 150); do L=$(LOGF 2>/dev/null); [ -n "$L" ] && [ "$L" != "$old" ] && grep -aq 'beacon Root Entity\|Loading Failed' "$L" 2>/dev/null && break; sleep 1; done
fi
L=$(LOGF); MARK=0; [ -n "${NOLAUNCH:-}" ] && MARK=$(wc -l < "$L")
since() { tail -n +"$((MARK + 1))" "$L"; }
grep -aq 'Loading Failed' "$L" && { record "the world loaded" "Loading Failed in $L"; exit 1; }
poll 60 gridsup || { record "the harness answers" "findgrids unanswered 60 s after the load"; exit 1; }
num "$(val AeroGearComponent.PadLocks)" || { record "aero: the pad gears' dev statics (AeroGearComponent.Pad*)" "not in this build - deploy this branch"; exit 1; }
[ "$(val WheelShockDev.DevImpact)" = "?" ] && { record "aero: the midair drop's ledger (WheelShockDev.DevImpact)" "not in this build - deploy it"; exit 1; }
record "loaded, the harness answers, this build has the pad gears and the midair ledger ($(el) s)" ""
set_ AeroGearComponent.PadRecord true
source "$MOD/tools/ingame/midair.sh"
ask "encon off" >/dev/null
echo "== M midair ($(el) s)"
midair_setup || exit 1

# ---- A: the rig (static, in the air beside the deck) -----------------------------------------------------------------------------
if [ "$PART" != drops ]; then
echo "== A rig ($(el) s)"
ask "gen spawn PADRIG.txt ahead=45 right=-10 up=12" >/dev/null
poll 20 spawned PADRIG
expect "A: the rig spawned whole" "$(ask 'gen whole' | cut -c1-120)" whole
PR=$(gid PADRIG)
poll 20 locked_ge 4
G=$(gear); note "rig: $(echo "$G" | cut -c1-240)"
expect "A: four feet locked level" "$(locked_n) locked" locked_ge 4
expect "A: four feet on their box collider" "$(boxed_n) boxed" ge "$(boxed_n)" 4
set_ AeroGearComponent.PadReset 1; hold 5
t=$(recf tilt); s=$(recf spin); note "rig still: tilt $t deg, spin $s rad/s | $(rec | cut -c1-300)"
expect "A: still - every foot level (< 1 deg) and not turning (< 0.02 rad/s)" "tilt $t, spin $s" both_lt "$t" 1 "$s" 0.02
set_ AeroGearComponent.PadReset 2
set_ AeroGearComponent.DevCommand 2; hold 7
G=$(gear); t=$(recf tilt); note "retracted: tilt $t | $(echo "$G" | cut -c1-200)"
expect "A: retracted - four up" "$(echo "$G" | cut -c1-200)" ge "$(count "$G" '| up p 0.00')" 4
expect "A: retracted - the feet level in the well (< 2 deg)" "tilt $t" lt "$t" 2
set_ AeroGearComponent.DevCommand 1; hold 7; set_ AeroGearComponent.DevCommand 0
G=$(gear)
expect "A: extended - four down, still locked" "$(echo "$G" | cut -c1-200)" all_of ge "$(count "$G" 'down p 1.00')" 4 -- locked_ge 4
[ -n "$PR" ] && rig_del "$PR"
fi

# ---- B: the lock axis sweep, midair drops ---------------------------------------------------------------------------------------
# feet: every "| foot NAME tilt T rate X Y Z" of the drop R - the largest tilt and the largest rate about each axis
feet() {
    awk -v RS='|' '/ foot / { for (i = 1; i <= NF; i++) { if ($i == "tilt") { t = $(i+1) + 0; if (t > mt) mt = t }
        if ($i == "rate") { x = $(i+1) + 0; y = $(i+2) + 0; z = $(i+3) + 0; if (x > mx) mx = x; if (y > my) my = y; if (z > mz) mz = z } } n++ }
        END { printf "feet %d tilt %.2f rx %.3f ry %.3f rz %.3f\n", n, mt, mx, my, mz }' <<< "$R"
}
ff() { sed -n "s/.* $1 \([0-9.]*\).*/\1/p" <<< "$F" | head -1; }
declare -A AX
flip=1
if [ "$PART" != rig ]; then
for ax in $AXES; do
    budget 70 || { skip "B: lock axis $ax" "deadline"; continue; }
    echo "== B lock axis $ax ($(el) s)"
    set_ AeroGearComponent.PadLockAxis "$ax"
    rig "PADA$ax" PADTEST.txt 7 || continue
    poll 15 locked_ge 3
    note "axis $ax: $(locked_n) feet locked level"
    for d in "1 0 4" "3 0 3" "1 10 3"; do
        set -- $d; v=$1; fw=$2; secs=$3; [ "$fw" != 0 ] && { fw=$((fw * flip)); flip=$((-flip)); }
        budget 20 || { skip "B: axis $ax sink $v fwd $fw" "deadline"; continue; }
        if ! dropm "PADA$ax" 2 "$fw" "$v" "$secs" || [[ "$R" == *aborted* ]]; then record "B: axis $ax sink $v fwd $fw - the drop ran" "$R"; continue; fi
        F=$(feet); tv=$(rf touch); rb=$(rf rebound); bo=$(rf bottomed); hu=$(rf hull)
        tl=$(ff tilt); rx=$(ff rx); ry=$(ff ry); rz=$(ff rz)
        AX["$ax/$v/$fw"]="touch $tv reb $rb bot $bo hull $hu | $F | aero $(imf aero) T $(imf impact) peak $(imf peakFn) kN dPn $(imf dPn) M(vi+vo) $(imf 'M(vin+vout)') G $(imf G) Jcid $(imf Jcid) Lmax $(imf Lmax)"
        note "axis $ax sink $v fwd $fw: ${AX["$ax/$v/$fw"]}"
        note "   ledger: $(echo "$I" | cut -c1-480)"
        expect "B: axis $ax sink $v fwd $fw - no air on the rig" "aero $(imf aero)" lt "$(imf aero)" 1
        [ "$ax" = "$AXIS0" ] || continue
        k="B: default axis $ax, sink $v fwd $fw"
        expect "$k - touched at $v +- 0.6 m/s" "touch $tv" near "$tv" "$v" 0.6
        expect "$k - rebound under 40 % of it, never bottomed, no hull contact" "rebound $rb, bottomed $bo, hull $hu" all_of lt "$rb" "$(awk -v v=$v 'BEGIN{print 0.4*v}')" -- eq "$bo" 0 -- eq "$hu" 0
        expect "$k - every foot level (tilt < 3 deg) and not turning (< 0.3 rad/s each axis)" "$F" all_of lt "$tl" 3 -- lt "$rx" 0.3 -- lt "$ry" 0.3 -- lt "$rz" 0.3
    done
    lo=$(val AeroGearComponent.PadLocksLost); expect "B: axis $ax - no lock lost so far" "lost $lo" eq "$lo" 0
    rig_del "$RIG"
done
echo "-- the lock axis table (axis/sink/forward)"
for k in $(printf '%s\n' "${!AX[@]}" | sort); do note "$k: ${AX[$k]}"; done
fi

# ---- C: the log ---------------------------------------------------------------------------------------------------------------
echo "== C log ($(el) s)"
never "C: no gear fault" "\[AERO-GEAR\] fault"
x=$(ourexc "$L" "$((MARK + 1))" | head -3); record "C: no exception in our code" "${x:+$x}"
b=$(bad); record "C: no Havok assertion, no fatal crash" "${b:+$b}"
note "counters: locks $(val AeroGearComponent.PadLocks) lost $(val AeroGearComponent.PadLocksLost) boxes $(val AeroGearComponent.PadBoxes)"
