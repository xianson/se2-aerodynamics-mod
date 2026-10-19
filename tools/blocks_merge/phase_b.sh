#!/usr/bin/env bash
# phase_b.sh STEP - PHASE B of folding the companion mod "Aerodynamics Mod Blocks" into the Aerodynamics Mod (2026-10-17,
# the user approved). Phase A (code and files, done with the game running) left the block Assets in this repo; phase B
# needs the game CLOSED (the build writes Content the game holds open; the checks launch it). docs/BLOCKS_MERGE.md has
# the why; each step below is one command, in this order, each PASS / FAIL by itself:
#
#   check      the live Aero repo has phase A merged, nobody playing (no game running), the tools build
#   assets     copy the binaries git does not carry (.fbx / .png - see BLOCKS_MERGE.md "git") from the Blocks mod into the
#              live Aero Assets (merge_blocks_assets.py: never overwrites, never deletes); records Aero's definitions before
#   baseline   [launch 1, OLD content] copy the world "$SRC_WORLD" -> "$MERGED" (refuses if it exists); load it as it is
#              (still listing the Blocks mod); every grid's block dump (gen dump) and aero block dump (AeroBlockSave
#              .DevDump) -> $OUT/baseline/
#   build      python tools/shared/build_mod_content.py "<live Aero>" (~15 min - run it with run_in_background; keeps the
#              previous Content as Content.before-<stamp>)
#   verify     the new Aero Content: every definition GUID the Blocks mod had (blocks_definitions.txt) and every one Aero
#              had before (assets step), every compiled file the Blocks mod had (blocks_content_files.txt)
#   migrate    the COPY's mod list without the Blocks mod (tools/modlist --drop: writes the extension-less "modlist" JSON
#              the game reads first; modlist.vrb untouched)
#   after      [launch 2, NEW content] load the migrated copy: loaded (no "Loading Failed"), no definition "Could not
#              find", every grid's block dump and aero dump equal to the baseline, no exception in our code
#   place      [launch 3] copy "$PLACE_SRC" (Orbital Mod only) -> "$PLACE", add the Aerodynamics Mod to its list, load it,
#              spawn every block of the former Blocks mod alone (Orbital harness `gen measure`: each block's server
#              prefab, from Assets/Aero/**/*_Server.partialdef), `gen report`: every one placed with a mass
#   worlds     list the user's worlds that still list the Blocks mod (they will NOT load once it is gone - and probably not
#              with both: the same resource ids twice in the content cache). APPLY=1 backs each up into
#              $BACKUPS/blocks-merge-<stamp>/ and drops the Blocks mod from its list - ONLY with the user's go
#
# Launch steps: every one is under 5 minutes and kills the game at its end. They REFUSE while a game runs (it may be the
# user's: KILL_GAME=1 closes it - only when nobody plays). The world comes from the -start: of $ARGS (the Steam launch
# reads it via tools/shared/launch-se2.bat): SET_ARGS=1 sets it for the step and puts the file back on exit; without it the
# step refuses unless the file already names the step's world. Results: $OUT/<step>/ and the step's last line.
# env: AERO (the live Aerodynamics Mod) BLOCKS (the Blocks mod) HARNESS (the Orbital Mod's tools/harness) OUT ARGS
#      (default: $SE2_LAUNCH_ARGS, else the live Aero tools/shared/se2-launch-args.txt - what the bat reads) BACKUPS
set -u
HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
AERO=${AERO:-/c/Users/slob/Documents/SpaceEngineers2/Mods/Aerodynamics Mod}
BLOCKS=${BLOCKS:-/c/Users/slob/Documents/SpaceEngineers2/Mods/Aerodynamics Mod Blocks}
HARNESS=${HARNESS:-/c/Users/slob/Documents/SpaceEngineers2/Mods/Orbital Mod/tools/harness}
SAVES=/c/Users/slob/AppData/Roaming/SpaceEngineers2/AppData/SaveGames
OUT=${OUT:-/c/Users/slob/AppData/Local/Temp/AeroBlocksMerge}
ARGS=${ARGS:-${SE2_LAUNCH_ARGS:+$(cygpath -u "$SE2_LAUNCH_ARGS")}}; ARGS=${ARGS:-$AERO/tools/shared/se2-launch-args.txt}
BACKUPS=${BACKUPS:-/c/Users/slob/Documents/SpaceEngineers2/SaveBackups}   # (the 2026-10-18 run: D:/aero/save_backups)
SRC_WORLD="Orbital Test Tu-134 Control Surfaces"; MERGED="Orbital Test Tu-134 Merged"
PLACE_SRC="Orbital Test World"; PLACE="Orbital Test Blocks Placement"
BLOCKS_GUID=483980e4-2c19-4ef2-8274-c58f3354c645; AERO_GUID=de164d94-7a67-4832-81e4-34c06c6a65f1
GEN=/c/Users/slob/AppData/Local/Temp/OrbitalMod/gen   # (GridGen's folder: %TEMP%\OrbitalMod\gen)
STEP=${1:-}; mkdir -p "$OUT"
source "$HARNESS/testlib.sh"   # (log / alive / ask / record / summary / ourexc / launch - the Orbital harness's own)
T0=$(date +%s); el() { echo $(( $(date +%s) - T0 )); }
# a -start: value up to the next " -option" (a world name may hold '-': "Tu-134") - as test_blocksave.sh reads it
startof() { grep -ao '\-start:[^"]*' | head -1 | sed -E 's/^-start://; s/[[:space:]]+-[a-zA-Z]+(:.*|[[:space:]].*)?$//; s/[[:space:]]*$//'; }
startworld() { startof < "$1" 2>/dev/null; }
logworld() { grep -a -m1 'Running application with arguments' "$1" | startof; }
modlist() { dotnet run --project "$HERE/../modlist" -c Release -- "$@"; }
win() { cygpath -w "$1"; }

guard_game() {
    alive || return 0
    [ -n "${KILL_GAME:-}" ] || { echo "REFUSED: Space Engineers 2 is running - it may be the user's game. KILL_GAME=1 closes it (only when nobody plays)."; exit 2; }
    powershell -NoProfile -Command "Get-Process SpaceEngineers2 -ErrorAction SilentlyContinue | Stop-Process -Force"; sleep 5
}
kill_game() { powershell -NoProfile -Command "Get-Process SpaceEngineers2 -ErrorAction SilentlyContinue | Stop-Process -Force" >/dev/null 2>&1; }

# the launch world: SET_ARGS=1 swaps the -start: of $ARGS for the step (restored on exit), else it must already be $1
ARGS_ORIG=""
use_world() {
    local w="$1" cur; cur=$(startworld "$ARGS")
    [ "$cur" = "$w" ] && return 0
    [ -n "${SET_ARGS:-}" ] || { echo "REFUSED: $ARGS starts '$cur', this step loads '$w' - set its -start: or run with SET_ARGS=1"; exit 2; }
    ARGS_ORIG="$OUT/launch-args.orig.txt"; cp "$ARGS" "$ARGS_ORIG"
    python - "$ARGS" "$w" <<'EOF'
import re, sys
p, w = sys.argv[1], sys.argv[2]
t = open(p, encoding="utf-8").read()
t2 = re.sub(r'"-start:[^"]*"|-start:\S+', lambda m: f'"-start:{w}"', t, count=1)
if t2 == t: t2 = f'"-start:{w}" ' + t
open(p, "w", encoding="utf-8", newline="").write(t2)
EOF
    trap 'kill_game; [ -n "$ARGS_ORIG" ] && cp "$ARGS_ORIG" "$ARGS" && echo "(launch args restored)"' EXIT
    echo "(launch args: -start:$w for this step)"
}

# one launch: the game started once, a NEW log with the world loaded, the harness answering (testlib's pieces)
L=""
start_world() {
    local w="$1" old i; old=$(LOGF 2>/dev/null)
    mkdir -p "$(dirname "$ST")"; touch "$(dirname "$ST")/dev.flag"; rm -f "$(dirname "$ST")/cmd.txt"
    launch
    for i in $(seq 1 150); do
        L=$(LOGF 2>/dev/null)
        # (a world whose start is far from any planet logs no "beacon Root Entity": the Orbital system's build then)
        [ -n "$L" ] && [ "$L" != "$old" ] && grep -aq 'beacon Root Entity\|\[ORBIT\] system built\|Loading Failed' "$L" 2>/dev/null && break
        L=""; sleep 1
    done
    [ -n "$L" ] || { record "'$w' loaded within 150 s" "no new log with a loaded world"; return 1; }
    MARK=0
    grep -aq 'Loading Failed' "$L" && { record "'$w' loaded" "Loading Failed - $(grep -a -m1 "Couldn't find the project\|Loading Failed" "$L" | cut -c1-200)"; return 1; }
    [ "$(logworld "$L")" = "$w" ] || { record "the game loaded '$w'" "its -start: is '$(logworld "$L")'"; return 1; }
    # (the findgrids REPLY must name a grid: orb.sh prints the whole status tail, whose other lines hold '=' too - the
    #  first baseline, 2026-10-18, dumped 0 grids while the server sat in a 14 s GC hitch before publishing its views)
    for i in $(seq 1 90); do ask findgrids | grep -aq ' [0-9]*=' && break; sleep 1; done
    sleep 3   # (grids stream in after the first answer)
    record "'$w' loaded, the harness answers ($(el) s)" ""
}

# every grid: findgrids, then the blocks of every grid of one NAME together (gen dump: cells, orientation - the ids may
# differ between two loads, the names do not) and each name's aero blocks (DevDump). Sized for a 5-minute launch with
# ~300 grids (the Tu-134 world, 2026-10-18; one ask a grid took ~5 s): gen dump writes its file at once - 100 a batch,
# the replies unread (status.txt keeps 20); DevDump is answered on a server step - pipelined, each batch reads the last
# name's answer and asks the next, until $DEADLINE s into the step (the launch's 5 minutes; ~2.5 s a name). With a
# second argument (the baseline's dir) the aero dump takes exactly the names that one has, in its order.
DEADLINE=${DEADLINE:-270}
dump_grids() {
    local dir="$1" from="${2:-}" ids=() i j a nm b names=() prev=""
    mkdir -p "$dir"; touch "$dir/.mark"   # (only dump files written after it count: older runs left dump_<id>.txt)
    ask findgrids | tr ' ' '\n' | grep -a '^[0-9]*=' | sort -t= -k2 > "$dir/grids.txt"
    mapfile -t ids < <(cut -d= -f1 "$dir/grids.txt")
    for ((i = 0; i < ${#ids[@]}; i += 100)); do
        b=(); for j in "${ids[@]:i:100}"; do b+=("gen dump $j"); done
        "$ORB" "${b[@]}" > /dev/null
    done
    while IFS= read -r nm; do
        for j in $(grep -a "=$nm\$" "$dir/grids.txt" | cut -d= -f1); do
            [ -n "$(find "$GEN/dump_$j.txt" -newer "$dir/.mark" 2>/dev/null)" ] && grep -v '^#' "$GEN/dump_$j.txt" | awk '{print $1, $2, $3, $4, $5, $6, $7, $8}'
        done | sort > "$dir/blocks_$nm.txt"
    done < <(cut -d= -f2 "$dir/grids.txt" | sort -u)
    if [ -n "$from" ]; then mapfile -t names < <(cat "$from/aero_names.txt")
    else mapfile -t names < <(cut -d= -f2 "$dir/grids.txt" | grep -v '(static)$' | sort -u); fi
    : > "$dir/aero_names.txt"
    aero_out() { echo "$1" | tr ' ' '\n' | grep -a '^[a-z]*@' | sed 's/;rb:[01]//' | sort > "$dir/aero_$2.txt"; }
    local G="aeroget AeroBlockSave.DevDump" S="aeroset AeroBlockSave.DevDump" late=()
    for nm in "${names[@]}" ""; do
        [ -n "$nm" ] && [ -z "$from" ] && [ "$(el)" -ge "$DEADLINE" ] && { echo "   (aero dump cut at $(el) s: the names after '$prev' not dumped)"; nm=""; }
        if [ -n "$prev" ]; then
            if [ -n "$nm" ]; then a=$(ask "$G" "$S $nm" | head -1); else a=$(ask "$G" | head -1); fi
            case "${a#*=}" in asked*|"") late+=("$prev") ;; *) aero_out "${a#*=}" "$prev" ;; esac
            echo "$prev" >> "$dir/aero_names.txt"
        elif [ -n "$nm" ]; then ask "$S $nm" > /dev/null; fi
        prev="$nm"
        [ -z "$nm" ] && break
    done
    for nm in "${late[@]}"; do   # (not answered by the next batch: asked again alone)
        ask "$S $nm" > /dev/null
        for i in $(seq 1 12); do a=$(ask "$G" | head -1); case "${a#*=}" in asked*|"") sleep 0.3 ;; *) break ;; esac; done
        aero_out "${a#*=}" "$nm"
    done
    echo "   $(wc -l < "$dir/grids.txt") grid(s) ($(ls "$dir"/blocks_*.txt 2>/dev/null | wc -l) names), $(cat "$dir"/blocks_*.txt 2>/dev/null | wc -l) block(s), $(cat "$dir"/aero_*.txt 2>/dev/null | wc -l) aero block(s) -> $dir"
}
notfound() { grep -ac 'DefinitionSerializationContext\] Could not find' "$L"; }

case "$STEP" in
check)
    guard_game
    [ -f "$AERO/tools/blocks_merge/merge_blocks_assets.py" ] && [ -d "$AERO/Assets/Aero/Propulsors" ] && [ -f "$AERO/Assets/aero.meta" ] \
        && record "phase A is merged into the live Aero repo" "" || record "phase A is merged into the live Aero repo" "no tools/blocks_merge or Assets/Aero in $AERO"
    [ -d "$BLOCKS/Assets/Aero" ] && record "the Blocks mod is still there (the binaries' source)" "" || record "the Blocks mod is still there" "no $BLOCKS/Assets/Aero"
    dotnet build "$HERE/../modlist" -c Release -nologo -v q 2>&1 | grep -q "Build succeeded" && record "tools/modlist builds" "" || record "tools/modlist builds" "build failed"
    [ -f "$HARNESS/testlib.sh" ] && record "the Orbital harness is there" "" || record "the Orbital harness is there" "no $HARNESS/testlib.sh"
    summary ;;
assets)
    guard_game
    python "$HERE/merge_blocks_assets.py" "$(win "$BLOCKS")" "$(win "$AERO")" && record "the Blocks mod's Assets in the live Aero mod (nothing differs)" "" \
        || record "the Blocks mod's Assets in the live Aero mod" "a file differs - see above"
    mkdir -p "$OUT/assets"
    modlist --defs "$(win "$AERO/Content")" | awk '{print $2}' | sort -u > "$OUT/assets/aero_defs_before.txt"
    echo "   Aero's definitions before the build: $(wc -l < "$OUT/assets/aero_defs_before.txt")"
    summary ;;
baseline)
    guard_game
    [ -e "$SAVES/$MERGED" ] && { echo "REFUSED: '$MERGED' exists - a baseline must come from a fresh copy of '$SRC_WORLD' (move the old copy away first)"; exit 2; }
    ls "$AERO/Content/Aero" >/dev/null 2>&1 && { echo "REFUSED: $AERO/Content/Aero exists - the baseline needs the OLD content (before 'build')"; exit 2; }
    cp -r "$SAVES/$SRC_WORLD" "$SAVES/$MERGED"
    python - "$SAVES/$MERGED/.container-info" "$MERGED" <<'EOF'
import json, sys
p, n = sys.argv[1], sys.argv[2]
d = json.load(open(p, encoding="utf-8-sig")); d["$Value"]["Meta"]["DisplayName"] = n
open(p, "w", encoding="utf-8").write(json.dumps(d, indent=2))
EOF
    modlist "$(win "$SAVES/$MERGED")" | tee "$OUT/modlist_before.txt"
    grep -q "$BLOCKS_GUID" "$OUT/modlist_before.txt" && record "the copy lists the Blocks mod (the case to migrate)" "" || record "the copy lists the Blocks mod" "it does not - nothing to migrate"
    use_world "$MERGED"
    start_world "$MERGED" || { summary; exit 1; }
    echo "   definitions not found on load: $(notfound)" | tee "$OUT/baseline_notfound.txt"
    dump_grids "$OUT/baseline"
    kill_game; summary ;;
build)
    guard_game
    python "$HERE/../shared/build_mod_content.py" "$(win "$AERO")" && record "the Aero content built" "" || record "the Aero content built" "build_mod_content.py failed - its log: %LOCALAPPDATA%/OrbitalModBuild/build-Aerodynamics_Mod.log"
    summary ;;
verify)
    mkdir -p "$OUT/verify"
    modlist --defs "$(win "$AERO/Content")" > "$OUT/verify/defs.txt" || { record "read the new definitionsets.vrb" "tools/modlist --defs failed"; summary; exit 1; }
    awk '{print $2}' "$OUT/verify/defs.txt" | sort -u > "$OUT/verify/aero_defs_after.txt"
    grep -v '^#' "$HERE/blocks_definitions.txt" | awk '{print $2}' | sort -u > "$OUT/verify/blocks_defs.txt"
    miss=$(comm -23 "$OUT/verify/blocks_defs.txt" "$OUT/verify/aero_defs_after.txt" | wc -l)
    record "every Blocks-mod definition ($(wc -l < "$OUT/verify/blocks_defs.txt")) in the Aero content" "$([ "$miss" -gt 0 ] && echo "$miss missing: $(comm -23 "$OUT/verify/blocks_defs.txt" "$OUT/verify/aero_defs_after.txt" | head -5 | tr '\n' ' ')")"
    if [ -f "$OUT/assets/aero_defs_before.txt" ]; then
        miss=$(comm -23 "$OUT/assets/aero_defs_before.txt" "$OUT/verify/aero_defs_after.txt" | wc -l)
        record "every definition Aero had before ($(wc -l < "$OUT/assets/aero_defs_before.txt")) still there" "$([ "$miss" -gt 0 ] && echo "$miss missing")"
    else skip "Aero's own definitions kept" "no $OUT/assets/aero_defs_before.txt (run 'assets' first)"; fi
    miss=0; while IFS= read -r f; do [ -f "$AERO/Content/$f" ] || { miss=$((miss + 1)); [ $miss -le 5 ] && echo "   missing: $f"; }; done < <(grep -v '^#' "$HERE/blocks_content_files.txt")
    record "every compiled Blocks-mod file ($(grep -vc '^#' "$HERE/blocks_content_files.txt")) in Aero's Content" "$([ $miss -gt 0 ] && echo "$miss missing")"
    echo "   $(wc -l < "$OUT/verify/aero_defs_after.txt") definitions in the Aero content (expected about 346 + 746 - 1 shared vanilla base = 1091)"
    summary ;;
migrate)
    guard_game
    [ -d "$SAVES/$MERGED" ] || { echo "REFUSED: no '$MERGED' (run 'baseline' first)"; exit 2; }
    modlist "$(win "$SAVES/$MERGED")" --drop "$BLOCKS_GUID" | tee "$OUT/modlist_after.txt"
    grep -q "$BLOCKS_GUID" <(sed -n '/^wrote/,$p' "$OUT/modlist_after.txt") && record "the copy no longer lists the Blocks mod" "it still does" \
        || record "the copy no longer lists the Blocks mod (its modlist JSON)" ""
    summary ;;
after)
    guard_game
    [ -f "$SAVES/$MERGED/modlist" ] || { echo "REFUSED: '$MERGED' is not migrated (run 'migrate')"; exit 2; }
    use_world "$MERGED"
    start_world "$MERGED" || { summary; exit 1; }
    n=$(notfound); record "no definition missing on load" "$([ "$n" -gt 0 ] && echo "$n 'Could not find': $(grep -a -m3 'DefinitionSerializationContext\] Could not find' "$L" | sed 's/.*Could not find //' | tr '\n' ' ')")"
    dump_grids "$OUT/after" "$OUT/baseline"
    diff -q "$OUT/baseline/grids.txt" "$OUT/after/grids.txt" >/dev/null && record "the same grids as before" "" || record "the same grids as before" "$(diff "$OUT/baseline/grids.txt" "$OUT/after/grids.txt" | grep '^[<>]' | head -4 | tr '\n' ' ')"
    for f in "$OUT"/baseline/blocks_*.txt "$OUT"/baseline/aero_*.txt; do
        g=$(basename "$f"); d=$(diff "$f" "$OUT/after/$g" 2>&1 | grep -c '^[<>]')
        record "$g: $(wc -l < "$f") block(s) as before" "$([ "$d" -gt 0 ] && echo "$d line(s) differ - diff $f $OUT/after/$g")"
    done
    e=$(ourexc "$L" | head -3); record "no exception in our code" "$e"
    kill_game; summary ;;
place)
    guard_game
    [ -e "$SAVES/$PLACE" ] || cp -r "$SAVES/$PLACE_SRC" "$SAVES/$PLACE"
    modlist "$(win "$SAVES/$PLACE")" --add "$AERO_GUID" "Aerodynamics Mod" | tail -3
    mkdir -p "$GEN" "$OUT/place"
    find "$AERO/Assets/Aero" -name "*_Server.partialdef" ! -path "*/Wheel/*" -print0 | xargs -0 python -c '
import json, sys
for p in sys.argv[1:]:
    v = json.load(open(p, encoding="utf-8-sig"))["$Value"]
    print(v["Manipulator"]["Guid"], "#", p.replace("\\", "/").split("/Assets/")[-1])' > "$GEN/blocks_merge.txt"
    cp "$GEN/blocks_merge.txt" "$OUT/place/prefabs.txt"; np=$(grep -c . "$GEN/blocks_merge.txt")
    use_world "$PLACE"
    start_world "$PLACE" || { summary; exit 1; }
    r=$(ask "gen measure blocks_merge.txt"); echo "   $r"
    echo "$r" | grep -q "measuring $np blocks (0 failed)" && record "every block ($np) spawned" "" || record "every block ($np) spawned" "$r"
    sleep 3; ask "gen report" > /dev/null; sleep 1
    cp "$GEN/catalog.txt" "$OUT/place/catalog.txt" 2>/dev/null
    bad=$(grep -v '^#' "$OUT/place/catalog.txt" 2>/dev/null | awk '$2 <= 0 || $3 == "?"' | wc -l); got=$(grep -vc '^#' "$OUT/place/catalog.txt" 2>/dev/null || echo 0)
    record "each placed with a mass and cells ($got in the catalog)" "$([ "$bad" -gt 0 ] || [ "$got" -ne "$np" ] && echo "$bad without mass/cells, $got of $np - $OUT/place/catalog.txt")"
    n=$(notfound); record "no definition missing on load" "$([ "$n" -gt 0 ] && echo "$n 'Could not find'")"
    e=$(ourexc "$L" | head -3); record "no exception in our code" "$e"
    kill_game; summary ;;
worlds)
    stamp=$(date +%Y%m%d-%H%M%S); n=0
    for d in "$SAVES"/*/; do
        w=$(basename "$d"); [ -f "$d/modlist" ] || [ -f "$d/modlist.vrb" ] || continue
        modlist "$(win "$d")" 2>/dev/null | grep -q "$BLOCKS_GUID" || continue
        n=$((n + 1)); echo "   lists the Blocks mod: $w"
        if [ -n "${APPLY:-}" ]; then
            guard_game
            mkdir -p "$BACKUPS/blocks-merge-$stamp"; cp -r "$d" "$BACKUPS/blocks-merge-$stamp/$w"
            modlist "$(win "$d")" --drop "$BLOCKS_GUID" | tail -1
        fi
    done
    echo "== $n world(s) list the Blocks mod$([ -n "${APPLY:-}" ] && echo " - migrated; backups in $BACKUPS/blocks-merge-$stamp" || echo " (APPLY=1 migrates them - with the user's go)")" ;;
*)
    sed -n '2,33p' "$0" | sed 's/^# \{0,1\}//'; exit 2 ;;
esac
