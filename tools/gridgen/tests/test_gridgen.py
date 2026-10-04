"""Offline unit tests for gridgen (from tools/gridgen: python -m unittest discover -s tests)."""
from __future__ import annotations

import json
import math
import os
import shutil
import sys
import tempfile
import unittest

import numpy as np
from scipy import ndimage

HERE = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
sys.path.insert(0, HERE)

import aero  # noqa: E402
import build  # noqa: E402
import com  # noqa: E402
import controls  # noqa: E402
import design as dz  # noqa: E402
import meshio  # noqa: E402
import se2blocks as sb  # noqa: E402
import spec as specio  # noqa: E402
import validate as val  # noqa: E402
import voxel as vx  # noqa: E402

CAT = sb.Catalog(snapshot=True)


def box_tris(lo, hi):
    """Closed axis-aligned box as 12 outward triangles."""
    x0, y0, z0 = lo
    x1, y1, z1 = hi
    v = np.array([[x0, y0, z0], [x1, y0, z0], [x1, y1, z0], [x0, y1, z0],
                  [x0, y0, z1], [x1, y0, z1], [x1, y1, z1], [x0, y1, z1]], float)
    quads = [(0, 3, 2, 1), (4, 5, 6, 7), (0, 1, 5, 4), (2, 3, 7, 6), (1, 2, 6, 5), (0, 4, 7, 3)]
    t = []
    for a, b, c, d in quads:
        t += [[v[a], v[b], v[c]], [v[a], v[c], v[d]]]
    return np.array(t)


def icosphere(r, n=3):
    t = (1 + 5 ** 0.5) / 2
    V = [(-1, t, 0), (1, t, 0), (-1, -t, 0), (1, -t, 0), (0, -1, t), (0, 1, t), (0, -1, -t), (0, 1, -t),
         (t, 0, -1), (t, 0, 1), (-t, 0, -1), (-t, 0, 1)]
    F = [(0, 11, 5), (0, 5, 1), (0, 1, 7), (0, 7, 10), (0, 10, 11), (1, 5, 9), (5, 11, 4), (11, 10, 2), (10, 7, 6),
         (7, 1, 8), (3, 9, 4), (3, 4, 2), (3, 2, 6), (3, 6, 8), (3, 8, 9), (4, 9, 5), (2, 4, 11), (6, 2, 10),
         (8, 6, 7), (9, 8, 1)]
    tris = [[np.array(V[a], float), np.array(V[b], float), np.array(V[c], float)] for a, b, c in F]
    for _ in range(n):
        new = []
        for a, b, c in tris:
            ab, bc, ca = (a + b) / 2, (b + c) / 2, (c + a) / 2
            new += [[a, ab, ca], [b, bc, ab], [c, ca, bc], [ab, bc, ca]]
        tris = new
    T = np.array(tris)
    T = T / np.linalg.norm(T, axis=2, keepdims=True) * r
    if meshio.signed_volume(T) < 0:
        T = T[:, ::-1]
    return T


class TestAdapter(unittest.TestCase):
    def test_orientations_are_24_proper_rotations(self):
        mats = {sb.orient_matrix(*o).tobytes() for o in sb.ALL_ORIENTS}
        self.assertEqual(len(sb.ALL_ORIENTS), 24)
        self.assertEqual(len(mats), 24)
        for o in sb.ALL_ORIENTS:
            M = sb.orient_matrix(*o)
            self.assertAlmostEqual(np.linalg.det(M), 1.0)
            self.assertEqual(sb.orient_names(M), o)

    def test_identity_and_catalog_convention(self):
        np.testing.assert_array_equal(sb.orient_matrix("Forward", "Up"), np.eye(3, dtype=int))
        lo, hi = CAT.box("ArmorCubeLight250", (0, 0, 0))
        np.testing.assert_array_equal(lo, [-5, -5, -5])
        np.testing.assert_array_equal(hi, [5, 5, 5])
        lo, hi = CAT.box("ArmorCubeLight50", (4, 2, 0))
        np.testing.assert_array_equal(lo, [3, 1, -1])
        self.assertAlmostEqual(CAT["ArmorCubeLight250"].mass, 1048.6)

    def test_box_position_roundtrip_any_orientation(self):
        for name in ("AtmosphericThruster500", "Battery150", "ControlSeat250", "HingeHead250"):
            for o in sb.ALL_ORIENTS:
                lo, hi = CAT.box(name, (7, -3, 11), o)
                np.testing.assert_array_equal(CAT.pos_for(name, lo, o), [7, -3, 11])
                np.testing.assert_array_equal(np.sort(hi - lo), np.sort(CAT[name].size))

    def test_hinge_orientation_axis_along_span_flap_aft_at_90(self):
        for span in ((1, 0, 0), (0, 1, 0)):
            o = sb.orient_for_hinge(span, (0, 0, 1))
            M = sb.orient_matrix(*o)
            np.testing.assert_array_equal(np.abs(M @ np.array(sb.CAL["hinge_axis_local"])), np.abs(span))
            # Up = aft x Forward: head +Y hangs along Up at 0 deg and trails aft at the 90 deg neutral
            np.testing.assert_array_equal(M[:, 1], np.cross([0, 0, 1], -M[:, 2]))
            at90 = sb.rot(sb.hinge_axis_vec(o), 90) @ (M @ np.array(sb.CAL["flap_mount_local"]))
            np.testing.assert_array_equal(at90, [0, 0, 1])
        # the coordinator's worked example: Forward = Left, aft = +Z -> Up = Down
        o = ("Left", "Down")
        M = sb.orient_matrix(*o)
        np.testing.assert_array_equal(sb.rot(sb.hinge_axis_vec(o), 90) @ M[:, 1], [0, 0, 1])

    def test_headgrid_roundtrip(self):
        o = sb.orient_for_hinge((1, 0, 0), (0, 0, 1))
        origin, Rh = sb.headgrid_frame((30, 0, 130), o)
        p, q = sb.to_headgrid((30, 0, 140), ("Forward", "Up"), origin, Rh)
        np.testing.assert_array_equal(p, [0, 10, 0])          # one block aft = head +Y (the measured mount side)
        back, o2 = sb.from_headgrid(p, q, origin, Rh)
        np.testing.assert_array_equal(back, [30, 0, 140])
        self.assertEqual(o2, ("Forward", "Up"))

    def test_thruster_orientation(self):
        o = sb.orient_for_thruster((0, 0, 1))
        self.assertEqual(o, ("Forward", "Up"))                 # coordinator: 'Forward Up' pushes toward -Z
        np.testing.assert_array_equal(sb.thruster_exhaust("x", sb.orient_for_thruster((0, -1, 0))), [0, -1, 0])

    def test_shape_normals(self):
        for shape in ("slope", "corner", "invcorner"):
            n0 = np.array(sb.CAL["shape_normal"][shape])
            for o in sb.ALL_ORIENTS:
                n = sb.orient_matrix(*o) @ n0
                o2 = sb.orient_for_normal(shape, n)
                np.testing.assert_array_equal(sb.orient_matrix(*o2) @ n0, n)


MEASURED = os.path.exists(os.path.join(HERE, "data", "mounts_measured.txt"))


@unittest.skipUnless(MEASURED, "data/mounts_measured.txt (measured in game) not present")
class TestMounts(unittest.TestCase):
    def test_measured_table_loaded(self):
        self.assertGreaterEqual(len(sb.MOUNT_MEASURED), 60)
        self.assertEqual(sb.mount_local("ArmorSlopeLight50"),
                         {(0, 0, -1), (0, -1, 0), (-1, 0, 0), (1, 0, 0)})        # Forward, Down + triangles
        self.assertEqual(sb.mount_local("Hinge50"), {(-1, 0, 0), (1, 0, 0), (0, -1, 0)})
        self.assertEqual(sb.mount_local("HingeHead50"), {(0, 1, 0)})
        self.assertNotIn((0, 0, -1), sb.mount_local("Battery50"))
        self.assertEqual(sb.mount_local("HydrogenThruster50"), {(0, 0, -1)})
        self.assertNotIn((0, 1, 0), sb.mount_local("Cockpit150"))

    def test_shape_normals_agree_with_measured_full_faces(self):
        """The cut-cube model's full faces must be exactly the faces the game reports as full (4 of 4)."""
        names = {"slope": "ArmorSlopeLight50", "invcorner": "ArmorInvCornerLight50", "corner": "ArmorCornerLight50",
                 "halfslope": "ArmorHalfLongSlope50Light"}
        for shape, name in names.items():
            faces = sb.MOUNT_MEASURED[CAT[name].guid.lower()]
            full = {sb._LOCAL[k] for k, v in faces.items() if v == 4}
            touched = {sb._LOCAL[k] for k, v in faces.items() if v > 0}
            n = np.array(sb.CAL["shape_normal"][shape])
            cut = sb.SHAPE_CUT[shape]
            model_full = set(sb.full_faces(shape, ("Forward", "Up"))) if shape != "halfslope" else {(0, -1, 0)}
            self.assertEqual(model_full, full, shape)
            # every touched face lies on the solid side of the cut: no measured mount on a face the cut removes
            for d in touched:
                self.assertLessEqual(np.dot(d, n) * 0.5, cut + 0.5 * np.abs(n).sum(), shape)
            # and the slanted face (toward +n) carries no full mount
            for d in sb.DIRS.values():
                if np.dot(d, n) > 0:
                    self.assertNotIn(tuple(d), full, shape)

    def test_validator_rejects_slope_held_by_its_slanted_face(self):
        d = tempfile.mkdtemp()
        try:
            cube, slope = CAT["ArmorCubeLight50"].guid, CAT["ArmorSlopeLight50"].guid
            # a slope ahead of the cube showing it its Backward face - the slanted side, no mount points
            lines = ["name t", f"B {cube} 0 0 0 Forward Up", f"B {slope} 0 0 -2 Forward Up"]
            p1 = os.path.join(d, "bad.txt")
            open(p1, "w").write("\n".join(lines) + "\n")
            side = {"grid": "small", "block_m": 0.5, "hinges": [], "lattice": {"unit_cells": 2, "i0": [-3, -3, -3],
                    "shape": [7, 7, 7]}, "cavity": [], "com_m": [0, 0, 0], "com_target_m": None,
                    "com_tolerance_m": None}
            self.assertFalse(val.validate(p1, side, CAT)["checks"]["connected"]["ok"])
            lines[2] = f"B {slope} 0 0 -2 Backward Up"         # turned round: its full Forward face meets the cube
            open(p1, "w").write("\n".join(lines) + "\n")
            self.assertTrue(val.validate(p1, side, CAT)["checks"]["connected"]["ok"])
        finally:
            shutil.rmtree(d)

    def test_hinge_needs_its_own_mount_faces(self):
        d = tempfile.mkdtemp()
        try:
            cube, hinge = CAT["ArmorCubeLight50"].guid, CAT["Hinge50"].guid
            side = {"grid": "small", "block_m": 0.5, "hinges": [], "lattice": {"unit_cells": 2, "i0": [-3, -3, -3],
                    "shape": [7, 7, 7]}, "cavity": [], "com_m": [0, 0, 0], "com_target_m": None,
                    "com_tolerance_m": None}
            p1 = os.path.join(d, "h.txt")
            # a cube in front of the hinge (its Forward face: no mount) -> loose; below it (Down: mount) -> held
            open(p1, "w").write(f"name t\nB {cube} 0 0 -2 Forward Up\nB {hinge} 0 0 0 Forward Up\n")
            self.assertFalse(val.validate(p1, side, CAT)["checks"]["connected"]["ok"])
            open(p1, "w").write(f"name t\nB {cube} 0 -2 0 Forward Up\nB {hinge} 0 0 0 Forward Up\n")
            self.assertTrue(val.validate(p1, side, CAT)["checks"]["connected"]["ok"])
        finally:
            shutil.rmtree(d)


class TestVoxeliser(unittest.TestCase):
    def test_implicit_sphere_volume(self):
        class Ball:
            def inside(self, x, y, z):
                return x * x + y * y + z * z <= 9.0

            def bbox(self):
                return np.array([-3.0] * 3), np.array([3.0] * 3)
        lat = vx.Lattice("small", (-3, -3, -3), (3, 3, 3), pad=1)
        cov, _ = vx.coverage_implicit(lat, [Ball()], s=3)
        cov1, _ = vx.coverage_implicit(lat, [Ball()], s=3, chunk=1)
        np.testing.assert_allclose(cov, cov1)                    # slab chunking changes nothing
        vol = (cov >= 0.5).sum() * lat.B ** 3
        self.assertAlmostEqual(vol / (4 / 3 * math.pi * 27), 1.0, delta=0.06)
        np.testing.assert_array_equal(cov, cov[::-1])           # mirror-symmetric

    def test_mesh_sphere_and_box(self):
        lat = vx.Lattice("small", (-3, -3, -3), (3, 3, 3), pad=1)
        X, Y, Z = lat.sample_axes(3)
        for tris, vol in ((icosphere(2.6, 3), 4 / 3 * math.pi * 2.6 ** 3), (box_tris((-2, -1.5, -1), (2, 1.5, 1)), 24.0)):
            m = meshio.MeshSolid(tris).inside_on_axes(X, Y, Z)
            got = m.mean() * (np.array(lat.shape) * lat.B).prod()
            self.assertAlmostEqual(got / vol, 1.0, delta=0.05)

    def test_overlapping_shells_union_and_holes(self):
        a = box_tris((0, 0, 0), (2, 2, 2))
        b = box_tris((1, 0, 0), (3, 2, 2))
        g = np.linspace(0.05, 2.95, 30), np.linspace(0.05, 1.95, 20), np.linspace(0.05, 1.95, 20)
        m = meshio.inside_grid(np.concatenate([a, b]), *g)
        self.assertGreater(m.mean(), 0.99)                     # parity would hollow the overlap; winding does not
        holed = box_tris((0, 0, 0), (3, 2, 2))[1:]             # one triangle missing: the 3-axis vote still fills it
        self.assertGreater(meshio.inside_grid(holed, *g).mean(), 0.97)

    def test_obj_quads_stl_roundtrip_and_axis_map(self):
        d = tempfile.mkdtemp()
        try:
            p = os.path.join(d, "cube.obj")
            with open(p, "w") as f:
                f.write("v 0 0 0\nv 1 0 0\nv 1 1 0\nv 0 1 0\nv 0 0 1\nv 1 0 1\nv 1 1 1\nv 0 1 1\n")
                f.write("f 1 4 3 2\nf 5 6 7 8\nf 1 2 6 5\nf 3 4 8 7\nf 2 3 7 6\nf 1 5 8 4\n")
            t = meshio.read_obj(p)
            self.assertEqual(t.shape, (12, 3, 3))
            self.assertAlmostEqual(meshio.signed_volume(t), 1.0)
            s = os.path.join(d, "cube.stl")
            meshio.write_stl(s, t)
            np.testing.assert_allclose(meshio.read_stl(s), t, atol=1e-6)
            self.assertAlmostEqual(meshio.signed_volume(meshio.map_axes(t, "-x y z")), 1.0)   # mirror keeps outward
            self.assertAlmostEqual(meshio.signed_volume(meshio.map_axes(t, "z x y")), 1.0)
        finally:
            shutil.rmtree(d)

    def test_hollow_shell_cavity_and_thin_parts(self):
        f = np.zeros((12, 12, 12), bool)
        f[2:10, 2:10, 2:10] = True                              # 8-thick block
        shell, cav = vx.hollow(f, 3)
        self.assertEqual(int(cav.sum()), 6 ** 3)
        self.assertEqual(ndimage.label(shell)[1], 1)
        self.assertTrue((vx.outside(shell) & cav).sum() == 0)   # sealed
        plate = np.zeros((12, 12, 6), bool)
        plate[1:11, 1:11, 2:4] = True                           # 2 thick: stays solid
        self.assertEqual(int(vx.hollow(plate, 3)[1].sum()), 0)
        p3 = np.zeros((12, 12, 7), bool)
        p3[1:11, 1:11, 2:5] = True                              # 3 thick: a 1-block shell around one inner layer
        self.assertEqual(int(vx.hollow(p3, 3)[1].sum()), 8 * 8)

    def test_shell_face_connected_on_round_section(self):
        y, z = np.mgrid[-6:7, -6:7]
        disc = (y ** 2 + z ** 2) <= 30
        f = np.repeat(disc[:, :, None], 10, axis=2)
        f = np.pad(f, 1)
        shell, cav = vx.hollow(f, 3)
        self.assertEqual(ndimage.label(shell)[1], 1)            # keel/spine rows stay face-attached

    def test_fix_connectivity_staircase(self):
        f = np.zeros((8, 8, 3), bool)
        for i in range(6):
            f[i + 1, i + 1, 1] = True                           # diagonal: edge contacts only
        cov = f.astype(np.float32) * 0.6
        g = vx.fix_connectivity(f, cov)
        self.assertEqual(ndimage.label(g)[1], 1)

    def test_rescue_thin_plate(self):
        cov = np.zeros((5, 5, 5), np.float32)
        cov[1:4, 2, 1:4] = 0.30                                 # a plate split across two voxel layers
        cov[1:4, 3, 1:4] = 0.25
        keep = vx.rescue_thin(cov)
        self.assertTrue(keep[2, 2, 2] and not keep[2, 3, 2])

    def test_refine_skin_slope_on_a_ramp(self):
        f = np.zeros((7, 8, 8), bool)
        for k in range(8):
            f[:, : max(1, 8 - k), k] = True
        f[0] = f[-1] = False
        cov = f.astype(np.float32)
        cov[1:-1] = np.where(f[1:-1], 0.6, 0.2)
        out = vx.outside(f)
        res = vx.refine_skin(f, np.zeros_like(f), out, cov, f, np.zeros_like(f))
        self.assertTrue(any(s == "slope" for s, n in res.values()))


    def test_halfslope_fill_in_stair_corner(self):
        f = np.zeros((5, 6, 6), bool)
        f[:, :2, :] = True                     # floor
        f[:, 2:4, 3:] = True                   # a step up toward +Z
        cov = f.astype(np.float32)
        cov[:, 2, 2] = 0.3                     # the inside corner is partly material
        out = vx.outside(f)
        res = vx.halfslope_fill(f, f, out, cov, np.zeros_like(f))
        self.assertIn((2, 2, 2), res)
        self.assertEqual(res[(2, 2, 2)], ("halfslope", (0, 2, -1)))      # mostly up, leaning away from the riser

    def test_shape_geometry_volumes(self):
        import preview
        for shape, vol in (("cube", 1.0), ("slope", 0.5), ("corner", 1 / 6), ("invcorner", 5 / 6), ("halfslope", 0.25)):
            faces = preview.shape_faces(shape, ("Forward", "Up"))
            v = 0.0                             # divergence theorem over the closed polyhedron
            for poly, n, _ in faces:
                newell = sum(np.cross(poly[i], poly[(i + 1) % len(poly)]) for i in range(len(poly)))
                if np.dot(newell, n) < 0:
                    poly = poly[::-1]
                for i in range(1, len(poly) - 1):
                    v += np.dot(poly[0], np.cross(poly[i], poly[i + 1])) / 6
            self.assertAlmostEqual(abs(v), vol, places=6, msg=shape)


class TestGeometry(unittest.TestCase):
    def test_wing_root_two_blocks_thick_tip_thin_te(self):
        s = dz.Surface(dict(role="wing", span=20, root_chord=3, tip_chord=1, x=0, z=0.0, thickness=0.05), None, 0.5)
        self.assertGreaterEqual(s.thickness_at(0.0), 1.0 - 1e-9)          # >= 2 small blocks at the root
        self.assertAlmostEqual(s.thickness_at(s.L), 0.5)                   # 1 block at the tip
        x = np.array([0.9, 2.95])                                          # at 30% chord vs at the trailing edge
        y = np.array([0.1, 0.1])
        zz = np.array([0.45, 0.45])
        np.testing.assert_array_equal(s.inside(x, y, zz), [True, False])   # thick at 30%, thin at the TE

    def test_scale(self):
        d = dz.defaults({"name": "s", "scale": 2.0, "fuselage": {"length": 10, "width": 2, "nose": 0.2},
                         "surfaces": [{"span": 8, "root_chord": 2, "x": 3, "sweep": 20}]})
        self.assertEqual(d["fuselage"]["length"], 20)
        self.assertEqual(d["fuselage"]["nose"], 0.2)
        self.assertEqual(d["surfaces"][0]["span"], 16)
        self.assertEqual(d["surfaces"][0]["sweep"], 20)


class TestCoM(unittest.TestCase):
    def test_reaches_target(self):
        xs = np.arange(0, 20, 1.0)
        masses = np.full(len(xs), 100.0)
        chosen, x, ok = com.solve(1000.0, 12.0, masses, xs, np.ones(len(xs), int), [np.array([], int)] * len(xs),
                                  9.0, 0.05, n_min=2)
        self.assertTrue(ok)
        self.assertLessEqual(abs(x - 9.0), 0.05)
        self.assertGreaterEqual(len(chosen), 2)
        M = 1000 + 100 * len(chosen)
        self.assertAlmostEqual((12000 + sum(100 * xs[k] for k in chosen)) / M, x)

    def test_unreachable_is_flagged(self):
        xs = np.array([10.0, 11.0])
        _, x, ok = com.solve(1000.0, 12.0, [10.0, 10.0], xs, [1, 1], [np.array([], int)] * 2, 0.0, 0.1)
        self.assertFalse(ok)

    def test_conflicts_and_max(self):
        xs = np.array([0.0, 0.0, 0.0])
        conf = [np.array([1]), np.array([0]), np.array([], int)]
        chosen, x, ok = com.solve(100.0, 10.0, [50, 50, 50], xs, [1, 1, 1], conf, 0.0, 0.01, n_max=3)
        self.assertFalse(0 in chosen and 1 in chosen)
        chosen, _, _ = com.solve(100.0, 10.0, [50, 50, 50], xs, [1, 1, 1], [np.array([], int)] * 3, 0.0, 0.01,
                                 n_max=2)
        self.assertEqual(len(chosen), 2)

    def test_slot_pairs_are_mirrored(self):
        lat = vx.Lattice("large", (-10, -5, -5), (10, 5, 5), pad=1)
        free = np.zeros(lat.shape, bool)
        free[1:-1, 1:-1, 1:-1] = True
        slots = com.slot_candidates(lat, free, CAT, ["Battery250", "Battery150"], sb.IDENTITY, [])
        self.assertTrue(slots)
        for s in slots:
            self.assertAlmostEqual(s["centre"][0], 0.0)
            for lo, hi, pos in s["boxes"]:
                np.testing.assert_array_equal(CAT.box(s["name"], pos)[0], lo)


class Plank:
    """A rectangular wing for the aero tests."""

    def __init__(self, sid, role, x_le, chord, span, vertical=False):
        self.id, self.role, self.mirror, self.vertical = sid, role, not vertical, vertical
        self.G = math.pi / 2 if vertical else 0.0
        self.x_le, self.cr = x_le, chord
        self.c, self.span = chord, span

    def strips(self):
        n = 50
        L = self.span if self.vertical else self.span / 2
        s = (np.arange(n) + 0.5) / n * L
        return s, L / n, np.full(n, self.c), np.full(n, self.x_le)


class TestAero(unittest.TestCase):
    def test_wing_alone_quarter_chord(self):
        r = aero.neutral_point([Plank("w", "wing", 10.0, 4.0, 24.0)])
        self.assertAlmostEqual(r["x_np"], 11.0, places=6)
        self.assertAlmostEqual(r["mac"], 4.0)

    def test_tail_moves_np_aft_fin_does_not(self):
        w = Plank("w", "wing", 10.0, 4.0, 24.0)
        t = Plank("t", "htail", 26.0, 2.0, 8.0)
        f = Plank("f", "vtail", 25.0, 3.0, 4.0, vertical=True)
        a = aero.neutral_point([w, t])["x_np"]
        b = aero.neutral_point([w, t, f])["x_np"]
        self.assertGreater(a, 11.5)
        self.assertAlmostEqual(a, b)
        # the classic formula: h_n = h_ac + eta V_H (a_t/a_w)(1 - de/da)
        r = aero.neutral_point([w, t])
        aw, at = aero.lift_slope(6.0), aero.lift_slope(4.0)
        VH = 16.0 * (26.5 - 11.0) / (96.0 * 4.0)
        k = 0.9 * at * (1 - r["downwash"]) * 16.0 / (aw * 96.0)
        self.assertAlmostEqual(a, (11.0 + k * 26.5) / (1 + k), places=6)
        self.assertGreater(VH, 0)

    def test_lift_slope_limits(self):
        self.assertAlmostEqual(aero.lift_slope(1000), 2 * math.pi, delta=0.05)
        self.assertLess(aero.lift_slope(2.0), aero.lift_slope(8.0))


SMALL = {
    "name": "unit_plane", "grid": "large",
    "fuselage": {"length": 40, "width": 11, "height": 11, "nose": 0.15, "tail": 0.3},
    "surfaces": [{"id": "wing", "role": "wing", "span": 44, "root_chord": 11, "tip_chord": 8, "x": 14, "z": "mid"}],
    "tail": "conventional",
    "engines": [{"mount": "pod", "type": "atmospheric", "size": 500, "span_frac": [0.4]}],
}


class TestPipeline(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.dir = tempfile.mkdtemp()
        cls.m = build.build(dict(SMALL), CAT, log=lambda s: None)
        cls.spec = os.path.join(cls.dir, "unit_plane.txt")
        cls.blocks, cls.side = specio.write(cls.spec, cls.m)

    @classmethod
    def tearDownClass(cls):
        shutil.rmtree(cls.dir)

    def test_validates(self):
        v = val.validate(self.spec, self.side, CAT)
        self.assertTrue(v["ok"], json.dumps(v["checks"], indent=1))

    def test_hinge_limits_around_neutral(self):
        for h in self.side["hinges"]:
            self.assertEqual(h["neutral_deg"], 90.0)
            self.assertAlmostEqual(h["limits"][0], 90.0 + h["deflection"][0])
            self.assertTrue(0 <= h["limits"][0] <= 90 <= h["limits"][1] <= 180)
            if h["role"] in ("aileron", "elevator", "rudder"):
                self.assertEqual(sorted(h["limits"]), [65.0, 115.0])

    def test_controls_full_and_symmetric(self):
        roles = [(h["role"], h["side"]) for h in self.side["hinges"]]
        for r in ("aileron", "elevator", "rudder"):
            self.assertTrue(any(x == r for x, _ in roles), r)
        for r in {x for x, s in roles if s in ("left", "right")}:
            self.assertEqual(roles.count((r, "left")), roles.count((r, "right")), r)
        for h in self.side["hinges"]:
            self.assertEqual(h["span_axis"], "Y" if h["role"] == "rudder" else "X")

    def test_spec_roundtrip(self):
        sp = specio.parse(self.spec, CAT)
        self.assertEqual(len(sp["main"]), len(self.blocks))
        self.assertEqual(len(sp["hinges"]), len(self.m.segs))
        self.assertEqual(sum(len(v) for v in sp["flaps"].values()), sum(len(s["flaps"]) for s in self.m.segs))

    def test_cubes_only_mode(self):
        d = dict(SMALL, skin="cubes", engines=[])
        m = build.build_once(d, CAT)
        self.assertTrue(all(b["name"].startswith("ArmorCube") or b["kind"] not in ("shell", "surface")
                            for b in m.main_blocks()))

    def test_validator_catches_corruption(self):
        lines = open(self.spec).read().splitlines()
        first_b = next(l for l in lines if l.startswith("B "))
        bad = os.path.join(self.dir, "overlap.txt")
        # a duplicated block appended after the hinges' B lines keeps H indices valid
        i = max(k for k, l in enumerate(lines) if l.startswith("B "))
        open(bad, "w").write("\n".join(lines[:i + 1] + [first_b] + lines[i + 1:]) + "\n")
        self.assertFalse(val.validate(bad, self.side, CAT)["checks"]["overlap"]["ok"])
        # a flap block moved to the head's -Y side does not attach
        moved = []
        done = False
        for l in lines:
            t = l.split()
            if not done and t and t[0] == "F" and int(t[4]) > 0:
                t[4] = str(-int(t[4]))
                l = " ".join(t)
                done = True
            moved.append(l)
        bad2 = os.path.join(self.dir, "flap.txt")
        open(bad2, "w").write("\n".join(moved) + "\n")
        self.assertFalse(val.validate(bad2, self.side, CAT)["checks"]["attached"]["ok"])

    def test_engine_lane_clear_and_pylon(self):
        for e in self.side["engines"]:
            self.assertEqual(e["exhaust"], [0, 0, 1])
        self.assertEqual(len(self.side["engines"]), 2)


class TestSmallGridFeatures(unittest.TestCase):
    def test_cluster_nacelle_fairing_small_grid(self):
        d = {"name": "smallplane", "fuselage": {"length": 14, "width": 2.2, "height": 2.2, "nose": 0.2, "tail": 0.3},
             "surfaces": [{"id": "wing", "role": "wing", "span": 14, "root_chord": 2.4, "tip_chord": 1.2, "x": 5.2,
                           "z": "low", "thickness": 0.15}],
             "tail": "conventional",
             "engines": [{"mount": "pod", "type": "atmospheric", "size": 100, "cluster": [2, 1], "span_frac": [0.4]}]}
        m = build.build(d, CAT, log=lambda s: None)
        dirn = tempfile.mkdtemp()
        try:
            sp = os.path.join(dirn, "s.txt")
            blocks, side = specio.write(sp, m)
            v = val.validate(sp, side, CAT)
            self.assertTrue(v["ok"], json.dumps(v["checks"], indent=1))
        finally:
            shutil.rmtree(dirn)
        self.assertEqual(side["grid"], "small")
        self.assertEqual(sum(1 for b in blocks if b["kind"] == "engine"), 4)      # 2 per pod x 2 pods
        self.assertTrue(all(e["nacelle_blocks"] > 0 for e in side["engines"]))
        self.assertTrue(any(c.role == "fairing" for c in m.comps))
        self.assertGreater(side["shaped_blocks"], 0)


SKETCH = {"name": "unit_sketch", "fuselage": {"length": 19, "width": 3.5, "height": 3.5},
          "wing": {"span": 14, "root_chord": 3.0, "tip_chord": 3.0, "x": 11.4}, "tail": True, "tail_taper": 1.0,
          "coverage": "sketch", "engines": [{"mount": "tail", "type": "hydrogen", "size": 200}]}


class TestCleanStyle(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.m = build.build(dict(SKETCH), CAT, log=lambda s: None)
        cls.dir = tempfile.mkdtemp()
        cls.spec = os.path.join(cls.dir, "unit_sketch.txt")
        cls.blocks, cls.side = specio.write(cls.spec, cls.m)

    @classmethod
    def tearDownClass(cls):
        shutil.rmtree(cls.dir)

    def test_validates(self):
        v = val.validate(self.spec, self.side, CAT)
        self.assertTrue(v["ok"], json.dumps(v["checks"], indent=1))

    def test_constant_section_and_flat_wing(self):
        m = self.m
        f = m.filled & (m.label == 0)
        ks = np.nonzero(f.any(axis=(0, 1)))[0]
        mid = ks[len(ks) // 3: len(ks) // 2]                      # body rows ahead of the wing
        for k in mid[1:]:
            np.testing.assert_array_equal(f[:, :, k], f[:, :, mid[0]])   # the same ring repeated
        wi = [i for i, c in enumerate(m.comps) if c.id == "wing"][0]
        js = np.nonzero((m.filled & (m.label == wi)).any(axis=(0, 2)))[0]
        self.assertEqual(len(js), 1)                              # one flat 1-block plate, no dihedral steps

    def test_wing_thin_enough_for_aero(self):
        """aero's wing detector rejects t/c > 0.5; the main grid keeps only the chord ahead of the hinges."""
        m = self.m
        wi = [i for i, c in enumerate(m.comps) if c.id == "wing"][0]
        cells = np.argwhere(m.solid & (m.label == wi))
        cols = {}
        for i, j, k in cells:
            cols.setdefault(i, []).append(k)
        chords = [max(v) - min(v) + 1 for v in cols.values()]
        self.assertLessEqual(1.0 / np.mean(chords), 0.5)         # 1 block thick over the mean remaining chord

    def test_cockpit_at_the_nose(self):
        self.assertLess(self.side["cockpit"]["x_m"], 0.25 * SKETCH["fuselage"]["length"])
        self.assertEqual(self.side["cockpit"]["how"], "inside")

    def test_sweep_checked_from_the_spawn_pose(self):
        """A block where a wing flap stands at hinge angle 0 (straight up, before the controller swings it to the
        90 deg neutral) must fail the sweep check, though nothing overlaps at neutral."""
        h = next(x for x in self.side["hinges"] if x["role"] == "flap")
        hp = np.array(h["hinge_pos"])
        lines = open(self.spec).read().splitlines()
        cube = CAT["ArmorCubeLight50"].guid
        above = hp + np.array([0, 4, 0])                           # two blocks above the hinge: the raised flap
        i = max(k for k, l in enumerate(lines) if l.startswith("B "))
        lines.insert(i + 1, f"B {cube} {above[0]} {above[1]} {above[2]} Forward Up")
        bad = os.path.join(self.dir, "spawnpose.txt")
        open(bad, "w").write("\n".join(lines) + "\n")
        v = val.validate(bad, self.side, CAT)
        self.assertFalse(v["checks"]["sweep"]["ok"], v["checks"]["sweep"])

    def test_square_flaps_sketch_coverage(self):
        wing = [s for s in self.m.segs if s["surface"] == "wing"]
        self.assertEqual(sorted(s["role"] for s in wing), ["aileron", "aileron", "flap", "flap"])
        for s in wing:
            rows = {f["voxel"][2] for f in s["flaps"]}
            self.assertLessEqual(abs(len(s["columns"]) - len(rows)), 1)   # span ~ chord: square-ish
        self.assertTrue(all(h["limits"][0] < 90 < h["limits"][1] or h["limits"][0] == 90 for h in self.side["hinges"]))

    def test_skin_is_slopes_only(self):
        shapes = {b["shape"] for b in self.blocks if b.get("shape")}
        self.assertTrue(shapes <= {"cube", "slope"}, shapes)


class TestMeshPipeline(unittest.TestCase):
    def test_box_plane_mesh(self):
        """A crude plane made of boxes (fuselage + wing + tailplane + fin) goes through the mesh pipeline."""
        parts = [box_tris((0, -5, -5), (40, 5, 5)), box_tris((21, -24, -1.2), (31, 24, 1.2)),
                 box_tris((33, -13, -1.0), (39, 13, 1.0)), box_tris((33, -1.0, 4), (39, 1.0, 12))]
        d = tempfile.mkdtemp()
        try:
            p = os.path.join(d, "boxplane.obj")
            meshio.write_obj(p, np.concatenate(parts))
            des = {"name": "boxplane", "grid": "large", "mesh": p, "_path": p, "batteries": {"max": 60},
                   "engines": [{"mount": "tail", "type": "hydrogen", "size": 250}]}
            m = build.build(des, CAT, log=lambda s: None)
            roles = sorted({s.role for s in m.surfs})
            self.assertEqual(roles, ["htail", "vtail", "wing"])
            sp = os.path.join(d, "boxplane.txt")
            blocks, side = specio.write(sp, m)
            v = val.validate(sp, side, CAT)
            self.assertTrue(v["ok"], json.dumps(v["checks"], indent=1))
        finally:
            shutil.rmtree(d)


if __name__ == "__main__":
    unittest.main()
