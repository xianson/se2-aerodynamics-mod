#!/usr/bin/env bash
# padgear_reload.sh - the pad-foot gears through SAVE -> KILL -> RELOAD (2026-10-20): the game saves no constraint a mod made, so
# every foot's orientation lock and box collider must be made again on the reloaded wheel. Two launches of an 'Orbital Test...'
# world, <= 8 min. Same guards and env as padgear_test.sh (KILL_GAME=1, HARNESS, ARGS); it SAVES into the very test world it loaded
# (the harness's `save` writes only 'Orbital Test...' containers). Stand the player on flat open ground, looking level.
#
#   launch 1: PADTEST (on the ground) and PADRIG (static, 40 m left) spawned - seven feet locked level; the rig's gears RETRACTED
#     (the DevCommand reaches every gear: the ship's go up too - it then sits on its housings; fine for this test);
#     `save <world>` -> Success
#   kill, launch 2: the grids loaded; seven feet locked level again (PadLocks >= 7 in this process: made anew), their box colliders;
#     the retracted ones still up and level in the well (tilt < 2 deg); extend - seven down, all locked; still 4 s: tilt < 2 deg;
#     no gear fault, no exception in our code, no Havok assertion.
#   release: dev overrides off, gears down - the test grids stay in the TEST world (saved there).
SUITE=padgear_reload; source "$(dirname "$0")/padlib.sh"
DEADLINE=${DEADLINE:-470}
[ "${1:-}" = "--release" ] && { release; exit 0; }
W=$(startof < "$ARGS" 2>/dev/null)
case "$W" in "Orbital Test"*) ;; *) echo "REFUSED: the launch world is '$W' (-start: in $ARGS) - not an 'Orbital Test...' world."; exit 2 ;; esac
if alive && [ -z "${KILL_GAME:-}" ]; then echo "REFUSED: Space Engineers 2 is running - it may be the user's game. KILL_GAME=1 closes it."; exit 2; fi
trap 'release; summary_all' EXIT
trap 'exit 130' INT TERM
mkdir -p "$GEN"; cp "$MOD/tools/ingame/PADTEST.txt" "$MOD/tools/ingame/PADRIG.txt" "$GEN/"

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
num "$(val AeroGearComponent.PadLocks)" || { record "aero: the pad gears' dev statics" "not in this build - deploy this branch"; exit 1; }
ask "gen spawn PADTEST.txt" >/dev/null; poll 20 spawned PADTEST
ask "gen spawn PADRIG.txt right=-40" >/dev/null; poll 20 spawned PADRIG
poll 30 locked_ge 7
expect "1: seven feet locked level" "$(locked_n) locked | $(gear | cut -c1-200)" locked_ge 7
set_ AeroGearComponent.DevCommand 2; hold 7; set_ AeroGearComponent.DevCommand 0
G=$(gear)
expect "1: retracted (saved stowed)" "$(echo "$G" | cut -c1-200)" ge "$(count "$G" '| up p 0.00')" 4
r=$(ask "save $W" | tail -1); note "save: $r"
expect "1: saved into the test world" "$r" ge "$(count "$r" 'Success')" 1

echo "== launch 2 ($(el) s)"
start "launch 2" || exit 1
poll 60 locked_ge 7
G=$(gear); note "reloaded: $(echo "$G" | cut -c1-240)"
expect "2: seven locks made anew on the reloaded wheels" "locks $(val AeroGearComponent.PadLocks), locked $(locked_n)" all_of ge "$(val AeroGearComponent.PadLocks)" 7 -- locked_ge 7
expect "2: seven box colliders made anew" "boxes $(val AeroGearComponent.PadBoxes)" ge "$(val AeroGearComponent.PadBoxes)" 7
set_ AeroGearComponent.PadRecord true; set_ AeroGearComponent.PadReset 1; hold 3
t=$(recf tilt)
expect "2: still up and level in the well (< 2 deg)" "tilt $t | $(echo "$G" | cut -c1-160)" all_of ge "$(count "$G" '| up p 0.00')" 4 -- lt "$t" 2
set_ AeroGearComponent.DevCommand 1; hold 7; set_ AeroGearComponent.DevCommand 0
set_ AeroGearComponent.PadReset 2; hold 4
G=$(gear); t=$(recf tilt)
expect "2: extended - seven down, locked, level (< 2 deg)" "tilt $t | $(echo "$G" | cut -c1-200)" all_of ge "$(count "$G" 'down p 1.00')" 7 -- locked_ge 7 -- lt "$t" 2
never "2: no gear fault" "\[AERO-GEAR\] fault"
x=$(ourexc "$L" 1 | head -3); record "2: no exception in our code" "${x:+$x}"
b=$(bad); record "2: no Havok assertion, no fatal crash" "${b:+$b}"
