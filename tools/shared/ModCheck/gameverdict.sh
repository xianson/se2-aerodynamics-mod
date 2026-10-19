#!/usr/bin/env bash
# usage: tools/shared/ModCheck/gameverdict.sh "<mod dir>" [bundle name] [--all] [--dump-whitelist=<file>]
# The game's exact verdict on the mod's Scripts/Simulation: runs the game's own compile path
# (ModCompilationDescriptor + ScriptCompiler, all of Keen's analyzers incl. the KCG KN* set) offline.
# Exit 0 = the game compiles the mod, 1 = it rejects it. ~10 s; ModCheck.csproj (~5 s) is the everyday check.
set -e
here="$(cd "$(dirname "$0")" && pwd)"
dotnet build "$here/GameVerdict/GameVerdict.csproj" -nologo -v:q -o "$here/GameVerdict/bin/out" > /dev/null \
  || { dotnet build "$here/GameVerdict/GameVerdict.csproj" -nologo -v:q -o "$here/GameVerdict/bin/out"; exit 2; }
mod="${1:?mod dir}"; shift
exec dotnet "$here/GameVerdict/bin/out/GameVerdict.dll" "$(cygpath -w "$mod" 2>/dev/null || echo "$mod")" "$@"
