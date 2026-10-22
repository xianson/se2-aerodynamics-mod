#!/usr/bin/env bash
# padgear_variants.sh - the pad slider VARIANTS side by side (2026-10-21; AeroGearComponent.PadSliderKind / PadSliderExcluded): one launch, midair, a fresh PADR100 per variant at rest and dropped at 1 m/s; then PADRIG s foot contacts (the trace). Built from padgear_look.sh:
# PADRIG static beside the deck and one rated rig (LOOK_RIG, PADR100) resting on the deck - engine shots of every foot from four sides
# (the harness's `cam grid`), the feet's compressions, tilts and slider limits noted. For the eye: where the foot meets the rod, how
# its collider sits on the deck. Same guards / env as padgear_test.sh; KIND (AeroGearComponent.PadSliderKind, 0 prismatic, 1 6-DOF).
SUITE=padgear_variants; GENCLEAR=1; source "$(dirname "$0")/padlib.sh"
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
# the variants: "kind excluded" (AeroGearComponent.PadSliderKind 0 prismatic / 1 6-DOF; PadSliderExcluded) - a fresh PADR100 each
IFS=, read -ra VS <<< "${VARIANTS:-0 false,0 true,1 false,1 true}"
for vr in "${VS[@]}"; do
    set -- $vr; k=$1; ex=$2
    budget 45 || { skip "variant $k/$ex" "deadline"; continue; }
    set_ AeroGearComponent.PadSliderKind "$k"; set_ AeroGearComponent.PadSliderExcluded "$ex"
    rig PADR100 PADR100.txt 8 14 -12 || continue
    hold 5; set_ AeroGearComponent.PadReset $RANDOM; hold 3
    a="rest: tilt $(recf tilt) spin $(recf spin) comps $(games | tr '\n' ' ')"
    dropm PADR100 2 0 1 3 || true
    F=$(awk -v RS='|' '/ foot / { for (i = 1; i <= NF; i++) { if ($i == "tilt") { t = $(i+1) + 0; if (t > mt) mt = t } if ($i == "rate") { for (j = 1; j <= 3; j++) { r = $(i+j) + 0; if (r > mr) mr = r } } } } END { printf "drop: feet tilt %.1f rate %.2f", mt, mr }' <<< "$R")
    note "variant kind $k excluded $ex | $a | $F | $(echo "$R" | cut -c1-160)"
    rig_del "$RIG"
done
set_ AeroGearComponent.PadSliderKind 0; set_ AeroGearComponent.PadSliderExcluded false
if [ "${RIGTRACE:-1}" = 1 ]; then
    set_ AeroGearComponent.PadTraceTicks 20
    ask "gen spawn PADRIG.txt ahead=45 right=-10 up=12" >/dev/null; poll 20 spawned PADRIG; hold 4
    note "PADRIG contacts: $(since | grep -a 'PADTRACE' | grep -a ' t10 ' | sed 's/.*PADTRACE\] //' | cut -c1-200 | tr '\n' ';')"
    set_ AeroGearComponent.PadTraceTicks 0
fi
x=$(ourexc "$L" "$((MARK + 1))" | head -3); record "no exception in our code" "${x:+$x}"
