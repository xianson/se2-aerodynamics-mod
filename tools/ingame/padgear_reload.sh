#!/usr/bin/env bash
# padgear_reload.sh - the pad-foot gears through SAVE -> KILL -> RELOAD, MIDAIR (2026-10-20; the slider since 2026-10-21): the game
# saves no constraint a mod made, so every foot's SLIDER and box collider must be made again on the reloaded wheel and its vanilla
# wheel constraint released again. Two launches, <= 8 min each. Same guards and env as padgear_test.sh (KILL_GAME=1, HARNESS, ARGS).
# The save goes into its OWN test container (RELOAD_WORLD, 'Orbital Test Pad Reload' - the harness's `save` writes only 'Orbital
# Test...' containers), never into the launch world; launch 2 starts that container: the launch args file's -start: is rewritten for
# it and put back BYTE-EXACT (a copy kept in the scratch dir; the EXIT trap restores it too).
#
#   launch 1: midair (the deck 4 km up, seated); PADRIG (static, beside the deck) - its four feet on their sliders, then RETRACTED;
#     PADR150 (four 1.5 m pads at their rating) dropped onto the deck at 1 m/s and resting on its feet (down);
#     `save <RELOAD_WORLD>` -> saving
#   kill, launch 2 (RELOAD_WORLD): the deck and the rig made static at once (the game's "no supporting voxel" check turns them
#     dynamic); eight feet on a slider again (PadSliders >= 8 in this process: made anew), released, on their box colliders; PADRIG's
#     still up and level in the well (< 2 deg); PADR150 still resting on its feet - every foot's compression 0.1 .. 0.35 m (the
#     oleo's sag at the rating: 0.23 m), level; extend PADRIG - four down, level; no gear fault, no exception in our code, no Havok
#     assertion.
#   release: dev overrides off, gears down, the test grids removed (`griddel`), the launch args file back byte-exact.
SUITE=padgear_reload; source "$(dirname "$0")/padlib.sh"
DEADLINE=${DEADLINE:-470}
RELOAD_WORLD=${RELOAD_WORLD:-Orbital Test Pad Reload}
SCR=${SCR:-/c/Users/slob/AppData/Local/Temp/claude/D--aero/6fa840e6-5a4f-4095-aa92-29c6c2c8affc/scratchpad}
[ "${1:-}" = "--release" ] && { release; exit 0; }
W=$(startof < "$ARGS" 2>/dev/null)
case "$W" in "Orbital Test"*) ;; *) echo "REFUSED: the launch world is '$W' (-start: in $ARGS) - not an 'Orbital Test...' world."; exit 2 ;; esac
case "$RELOAD_WORLD" in "Orbital Test"*) ;; *) echo "REFUSED: RELOAD_WORLD '$RELOAD_WORLD' is not an 'Orbital Test...' container."; exit 2 ;; esac
if alive && [ -z "${KILL_GAME:-}" ]; then echo "REFUSED: Space Engineers 2 is running - it may be the user's game. KILL_GAME=1 closes it."; exit 2; fi
ARGS_COPY="$SCR/se2-launch-args.reload-backup"
cp -p "$ARGS" "$ARGS_COPY" || { echo "REFUSED: cannot keep a copy of $ARGS"; exit 2; }
restore_args() { cmp -s "$ARGS_COPY" "$ARGS" || cp -p "$ARGS_COPY" "$ARGS"; cmp -s "$ARGS_COPY" "$ARGS" && echo "== the launch args file is back byte-exact" || echo "!! the launch args file differs from $ARGS_COPY"; }
cleanup() { local id; for id in ${RIGS:-}; do ask "griddel $id" >/dev/null 2>&1; done; }
trap 'cleanup; release; restore_args; summary_all' EXIT
trap 'exit 130' INT TERM
mkdir -p "$GEN"; cp "$MOD/tools/ingame/PADRIG.txt" "$GEN/"
python "$MOD/tools/ingame/build_wheelbrick.py" --out "$(cygpath -w "$GEN")" >/dev/null
python "$MOD/tools/ingame/build_padrigs.py" --out "$(cygpath -w "$GEN")" >/dev/null

start() {   # start NAME: the Steam launch, a new log with the world loaded, the harness answering
    local old; old=$(LOGF 2>/dev/null); L=""
    mkdir -p "$(dirname "$ST")"; touch "$(dirname "$ST")/dev.flag"; rm -f "$(dirname "$ST")/cmd.txt"
    launch
    local i; for i in $(seq 1 150); do L=$(LOGF 2>/dev/null); [ -n "$L" ] && [ "$L" != "$old" ] && grep -aq 'beacon Root Entity\|Loading Failed' "$L" 2>/dev/null && break; sleep 1; done
    MARK=0
    grep -aq 'beacon Root Entity' "$L" 2>/dev/null || { record "$1: the world loaded" "no new log with a loaded world (${L:-none})"; return 1; }
    poll 60 gridsup || { record "$1: the harness answers" "findgrids unanswered 60 s after the load"; return 1; }
    record "$1: loaded ($(el) s)" ""
}
since() { tail -n +"$((MARK + 1))" "$L"; }
games() { gear | tr '|' '\n' | grep "PAD " | sed -n 's/.* game \(-\?[0-9.]*\)).*/\1/p'; }

echo "== launch 1 ($(el) s): '$W'"
start "launch 1" || exit 1
num "$(val AeroGearComponent.PadSliders)" || { record "aero: the pad gears' slider statics" "not in this build - deploy this branch"; exit 1; }
source "$MOD/tools/ingame/midair.sh"
ask "encon off" >/dev/null
midair_setup || exit 1
RIGS="$DECK"
ask "gen spawn PADRIG.txt ahead=45 right=-10 up=12" >/dev/null; poll 20 spawned PADRIG
PR=$(gid PADRIG); RIGS="$RIGS $PR"
for k in $(seq 1 20); do grep -aq "No supporting voxel found for grid: PADRIG" "$L" && break; sleep 1; done
note "1: rig $PR $(ask "gridstatic $PR on")"
poll 15 alone_ge 4
set_ AeroGearComponent.DevCommand 2; hold 7; set_ AeroGearComponent.DevCommand 0
G=$(gear)
expect "1: PADRIG retracted (saved stowed)" "$(echo "$G" | cut -c1-200)" ge "$(count "$G" '| up p 0.00')" 4
rig PADR150 PADR150.txt 9 18 -14 || exit 1
RIGS="$RIGS $RIG"
poll 15 alone_ge 8
dropm PADR150 2 0 1 3 || true
note "1: PADR150 dropped: $(echo "$R" | cut -c1-200)"
hold 2
G=$(gear); note "1: before the save: compressions $(games | tr '\n' ' ') | $(echo "$G" | cut -c1-300)"
r=$(ask "save $RELOAD_WORLD" | tail -1); note "save: $r"
expect "1: saved into '$RELOAD_WORLD'" "$r" ge "$(count "$r" 'saving to')" 1
for k in $(seq 1 30); do since | grep -aq "Save.*$RELOAD_WORLD\|$RELOAD_WORLD.*[Ss]av" && break; sleep 1; done
hold 3
note "1: save log: $(since | grep -a "$RELOAD_WORLD" | tail -2 | cut -c1-200)"

echo "== launch 2 ($(el) s): '$RELOAD_WORLD'"
sed -i "s|-start:$W\"|-start:$RELOAD_WORLD\"|" "$ARGS"
grep -q -- "-start:$RELOAD_WORLD\"" "$ARGS" || { record "2: the launch args file rewritten for '$RELOAD_WORLD'" "$(cut -c1-120 "$ARGS")"; exit 1; }
RELEASED=""   # (the release again after launch 2)
start "launch 2" || exit 1
restore_args
DECK=$(gid WSDECK); PR=$(gid PADRIG); RIG=$(gid PADR150); RIGS="$DECK $PR $RIG"
note "2: deck $DECK $(ask "gridstatic $DECK on") | rig $PR $(ask "gridstatic $PR on") | PADR150 $RIG"
for k in $(seq 1 20); do grep -aq "No supporting voxel found for grid" "$L" && break; sleep 1; done
ask "gridstatic $DECK on" "gridstatic $PR on" >/dev/null
poll 30 alone_ge 8
G=$(gear); note "2: reloaded: compressions $(games | tr '\n' ' ') | $(echo "$G" | cut -c1-400)"
expect "2: eight sliders made anew on the reloaded feet, the vanilla constraints released" "sliders $(val AeroGearComponent.PadSliders), released $(val AeroGearComponent.PadReleased), on a slider $(slid_n), alone $(alone_n)" all_of ge "$(val AeroGearComponent.PadSliders)" 8 -- alone_ge 8
expect "2: eight box colliders made anew" "boxes $(val AeroGearComponent.PadBoxes)" ge "$(val AeroGearComponent.PadBoxes)" 8
set_ AeroGearComponent.PadRecord true; set_ AeroGearComponent.PadReset 1; hold 3
t=$(recf tilt); G=$(gear)
expect "2: PADRIG still up and every foot level (< 2 deg)" "tilt $t | $(echo "$G" | cut -c1-200)" all_of ge "$(count "$G" '| up p 0.00')" 4 -- lt "$t" 2
n=0; for x in $(games); do awk -v x="$x" 'BEGIN{exit !(x >= 0.1 && x <= 0.35)}' && n=$((n + 1)); done
expect "2: PADR150 still resting on its feet (four compressions 0.1 .. 0.35 m)" "compressions $(games | tr '\n' ' ')" ge "$n" 4
set_ AeroGearComponent.DevCommand 1; hold 7; set_ AeroGearComponent.DevCommand 0
set_ AeroGearComponent.PadReset 2; hold 4
G=$(gear); t=$(recf tilt)
expect "2: extended - eight down, on their sliders, level (< 2 deg)" "tilt $t | $(echo "$G" | cut -c1-200)" all_of ge "$(count "$G" 'down p 1.00')" 8 -- alone_ge 8 -- lt "$t" 2
never "2: no gear fault" "\[AERO-GEAR\] fault"
x=$(ourexc "$L" 1 | head -3); record "2: no exception in our code" "${x:+$x}"
b=$(bad); record "2: no Havok assertion, no fatal crash" "${b:+$b}"
