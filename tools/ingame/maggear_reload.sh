#!/usr/bin/env bash
# maggear_reload.sh - the magnetic gear through SAVE -> KILL -> RELOAD (2026-10-20): the pads are found again by their names and
# joined again (the game saves no constraint a mod made), no new pad, none orphaned, the vanilla magnets' locks restored. Two
# launches of an 'Orbital Test...' world, <= 8 min. Same guards and env as maggear_test.sh (KILL_GAME=1, HARNESS, ARGS); it SAVES
# into the very test world it loaded (the harness's `save` writes only 'Orbital Test...' containers).
#
# TIMELINE:
#   launch 1 (~60-150 s); MAGTEST spawned onto the ground, its pads joined and LOCKED (Auto-Lock); the three pad ids (Summary);
#     the RIG (static, 8 m up, 40 m left) spawned and its gears RETRACTED - its pads saved inside the wells (the ship's are
#     refused: locked); `save <world>` -> Success
#   kill, launch 2 (~60-150 s), wait for the grids
#   check: 6 gears, all Joined with the SAME ids as saved; "found its pad" x6 in the log; Spawns 0 in this process; MAGTEST's
#     three LOCKED again; the rig's three stowed, up, no lock; after 25 s OrphansRemoved 0 (nothing claimed late); recorder 5 s:
#     drift < 0.02 m (the pads did not jump at the load - they come in with no collision until joined); extend the rig: down,
#     joined; no fault, no exception in our code, no Havok assertion
#   release: dev overrides off, gears down - the test grids stay in the TEST world (saved there; `gen clear` knows only this
#     process's spawns)
SUITE=maggear_reload; source "$(dirname "$0")/maglib.sh"
DEADLINE=${DEADLINE:-470}
[ "${1:-}" = "--release" ] && { release; exit 0; }
W=$(startof < "$ARGS" 2>/dev/null)
case "$W" in "Orbital Test"*) ;; *) echo "REFUSED: the launch world is '$W' (-start: in $ARGS) - not an 'Orbital Test...' world."; exit 2 ;; esac
if alive && [ -z "${KILL_GAME:-}" ]; then echo "REFUSED: Space Engineers 2 is running - it may be the user's game. KILL_GAME=1 closes it."; exit 2; fi
trap 'release; summary_all' EXIT
trap 'exit 130' INT TERM
mkdir -p "$GEN"; cp "$MOD/tools/ingame/MAGTEST.txt" "$MOD/tools/ingame/MAGRIG.txt" "$GEN/"
ids() { mag | tr '|' '\n' | grep -o 'Gear Pad \[[0-9a-f]*\]' | sort | tr '\n' ' '; }

start() {   # start NAME: the Steam launch, a new log with the world loaded, the harness answering
    local old; old=$(LOGF 2>/dev/null); L=""
    mkdir -p "$(dirname "$ST")"; touch "$(dirname "$ST")/dev.flag"; rm -f "$(dirname "$ST")/cmd.txt"
    launch
    local i; for i in $(seq 1 150); do L=$(LOGF 2>/dev/null); [ -n "$L" ] && [ "$L" != "$old" ] && grep -aq 'beacon Root Entity\|Loading Failed' "$L" 2>/dev/null && break; sleep 1; done
    MARK=0
    grep -aq 'beacon Root Entity' "$L" 2>/dev/null || { record "$1: the world loaded" "no new log with a loaded world (${L:-none})"; return 1; }
    poll 60 gridsup || { record "$1: the harness answers" "findgrids unanswered 60 s after the load"; return 1; }
    record "$1: '$W' loaded ($(el) s)" ""
}
since() { tail -n +"$((MARK + 1))" "$L"; }

echo "== launch 1 ($(el) s)"
start "launch 1" || exit 1
num "$(val AeroMagGearComponent.Spawns)" || { record "aero: the magnetic gear's dev statics" "not in this build - deploy this branch"; exit 1; }
ask "gen spawn MAGTEST.txt" >/dev/null; poll 20 spawned MAGTEST
ask "gen spawn MAGRIG.txt right=-40" >/dev/null; poll 20 spawned MAGRIG
poll 30 locked_ge 3
poll 20 joined_ge 6
expect "1: six pads joined, the ship's three LOCKED" "$(mag | cut -c1-200)" all_of joined_ge 6 -- locked_ge 3
# (the rig's gears up - its pads saved stowed; the DevCommand goes to every gear: the locked ship's are refused and stay down)
set_ AeroGearComponent.DevCommand 2; hold 6; set_ AeroGearComponent.DevCommand 0
G=$(gear)
expect "1: the rig's three gears up, the locked ship's three refused" "$(echo "$G" | cut -c1-200)" all_of ge "$(count "$G" '| up p 0.00')" 3 -- ge "$(count "$G" 'down p 1.00')" 3
SAVED=$(ids); note "saved pads: $SAVED"
r=$(ask "save $W" | tail -1); note "save: $r"
expect "1: saved into the test world" "$r" ge "$(count "$r" 'Success')" 1

echo "== launch 2 ($(el) s)"
start "launch 2" || exit 1
poll 60 joined_ge 6
set_ AeroMagGearComponent.DevRecord true; set_ AeroMagGearComponent.DevReset 1
M=$(mag); NOW=$(ids); note "reloaded: $(echo "$M" | cut -c1-220)"
expect "2: six pads found again and joined" "$(echo "$M" | cut -c1-200)" joined_ge 6
expect "2: the same pads (ids) as saved" "saved: $SAVED / now: $NOW" eq "$NOW" "$SAVED"
expect "2: found by name, six times" "$(since | grep -ac 'found its pad')" ge "$(since | grep -ac 'found its pad')" 6
expect "2: no pad spawned anew" "spawns $(val AeroMagGearComponent.Spawns)" eq "$(val AeroMagGearComponent.Spawns)" 0
expect "2: the ship's three locks restored (the vanilla magnet's own save)" "$(locked_n) locked" locked_ge 3
G=$(gear)
expect "2: the rig still up (stowed)" "$(echo "$G" | cut -c1-200)" ge "$(count "$G" '| up p 0.00')" 3
hold 25
expect "2: nothing orphaned (every pad claimed in time)" "orphans $(val AeroMagGearComponent.OrphansRemoved)" eq "$(val AeroMagGearComponent.OrphansRemoved)" 0
d=$(recf drift)
expect "2: the pads did not jump at the load (drift < 0.02 m)" "drift $d | $(rec | cut -c1-160)" lt "$d" 0.02
set_ AeroGearComponent.DevCommand 1; hold 6; set_ AeroGearComponent.DevCommand 0
G=$(gear)
expect "2: the rig extends again, its pads joined" "$(echo "$G" | cut -c1-200)" all_of ge "$(count "$G" 'down p 1.00')" 6 -- joined_ge 6
never "2: no magnetic gear fault" "\[AERO-MAGGEAR\] fault"
x=$(ourexc "$L" 1 | head -3); record "2: no exception in our code" "${x:+$x}"
b=$(bad); record "2: no Havok assertion, no fatal crash" "${b:+$b}"
