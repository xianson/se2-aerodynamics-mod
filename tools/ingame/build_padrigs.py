"""build_padrigs.py - the pad-foot gears' RATED test rigs (2026-10-21, tools/ingame/padgear_test.sh): one per pad size, a slab of
2.5 m HEAVY ARMOUR cubes (3325 kg each) on FOUR pads of that size at its bottom corners, sized so each pad carries its rating at 2 g
(Gear/PadFoot.cs PadFootSpec - the housing's wheel's: 3.75 / 15 / 33.75 / 93.75 t). GridGen specs for the Orbital Mod's harness
(`gen spawn FILE`, one block a line, positions in 0.25 m cells; the convention of PADRIG / PADTEST, verified in game: a cube occupies
-5..5 about its position, a pad gear -h..h in Y - its top meets the cubes' bottom (-5) at y = -5 - h).

  PADR050  2 x 2 x 1 cubes   4 x Plate 0.5 m   ~15 t    (rated 4 x 3.75 t)
  PADR100  3 x 3 x 2 cubes   4 x Pad 1 m       ~61 t    (rated 4 x 15 t)
  PADR150  4 x 5 x 2 cubes   4 x Pad 1.5 m     ~135 t   (rated 4 x 33.75 t)
  PADR250  4 x 7 x 4 cubes   4 x Plate 2.5 m   ~374 t   (rated 4 x 93.75 t)
The long side runs along +Z (the drop's forward: WheelShockDev.DevNose 1). A charged Battery150 on the top (the gears' power).

  python tools/ingame/build_padrigs.py [--out DIR]       (writes PADR*.txt; prints the masses and shares)
"""
import argparse, os

HERE = os.path.dirname(os.path.abspath(__file__))
HEAVY = ("d4915136-8884-4cf8-9480-f5c98a76c8cf", 3325.39)     # ArmorHeavyCube (2.5 m)
BATTERY = ("7046873e-c9fe-408c-96c8-53a7f0e62bcd", 1000.0)    # Battery150 (as PADTEST; mass approximate)
# size: (server prefab, half height in cells, rated kg at 2 g, cubes nx, nz, ny, the gear's approximate mass with its foot)
PADS = {
    "050": ("ae200001-ae00-9e21-0011-000000000001", 1, 3750.0, (2, 2, 1), 200.0),
    "100": ("ae200001-ae00-9e22-0011-000000000001", 2, 15000.0, (3, 3, 2), 400.0),
    "150": ("ae200001-ae00-9e23-0011-000000000001", 3, 33750.0, (4, 5, 2), 900.0),
    "250": ("ae200001-ae00-9e24-0011-000000000001", 5, 93750.0, (4, 7, 4), 2000.0),
}


def rig(size):
    guid, h, rated, (nx, nz, ny), gm = PADS[size]
    name = f"PADR{size}"
    lines = [f"name {name}", "ahead 40", "up 6", "static 0"]
    mass = 0.0
    for ix in range(nx):
        for iz in range(nz):
            for iy in range(ny):
                lines.append(f"B {HEAVY[0]} {10 * ix} {10 * iy} {10 * iz} Forward Up   # heavy armour 2.5 m")
                mass += HEAVY[1]
    # the battery on the top of the middle column (as PADTEST's: y 7 over a cube's centre)
    cx, cz = 10 * (nx // 2), 10 * (nz // 2)
    lines.append(f"B {BATTERY[0]} {cx} {10 * (ny - 1) + 7} {cz} Forward Up charge=1   # Battery150 (the gears' power)")
    mass += BATTERY[1]
    for x in (0, 10 * (nx - 1)):
        for z in (0, 10 * (nz - 1)):
            lines.append(f"B {guid} {x} {-5 - h} {z} Forward Up   # pad {size} {'L' if x == 0 else 'R'} {'front' if z == 0 else 'back'}")
            mass += gm
    share = mass / 4.0
    head = [f"# {name} - the pad-foot gears' rated rig (tools/ingame/build_padrigs.py; do not edit by hand)",
            f"# {nx} x {nz} x {ny} heavy cubes on four {size} pads: ~{mass / 1000:.1f} t, ~{share / 1000:.2f} t a pad = "
            f"{share / rated * 100:.0f} % of its rating ({rated / 1000:.2f} t)"]
    return head + lines, mass, share, rated


def main():
    ap = argparse.ArgumentParser(); ap.add_argument("--out", default=HERE); a = ap.parse_args()
    for size in PADS:
        lines, mass, share, rated = rig(size)
        open(os.path.join(a.out, f"PADR{size}.txt"), "w", encoding="utf-8", newline="\n").write("\n".join(lines) + "\n")
        print(f"PADR{size}: ~{mass / 1000:.1f} t, ~{share / 1000:.2f} t a pad ({share / rated * 100:.0f} % of {rated / 1000:.2f} t)")


if __name__ == "__main__":
    main()
