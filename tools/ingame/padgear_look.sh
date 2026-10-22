#!/usr/bin/env bash
# padgear_look.sh - THE PAD FEET LOOKED AT (2026-10-21; Integration/AeroGearComponent.PadFoot.cs): one launch, midair (midair.sh),
# PADRIG static beside the deck and one rated rig (LOOK_RIG, PADR100) resting on the deck - engine shots of every foot from four sides
# (the harness's `cam grid`), the feet's compressions, tilts and slider limits noted. For the eye: where the foot meets the rod, how
# its collider sits on the deck. Same guards / env as padgear_test.sh; KIND (AeroGearComponent.PadSliderKind, 0 prismatic, 1 6-DOF).
SUITE=padgear_look; GENCLEAR=1; source "$(dirname "$0")/padlib.sh"
DEADLINE=${DEADLINE:-420}; KIND=${KIND:-0}; LOOK_RIG=${LOOK_RIG:-PADR100}
[ "${1:-}" = "--release" ] && { release; exit 0; }
W=$(startof < "$ARGS" 2>/dev/null)
case "$W" in "Orbital Test"*) ;; *) echo "REFUSED: the launch world is '$W' - not an 'Orbital Test...' world."; exit 2 ;; esac
if [ -z "${NOLAUNCH:-}" ] && alive && [ -z "${KILL_GAME:-}" ]; then echo "REFUSED: Space Engineers 2 is running - it may be the user's game. KILL_GAME=1 closes it."; exit 2; fi
trap 'ask "cam off" >/dev/null 2>&1; release; summary_all' EXIT
trap 'exit 130' INT TERM
mkdir -p "$GEN"; cp "$MOD/tools/ingame/PADRIG.txt" "$GEN/"
python "$MOD/tools/ingame/build_wheelbrick.py" --out "$(cygpath -w "$GEN")" >/dev/null
python "$MOD/tools/ingame/build_padrigs.py" --out "$(cygpath -w "$GEN")" >/dev/null
if [ -z "${NOLAUNCH:-}" ]; then
    old=$(LOGF 2>/dev/null); mkdir -p "$(dirname "$ST")"; touch "$(dirname "$ST")/dev.flag"; rm -f "$(dirname "$ST")/cmd.txt"
    echo "== launch ($(el) s): '$W'"; launch
    for i in $(seq 1 150); do L=$(LOGF 2>/dev/null); [ -n "$L" ] && [ "$L" != "$old" ] && grep -aq 'beacon Root Entity\|Loading Failed' "$L" 2>/dev/null && break; sleep 1; done
fi
L=$(LOGF); MARK=0; [ -n "${NOLAUNCH:-}" ] && MARK=$(wc -l < "$L")
since() { tail -n +"$((MARK + 1))" "$L"; }
poll 60 gridsup || { record "the harness answers" "findgrids unanswered"; exit 1; }
set_ AeroGearComponent.PadSliderKind "$KIND"; set_ AeroGearComponent.PadRecord true; set_ AeroGearComponent.PadTraceTicks "${TRACE:-0}"
record "loaded ($(el) s), slider kind $(val AeroGearComponent.PadSliderKind)" ""
source "$MOD/tools/ingame/midair.sh"
ask "encon off" >/dev/null
midair_setup || exit 1
games() { gear | tr '|' '\n' | grep "PAD " | sed -n 's/.* game \(-\?[0-9.]*\)).*/\1/p'; }
shots() {   # shots GRID LABEL DIST_KM
    local b
    [ "${SHOTS:-1}" = 1 ] || return 0
    for b in 0 90 180 270; do ask "cam grid $1 $3 $b 10" >/dev/null; sleep 1.2; mshot "$2 bearing $b"; done
    ask "cam grid $1 $3 45 -25" >/dev/null; sleep 1.2; mshot "$2 from below"
    ask "cam off" >/dev/null
}
ask "gen spawn PADRIG.txt ahead=45 right=-10 up=12" >/dev/null; poll 20 spawned PADRIG
PR=$(gid PADRIG)
for k in $(seq 1 20); do grep -aq "No supporting voxel found for grid: PADRIG" "$L" && break; sleep 1; done
note "rig $PR $(ask "gridstatic $PR on")"
poll 15 alone_ge 4; hold 3
set_ AeroGearComponent.PadReset 1; hold 3
note "PADRIG still: compressions $(games | tr '\n' ' ') | $(rec | cut -c1-500)"
note "PADRIG gears: $(gear | cut -c1-700)"
shots "$PR" PADRIG 0.02
case $LOOK_RIG in PADR050) pl="7 12 -10" ;; PADR100) pl="8 14 -12" ;; PADR150) pl="9 18 -14" ;; *) pl="12 22 -14" ;; esac
rig_del "$PR"
rig "$LOOK_RIG" "$LOOK_RIG.txt" $pl || exit 1
hold 6; note "$LOOK_RIG after 6 s: $("$ORB" "findgrids $LOOK_RIG" | grep -a "> findgrids" | tail -1 | cut -c1-200) | compressions $(games | tr '
' ' ')"
dropm "$LOOK_RIG" 2 0 1 3 || true
note "$LOOK_RIG drop: $(echo "$R" | cut -c1-400)"
hold 2; set_ AeroGearComponent.PadReset 2; hold 3
note "$LOOK_RIG resting: compressions $(games | tr '\n' ' ') | $(rec | cut -c1-500)"
shots "$RIG" "$LOOK_RIG" 0.025
note "slider log: $(since | grep -a 'pad foot: slider on' | tail -1 | cut -c60-500)"
x=$(ourexc "$L" "$((MARK + 1))" | head -3); record "no exception in our code" "${x:+$x}"
