#!/usr/bin/env bash
# midair.sh - THE MIDAIR IMPACT RIG (2026-10-21, the user: "spawn it midair and with relative velocities, measure change in momentum";
# "make sure there's no aero interference"). Sourced by padgear_test.sh and wheelshock_test.sh after the harness library (testlib.sh):
# no flat ground needed, any 'Orbital Test...' world on a planet.
#  midair_setup     the player 2 km (MIDAIR_UP) straight up from where the world loads them, looking level; WSDECK - a STATIC flat
#                   armour deck (build_wheelbrick.py: 11 x 72 light 2.5 m cubes, 27.5 x 180 m, a control seat on its right edge) -
#                   spawned 40 m to the right; the player seated on it. Every rig is spawned from that seat (the same camera every
#                   run), 10 m to its left, over the deck's middle; the deck runs 90 m ahead and behind (the forward drops alternate).
#  rig NAME SPEC UP spawned from the seat (UP m above the seated camera), whole, levelled, its dampeners off (no thrust in the
#                   ledger); its id in RIG.
#  rig_del ID       removed (and its hinge heads / subgrids: gen owner's).
#  dropm NAME MODE FWD SINK SECONDS   one midair drop (WheelShockDev: DevApproach 1, DevNoAero 1 - no air on the rig, the game's wind
#                   taken back; the shocks and the gears on): lifted clear if it rests on the deck, FreeTicks free (the gravity measured),
#                   the approach, then free and recorded for SECONDS. R: DevResult (per wheel / foot), I: DevImpact (the momentum ledger).
#  midair_release   the drop's dev values back (DevApproach / DevNoAero off), Wheels.Shocks 2.
MIDAIR_UP=${MIDAIR_UP:-2000}

gid() { local i x; for i in 1 2 3 4 5 6 7 8; do x=$("$ORB" "findgrids $1" | grep -a "> findgrids" | tail -1 | grep -oE "[0-9]+=$1([ (]|$)" | tail -1 | cut -d= -f1); [ -n "$x" ] && { echo "$x"; return; }; sleep 1; done; }

# the point MIDAIR_UP above the camera along the nearest planet's up, and a point 200 m level ahead of it ("tp X Y Z|look X Y Z")
midair_point() {
    ask "planets" >/dev/null; sleep 0.5
    awk -v up="$MIDAIR_UP" '
        /^camera / { cx=$2; cy=$3; cz=$4 }
        /^planet [0-9]+ / { if (match($0, /center=[-0-9.]+,[-0-9.]+,[-0-9.]+/)) { split(substr($0, RSTART + 7, RLENGTH - 7), c, ","); n++; px[n]=c[1]; py[n]=c[2]; pz[n]=c[3] } }
        END {
            best=0; bd=1e300
            for (i = 1; i <= n; i++) { d=(cx-px[i])^2+(cy-py[i])^2+(cz-pz[i])^2; if (d < bd) { bd=d; best=i } }
            ux=cx-px[best]; uy=cy-py[best]; uz=cz-pz[best]; l=sqrt(ux*ux+uy*uy+uz*uz); ux/=l; uy/=l; uz/=l
            x=cx+ux*up; y=cy+uy*up; z=cz+uz*up
            # a level direction: the world X less its part along up (or Z if X is nearly up)
            ax=1; ay=0; az=0; if (ux*ux > 0.8) { ax=0; az=1 }
            dt=ax*ux+ay*uy+az*uz; fx=ax-dt*ux; fy=ay-dt*uy; fz=az-dt*uz; fl=sqrt(fx*fx+fy*fy+fz*fz)
            printf "tp %.1f %.1f %.1f|look %.1f %.1f %.1f\n", x, y, z, x+200*fx/fl, y+200*fy/fl, z+200*fz/fl
        }' "$ST"
}

midair_setup() {
    local pt tp lk d k r
    pt=$(midair_point); tp=${pt%%|*}; lk=${pt##*|}
    [ -n "$tp" ] && [[ "$tp" == tp* ]] || { record "midair: the point $MIDAIR_UP m up" "no camera / planet in status.txt"; return 1; }
    note "midair: $tp, $lk"
    ask "unseat" >/dev/null; ask "$tp" >/dev/null; sleep 0.3; ask "$lk" >/dev/null; sleep 0.3
    ask "gen spawn WSDECK.txt right=40 ahead=0 up=-6" >/dev/null
    poll 25 spawned WSDECK || { record "midair: the deck spawned" "$("$ORB" 'gen status' | tail -1 | cut -c1-140)"; return 1; }
    DECK=$(gid WSDECK)
    for k in 1 2 3 4; do
        ask "tpgrid $DECK 3" >/dev/null; sleep 0.4
        r=$(ask "seatid $DECK"); echo "$r" | grep -q "^seating" && break
    done
    sleep 2.5
    expect "midair: the static deck spawned and the player seated on it ($r)" "$r" bash -c "[[ '$r' == seating* ]]"
    hold 2
}

rig() {
    local name=$1 spec=$2 up=$3 w have want
    ask "gen spawn $spec name=$name ahead=0 right=-10 up=$up" >/dev/null
    poll 25 spawned "$name" || { record "midair: $name spawned" "$("$ORB" 'gen status' | tail -1 | cut -c1-140)"; RIG=""; return 1; }
    hold 2
    RIG=$(gid "$name")
    w=$(ask "gen whole" | sed 's/ || /\n/g' | grep -a "'$name'" | head -1 | cut -c1-200)
    have=$(sed -n 's/.*: \([0-9]*\) of \([0-9]*\) blocks.*/\1/p' <<< "$w"); want=$(sed -n 's/.*: \([0-9]*\) of \([0-9]*\) blocks.*/\2/p' <<< "$w")
    expect "midair: $name spawned whole" "${w:-no gen whole line}" ge "${have:-0}" "${want:-1}"
    ask "gridlevel $RIG" "griddamp $RIG off" >/dev/null; hold 2
}

rig_del() {
    local h
    h=$(ask "gen owner $1" | grep -o "head grid [0-9]*" | grep -o "[0-9]*$")
    ask "griddel $1" >/dev/null; for x in $h; do ask "griddel $x" >/dev/null; done; hold 1
}

dropdone() { local p r; p=$(val WheelShockDev.DevPhase); r=$(val WheelShockDev.DevResult); [ "$p" = "idle" ] && [[ "$r" == "drop $1:"* ]] && [[ "$r" != *running* ]]; }
DROPN=${DROPN:-0}
dropm() {
    local name=$1 mode=$2 fwd=$3 v=$4 secs=$5
    DROPN=$((DROPN + 1))
    ask "aeroset WheelShockDev.DevApproach true" "aeroset WheelShockDev.DevNoAero true" "aeroset WheelShockSettings.Mode $mode" \
        "aeroset WheelShockDev.DevGrid $name" "aeroset WheelShockDev.DevSink $v" "aeroset WheelShockDev.DevForward $fwd" \
        "aeroset WheelShockDev.DevRecordSeconds $secs" "aeroset WheelShockDev.DevDrop $DROPN" >/dev/null
    if ! poll $((10 + ${secs%.*})) dropdone "$DROPN"; then R="drop $DROPN: aborted - not done ($(val WheelShockDev.DevPhase))"; I=""; return 1; fi
    R=$(val WheelShockDev.DevResult); I=$(val WheelShockDev.DevImpact)
    return 0
}
# a field of R / I: "name value"
rf() { sed -n "s/.* $1 \(-\?[0-9.]*\).*/\1/p" <<< "$R" | head -1; }
imf() { sed -n "s/.* $1 \(-\?[0-9.]*\).*/\1/p" <<< "$I" | head -1; }

midair_release() {
    ask "aeroset WheelShockDev.DevApproach false" "aeroset WheelShockDev.DevNoAero false" "aeroset WheelShockSettings.Mode 2" \
        "aeroset WheelShockDev.DevForward 0" "aeroset WheelShockDev.DevSink 3" "aeroset WheelShockDev.DevRecordSeconds 4" "aeroset WheelShockDev.DevGrid -" >/dev/null 2>&1
}
