"""build_wheelbrick.py - the wheel shocks' in-game test grids (2026-10-20, tools/ingame/wheelshock_test.sh): GridGen specs for the Orbital
Mod's harness (`gen spawn FILE [name= ahead= up= right=]`, Orbital Dev/GridGen.cs: one block a line, positions in 0.25 m cells,
`B PREFAB X Y Z FWD UP [charge=F]`).

  BRICKS: a slab of 2.5 m HEAVY ARMOUR cubes (3325 kg each) with a charged 2.5 m battery on top and FOUR suspensions at its bottom
  corners, sized so each wheel carries a chosen share of its rating (Gear/WheelOleo.cs: a 1 m wheel is rated 15 t):
    WSBRICK  2 x 4 x 2 cubes  ~56 t   ~14 t a wheel (93 % - the Tu-134 Big's share)
    WSLIGHT  2 x 2 x 1 cubes  ~16 t   ~4 t a wheel  (27 %)
    WSOVER   3 x 4 x 3 cubes  ~123 t  ~31 t a wheel (204 %)
  The long side runs along +Z (the wheels roll that way: WheelShockDev.DevNose 1).
  DECK: WSDECK, the MIDAIR deck (2026-10-21; midair.sh): a STATIC slab of 2.5 m light armour cubes, 11 x 72 (27.5 x 180 m), centred,
  top face at +1.25 m, a control seat on its right edge - spawned 2 km up, the rigs dropped onto it.

THE WHEELS (a parameter - swap them for the pad-gear variants of feat/a-maggear later):
  --wheel vanilla100 (default): WheelCornerModule100 (prefab b3826409-..., raw cell box [0,-1,0]..[0,1,1], 43 kg, its 200 kg wheel)
     mounted on the brick's LEFT / RIGHT faces at the bottom corners, the wheel OUTBOARD - as the Tu-134 Big carries them (its left
     modules Forward/Up, its right ones Backward/Up: the wheel on the module's local -X side; the wheel pivot dummy at (-0.075,
     -0.087, 0.125) m, travel 0.25 m along +Y - WheelCornerModule100_WheelCornerModuleBlockModelDefinition.def). The module's cells
     y -4..-2 beside the brick's bottom cubes (y -4..5): its wheel's bottom hangs ~0.34 m under the brick extended, ~0.09 m at full
     compression (a hard bottom-out touches the hull: counted).
  --wheel custom --wheel-guid G --wheel-box x0,y0,z0,x1,y1,z1 --wheel-mass KG --mount side|bottom [--wheel-y Y]: any suspension
     block (a mod gear: its SERVER prefab Guid, its raw inclusive cell box at Forward/Up); bottom: its +Y face up against the brick's
     bottom at the four corners (a retractable / pad gear hangs under the hull).
UNVERIFIED in game (2026-10-20, written with the game busy): the suspension's mount face (assumed its +X - the side opposite the
wheel - for 'side') and that the wheel clears the brick. `gen whole` after the spawn says whether every block joined; the first
run's `gen whole` / the parked readout settle it - fix the offsets here, not in the script.

  python tools/ingame/build_wheelbrick.py            (writes tools/ingame/WS*.txt; prints the masses and shares)
"""
import argparse, os

HERE = os.path.dirname(os.path.abspath(__file__))
HEAVY = ("d4915136-8884-4cf8-9480-f5c98a76c8cf", 3325.39)     # ArmorHeavyCube (2.5 m), cells -4..5
LIGHT = ("2eacbbf2-d8fb-4a78-91dc-7b492517ef97", 1048.55)     # ArmorCubeLight250
BATTERY = ("b22450b6-ad1d-4902-a768-f54adb49516f", 1902.37)   # Battery250, cells -4..5, Down mount full
RATED_1M = 15000.0                                              # Gear/WheelOleo.cs WheelRating: 1 m wheel
WHEELS = {
    "vanilla100": dict(guid="b3826409-8168-4219-a4e9-58d29b2fd8fa", box=((0, -1, 0), (0, 1, 1)), mass=43.12, wheel_mass=200.0,
                       mount="side", y=-3, rated=RATED_1M),
}


def cells(box, fwd):
    """The cells a block with raw inclusive box takes at P = 0, Forward/Up or Backward/Up (a 180 deg turn about Y: x, z negated)."""
    (x0, y0, z0), (x1, y1, z1) = box
    if fwd == "Forward":
        return (x0, x1), (y0, y1), (z0, z1)
    return (-x1, -x0), (y0, y1), (-z1, -z0)


def brick(name, nx, nz, ny, w, ahead=40, up=4):
    lines = [f"# {name} - the wheel shocks' test brick (tools/ingame/build_wheelbrick.py; do not edit by hand)", f"name {name}",
             f"ahead {ahead}", f"up {up}", "static 0"]
    mass = 0.0
    for ix in range(nx):
        for iz in range(nz):
            for iy in range(ny):
                lines.append(f"B {HEAVY[0]} {10 * ix} {10 * iy} {10 * iz} Forward Up   # heavy armour 2.5 m")
                mass += HEAVY[1]
    # the battery on the top, over the first column (its Down face on a full cube face)
    lines.append(f"B {BATTERY[0]} 0 {10 * ny} 0 Forward Up charge=1   # Battery250 (the suspensions' power)")
    mass += BATTERY[1]
    xmin, xmax = -4, 10 * (nx - 1) + 5          # the brick's cells
    zmin, zmax = -4, 10 * (nz - 1) + 5
    ymin = -4
    for side in ("L", "R"):
        for end in ("front", "back"):
            if w["mount"] == "side":
                fwd = "Forward" if side == "L" else "Backward"
                (cx0, cx1), (cy0, cy1), (cz0, cz1) = cells(w["box"], fwd)
                px = (xmin - 1 - cx1) if side == "L" else (xmax + 1 - cx0)   # against the face, outboard
                py = w["y"]
                pz = (zmin - cz0) if end == "front" else (zmax - cz1)
            else:   # bottom: its top face against the brick's bottom, inside the corner
                fwd = "Forward"
                (cx0, cx1), (cy0, cy1), (cz0, cz1) = cells(w["box"], fwd)
                px = (xmin - cx0) if side == "L" else (xmax - cx1)
                py = ymin - 1 - cy1
                pz = (zmin - cz0) if end == "front" else (zmax - cz1)
            lines.append(f"B {w['guid']} {px} {py} {pz} {fwd} Up   # suspension {side} {end}")
            mass += w["mass"] + w["wheel_mass"]
    share = mass / 4.0
    lines.insert(1, f"# {nx} x {nz} x {ny} heavy cubes: {mass / 1000:.1f} t with its wheels, {share / 1000:.2f} t a wheel = "
                    f"{share / w['rated'] * 100:.0f} % of the wheel's rating ({w['rated'] / 1000:.1f} t)")
    return lines, mass, share


SEAT = "a029b001-36b5-4508-b05b-0223662d91a5"                  # ControlSeat250 (2.5 m)


def deck(nx=11, nz=72):
    """The MIDAIR deck (2026-10-21, tools/ingame/midair.sh): STATIC, 2.5 m light armour, nx x nz cubes centred on the grid's origin
    (11 x 72: 27.5 x 180 m), its top face at +1.25 m, a control seat on the top at its right edge (x +10 m, z 0) facing the grid's
    forward (-Z): the player sits there and every rig is spawned from that camera, 10 m to its left, over the deck's middle - the
    forward drops alternate +Z / -Z along its length."""
    x0, z0 = -(nx // 2), -(nz // 2)
    lines = ["# WSDECK - the midair impact deck (tools/ingame/build_wheelbrick.py; tools/ingame/midair.sh): 2.5 m light armour, "
             f"{nx} x {nz} ({nx * 2.5:.1f} x {nz * 2.5:.0f} m), STATIC, top face at +1.25 m, centred; a control seat at x +10 m",
             "name WSDECK", "ahead 40", "up 0", "static 1"]
    for ix in range(x0, x0 + nx):
        for iz in range(z0, z0 + nz):
            lines.append(f"B {LIGHT[0]} {10 * ix} 0 {10 * iz} Forward Up")
    lines.append(f"B {SEAT} {10 * (x0 + nx - 2)} 7 0 Forward Up   # ControlSeat250 - the player's seat (y 7: on the cube's top, as APTEST's)")
    return lines


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--wheel", default="vanilla100")
    ap.add_argument("--wheel-guid"); ap.add_argument("--wheel-box"); ap.add_argument("--wheel-mass", type=float)
    ap.add_argument("--wheel-body-mass", type=float, default=0.0); ap.add_argument("--wheel-rated", type=float, default=RATED_1M)
    ap.add_argument("--mount", default="bottom"); ap.add_argument("--wheel-y", type=int, default=-3)
    ap.add_argument("--out", default=HERE)
    a = ap.parse_args()
    if a.wheel == "custom":
        b = [int(v) for v in a.wheel_box.split(",")]
        w = dict(guid=a.wheel_guid, box=(tuple(b[:3]), tuple(b[3:])), mass=a.wheel_mass, wheel_mass=a.wheel_body_mass,
                 mount=a.mount, y=a.wheel_y, rated=a.wheel_rated)
    else:
        w = WHEELS[a.wheel]
    for name, dims in (("WSBRICK", (2, 4, 2)), ("WSLIGHT", (2, 2, 1)), ("WSOVER", (3, 4, 3))):
        lines, mass, share = brick(name, *dims, w)
        open(os.path.join(a.out, name + ".txt"), "w", encoding="utf-8", newline="\n").write("\n".join(lines) + "\n")
        print(f"{name}: {mass / 1000:.1f} t, {share / 1000:.2f} t a wheel ({share / w['rated'] * 100:.0f} % of {w['rated'] / 1000:.1f} t)")
    open(os.path.join(a.out, "WSDECK.txt"), "w", encoding="utf-8", newline="\n").write("\n".join(deck()) + "\n")
    print("WSDECK: the midair deck (static), a control seat")


if __name__ == "__main__":
    main()
