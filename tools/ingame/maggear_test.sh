#!/usr/bin/env bash
# maggear_test.sh - THE MAGNETIC LANDING GEAR IN GAME (2026-10-20; Integration/AeroMagGearComponent.cs). One launch of an
# 'Orbital Test...' world (the Orbital Mod's harness drives it: its testlib.sh / orb.sh, `gen` spawns), ~5-6 min, PASS / FAIL /
# SKIP from the aero mod's own probes (aeroget AeroMagGearComponent.Summary / DevRecordResult / counters,
# AeroGearComponent.Summary) and the game log - nothing judged by eye. Run it in the background; never while someone plays.
#
# usage: tools/ingame/maggear_test.sh           the run. REFUSES while a game is running (it may be the user's): KILL_GAME=1
#                                               closes it - only when nobody plays. REFUSES unless the launch world is an
#                                               'Orbital Test...' world (it spawns test grids into the world it loads).
#        tools/ingame/maggear_test.sh --release only the dev overrides back in the running game (after a killed run)
# env: HARNESS (the Orbital Mod's tools/harness; default the sibling checkout's, else the installed mod's), DEADLINE s (420),
#      ARGS (the launch args file, as test_blocksave.sh), NOLAUNCH=1 (use the game already running - still a test world)
# needs: THIS branch's aero build deployed to the game's mod folder (scripts + the content build: tools/shared/build_mod_content.py)
#
# TIMELINE (launch ~60-150 s, then ~4 min):
#  A  RIG (MAGRIG, static, 8 m up - the pads hang free): 3 pads spawned and joined (the pad's life); held still 5 s - the pad's
#     drift off its joined pose < 1 cm, its tilt < 1 deg (the join, the axle hold); retract: the three gears up, the pads stowed
#     with their collision off, no lock onto its own housing (no LOCKED, no self-lock veto needed... counted); extend: down and
#     joined again.
#  B  SHIP (MAGTEST, 33 t, dropped onto its pads on the ground): 3 joined, all LOCKED by Auto-Lock on the voxel ground; the
#     suspension never bottomed; retract refused while locked (AeroGearComponent: "refused: magnet locked..."), still down;
#     unlock (the panel's Unlock path) -> none locked; Auto-Lock off; HOPS at 1 / 3 / 5 m/s (DevHop: the ship and its wheels and
#     pads up at v, it lands at v): the touch speed within 0.6 m/s of v, rebound under 40 % of v (1 and 3), no bottom-out at 1 and
#     3 (5: reported), pad drift < 3 cm and tilt < 3 deg, never unjoined, never locked (Auto-Lock off); Auto-Lock on + a 1 m/s
#     hop -> LOCKED again (the cooldown ends in flight, as vanilla); PAD LOSS (DevCommand 5: as if destroyed) -> lost, the ship
#     still stands (its hidden wheels take the ground); REPAIR (DevCommand 6) -> a new pad spawned and joined.
#  C  ANOTHER GRID (MAGDECK, a static deck 40 m right; a MAGTEST copy dropped on it): its three pads LOCKED onto the deck.
#  D  the run's log: no [AERO-MAGGEAR] fault, no exception in our code, no Havok assertion; orphans: the rig's pads removed
#     after `gen clear` (no gear claims them: OrphanSeconds) - every grid we spawned is gone at the end.
#  release (also from the EXIT trap): DevRecord off, DevHop 0, every DevCommand 0, gears down, Auto-Lock on, gen clear.
SUITE=maggear_test; source "$(dirname "$0")/maglib.sh"
[ "${1:-}" = "--release" ] && { release; exit 0; }

# ---- guards ------------------------------------------------------------------------------------------------------------------
W=$(startof < "$ARGS" 2>/dev/null)
case "$W" in "Orbital Test"*) ;; *) echo "REFUSED: the launch world is '$W' (-start: in $ARGS) - not an 'Orbital Test...' world."; exit 2 ;; esac
if [ -z "${NOLAUNCH:-}" ] && alive && [ -z "${KILL_GAME:-}" ]; then echo "REFUSED: Space Engineers 2 is running - it may be the user's game. KILL_GAME=1 closes it."; exit 2; fi
trap 'release; summary_all' EXIT
trap 'exit 130' INT TERM
mkdir -p "$GEN"; cp "$MOD/tools/ingame/MAGTEST.txt" "$MOD/tools/ingame/MAGRIG.txt" "$MOD/tools/ingame/MAGDECK.txt" "$GEN/"

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
num "$(val AeroMagGearComponent.Spawns)" || { record "aero: the magnetic gear's dev statics (AeroMagGearComponent.*)" "not in this build - deploy this branch"; exit 1; }
record "loaded, the harness answers, this build has the magnetic gear ($(el) s)" ""
set_ AeroMagGearComponent.DevRecord true

# ---- A: the static rig ---------------------------------------------------------------------------------------------------------
echo "== A rig ($(el) s)"
ask "gen spawn MAGRIG.txt" >/dev/null
poll 20 spawned MAGRIG
expect "A: the rig spawned whole" "$(ask 'gen whole' | cut -c1-120)" whole
poll 25 joined_ge 3
expect "A: three pads spawned and joined" "$(mag | cut -c1-200)" joined_ge 3
set_ AeroMagGearComponent.DevReset 1; hold 5
d=$(recf drift); t=$(recf tilt); note "rig still: drift $d m, tilt $t deg | $(rec | cut -c1-160)"
expect "A: still, the pads hold their joined pose (drift < 0.01 m, tilt < 1 deg)" "drift $d, tilt $t" both_lt "$d" 0.01 "$t" 1
set_ AeroGearComponent.DevCommand 2; hold 6
G=$(gear); note "retracted: $(echo "$G" | cut -c1-200)"
expect "A: retracted - three gears up, the wheels' collision off" "$(echo "$G" | cut -c1-160)" ge "$(count "$G" '| up p 0.00')" 3
expect "A: stowed - no pad locked onto its own housing" "$(mag | cut -c1-200)" eq "$(locked_n)" 0
expect "A: no self-lock veto needed" "selflock $(val AeroMagGearComponent.SelfLockVetoes)" eq "$(val AeroMagGearComponent.SelfLockVetoes)" 0
set_ AeroGearComponent.DevCommand 1; hold 6; set_ AeroGearComponent.DevCommand 0
G=$(gear)
expect "A: extended - three gears down, pads joined" "$(mag | cut -c1-200)" all_of joined_ge 3 -- ge "$(count "$G" 'down p 1.00')" 3
ask "gen clear" >/dev/null

# ---- B: the ship on the ground ------------------------------------------------------------------------------------------------
echo "== B ship ($(el) s)"
ask "gen spawn MAGTEST.txt" >/dev/null
poll 20 spawned MAGTEST
expect "B: the ship spawned whole" "$(ask 'gen whole' | cut -c1-120)" whole
poll 30 locked_ge 3
M=$(mag); note "landed: $(echo "$M" | cut -c1-220)"
expect "B: three pads joined" "$(echo "$M" | cut -c1-200)" ge "$(count "$M" '| Joined')" 3
expect "B: Auto-Lock locked all three onto the ground" "$(locked_n) locked" locked_ge 3
set_ AeroMagGearComponent.DevReset 1; hold 4
R=$(rec); note "parked: $(echo "$R" | cut -c1-200)"
expect "B: parked - never bottomed, the pads hold (drift < 0.02 m)" "$(echo "$R" | cut -c1-160)" all_of eq "$(count "$R" 'bottom 1')" 0 -- lt "$(recf drift)" 0.02
set_ AeroGearComponent.DevCommand 2; hold 2
G=$(gear)
expect "B: retract refused while locked (still down, the reason given)" "$(echo "$G" | cut -c1-200)" all_of ge "$(count "$G" 'refused: magnet locked')" 1 -- ge "$(count "$G" 'down p 1.00')" 3
set_ AeroGearComponent.DevCommand 0
set_ AeroMagGearComponent.DevCommand 2; hold 2; set_ AeroMagGearComponent.DevCommand 0
expect "B: unlocked (the panel's Unlock)" "$(locked_n) locked" eq "$(locked_n)" 0
set_ AeroMagGearComponent.DevCommand 4; set_ AeroMagGearComponent.DevCommand 0   # (Auto-Lock off for the hops)
for v in 1 3 5; do
    budget 60 || { skip "B: hop at $v m/s" "deadline"; continue; }
    set_ AeroMagGearComponent.DevReset $((10 + v)); hold 1
    set_ AeroMagGearComponent.DevHop "$v"
    hold $(( 3 + v / 2 + 2 ))
    R=$(rec); tv=$(recmin touch); rb=$(recf rebound); dr=$(recf drift); tl=$(recf tilt); bo=$(count "$R" 'bottom 1'); uj=$(recf unjoined)
    note "hop $v m/s: touch $tv rebound $rb drift $dr tilt $tl bottomed $bo unjoined $uj"
    expect "B: hop $v m/s - touched at $v +- 0.6 m/s" "touch $tv" near "$tv" "$v" 0.6
    if [ "$v" -lt 5 ]; then
        expect "B: hop $v m/s - rebound under 40 % of it, never bottomed" "rebound $rb, bottomed $bo" all_of lt "$rb" "$(awk -v v=$v 'BEGIN{print 0.4*v}')" -- eq "$bo" 0
    else note "hop 5 m/s: bottomed on $bo gear(s) (reported, not judged)"; fi
    expect "B: hop $v m/s - the pads held (drift < 0.03 m, tilt < 3 deg, never unjoined)" "drift $dr tilt $tl unjoined $uj" all_of both_lt "$dr" 0.03 "$tl" 3 -- eq "${uj:-0}" 0
    expect "B: hop $v m/s - Auto-Lock off: not locked" "$(locked_n) locked" eq "$(locked_n)" 0
done
set_ AeroMagGearComponent.DevCommand 3; set_ AeroMagGearComponent.DevCommand 0   # (Auto-Lock on)
set_ AeroMagGearComponent.DevHop 1; hold 4
expect "B: Auto-Lock on - locked again after a 1 m/s hop" "$(locked_n) locked" locked_ge 3
losses0=$(val AeroMagGearComponent.Losses); spawns0=$(val AeroMagGearComponent.Spawns)
set_ AeroMagGearComponent.DevCommand 2; hold 1; set_ AeroMagGearComponent.DevCommand 0   # (unlocked first)
set_ AeroMagGearComponent.DevCommand 5; hold 3; set_ AeroMagGearComponent.DevCommand 0
M=$(mag)
expect "B: pad loss - three lost, the gear carries on" "$(echo "$M" | cut -c1-200)" all_of ge "$(count "$M" '| Lost')" 3 -- ge "$(val AeroMagGearComponent.Losses)" $((losses0 + 3))
G=$(gear)
expect "B: pad loss - the ship still stands on its hidden wheels" "$(echo "$G" | cut -c1-160)" ge "$(count "$G" 'no pad')" 3
set_ AeroMagGearComponent.DevCommand 6; set_ AeroMagGearComponent.DevCommand 0
poll 20 joined_ge 3
expect "B: repaired - new pads spawned and joined" "spawns $(val AeroMagGearComponent.Spawns) (before $spawns0) | $(mag | cut -c1-160)" all_of ge "$(val AeroMagGearComponent.Spawns)" $((spawns0 + 3)) -- joined_ge 3

# ---- C: onto another grid -----------------------------------------------------------------------------------------------------
echo "== C deck ($(el) s)"
if budget 60; then
    l0=$(locked_n)
    ask "gen spawn MAGDECK.txt" >/dev/null; poll 15 spawned MAGDECK
    ask "gen spawn MAGTEST.txt name=MAGTEST2 right=40 up=8.5" >/dev/null
    poll 30 locked_ge $((l0 + 3))
    expect "C: the copy's three pads locked onto the deck" "$(locked_n) locked (before $l0) | $(mag | cut -c1-160)" locked_ge $((l0 + 3))
else skip "C: onto another grid" "deadline"; fi

# ---- D: the log, the orphans ------------------------------------------------------------------------------------------------------
echo "== D log ($(el) s)"
o0=$(val AeroMagGearComponent.OrphansRemoved)
ask "gen clear" >/dev/null
hold 26
o1=$(val AeroMagGearComponent.OrphansRemoved)
expect "D: the cleared grids' pads removed (no gear claims them)" "orphans removed $o1 (before $o0)" ge "$o1" $((o0 + 1))
never "D: no magnetic gear fault" "\[AERO-MAGGEAR\] fault"
x=$(ourexc "$L" "$((MARK + 1))" | head -3); record "D: no exception in our code" "${x:+$x}"
b=$(bad); record "D: no Havok assertion, no fatal crash" "${b:+$b}"
alive || record "D: the game alive at the end" "it died"
note "counters: $(val AeroMagGearComponent.Counters)"
