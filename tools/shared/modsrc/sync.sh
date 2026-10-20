#!/usr/bin/env bash
# sync.sh - the source both mods carry (the in-game settings menu, settings DESIGN.md W4; anything else added under files/),
# written ONCE here and copied into each mod. The two mods compile separately and share no code: each compiles its own copy,
# whose one difference is the namespace - the placeholder lines `namespace __MOD__;` / `using __MOD__;` become AeroMod or
# OrbitalMod. Every file under files/ is shared, at the same path in each mod (files/Scripts/Simulation/Ui/SettingsTab.cs ->
# <mod>/Scripts/Simulation/Ui/SettingsTab.cs). Each mod's adapter (Scripts/Simulation/Ui/SettingsTab.Mod.cs: its id, title,
# files, registry calls) and test project files (*.csproj) are its own and never here.
#   tools/shared/modsrc/sync.sh                                    write both copies: this checkout's aero mod, and the
#                                                                  Orbital Mod at $ORBITAL_ROOT, else the sibling ../Orbital Mod
#   tools/shared/modsrc/sync.sh --aero <dir> --orbital <dir>       write the copies in those checkouts (either one alone: that one)
#   tools/shared/modsrc/sync.sh --check --aero <dir> [--orbital <dir>]
#                                                                  compare only - the named checkouts (exit 1 on a difference);
#                                                                  aero's tools/test_fast.sh checks aero, orbital's
#                                                                  tools/check_contract.sh checks both
# Edit the files here, then sync, then run both mods' fast gates. (Line ends aside: git checks the copies out CRLF.)
here="$(cd "$(dirname "$0")" && pwd)"
check=0; aero=""; orbital=""; named=0
while [ $# -gt 0 ]; do
  case "$1" in
    --check) check=1 ;;
    --aero) aero="${2:?--aero <dir>}"; named=1; shift ;;
    --orbital) orbital="${2:?--orbital <dir>}"; named=1; shift ;;
    *) echo "usage: sync.sh [--check] [--aero <aero checkout>] [--orbital <orbital checkout>]"; exit 2 ;;
  esac
  shift
done
if [ $named -eq 0 ]; then
  [ $check -eq 1 ] && { echo "sync.sh --check: name the checkout(s) to compare (--aero <dir>, --orbital <dir>)"; exit 2; }
  aero="$here/../../.."; orbital="${ORBITAL_ROOT:-$aero/../Orbital Mod}"
fi
# (absolute before the cd below: a relative --aero . named this folder's files/ itself)
abs() { [ -z "$1" ] && return 0; (cd "$1" 2>/dev/null && pwd) || echo "$1"; }
aero=$(abs "$aero"); orbital=$(abs "$orbital")
ADAPTER="Scripts/Simulation/Ui/SettingsTab.Mod.cs"
cd "$here/files" || { echo "no $here/files"; exit 2; }
mapfile -t SHARED < <(find . -type f -name '*.cs' | sed 's|^\./||' | sort)
[ ${#SHARED[@]} -gt 0 ] || { echo "no shared files under $here/files"; exit 2; }
rc=0
# the placeholder only on its two lines (anything else naming it is a mistake: the copies would not compile)
for f in "${SHARED[@]}"; do
  n=$(grep -c '__MOD__' "$f"); ok=$(grep -cE '^(namespace|using) __MOD__;' "$f")
  [ "$n" -ge 1 ] && [ "$n" = "$ok" ] || { echo "files/$f: __MOD__ must appear only as 'namespace __MOD__;' / 'using __MOD__;' ($ok of $n)"; rc=1; }
done
[ $rc -eq 0 ] || exit $rc
# copy <file> <namespace>: the mod's copy (the one rewrite; LF)
copy() { sed -e "s/^namespace __MOD__;/namespace $2;/" -e "s/^using __MOD__;/using $2;/" "$1" | tr -d '\r'; }
id() { grep -o 'ModId = "[A-Za-z0-9]*"' "$1/$ADAPTER" 2>/dev/null | head -1; }
side() {   # side <mod root> <namespace> <name>
  local root="$1" ns="$2" name="$3" f
  [ -d "$root/Scripts/Simulation" ] || { echo "not a mod checkout ($name): $root"; rc=1; return; }
  for f in "${SHARED[@]}"; do
    if [ $check -eq 1 ]; then
      [ -f "$root/$f" ] || { echo "missing in $name: $f (run tools/shared/modsrc/sync.sh)"; rc=1; continue; }
      if ! diff -q <(copy "$f" "$ns") <(tr -d '\r' < "$root/$f") >/dev/null; then
        echo "$name's copy differs: $f (edit it in the Aerodynamics Mod's tools/shared/modsrc/files/, then run its sync.sh)"
        diff <(copy "$f" "$ns") <(tr -d '\r' < "$root/$f") | head -6 | sed 's/^/        /'
        rc=1
      fi
    else
      mkdir -p "$root/$(dirname "$f")"
      copy "$f" "$ns" > "$root/$f" && echo "$name: wrote $f"
    fi
  done
  [ -f "$root/$ADAPTER" ] || { echo "no adapter in $name: $ADAPTER"; rc=1; }
}
[ -n "$aero" ] && side "$aero" AeroMod aero
[ -n "$orbital" ] && side "$orbital" OrbitalMod orbital
# two sections may never share a Tag
if [ -n "$aero" ] && [ -n "$orbital" ]; then
  a=$(id "$aero"); o=$(id "$orbital")
  [ -n "$a" ] && [ -n "$o" ] && [ "$a" != "$o" ] || { echo "the two adapters' ModId must differ: $a / $o"; rc=1; }
fi
exit $rc
