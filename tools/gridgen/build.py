"""build - the pipeline.

design (or mesh) -> coverage voxels -> face-connected solid -> 1-block shell + cavity -> engines (lanes, pylons)
-> control surfaces (hinges, flaps on head grids) -> cockpit -> skin refinement -> batteries for the CoM target
-> block list.  build() wraps it in the auto-wing loop: if the batteries cannot reach the CoM target within their
count limit, the wing group slides fore/aft (secant on the CoM error) and everything is rebuilt.
"""
from __future__ import annotations

import itertools
import math
import os

import numpy as np

import aero
import classify
import com
import controls
import design as dz
import engines
import meshio
import se2blocks as sb
import voxel as vx

FUSELAGE_LIKE = ("fuselage", "boom", "nacelle", "fairing")


class Pseudo:
    def __init__(self, cid, role):
        self.id, self.role = cid, role
        self.moves = False


def _m(cells):
    """grid cells (X, Y, Z) -> design metres (x, y, z)."""
    c = np.asarray(cells, float) * sb.CELL_M
    return np.array([c[2], c[0], c[1]])


class Model:
    def __init__(self, d: dict, cat: sb.Catalog, dx: float = 0.0):
        self.cat = cat
        self.dx = dx
        self.problems = []
        d = dz.defaults(d)
        self.grid = d["grid"]
        if d.get("mesh"):
            self._from_mesh(d)
        else:
            self._from_design(d)
        lat = self.lat
        shape = lat.shape
        self.comps.append(Pseudo("nacelle", "nacelle"))
        self.nacelle_label = len(self.comps) - 1
        body = np.isin(self.label, [i for i, c in enumerate(self.comps) if c.role in FUSELAGE_LIKE])
        self.shell, self.cavity = vx.hollow(self.filled, int(self.d["solid_below"]), only=body)
        self.cavity0 = self.cavity.copy()
        self.solid = self.shell.copy()
        z = lambda: np.zeros(shape, bool)
        self.engine_vox, self.lane_vox, self.frozen, self.swept_taken, self.pylon, self.flapvox = z(), z(), z(), z(), \
            z(), z()
        self.extra = []
        self.engines = []
        self.segs = []
        self.equip_boxes = []
        self.shapes = {}
        self.batteries = []
        self.cockpit = None

    @property
    def blocked(self):
        return self.engine_vox | self.lane_vox

    # --------------------------------------------------------------------------------------------------- geometry
    def _engine_pad(self, d):
        u = sb.GRID_UNIT[d["grid"]]
        m = 1
        for e in d.get("engines", []):
            name = e.get("prefab") or sb.thruster_name(e.get("type", "atmospheric"), e.get("size"), d["grid"])
            if name in self.cat:
                m = max(m, int(np.ceil(self.cat[name].size.max() / u)) * max(e.get("cluster", [1, 1])))
        return 6 + 2 * m

    def _from_design(self, d):
        d, fus, comps = dz.components(d, self.dx)
        self.d, self.fus, self.comps = d, fus, comps
        self.surfs = [c for c in comps if isinstance(c, dz.Surface)]
        bb = [c.bbox() for c in comps]
        lo = np.min([b[0] for b in bb], 0)
        hi = np.max([b[1] for b in bb], 0)
        self.lat = vx.Lattice(d["grid"], lo, hi, pad=self._engine_pad(d))
        cov, covs = vx.coverage_implicit(self.lat, comps, s=3, symmetric=True)
        filled = vx.fix_connectivity(cov >= 0.5, cov)
        filled, dropped = vx.largest_component(filled)
        if dropped:
            self.problems.append(f"dropped {len(dropped)} detached voxel islands {dropped}") if max(dropped) > 3 \
                else None
        self.cov, self.filled = cov, filled
        self.label = vx.labels_from(covs, filled)
        self.np_surfs, self.np_fus = self.surfs, fus

    def _from_mesh(self, d):
        self.d = d
        base = os.path.dirname(d.get("_path", "."))
        path = d["mesh"] if os.path.isabs(d["mesh"]) else os.path.join(base, d["mesh"])
        tris = meshio.map_axes(meshio.read_mesh(path), d.get("mesh_axes", "x y z"))
        p = tris.reshape(-1, 3)
        lo, hi = p.min(0), p.max(0)
        scale = float(d["mesh_length"]) / (hi[0] - lo[0]) if d.get("mesh_length") else float(d.get("mesh_scale", 1))
        tris = (tris - np.array([lo[0], (lo[1] + hi[1]) / 2, (lo[2] + hi[2]) / 2])) * scale
        self.tris = tris
        p = tris.reshape(-1, 3)
        self.lat = vx.Lattice(d["grid"], p.min(0), p.max(0), pad=self._engine_pad(d))
        ns = int(d.get("mesh_samples", 4))
        X, Y, Z = self.lat.sample_axes(ns)
        m = meshio.MeshSolid(tris).inside_on_axes(X, Y, Z)
        nx, ny, nz = self.lat.shape
        cov = m.reshape(nx, ns, ny, ns, nz, ns).mean(axis=(1, 3, 5)).astype(np.float32)
        if d.get("symmetric", True):
            cov = (cov + cov[::-1]) / 2
        filled = vx.fix_connectivity(vx.rescue_thin(cov), cov)
        filled, dropped = vx.largest_component(filled)
        self.cov, self.filled = cov, filled
        label, surfs, vfus = classify.classify(filled, self.lat)
        self.label = label
        self.surfs = surfs
        self.comps = [vfus] + surfs
        self.fus = None
        self.np_surfs, self.np_fus = surfs, vfus
        for s in surfs:   # mesh controls options apply to every detected surface
            s.spec = {"controls": dict(d.get("controls", {}))}

    # --------------------------------------------------------------------------------------------------- engines
    def tail_point(self):
        """Design metres of the fuselage's aft end on the centreline (for meshes: from the voxels)."""
        if self.fus is not None:
            return np.array([self.fus.L, 0.0, self.fus.centre_z(self.fus.L - 0.01)])
        f = self.filled & (self.label == 0)
        c = f[self.lat.shape[0] // 2]
        jj, kk = np.nonzero(c)
        k = kk.max()
        j = jj[kk == k].mean()
        p = self.lat.centre_m(np.array([self.lat.shape[0] // 2, j, k]))
        p[0] += self.lat.B / 2
        return p

    def nose_point(self):
        if self.fus is not None:
            return np.array([0.0, 0.0, self.fus.centre_z(0.5)])
        f = self.filled & (self.label == 0)
        c = f[self.lat.shape[0] // 2]
        jj, kk = np.nonzero(c)
        k = kk.min()
        p = self.lat.centre_m(np.array([self.lat.shape[0] // 2, jj[kk == k].mean(), k]))
        p[0] -= self.lat.B / 2
        return p

    def _region(self, lo, hi, clip=False):
        a, b = self.lat.index_range(lo, hi)
        if clip:
            a = np.maximum(a, 0)
            b = np.minimum(b, np.array(self.lat.shape))
            if (a >= b).any():
                return None
        elif (a < 0).any() or (b > np.array(self.lat.shape)).any():
            return None
        return tuple(slice(int(x), int(y)) for x, y in zip(a, b))

    def engine_fits(self, lo, hi, lanes, embedded, carve_ids):
        ids = [i for i, c in enumerate(self.comps) if c.id in carve_ids or c.role in carve_ids]
        r = self._region(lo, hi)
        if r is None:
            return False, None
        if self.engine_vox[r].any() or self.lane_vox[r].any() or self.swept_taken[r].any():
            return False, None
        filled = self.solid | self.cavity
        carve = []
        sub = filled[r]
        if sub.any():
            if not embedded or not np.isin(self.label[r][sub], ids).all():
                return False, None
            carve += [tuple(int(a) + s.start for a, s in zip(v, r)) for v in np.argwhere(sub)]
        for l2, h2, tag in lanes:
            r2 = self._region(l2, h2, clip=True)
            if r2 is None:
                continue
            if self.engine_vox[r2].any() or self.cavity[r2].any():
                return False, None              # never exhaust into / breathe from the enclosed hollow
            sub2 = filled[r2]
            if sub2.any():
                if not embedded or not np.isin(self.label[r2][sub2], ids).all():
                    return False, None
                carve += [tuple(int(a) + s.start for a, s in zip(v, r2)) for v in np.argwhere(sub2)]
        if lanes:
            # every lane voxel must be open air afterwards (a channel cut into a solid tail is a dead end)
            occ = self.solid | self.cavity | self.engine_vox
            for v in carve:
                occ[v] = False
            if self.lat.covers_exactly(lo, hi):
                occ[r] = True               # (a sub-block engine shares its voxel with its own lane: leave it open)
            out = vx.outside(occ)
            for l2, h2, tag in lanes:
                r2 = self._region(l2, h2, clip=True)
                if r2 is not None and not out[r2].all():
                    return False, None
        return True, carve

    def add_engine(self, rec, carve):
        for v in carve:
            self.solid[v] = False
            self.cavity[v] = False
        r = self._region(rec["lo"], rec["hi"])
        self.engine_vox[r] = True
        self.frozen[r] = True
        for l2, h2, tag in rec["lanes"]:
            r2 = self._region(l2, h2, clip=True)
            if r2 is not None:
                self.lane_vox[r2] = True
        for pos, ulo, uhi in rec.get("units", [(rec["pos"], rec["lo"], rec["hi"])]):
            self.extra.append(dict(name=rec["name"], kind="engine", pos=np.asarray(pos, int), orient=rec["orient"],
                                   lo=ulo, hi=uhi))
        rec["carved"] = len(carve)
        self.engines.append(rec)

    def add_pylon(self, rec, prefer):
        lat = self.lat
        lo, hi = rec["lo"], rec["hi"]
        ex_axis = int(np.nonzero(rec["exhaust"])[0][0])
        dirs = [tuple(int(v) for v in prefer)] + [d for d in vx.N6 if d != tuple(int(v) for v in prefer)]
        dirs = [d for d in dirs if d[ex_axis] == 0]
        a, b = lat.index_range(lo, hi)
        best = None
        for dvec in dirs:
            ax = int(np.nonzero(dvec)[0][0])
            s = dvec[ax]
            start = b[ax] if s > 0 else a[ax] - 1
            others = [i for i in range(3) if i != ax]
            for i in range(a[others[0]], b[others[0]]):
                for j in range(a[others[1]], b[others[1]]):
                    for k in range(0, 9):
                        v = [0, 0, 0]
                        v[ax], v[others[0]], v[others[1]] = start + s * k, i, j
                        v = tuple(v)
                        if any(c < 0 or c >= n for c, n in zip(v, lat.shape)):
                            break
                        if self.solid[v] and not self.flapvox[v]:
                            key = (k, 0 if dvec == dirs[0] else 1, v[2] if ax != 2 else 0)
                            if best is None or key < best[0]:
                                best = (key, dvec, start, (i, j), ax, others, k)
                            break
                        if self.engine_vox[v] or self.lane_vox[v] or self.cavity[v]:
                            break
            if best is not None and best[1] == dvec:
                break
        if best is None:
            self.problems.append(f"engine {rec['index']} side {rec['side']}: nothing to attach a pylon to")
            return
        key, dvec, start, (i, j), ax, others, dist = best
        s = dvec[ax]
        aligned = lat.covers_exactly(lo, hi)
        voxs = []
        for k in range(dist):
            v = [0, 0, 0]
            v[ax], v[others[0]], v[others[1]] = start + s * k, i, j
            voxs.append(tuple(v))
        if aligned:
            for v in voxs:
                self.solid[v] = True
                self.pylon[v] = True
                self.engine_vox[v] = True
                self.frozen[v] = True
                self.label[v] = -2
        else:
            # small cubes from the engine face to the structure's face, a 1-small-block column inside the overlap of
            # the engine face and the structure voxel (engine and voxel edges both sit on the small-block lattice)
            small = sb.armor("cube", "small")
            sv = [0, 0, 0]
            sv[ax], sv[others[0]], sv[others[1]] = start + s * dist, i, j
            slo, shi = lat.voxel_box(np.array(sv))
            face = hi[ax] if s > 0 else lo[ax]
            end = slo[ax] if s > 0 else shi[ax]
            c = [0, 0, 0]
            for o in others:
                a0, a1 = max(lo[o], slo[o]), min(hi[o], shi[o])
                mid = (a0 + a1) / 2
                co = int(np.floor((mid - 1 - 1) / 2)) * 2 + 1
                c[o] = int(min(max(co, a0), a1 - 2))
            p = face
            n = 0
            while (s > 0 and p < end) or (s < 0 and p > end):
                blo = np.array(c)
                blo[ax] = p if s > 0 else p - 2
                bhi = blo + 2
                self.extra.append(dict(name=small, kind="pylon", pos=self.cat.pos_for(small, blo), orient=sb.IDENTITY,
                                       lo=blo, hi=bhi))
                r = self._region(blo, bhi)
                if r is not None:
                    self.engine_vox[r] = True
                p += 2 * s
                n += 1
            dist = n
        rec["pylon"] = dist

    def add_nacelle(self, rec, lip=1.0):
        """A round cowling around an external engine: voxels within one block of the engine's face circle, outside
        its footprint (the open duct), from `lip` blocks ahead of the intake to `lip` behind the nozzle."""
        lat = self.lat
        lo, hi = np.asarray(rec["lo"]), np.asarray(rec["hi"])
        if not lat.covers_exactly(lo, hi):
            return
        a = int(np.nonzero(rec["exhaust"])[0][0])
        p, q = [i for i in range(3) if i != a]
        c = (lo + hi) / 2.0
        r_in = math.hypot((hi[p] - lo[p]) / 2, (hi[q] - lo[q]) / 2)
        r_out = r_in + lat.u
        ia, ib = lat.index_range(lo, hi)
        L = int(round(lip))
        rng = [None, None, None]
        rng[a] = range(max(ia[a] - L, 0), min(ib[a] + L, lat.shape[a]))
        for k in (p, q):
            m = int(math.ceil(r_out / lat.u)) + 1
            ck = int(round(c[k] / lat.u)) - lat.i0[k]
            rng[k] = range(max(ck - m, 0), min(ck + m + 1, lat.shape[k]))
        lab = self.nacelle_label
        n = 0
        for v in itertools.product(*rng):
            cc = lat.centre_cells(np.array(v))
            if math.hypot(cc[p] - c[p], cc[q] - c[q]) > r_out:
                continue
            if lo[p] - lat.u / 2 < cc[p] < hi[p] + lat.u / 2 and lo[q] - lat.u / 2 < cc[q] < hi[q] + lat.u / 2:
                continue                                        # the duct: the engine's own footprint stays open
            if self.solid[v] or self.cavity[v] or self.engine_vox[v] or self.lane_vox[v] or self.swept_taken[v]:
                continue
            self.solid[v] = True
            self.label[v] = lab
            n += 1
        rec["nacelle"] = n

    # --------------------------------------------------------------------------------------------------- cockpit
    def place_cockpit(self):
        d = self.d
        ck = d.get("cockpit") or {}
        if ck is False or ck.get("none"):
            return
        name = ck.get("prefab") or sb.DEFAULT_COCKPIT[self.grid]
        orient = tuple(sb.CAL["seat_fwd_up"])
        free = self.cavity & ~self.blocked
        target = None
        if "x" in ck:
            target = float(ck["x"])
        elif "x_frac" in ck and self.fus is not None:
            target = float(ck["x_frac"]) * self.fus.L
        slots = [s for s in com.slot_candidates(self.lat, free, self.cat, [name], orient, self.equip_boxes)
                 if s["count"] == 1]
        how = "inside"
        if not slots:
            # fall back: the cockpit becomes part of the hull - it replaces the hull blocks it covers (exactly, so the
            # shell stays sealed) and may stand proud of it like a canopy, at least half inside the airframe
            fus_ids = [i for i, c in enumerate(self.comps) if c.role in FUSELAGE_LIKE]
            hull = self.cavity | (self.solid & np.isin(self.label, fus_ids))
            occ = self.solid | self.cavity | self.engine_vox | self.flapvox
            air = vx.outside(occ)
            allow = (hull | air) & ~self.blocked & ~self.frozen & ~self.swept_taken
            slots = []
            for s in com.slot_candidates(self.lat, allow, self.cat, [name], orient, self.equip_boxes):
                lo, hi, pos = s["boxes"][0]
                if s["count"] != 1 or not self.lat.covers_exactly(lo, hi):
                    continue
                r = self._region(lo, hi)
                s["hull_frac"] = float(hull[r].mean())
                if s["hull_frac"] >= 0.5:
                    slots.append(s)
            how = "in hull"
        if not slots:
            self.problems.append("no room for the cockpit")
            return

        def score(s):
            c = s["centre"]
            zx = c[2] * sb.CELL_M
            return (abs(zx - target) if target is not None else zx, -c[1])
        snug = [s for s in slots if s.get("hull_frac", 1.0) >= 0.75]   # hull fallback: mostly enclosed, then forward
        if snug:
            slots = snug
        s = min(slots, key=score)
        lo, hi, pos = s["boxes"][0]
        r = self._region(lo, hi)
        self.solid[r] = False
        self.cavity[r] = self.cavity[r] & ~self.lat.covers_exactly(lo, hi)
        self.frozen[r] = True
        self.engine_vox[r] |= self.lat.covers_exactly(lo, hi)
        self.equip_boxes.append((lo, hi))
        blk = dict(name=name, kind="cockpit", pos=np.asarray(pos, int), orient=orient, lo=lo, hi=hi)
        self.extra.append(blk)
        self.cockpit = dict(name=name, how=how, x_m=float(_m((lo + hi) / 2)[0]))

    # --------------------------------------------------------------------------------------------------- skin
    def refine_skin(self):
        if self.d.get("skin", "slopes") != "slopes":
            return
        occ = self.solid | self.cavity | self.engine_vox | self.flapvox
        for sg in self.segs:
            occ[sg["hinge_voxel"]] = True
        out = vx.outside(occ)
        allowed = self.solid & (self.label >= 0) & ~self.pylon      # hull, nacelles, fairings, wings and tails
        shapes = vx.refine_skin(self.solid, self.cavity0 | self.cavity, out, self.cov, allowed,
                                self.frozen | self.flapvox)
        ok = {}
        for idx, (shape, n) in shapes.items():
            if sb.orient_for_normal(shape, n) is not None:
                ok[idx] = (shape, n)
        self.shapes = ok
        cubes = self.solid & ~self.pylon & ~self.frozen
        for idx in ok:
            cubes[idx] = False
        near = np.zeros_like(self.solid)
        for dv in vx.N6:
            near |= vx._shift(self.frozen | self.flapvox, dv)
        forbid = self.cavity | self.engine_vox | self.lane_vox | self.swept_taken | self.flapvox | near
        self.fills = {i: v for i, v in vx.halfslope_fill(self.solid, cubes, out, self.cov, forbid).items()
                      if sb.orient_for_normal("halfslope", v[1]) is not None}

    # --------------------------------------------------------------------------------------------------- mass
    def main_blocks(self, include_batteries=True):
        """Main-grid blocks (dicts with name, kind, pos, orient, lo, hi) in spec order (hinges last)."""
        lat = self.lat
        out = []
        idxs = np.argwhere(self.solid)
        order = np.lexsort((idxs[:, 0], idxs[:, 1], idxs[:, 2]))
        surf_ids = {i for i, c in enumerate(self.comps) if c.role not in FUSELAGE_LIKE}
        for idx in idxs[order]:
            t = tuple(int(v) for v in idx)
            shape, n = self.shapes.get(t, ("cube", None))
            name = sb.armor(shape, self.grid)
            orient = sb.IDENTITY if shape == "cube" else sb.orient_for_normal(shape, n)
            pos = lat.centre_cells(idx)
            lo, hi = self.cat.box(name, pos, orient)
            lab = int(self.label[t])
            kind = "pylon" if self.pylon[t] else ("surface" if lab in surf_ids else "shell")
            out.append(dict(name=name, kind=kind, pos=pos.astype(int), orient=orient, lo=lo, hi=hi, voxel=t,
                            shape=shape))
        for idx, (shape, n) in sorted(getattr(self, "fills", {}).items()):
            name = sb.armor(shape, self.grid)
            orient = sb.orient_for_normal(shape, n)
            pos = lat.centre_cells(np.array(idx))
            lo, hi = self.cat.box(name, pos, orient)
            out.append(dict(name=name, kind="shell", pos=pos.astype(int), orient=orient, lo=lo, hi=hi, voxel=idx,
                            shape=shape))
        for e in self.extra:
            if e["kind"] == "battery" and not include_batteries:
                continue
            out.append(e)
        for sg in self.segs:
            out.append(dict(name=sg["hinge_name"], kind="hinge", pos=sg["hinge_pos"], orient=sg["hinge_orient"],
                            lo=sg["hinge_lo"], hi=sg["hinge_hi"], seg=sg))
        return out

    def mass_items(self, include_batteries=True):
        """(mass, centre metres, kind) of every block on every grid."""
        items = []
        for b in self.main_blocks(include_batteries):
            items.append((self.cat[b["name"]].mass, _m((np.asarray(b["lo"]) + b["hi"]) / 2), b["kind"]))
        for sg in self.segs:
            items.append((self.cat[sg["head_name"]].mass, _m((np.asarray(sg["head_lo"]) + sg["head_hi"]) / 2), "hinge"))
            for f in sg["flaps"]:
                items.append((self.cat[f["name"]].mass, _m((np.asarray(f["lo"]) + f["hi"]) / 2), "flap"))
        return items

    def com(self, include_batteries=True):
        it = self.mass_items(include_batteries)
        m = np.array([a for a, _, _ in it])
        c = np.array([b for _, b, _ in it])
        M = float(m.sum()) + sb.CAL["grid_overhead_kg"] * (1 + len(self.segs))
        return M, (m[:, None] * c).sum(0) / m.sum()

    # --------------------------------------------------------------------------------------------------- stability
    def neutral_point(self):
        hs = [s for s in self.np_surfs]
        if not hs:
            return None
        return aero.neutral_point(hs, self.np_fus)

    # --------------------------------------------------------------------------------------------------- batteries
    def place_batteries(self, np_res):
        d = self.d
        bat = d.get("batteries") or {}
        names = bat.get("prefabs") or sb.DEFAULT_BATTERIES[self.grid]
        n_min = int(bat.get("min", 2))
        n_max = int(bat.get("max", 24 if self.grid == "large" else 80))
        free = self.cavity & ~self.blocked
        slots = com.slot_candidates(self.lat, free, self.cat, names, sb.IDENTITY, self.equip_boxes)
        conf = com.conflicts_of(slots)
        M0, c0 = self.com(include_batteries=False)
        mac = np_res["mac"]
        x_t = np_res["x_np"] - float(d["static_margin"]) * mac
        tol = float(d["com_tolerance"]) * mac
        self.target = dict(x=x_t, tol=tol)
        if not slots:
            self.problems.append("no room for batteries inside the hull")
            self.com_ok = abs(c0[0] - x_t) <= tol
            self.com_residual = self.com_nobatt = float(c0[0] - x_t)
            return
        masses = [s["mass"] for s in slots]
        xs = [s["centre"][2] * sb.CELL_M for s in slots]
        counts = [s["count"] for s in slots]
        chosen, x, ok = com.solve(M0, c0[0], masses, xs, counts, conf, x_t, 0.9 * tol, n_min, n_max)
        ok = abs(x - x_t) <= tol and sum(counts[k] for k in chosen) >= n_min     # aimed at 90%, judged at 100%
        self.batteries = []
        for k in chosen:
            for lo, hi, pos in slots[k]["boxes"]:
                blk = dict(name=slots[k]["name"], kind="battery", pos=np.asarray(pos, int), orient=sb.IDENTITY,
                           lo=lo, hi=hi)
                self.extra.append(blk)
                self.batteries.append(blk)
                self.equip_boxes.append((lo, hi))
        self.com_ok = ok
        self.com_residual = float(x - x_t)
        self.com_nobatt = float(c0[0] - x_t)
        self.battery_slots = len(slots)


def build_once(d, cat, dx=0.0):
    m = Model(d, cat, dx)
    d = m.d
    m.engine_recs = engines.place_all(m, d, m.fus, m.surfs, cat)
    m.segs = controls.place_controls(m, d, m.surfs, cat)
    for sg in m.segs:
        for f in sg["flaps"]:
            m.flapvox[f["voxel"]] = True
    m.place_cockpit()
    m.refine_skin()
    m.np_res = m.neutral_point()
    if m.np_res is None:
        m.problems.append("no lifting surface found: no neutral point")
        m.com_ok = False
        m.target = None
        return m
    m.place_batteries(m.np_res)
    return m


def build(d, cat, log=print):
    """Build with the auto-wing loop.  Returns the best Model: on target and problem-free if any build was, else
    problem-free and closest to the target.  The wing group slides at most max_wing_shift (default a quarter of the
    fuselage length); engine nudges make the CoM a discontinuous function of the slide, so every build is kept."""
    d = dz.defaults(d)
    auto = bool(d.get("auto_wing", True)) and not d.get("mesh")
    L = float(d["fuselage"]["length"]) if d.get("fuselage") else 20.0
    lim = float(d.get("max_wing_shift", 0.25 * L))
    dx = 0.0
    hist, built = [], []
    for it in range(8):
        m = build_once(d, cat, dx)
        built.append(m)
        if (m.com_ok and not m.problems) or not auto or m.target is None:
            break
        g = getattr(m, "com_nobatt", m.com_residual)
        hist.append((dx, g))
        if len(hist) >= 2 and abs(hist[-1][1] - hist[-2][1]) > 1e-6 and                 np.sign(hist[-1][1] - hist[-2][1]) == np.sign(hist[-1][0] - hist[-2][0]) * -1:
            (x0, g0), (x1, g1) = hist[-2], hist[-1]
            nxt = x1 - g1 * (x1 - x0) / (g1 - g0)           # secant, only while the response is monotonic
        else:
            nxt = dx + g * 1.3 * (0.6 if len(hist) > 1 else 1.0)
        B = m.lat.B
        nxt = float(np.clip(round(nxt / (B / 4)) * (B / 4), -lim, lim))
        while any(abs(nxt - h[0]) < 1e-9 for h in hist):
            nxt += B / 4 * (1 if g > 0 else -1)
        dx = nxt
        log(f"    auto-wing: CoM {g:+.2f} m from target, sliding the wing group to dx = {dx:+.2f} m")
    best = min(built, key=lambda m: (bool(m.problems), not m.com_ok, abs(getattr(m, "com_residual", 1e9))))
    best.wing_dx = best.dx
    return best
