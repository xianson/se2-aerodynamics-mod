#!/usr/bin/env bash
# padgear_test.sh - THE PAD-FOOT GEARS IN GAME (2026-10-20; Integration/AeroGearComponent.PadFoot.cs). One launch of an
# 'Orbital Test...' world (the Orbital Mod's harness drives it: its testlib.sh / orb.sh, `gen` spawns), ~5 min, PASS / FAIL / SKIP
# from the aero mod's own probes (aeroget AeroGearComponent.Summary / PadRecordResult / counters) and the game log - nothing judged
# by eye. Run it in the background; never while someone plays. Stand the player on flat open ground, looking level (the spawns are
# placed from the camera).
#
# usage: tools/ingame/padgear_test.sh           the run. REFUSES while a game is running (it may be the user's): KILL_GAME=1 closes
#                                              it - only when nobody plays. REFUSES unless the launch world is an 'Orbital Test...'
#                                              world (it spawns test grids into the world it loads).
#        tools/ingame/padgear_test.sh --release only the dev overrides back in the running game (after a killed run)
# env: HARNESS (the Orbital Mod's tools/harness; default the sibling checkout's), DEADLINE s (420), ARGS (the launch args file),
#      NOLAUNCH=1 (use the game already running - still a test world)
# needs: THIS branch's aero build deployed to the game's mod folder (scripts + the content build: tools/shared/build_mod_content.py)
#
#  A  RIG (PADRIG: one of each size, static, 10 m up): four feet with their box collider and their orientation lock; still 5 s - each
#     foot within 1 deg of its block's up, turning < 0.02 rad/s about its axle; retract - four up, the feet level in the well (tilt
#     < 2 deg); extend - four down, locks still there (or made again: never none).
#  B  SHIP (PADTEST, 33 t on three 1.5 m pads, dropped on the ground): three locked level; parked 4 s - never bottomed, tilt < 2 deg;
#     HOPS at 1 / 3 / 5 m/s (PadHop: the ship and its feet up at v - it lands at v): the touch speed within 0.6 m/s of v, the
#     rebound under 40 % of v (1 and 3), no bottom-out at 1 and 3 (5: reported), tilt < 3 deg, spin < 0.3 rad/s, the lock never
#     lost; SLIDE (PadPush 5 m/s forward on the ground): the feet stay level (tilt < 3 deg, spin < 0.3 rad/s - they slide, not roll);
#     retracting on the ground is the gear's own rule (no weight gate): not tested here.
#  C  the run's log: no gear fault, no exception in our code, no Havok assertion; the test grids removed at the end.
#  release (also from the EXIT trap): PadRecord off, PadHop / PadPush 0, gears down, gen clear.
SUITE=padgear_test; GENCLEAR=1; source "$(dirname "$0")/padlib.sh"
[ "${1:-}" = "--release" ] && { release; exit 0; }
W=$(startof < "$ARGS" 2>/dev/null)
case "$W" in "Orbital Test"*) ;; *) echo "REFUSED: the launch world is '$W' (-start: in $ARGS) - not an 'Orbital Test...' world."; exit 2 ;; esac
if [ -z "${NOLAUNCH:-}" ] && alive && [ -z "${KILL_GAME:-}" ]; then echo "REFUSED: Space Engineers 2 is running - it may be the user's game. KILL_GAME=1 closes it."; exit 2; fi
trap 'release; summary_all' EXIT
trap 'exit 130' INT TERM
mkdir -p "$GEN"; cp "$MOD/tools/ingame/PADTEST.txt" "$MOD/tools/ingame/PADRIG.txt" "$GEN/"

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
record "loaded, the harness answers, this build has the pad gears ($(el) s)" ""
set_ AeroGearComponent.PadRecord true

# ---- A: the rig ------------------------------------------------------------------------------------------------------------------
echo "== A rig ($(el) s)"
ask "gen spawn PADRIG.txt" >/dev/null
poll 20 spawned PADRIG
expect "A: the rig spawned whole" "$(ask 'gen whole' | cut -c1-120)" whole
poll 20 locked_ge 4
G=$(gear); note "rig: $(echo "$G" | cut -c1-240)"
expect "A: four feet locked level" "$(locked_n) locked" locked_ge 4
expect "A: four feet on their box collider" "$(boxed_n) boxed" ge "$(boxed_n)" 4
set_ AeroGearComponent.PadReset 1; hold 5
t=$(recf tilt); s=$(recf spin); note "rig still: tilt $t deg, spin $s rad/s | $(rec | cut -c1-200)"
expect "A: still - every foot level (< 1 deg) and not turning (< 0.02 rad/s)" "tilt $t, spin $s" both_lt "$t" 1 "$s" 0.02
set_ AeroGearComponent.PadReset 2
set_ AeroGearComponent.DevCommand 2; hold 7
G=$(gear); t=$(recf tilt); note "retracted: tilt $t | $(echo "$G" | cut -c1-200)"
expect "A: retracted - four up" "$(echo "$G" | cut -c1-200)" ge "$(count "$G" '| up p 0.00')" 4
expect "A: retracted - the feet level in the well (< 2 deg)" "tilt $t" lt "$t" 2
set_ AeroGearComponent.DevCommand 1; hold 7; set_ AeroGearComponent.DevCommand 0
G=$(gear)
expect "A: extended - four down, still locked" "$(echo "$G" | cut -c1-200)" all_of ge "$(count "$G" 'down p 1.00')" 4 -- locked_ge 4
ask "gen clear" >/dev/null

# ---- B: the ship on the ground ------------------------------------------------------------------------------------------------
echo "== B ship ($(el) s)"
ask "gen spawn PADTEST.txt" >/dev/null
poll 20 spawned PADTEST
expect "B: the ship spawned whole" "$(ask 'gen whole' | cut -c1-120)" whole
poll 20 locked_ge 3
hold 6
expect "B: three feet locked level" "$(locked_n) locked" locked_ge 3
set_ AeroGearComponent.PadReset 3; hold 4
R=$(rec); note "parked: $(echo "$R" | cut -c1-220)"
expect "B: parked - never bottomed, level (< 2 deg)" "$(echo "$R" | cut -c1-160)" all_of eq "$(count "$R" 'bottom 1')" 0 -- lt "$(recf tilt)" 2
for v in 1 3 5; do
    budget 50 || { skip "B: hop at $v m/s" "deadline"; continue; }
    set_ AeroGearComponent.PadReset $((10 + v)); hold 1
    set_ AeroGearComponent.PadHop "$v"
    hold $(( 3 + v / 2 + 2 ))
    R=$(rec); tv=$(recmin touch); rb=$(recf rebound); tl=$(recf tilt); sp=$(recf spin); bo=$(count "$R" 'bottom 1'); lo=$(recf lost)
    note "hop $v m/s: touch $tv rebound $rb tilt $tl spin $sp bottomed $bo lost $lo"
    expect "B: hop $v m/s - touched at $v +- 0.6 m/s" "touch $tv" near "$tv" "$v" 0.6
    if [ "$v" -lt 5 ]; then
        expect "B: hop $v m/s - rebound under 40 % of it, never bottomed" "rebound $rb, bottomed $bo" all_of lt "$rb" "$(awk -v v=$v 'BEGIN{print 0.4*v}')" -- eq "$bo" 0
    else note "hop 5 m/s: bottomed on $bo gear(s) (reported, not judged)"; fi
    expect "B: hop $v m/s - the feet stayed level (tilt < 3 deg, spin < 0.3 rad/s, lock kept)" "tilt $tl spin $sp lost $lo" all_of both_lt "$tl" 3 "$sp" 0.3 -- eq "${lo:-0}" 0
done
if budget 30; then
    set_ AeroGearComponent.PadReset 20; hold 1
    set_ AeroGearComponent.PadPush 5; hold 4
    R=$(rec); tl=$(recf tilt); sp=$(recf spin); lo=$(recf lost)
    note "slide 5 m/s: tilt $tl spin $sp lost $lo | $(echo "$R" | cut -c1-160)"
    expect "B: sliding at 5 m/s - the feet level and not rolling (tilt < 3 deg, spin < 0.3 rad/s, lock kept)" "tilt $tl spin $sp lost $lo" all_of both_lt "$tl" 3 "$sp" 0.3 -- eq "${lo:-0}" 0
else skip "B: slide" "deadline"; fi

# ---- C: the log ---------------------------------------------------------------------------------------------------------------
echo "== C log ($(el) s)"
never "C: no gear fault" "\[AERO-GEAR\] fault"
x=$(ourexc "$L" "$((MARK + 1))" | head -3); record "C: no exception in our code" "${x:+$x}"
b=$(bad); record "C: no Havok assertion, no fatal crash" "${b:+$b}"
note "counters: locks $(val AeroGearComponent.PadLocks) lost $(val AeroGearComponent.PadLocksLost) boxes $(val AeroGearComponent.PadBoxes)"
