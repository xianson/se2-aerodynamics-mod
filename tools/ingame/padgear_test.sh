#!/usr/bin/env bash
# padgear_test.sh - THE PAD-FOOT GEARS IN GAME, MIDAIR (2026-10-20; midair since 2026-10-21 - tools/ingame/midair.sh; the foot on ONE
# slider since 2026-10-21 - Integration/AeroGearComponent.PadFoot.cs, Gear/PadFoot.cs, Dev/WheelShockDev.cs). One launch of an
# 'Orbital Test...' world on a planet (no flat ground needed: the user, "spawn it midair and with relative velocities, measure change in
# momentum"; "no aero interference"), the Orbital Mod's harness driving it; <= 8 min; PASS / FAIL / SKIP from the aero mod's own probes
# (aeroget AeroGearComponent.Summary / PadRecordResult, WheelShockDev.DevResult / DevImpact) and the game log. Run it in the background;
# never while someone plays.
#
# usage: tools/ingame/padgear_test.sh           the run. REFUSES while a game is running (it may be the user's): KILL_GAME=1 closes
#                                              it - only when nobody plays. REFUSES unless the launch world is an 'Orbital Test...'
#                                              world (it spawns test grids into the world it loads).
#        tools/ingame/padgear_test.sh --release only the dev overrides back in the running game (after a killed run)
# env: HARNESS, DEADLINE s (470), ARGS (the launch args file), NOLAUNCH=1, PART (rig | drops | all), SIZES ("050 100 150 250": the
#      rated rigs dropped), SINKS ("1 3 5"), SLIDE (150: the rig the 10 m/s forward slide is run on; "" none), MIDAIR_UP (4000 m)
# needs: THIS build deployed to the game's mod folder (scripts + the content build: tools/shared/build_mod_content.py)
#
#  M  midair: a STATIC armour deck 4 km up (WSDECK, build_wheelbrick.py), the player seated on it (midair.sh).
#  A  RIG (PADRIG: one of each size under a STATIC platform, in the air beside the deck - made static again after the game's
#     "no supporting voxel" check): four feet on their box collider and their slider, the vanilla wheel constraint released ("alone");
#     still 5 s - each foot within 1 deg of its block's up, turning < 0.02 rad/s, hanging at its slider's extended end (the slider's
#     sense: every foot's compression at -Extra, never at the far end); retract - four up, level in the well (< 2 deg); extend - four
#     down, the sliders still there.
#  B  DROPS (the rated rigs, build_padrigs.py: four pads of a size at 101-104 % of their rating; WheelShockDev midair: no air on the
#     rig, the momentum ledger): per size sink 1 / 3 / 5 m/s straight down; one 10 m/s forward slide at sink 1 (SLIDE). JUDGED (as
#     the wheels' spec): touched within 0.6 m/s of the sink; 1 and 3 m/s never bottomed; 3 m/s within 80 % of the stroke, its peak
#     under 3 g beyond the weight (the 0.5 m plate: reported - its 0.175 m strut cannot stop 3 m/s at 2 g under 3 g), rebound under
#     30 %; every drop - no hull contact, every foot level (tilt < 3 deg) and not turning (each rate < 0.3 rad/s), no air on the rig,
#     no slider lost; 5 m/s and the slide reported (the slide also judged level / not turning / no hull).
#  C  the run's log: no gear fault, no exception in our code, no Havok assertion; the vanilla wheel constraints released once a foot.
#  release (also from the EXIT trap): PadRecord off, PadHop / PadPush 0, gears down, the drop's dev values, gen clear.
SUITE=padgear_test; GENCLEAR=1; source "$(dirname "$0")/padlib.sh"
DEADLINE=${DEADLINE:-470}
PART=${PART:-all}; SIZES=${SIZES:-050 100 150 250}; SINKS=${SINKS:-1 3 5}; SLIDE=${SLIDE-150}
[ "${1:-}" = "--release" ] && { release; exit 0; }
W=$(startof < "$ARGS" 2>/dev/null)
case "$W" in "Orbital Test"*) ;; *) echo "REFUSED: the launch world is '$W' (-start: in $ARGS) - not an 'Orbital Test...' world."; exit 2 ;; esac
if [ -z "${NOLAUNCH:-}" ] && alive && [ -z "${KILL_GAME:-}" ]; then echo "REFUSED: Space Engineers 2 is running - it may be the user's game. KILL_GAME=1 closes it."; exit 2; fi
trap 'release; summary_all' EXIT
trap 'exit 130' INT TERM
mkdir -p "$GEN"; cp "$MOD/tools/ingame/PADRIG.txt" "$GEN/"
python "$MOD/tools/ingame/build_wheelbrick.py" --out "$(cygpath -w "$GEN")" >/dev/null   # (the midair deck: WSDECK)
python "$MOD/tools/ingame/build_padrigs.py" --out "$(cygpath -w "$GEN")" | sed 's/^/   rig: /'

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
num "$(val AeroGearComponent.PadSliders)" || { record "aero: the pad gears' slider statics (AeroGearComponent.PadSliders)" "not in this build - deploy this branch"; exit 1; }
[ "$(val WheelShockDev.DevImpact)" = "?" ] && { record "aero: the midair drop's ledger (WheelShockDev.DevImpact)" "not in this build - deploy it"; exit 1; }
record "loaded, the harness answers, this build has the pad sliders and the midair ledger ($(el) s)" ""
set_ AeroGearComponent.PadRecord true
source "$MOD/tools/ingame/midair.sh"
ask "encon off" >/dev/null
echo "== M midair ($(el) s)"
midair_setup || exit 1

# the gear summary's per-gear fields: "game X" (the foot's compression, the corner module's geometry) of every pad gear
games() { gear | tr '|' '\n' | grep "PAD " | sed -n 's/.* game \(-\?[0-9.]*\)).*/\1/p'; }
# every pad's compression within [lo, hi]
games_in() { local x n=0; for x in $(games); do awk -v x="$x" -v a="$1" -v b="$2" 'BEGIN{exit !(x >= a && x <= b)}' || return 1; n=$((n + 1)); done; [ "$n" -ge "$3" ]; }

# ---- A: the rig (static, in the air beside the deck) -----------------------------------------------------------------------------
if [ "$PART" != drops ]; then
echo "== A rig ($(el) s)"
ask "gen spawn PADRIG.txt ahead=45 right=-10 up=12" >/dev/null
poll 20 spawned PADRIG
expect "A: the rig spawned whole" "$(ask 'gen whole' | cut -c1-120)" whole
PR=$(gid PADRIG)
poll 20 slid_ge 4
for k in $(seq 1 20); do grep -aq "No supporting voxel found for grid: PADRIG" "$L" && break; sleep 1; done
note "A: rig $PR $(ask "gridstatic $PR on")"; hold 1
poll 15 alone_ge 4
G=$(gear); note "rig: $(echo "$G" | cut -c1-400)"
expect "A: four feet on one slider each, the vanilla wheel constraint released" "$(slid_n) on a slider, $(alone_n) alone" all_of slid_ge 4 -- alone_ge 4
expect "A: four feet on their box collider" "$(boxed_n) boxed" ge "$(boxed_n)" 4
set_ AeroGearComponent.PadReset 1; hold 5
t=$(recf tilt); s=$(recf spin); note "rig still: tilt $t deg, spin $s rad/s, compressions $(games | tr '\n' ' ') | $(rec | cut -c1-300)"
expect "A: still - every foot level (< 1 deg) and not turning (< 0.02 rad/s)" "tilt $t, spin $s" both_lt "$t" 1 "$s" 0.02
expect "A: the slider's sense - every foot hangs at its extended end (-Extra .. 0.02 m), not the far end" "compressions $(games | tr '\n' ' ')" games_in -0.30 0.02 4
set_ AeroGearComponent.PadReset 2
set_ AeroGearComponent.DevCommand 2; hold 7
G=$(gear); t=$(recf tilt); note "retracted: tilt $t, compressions $(games | tr '\n' ' ') | $(echo "$G" | cut -c1-300)"
expect "A: retracted - four up" "$(echo "$G" | cut -c1-200)" ge "$(count "$G" '| up p 0.00')" 4
expect "A: retracted - the feet up their slide (compression > 0.3 m)" "compressions $(games | tr '\n' ' ')" games_in 0.3 10 4
expect "A: retracted - the feet level in the well (< 2 deg)" "tilt $t" lt "$t" 2
set_ AeroGearComponent.PadReset 3
set_ AeroGearComponent.DevCommand 1; hold 7; set_ AeroGearComponent.DevCommand 0
G=$(gear); t=$(recf tilt)
expect "A: extended - four down, still on their sliders, level (< 2 deg)" "tilt $t | $(echo "$G" | cut -c1-200)" all_of ge "$(count "$G" 'down p 1.00')" 4 -- slid_ge 4 -- lt "$t" 2
[ -n "$PR" ] && rig_del "$PR"
fi

# ---- B: the rated rigs, midair drops ----------------------------------------------------------------------------------------------
# feet: every "| foot NAME tilt T rate X Y Z" of the drop R - the largest tilt and the largest rate about each axis
feet() {
    awk -v RS='|' '/ foot / { for (i = 1; i <= NF; i++) { if ($i == "tilt") { t = $(i+1) + 0; if (t > mt) mt = t }
        if ($i == "rate") { x = $(i+1) + 0; y = $(i+2) + 0; z = $(i+3) + 0; if (x > mx) mx = x; if (y > my) my = y; if (z > mz) mz = z } } n++ }
        END { printf "feet %d tilt %.2f rx %.3f ry %.3f rz %.3f\n", n, mt, mx, my, mz }' <<< "$R"
}
ff() { sed -n "s/.* $1 \([0-9.]*\).*/\1/p" <<< "$F" | head -1; }
# the peak beyond the weight in g: the ledger's "peakFn X kN (Y W)" - (Y - 1) x g / 9.81
peakg() { local w gl; w=$(sed -n 's/.*kN (\([0-9.]*\) W).*/\1/p' <<< "$I" | head -1); gl=$(imf g); awk -v w="$w" -v g="$gl" 'BEGIN{ if (w == "" || g == "") print ""; else printf "%.2f", (w - 1) * g / 9.81 }'; }
declare -A DR
flip=1
if [ "$PART" != rig ]; then
for size in $SIZES; do
    budget 60 || { skip "B: rig PADR$size" "deadline"; continue; }
    echo "== B rig PADR$size ($(el) s)"
    case $size in 050) pl="7 12 -10" ;; 100) pl="8 14 -12" ;; 150) pl="9 18 -14" ;; *) pl="12 22 -14" ;; esac   # (up, ahead, right: the feet clear of the deck, the rig clear of the seat)
    rig "PADR$size" "PADR$size.txt" $pl || continue
    poll 15 slid_ge 4; poll 10 alone_ge 4
    note "PADR$size: $(slid_n) feet on a slider, $(alone_n) alone | $(gear | cut -c1-200)"
    drops="$(for v in $SINKS; do echo "$v 0 3"; done)"
    [ "$size" = "$SLIDE" ] && drops="$drops"$'\n'"1 10 3"
    while read -r v fw secs; do
        [ -n "$v" ] || continue
        [ "$fw" != 0 ] && { fw=$((fw * flip)); flip=$((-flip)); }
        budget 20 || { skip "B: PADR$size sink $v fwd $fw" "deadline"; continue; }
        if ! dropm "PADR$size" 2 "$fw" "$v" "$secs" || [[ "$R" == *aborted* ]]; then record "B: PADR$size sink $v fwd $fw - the drop ran" "$R"; continue; fi
        F=$(feet); tv=$(rf touch); rb=$(rf rebound); bo=$(rf bottomed); hu=$(rf hull); mc=$(rf maxcomp); pg=$(peakg)
        tl=$(ff tilt); rx=$(ff rx); ry=$(ff ry); rz=$(ff rz)
        DR["$size/$v/$fw"]="touch $tv comp $mc bot $bo reb $rb hull $hu peak ${pg} g | $F | aero $(imf aero) M $(imf M) t T $(imf impact) Jn $(imf Jn) Jt $(imf Jt) Jcid $(imf Jcid)"
        note "PADR$size sink $v fwd $fw: ${DR["$size/$v/$fw"]}"
        note "   ledger: $(echo "$I" | cut -c1-420)"
        k="B: PADR$size sink $v fwd $fw"
        expect "$k - no air on the rig" "aero $(imf aero)" lt "$(imf aero)" 1
        expect "$k - every foot level (tilt < 3 deg) and not turning (< 0.3 rad/s each axis), no hull contact" "$F, hull $hu" all_of lt "$tl" 3 -- lt "$rx" 0.3 -- lt "$ry" 0.3 -- lt "$rz" 0.3 -- eq "$hu" 0
        [ "$fw" != 0 ] && continue
        expect "$k - touched at $v +- 0.6 m/s" "touch $tv" near "$tv" "$v" 0.6
        expect "$k - rebound under 30 % of it" "rebound $rb" lt "$rb" "$(awk -v v=$v 'BEGIN{print 0.3*v}')"
        [ "$v" = 5 ] && continue
        expect "$k - never bottomed" "bottomed $bo (comp $mc)" eq "$bo" 0
        if [ "$v" = 3 ]; then
            expect "$k - within 80 % of the stroke" "comp $mc" le "$mc" 0.80
            if [ "$size" = 050 ]; then note "$k: peak $pg g beyond the weight (reported: the 0.175 m strut)"
            else expect "$k - peak under 3 g beyond the weight" "peak $pg g" lt "$pg" 3; fi
        fi
    done <<< "$drops"
    lo=$(val AeroGearComponent.PadSlidersLost); expect "B: PADR$size - no slider lost so far" "lost $lo" eq "$lo" 0
    rig_del "$RIG"
done
echo "-- the drops (size/sink/forward)"
for k in $(printf '%s\n' "${!DR[@]}" | sort); do note "$k: ${DR[$k]}"; done
fi

# ---- C: the log ---------------------------------------------------------------------------------------------------------------
echo "== C log ($(el) s)"
never "C: no gear fault" "\[AERO-GEAR\] fault"
never "C: no pad release failure" "vanilla wheel constraint not released"
x=$(ourexc "$L" "$((MARK + 1))" | head -3); record "C: no exception in our code" "${x:+$x}"
b=$(bad); record "C: no Havok assertion, no fatal crash" "${b:+$b}"
note "counters: sliders $(val AeroGearComponent.PadSliders) lost $(val AeroGearComponent.PadSlidersLost) released $(val AeroGearComponent.PadReleased) boxes $(val AeroGearComponent.PadBoxes) fault '$(val AeroGearComponent.PadFault)'"
note "slider log: $(since | grep -a 'pad foot: slider on' | head -2 | cut -c1-400)"
note "box log: $(since | grep -a 'pad foot: box collider' | head -4 | sed 's/.*pad foot: //' | tr '\n' ';' | cut -c1-400)"
