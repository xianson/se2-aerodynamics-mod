"""ROTORCRAFT DETAIL PASS (2026-10-16, the user: "can we see how far we can get, with you using decals and adding nice
geometry details?" - after comparing our blockouts with Keen's atmospheric thruster). The pilot: the 2.5 turboprop.

Keen's modern look (measured on the vanilla AtmosphericThruster250, see decal_harvest.py): an armour shell of separate
colourable plates over a darker core, gaps and openings where pipes and brackets show, almost no square edges (chamfers),
and a decal layer ~1 mm over the hull: mostly plain trim-sheet bands (grey / dark / white, Trims03) for two-tone colour
blocking and stripes, a few patterned trims (seam plates, hex tabs), and Parts02 stamps (bolts, grilles, hatches, handles).

  blender -b --factory-startup -P rotorcraft_detail.py -- <out.blend> <stamps.json>

The blades and spinner are rotorcraft_blockouts' (imported); the nacelle is rebuilt here. Blender Y = Forward, Z up.
"""
import bpy, bmesh, json, math, os, sys
from mathutils import Matrix, Vector

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import rotorcraft_blockouts as rb

STAMP = {}   # (harvested decal stamps by id: filled by main() or by an importer)
GAP_D = 0.001   # (decals over the hull, as Keen's: ~1 mm)

# Trims03 rows Keen uses (harvested): (v0, v1)
ROW = {"grey": (0.9865, 0.9905), "dark": (0.5865, 0.5905), "white": (0.0162, 0.0188), "seam": (0.335, 0.366),
       "hextab_dark": (0.497, 0.519), "hextab_grey": (0.906, 0.929), "cable": (0.668, 0.689)}
TRIM_M_U, TRIM_M_V = 0.80, 3.63   # (metres per UV unit at Keen's density)


# ── geometry helpers ──
def extrude_outline(bm, pts, O, U, V, N, d0, d1, lean=None):
    """A slab: a 2D outline (u, v) in the frame (O; U, V, N) from N-offset d0 to d1; lean(u) adds to both (a plate tilted
    along U)."""
    dl = lean or (lambda u: 0.0)
    lo = [bm.verts.new(O + U * u + V * v + N * (d0 + dl(u))) for (u, v) in pts]
    hi = [bm.verts.new(O + U * u + V * v + N * (d1 + dl(u))) for (u, v) in pts]
    n = len(pts)
    bm.faces.new(lo[::-1]); bm.faces.new(hi)
    for i in range(n): bm.faces.new((lo[i], lo[(i + 1) % n], hi[(i + 1) % n], hi[i]))


def plate_outline(u0, u1, w, cut=0.0, cuts=(1, 0, 1, 0), taper=0.0):
    """A plate's outline: u0..u1 long, w wide; 45 deg corner cuts of 'cut' on the corners flagged (u0-v, u1-v, u1+v, u0+v);
    'taper' narrows the u1 end by that much each side."""
    h0, h1 = w / 2, w / 2 - taper
    corners = [(u0, -h0, (1, 1)), (u1, -h1, (-1, 1)), (u1, h1, (-1, -1)), (u0, h0, (1, -1))]
    pts = []
    for (u, v, (su, sv)), c in zip(corners, cuts):
        if c and cut > 0:
            # (the corner replaced by two points cut back along both edges)
            if su > 0 and sv > 0: pts += [(u, v + cut), (u + cut, v)]
            elif su < 0 and sv > 0: pts += [(u - cut, v), (u, v + cut)]
            elif su < 0 and sv < 0: pts += [(u, v - cut), (u - cut, v)]
            else: pts += [(u + cut, v), (u, v - cut)]
        else: pts.append((u, v))
    return pts


def oriented_lathe(bm, prof, seg, origin, axis):
    """rb.lathe along an arbitrary axis from origin."""
    tmp = bmesh.new(); rb.lathe(tmp, prof, seg)
    R = Vector((0, 1, 0)).rotation_difference(axis.normalized()).to_matrix().to_4x4()
    tmp.transform(Matrix.Translation(origin) @ R)
    me = bpy.data.meshes.new("_t"); tmp.to_mesh(me); tmp.free(); bm.from_mesh(me); bpy.data.meshes.remove(me)


# ── decal helpers (one decal mesh per model, several atlas materials) ──
class Decals:
    def __init__(self):
        self.bm = bmesh.new(); self.uv = self.bm.loops.layers.uv.new("UVMap"); self.mats = []

    def _mat(self, name):
        if name not in self.mats: self.mats.append(name)
        return self.mats.index(name)

    def quad(self, C, U, V, N, w, h, uvr, mat, rot=0.0):
        """A decal quad centred on C in the plane (U, V; normal N), w along U, h along V, atlas rect uvr (u0, v0, u1, v1)."""
        if rot: U, V = U * math.cos(rot) + V * math.sin(rot), V * math.cos(rot) - U * math.sin(rot)
        P = C + N * GAP_D
        vs = [self.bm.verts.new(P + U * (x * w / 2) + V * (y * h / 2)) for (x, y) in ((-1, -1), (1, -1), (1, 1), (-1, 1))]
        f = self.bm.faces.new(vs); f.material_index = self._mat(mat)
        u0, v0, u1, v1 = uvr
        for l, uv in zip(f.loops, ((u0, v0), (u1, v0), (u1, v1), (u0, v1))): l[self.uv].uv = uv

    def strip(self, C, U, V, N, length, width, row, u_start=0.0):
        """A trim-sheet ribbon: 'length' along U, 'width' along V, one Trims03 row, Keen's texel aspect kept."""
        v0, v1 = ROW[row]; k = width / ((v1 - v0) * TRIM_M_V)
        du = length / (TRIM_M_U * k)
        self.quad(C, U, V, N, length, width, (u_start, v0, u_start + du, v1), "AtlasDecal_Trims03")

    def stamp(self, sid, C, U, V, N, size, rot=0.0):
        """A harvested Parts02 / Stickers stamp, its longer side 'size' metres (aspect from its atlas rect)."""
        s = STAMP[sid]; u0, v0, u1, v1 = s["uv"]; du, dv = u1 - u0, v1 - v0
        w, h = (size, size * dv / du) if du >= dv else (size * du / dv, size)
        self.quad(C, U, V, N, w, h, s["uv"], s["mat"], rot)

    def finish(self, name, col):
        mats = [rb.mat(m, (0.5, 0.5, 0.5)) for m in self.mats]
        o = rb.obj(name, self.bm, mats, col, bevel=None, sharp=89.0)
        for m in mats:
            m.blend_method = "HASHED" if hasattr(m, "blend_method") else None
        return o


# ── the detailed 2.5 turboprop ──
def turboprop_detail(name, s, col):
    k = s / 2.5                                   # (everything below in metres at 2.5, scaled by k)
    Y0, Y1 = -3.6 * k, 3.30 * k                   # (core length; the nacelle cells are -3.75 .. 3.75)
    D = Decals()
    # the core: a dark octagon (flats on the axes and diagonals), inradius 1.0
    bm = bmesh.new(); rb.prism(bm, 8, 1.0 * k / math.cos(math.pi / 8), Y0, Y1, rot=math.pi / 8)
    rb.obj(name + "_Core", bm, [rb.DARK], col)
    # armour plates on the four flats: gaps 4 cm, alternate plates raised 4 cm, corners cut at 45 deg
    segs = [(-3.6, -1.70, False), (-1.66, 0.50, True), (0.54, 2.30, False), (2.34, 3.30, True)]
    sides = {"top": (Vector((0, 0, 1)), Vector((1, 0, 0))), "bottom": (Vector((0, 0, -1)), Vector((-1, 0, 0))),
             "right": (Vector((1, 0, 0)), Vector((0, 0, -1))), "left": (Vector((-1, 0, 0)), Vector((0, 0, 1)))}
    U = Vector((0, 1, 0))
    bm = bmesh.new()
    plates = []
    for side, (N, V) in sides.items():
        for i, (u0, u1, raised) in enumerate(segs):
            if side in ("left", "right") and i == 1: continue          # (the side opening: the core and its pipes show)
            d0 = (1.10 + (0.04 if raised else 0.0)) * k; d1 = d0 + 0.08 * k
            w = 1.90 * k
            cuts = (1, 0, 1, 0) if i % 2 == 0 else (0, 1, 0, 1)
            taper = 0.20 * k if i == 3 else 0.0
            # (the front plates lean in toward the spinner: the nacelle tapers instead of ending as a box)
            lean = (lambda u, a=u0 * k, L=(u1 - u0) * k: -0.22 * k * (u - a) / L) if i == 3 else None
            extrude_outline(bm, plate_outline(u0 * k, u1 * k, w, 0.18 * k, cuts, taper), Vector(), U, V, N, d0, d1, lean)
            plates.append((side, i, u0 * k, u1 * k, w, taper, N, V, d1, lean))
    rb.obj(name + "_Plates", bm, [rb.BODY], col)
    # the side openings: two pipes along the core flat, clamped by brackets
    bm = bmesh.new()
    for sx in (-1, 1):
        for z in (-0.24, 0.24):
            oriented_lathe(bm, [(0.0, 0.065 * k), (2.0 * k, 0.065 * k)], 24, Vector((sx * 1.07 * k, -1.62 * k, z * k)), Vector((0, 1, 0)))
    rb.obj(name + "_Pipes", bm, [rb.METAL], col)
    bm = bmesh.new()
    for sx in (-1, 1):
        for y in (-1.35, 0.15):
            rb.chamfer_box(bm, (sx * 1.0 * k - 0.07 * k, y * k - 0.07 * k, -0.40 * k), (sx * 1.0 * k + 0.12 * k, y * k + 0.07 * k, 0.40 * k), 0.02 * k)
    rb.obj(name + "_Brackets", bm, [rb.DARK], col)
    # exhaust stubs on the upper diagonals: tubes with a wall, pointing out, back and up
    bm = bmesh.new()
    for sx in (-1, 1):
        for z in (0.55, -0.55):
            ax = Vector((sx * 0.55, -0.80, 0.0)).normalized(); base = Vector((sx * 0.95 * k, 0.30 * k, z * k))
            oriented_lathe(bm, [(0.0, 0.13 * k), (0.40 * k, 0.13 * k), (0.40 * k, 0.10 * k), (0.05 * k, 0.10 * k)], 32, base, ax)
    rb.obj(name + "_Exhaust", bm, [rb.METAL], col)
    # the front collar (where the spinner meets the nacelle) and the rear cap
    bm = bmesh.new()
    rb.lathe(bm, [(3.22 * k, 1.04 * k), (3.26 * k, 1.12 * k), (3.36 * k, 1.12 * k), (3.40 * k, 1.04 * k)], 64)
    rb.obj(name + "_Collar", bm, [rb.METAL], col)
    bm = bmesh.new(); rb.lathe(bm, [(-3.70 * k, 0.0), (-3.70 * k, 0.70 * k), (-3.62 * k, 0.78 * k), (-3.58 * k, 0.78 * k)], 48)
    rb.obj(name + "_RearCap", bm, [rb.DARK], col)
    # spinner + blades (the blockout's), and blade cuffs (collars at each blade root)
    bm = bmesh.new(); rb.lathe(bm, [(1.30 * s, 0.30 * s), (1.35 * s, 0.40 * s), (1.75 * s, 0.40 * s)] + rb.ogive(1.75 * s, 0.75 * s, 0.40 * s)[1:])
    rb.obj(name + "_Spinner", bm, [rb.BODY], col)
    bm = bmesh.new()
    for b in range(3): rb.blade(bm, 0.3 * s, 2.45 * s, 0.62 * s, 55, 22, 2 * math.pi * b / 3, "prop", sweep=0.10 * s)
    for v in bm.verts: v.co.y += 1.9 * s
    rb.obj(name + "_Blades", bm, [rb.BLADE, rb.STRIPE], col, bevel=None, sharp=80.0)
    bm = bmesh.new()
    for b in range(3):
        a = 2 * math.pi * b / 3; ax = Matrix.Rotation(a, 3, "Y") @ Vector((0, 0, 1))
        oriented_lathe(bm, [(0.0, 0.17 * k), (0.06 * k, 0.21 * k), (0.30 * k, 0.21 * k), (0.36 * k, 0.15 * k)], 32, Vector((0, 1.9 * s, 0)) + ax * 0.36 * s, ax)
    rb.obj(name + "_Cuffs", bm, [rb.DARK], col)

    # ── decals ──
    for (side, i, u0, u1, w, taper, N, V, d1, lean) in plates:
        if lean: continue   # (no decals on the leaning nose plates: they'd float off the tilted surface)
        C0 = N * d1; L = u1 - u0; cu = (u0 + u1) / 2
        # two-tone: a dark band down one edge of every plate, a thin white stripe beside it on the top / sides
        D.strip(C0 + U * cu + V * (w / 2 - 0.16 * k - taper / 2), U, V, N, L - 0.40 * k, 0.16 * k, "dark")
        if side != "bottom": D.strip(C0 + U * cu + V * (w / 2 - 0.30 * k - taper / 2), U, V, N, L - 0.50 * k, 0.025 * k, "white")
        # bolts in the corners (inset clear of the cuts)
        for su in (-1, 1):
            for sv in (-1, 1):
                D.stamp(22, C0 + U * (cu + su * (L / 2 - 0.30 * k)) + V * (sv * (w / 2 - 0.30 * k - (taper if su > 0 else 0))), U, V, N, 0.06 * k)
        # a patterned seam row across the plate's front end
        D.strip(C0 + U * (u1 - 0.12 * k) + V * 0.0, V, -U, N, w - 0.5 * k - 2 * taper, 0.10 * k, "seam")
        if side == "top" and i == 0: D.stamp(695, C0 + U * (cu - 0.1 * k), U, V, N, 0.9 * k)          # (a hatch)
        if side == "top" and i == 2: D.stamp(3323, C0 + U * cu - V * 0.25 * k, U, V, N, 0.55 * k)     # (a grille)
        if side in ("left", "right") and i == 2: D.stamp(1044, C0 + U * cu, U, V, N, 0.30 * k, rot=math.pi / 2)   # (a handle)
        if side in ("left", "right") and i == 0: D.strip(C0 + U * (cu + 0.2 * k), U, V, N, L * 0.6, 0.14 * k, "hextab_grey")
        if side == "bottom" and i == 3: D.stamp(3323, C0 + U * cu, U, V, N, 0.70 * k)                 # (the intake grille)
    # the core's diagonal flats: dark structural trims; the side openings: a cable run on the core
    for (cx, cz) in ((1, 1), (-1, 1), (1, -1), (-1, -1)):
        N = Vector((cx, 0, cz)).normalized(); V = N.cross(U)
        D.strip(N * 1.0 * k + U * (-0.2 * k), U, V, N, 6.4 * k, 0.30 * k, "hextab_dark")
    for sx in (-1, 1):
        N = Vector((sx, 0, 0)); V = Vector((0, 0, 1))
        D.strip(N * 1.0 * k + U * (-0.58 * k), U, V, N, 2.0 * k, 0.16 * k, "cable")
    D.finish(name + "_Decals", col)


def main():
    global OUT
    args = sys.argv[sys.argv.index("--") + 1:]
    OUT = args[0]; STAMP.update({s["id"]: s for s in json.load(open(args[1]))["stamps"]})
    rb.reset()
    for m in ("AtlasDecal_Trims03", "AtlasDecal_Parts02"): rb.mat(m, (0.5, 0.5, 0.5))
    col = rb.collection("Turboprop_250_Detail")
    turboprop_detail("Turboprop_250D", 2.5, col)
    rb.occupancy("Turboprop_250D_Occupancy", [((-0.5, -1.5, -0.5), (0.5, 1.5, 0.5)), ((-2.5, 1.5, -2.5), (2.5, 2.5, 2.5))], col, 2.5)
    rb.finish(OUT)
    for m in bpy.data.materials:   # (decals blend as Keen's: alpha hashed)
        if m.name.startswith("AtlasDecal") and hasattr(m, "blend_method"): m.blend_method = "HASHED"
    bpy.ops.wm.save_mainfile()
    print("DETAIL saved", OUT, sum(len(o.data.polygons) for o in bpy.data.objects if o.type == "MESH"), "faces")


if __name__ == "__main__":
    main()
