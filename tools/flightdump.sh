#!/usr/bin/env bash
# flightdump.sh ID NAME - a grid in the running game as the thruster steering sees it -> Tests/data/NAME.flight
# (the Orbital Mod's harness 'flightdump': AeroMod.FlightFixture; ThrustTests flies every fixture there)
cd "$(dirname "$0")/.."
[ $# -ge 2 ] || { echo "usage: flightdump.sh GRID_ID NAME"; exit 2; }
ORB="$(dirname "$PWD")/Orbital Mod/tools/harness/orb.sh"
out=$(timeout 60 "$ORB" "flightdump $1 $2" | grep -a "=>" | tail -1 | sed 's/.*=>  //')
case "$out" in
  FIXTURE*) echo "$out" | sed 's/^FIXTURE //; s/ ;; END$//; s/ ;; /\n/g' > "Tests/data/$2.flight"
            echo "Tests/data/$2.flight: $(grep -c '^thruster' "Tests/data/$2.flight") thrusters, $(grep -c '^air' "Tests/data/$2.flight") air samples";;
  *) echo "flightdump failed: $out"; exit 1;;
esac
# the hull's blocks also as Tests/data/NAME_game.boxes (never over a blueprint export) (AeroBench: the air tests on the real shape)
if [ -f "Tests/data/$2.flight" ] && grep -q '^box ' "Tests/data/$2.flight"; then
  m=$(grep '^mass ' "Tests/data/$2.flight" | cut -d' ' -f2)
  { echo "# blockSize 2.5 mass ${m%.*}"; grep '^box ' "Tests/data/$2.flight" | cut -d' ' -f2-; } > "Tests/data/${2}_game.boxes"
  echo "Tests/data/${2}_game.boxes: $(grep -c '^box ' "Tests/data/$2.flight") boxes"
fi
