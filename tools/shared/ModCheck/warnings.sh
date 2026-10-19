#!/usr/bin/env bash
# usage: tools/ModCheck/warnings.sh "<mod dir>"
# Lists dead-code compiler warnings (unused locals/fields, unreachable code, unused events) that the mods'
# blanket `#pragma warning disable` hides: compiles a scratch copy of Scripts/Simulation with the pragmas
# stripped (blanket or narrow-list pragmas). The mod's own files are not touched.
set -e
mod="${1:?mod dir}"; here="$(cd "$(dirname "$0")" && pwd)"
tmp="$(mktemp -d)"; trap 'rm -rf "$tmp"' EXIT
mkdir -p "$tmp/mod/Scripts"; cp -r "$mod/Scripts/Simulation" "$tmp/mod/Scripts/"
find "$tmp/mod" -name '*.cs' -exec sed -i 's/^\s*#pragma warning disable.*$//' {} +
dotnet build "$here/ModCheck.csproj" "-p:ModDir=$(cygpath -w "$tmp/mod")" "-p:NoWarn=CS1591%3BCS8632" -o "$tmp/out" 2>&1 \
  | grep -a "warning CS0\(168\|219\|169\|414\|649\|162\|67\)" | sed 's|.*Simulation.||; s| \[.*||' | sort -u
