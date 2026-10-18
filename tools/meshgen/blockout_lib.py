"""Blockout helpers for SE-style block forms (2026-10-16, from the fresh turboprop build).

One section language: every mass is a loft of 45-degree octagons (hw, top, bottom, top corner, bottom corner).
Corresponding edges stay parallel between stations, so every face is planar. Cores and grooves are even insets
of the same section (ins), so their corner faces run parallel to the shells'.

Usable inside Blender (MCP execute or exec(open(...).read())) and from `blender -b -P`.
"""
import bpy, bmesh, math, itertools
from mathutils import Vector

K = math.sqrt(2) - 1          # an octagon inset by d keeps 45-degree corners with corner - K*d


# ---------- sections ----------
def octo(hw, t, b, ct, cb):
    """45-degree octagon in XZ: half width, top, bottom, top-corner size, bottom-corner size."""
    return [(hw-ct, t), (-(hw-ct), t), (-hw, t-ct), (-hw, b+cb), (-(hw-cb), b), (hw-cb, b), (hw, b+cb), (hw, t-ct)]


def ins(s, d):
    """Even inset of a section by d on every side (corner faces stay parallel)."""
    hw, t, b, ct, cb = s
    return (hw-d, t-d, b+d, ct-K*d, cb-K*d)


def on_line(y, ya, sa, yb, sb):
    """The section at y on the straight line from (ya, sa) to (yb, sb): keeps a station on a flat taper."""
    f = (y-ya)/(yb-ya)
    return tuple(a+(b-a)*f for a, b in zip(sa, sb))


# ---------- scene ----------
def collection(name, parent=None):
    c = bpy.data.collections.get(name) or bpy.data.collections.new(name)
    p = parent or bpy.context.scene.collection
    if c.name not in p.children:
        p.children.link(c)
    return c


def material(name, rgb, metal=0.5, rough=0.5):
    m = bpy.data.materials.get(name) or bpy.data.materials.new(name)
    m.use_nodes = True
    b = next(n for n in m.node_tree.nodes if n.type == "BSDF_PRINCIPLED")
    b.inputs["Base Color"].default_value = (*rgb, 1)
    b.inputs["Metallic"].default_value = metal
    b.inputs["Roughness"].default_value = rough
    m.diffuse_color = (*rgb, 1)
    return m


def to_object(name, bm, col, mats, smooth=False):
    old = bpy.data.objects.get(name)
    if old:
        bpy.data.objects.remove(old, do_unlink=True)
    me = bpy.data.meshes.new(name)
    bm.to_mesh(me)
    bm.free()
    o = bpy.data.objects.new(name, me)
    col.objects.link(o)
    for m in mats:
        me.materials.append(m)
    for p in me.polygons:
        p.use_smooth = smooth
    return o


# ---------- geometry ----------
def add_loft(bm, stations, mat_index=0):
    """stations: [(y, hw, t, b, ct, cb)], ordered; capped. Two stations at one y give a step face."""
    rings = [[bm.verts.new((x, s[0], z)) for x, z in octo(*s[1:])] for s in stations]
    faces = []
    for r0, r1 in zip(rings, rings[1:]):
        for i in range(8):
            j = (i+1) % 8
            faces.append(bm.faces.new((r0[i], r0[j], r1[j], r1[i])))
    faces.append(bm.faces.new(rings[0]))
    faces.append(bm.faces.new(list(reversed(rings[-1]))))
    for f in faces:
        f.material_index = mat_index


def build(name, parts, col, mats):
    """parts: [(stations, mat_index)] -> one mesh; coplanar quads merged so flat tapers show no edge."""
    bm = bmesh.new()
    for st, mi in parts:
        add_loft(bm, st, mi)
    bmesh.ops.recalc_face_normals(bm, faces=bm.faces)
    bmesh.ops.dissolve_limit(bm, angle_limit=math.radians(0.5), use_dissolve_boundaries=False, verts=bm.verts, edges=bm.edges)
    return to_object(name, bm, col, mats)


def hull(name, pts, col, mats):
    bm = bmesh.new()
    vs = [bm.verts.new(p) for p in pts]
    r = bmesh.ops.convex_hull(bm, input=vs)
    loose = {g for g in r["geom_interior"]+r["geom_unused"] if isinstance(g, bmesh.types.BMVert)}
    bmesh.ops.delete(bm, geom=list(loose), context='VERTS')
    bmesh.ops.recalc_face_normals(bm, faces=bm.faces)
    return to_object(name, bm, col, mats)


def ring_y(bm, r, y0, y1, seg=16, r1=None, x=0.0, z=0.0):
    """Cylinder / frustum along Y added to bm, capped; returns its faces."""
    r1 = r if r1 is None else r1
    a = [bm.verts.new((x+r*math.cos(2*math.pi*i/seg), y0, z+r*math.sin(2*math.pi*i/seg))) for i in range(seg)]
    b = [bm.verts.new((x+r1*math.cos(2*math.pi*i/seg), y1, z+r1*math.sin(2*math.pi*i/seg))) for i in range(seg)]
    fs = [bm.faces.new((a[i], a[(i+1) % seg], b[(i+1) % seg], b[i])) for i in range(seg)]
    fs += [bm.faces.new(a), bm.faces.new(list(reversed(b)))]
    return fs


def ogive_y(bm, R, y_base, barrel, L, seg=24, n=12):
    """Tangent ogive along +Y: barrel of length `barrel`, then the nose of length L to a point."""
    rho = (R*R+L*L)/(2*R)
    yb = y_base+barrel
    prof = [(y_base, R), (yb, R)]
    for i in range(1, n):
        x = L*(1-(1-i/n)**1.5)
        prof.append((yb+x, math.sqrt(rho*rho-x*x)+R-rho))
    rings = [[bm.verts.new((r*math.cos(2*math.pi*i/seg), y, r*math.sin(2*math.pi*i/seg))) for i in range(seg)] for y, r in prof]
    tip = bm.verts.new((0, yb+L, 0))
    fs = [bm.faces.new(rings[0])]
    for a, b in zip(rings, rings[1:]):
        fs += [bm.faces.new((a[i], a[(i+1) % seg], b[(i+1) % seg], b[i])) for i in range(seg)]
    fs += [bm.faces.new((rings[-1][i], rings[-1][(i+1) % seg], tip)) for i in range(seg)]
    return fs


def inset_poly(pts, n, d):
    """Constant-width inset of a planar convex polygon: every edge parallel to the outline."""
    k = len(pts)
    cen = sum(pts, Vector())/k
    lines = []
    for i in range(k):
        a, b = pts[i], pts[(i+1) % k]
        e = (b-a).normalized()
        inw = n.cross(e)
        if inw.dot(cen-a) < 0:
            inw = -inw
        lines.append((a+inw*d, e))
    out = []
    for i in range(k):
        p1, e1 = lines[i-1]
        p2, e2 = lines[i]
        c = e1.cross(e2)
        t = (p2-p1).cross(e2).dot(c)/c.length_squared
        out.append(p1+e1*t)
    return out


def cutter(o):
    o.display_type = 'WIRE'
    o.hide_render = True
    return o


def boolean(target, cut, name):
    md = target.modifiers.get(name) or target.modifiers.new(name, 'BOOLEAN')
    md.operation = 'DIFFERENCE'
    md.solver = 'EXACT'
    md.use_self = True            # targets built from several overlapping islands (shell + core ring) need it
    md.use_hole_tolerant = True
    md.object = cut
    md.material_mode = 'TRANSFER'
    return md


def side_slabs(name, y0, y1, z0, z1, x_in, col, mats, x_out=1.5):
    """Two box cutters on +-X (window / side recess), one object, no nested booleans."""
    bm = bmesh.new()
    for s in (1, -1):
        r = bmesh.ops.create_cube(bm, size=1.0)
        xs = (x_in, x_out) if s > 0 else (-x_out, -x_in)
        for v in r["verts"]:
            v.co = Vector((xs[0]+(v.co.x+0.5)*(xs[1]-xs[0]), y0+(v.co.y+0.5)*(y1-y0), z0+(v.co.z+0.5)*(z1-z0)))
    return cutter(to_object(name, bm, col, mats))


def grid_lattice(name, mn, mx, step, col):
    """Wire lattice of block cells (e.g. 2.5 m), drawn in front, unselectable."""
    bm = bmesh.new()
    V = {}

    def v(p):
        k = tuple(round(c, 4) for c in p)
        if k not in V:
            V[k] = bm.verts.new(k)
        return V[k]
    ax = [[mn[i]+j*step for j in range(round((mx[i]-mn[i])/step)+1)] for i in range(3)]
    xs, ys, zs = ax
    for y in ys:
        for z in zs:
            bm.edges.new((v((xs[0], y, z)), v((xs[-1], y, z))))
        for x in xs:
            bm.edges.new((v((x, y, zs[0])), v((x, y, zs[-1]))))
    for x in xs:
        for z in zs:
            bm.edges.new((v((x, ys[0], z)), v((x, ys[-1], z))))
    o = to_object(name, bm, col, [])
    o.display_type = 'WIRE'
    o.show_in_front = True
    o.hide_select = True
    o.hide_render = True
    return o


def parent_keep(child, parent):
    """Parent without moving: update the view layer first, or the inverse uses a stale matrix."""
    bpy.context.view_layer.update()
    child.parent = parent
    child.matrix_parent_inverse = parent.matrix_world.inverted()


# ---------- checks (run all three, then look at close-ups of every joint) ----------
def check_coplanar(names):
    """Pairs of objects with overlapping faces on one plane (z-fighting)."""
    dg = bpy.context.evaluated_depsgraph_get()
    F = {}
    for nm in names:
        o = bpy.data.objects[nm]
        e = o.evaluated_get(dg)
        me = e.to_mesh()
        mw = o.matrix_world
        F[nm] = []
        for p in me.polygons:
            n = (mw.to_3x3() @ p.normal).normalized()
            vs = [mw @ me.vertices[i].co for i in p.vertices]
            F[nm].append((n, n.dot(vs[0]), vs))
        e.to_mesh_clear()

    def bb(vs):
        return [min(v[i] for v in vs) for i in range(3)], [max(v[i] for v in vs) for i in range(3)]
    hits = set()
    for a, b in itertools.combinations(F, 2):
        for na, da, va in F[a]:
            A0, A1 = bb(va)
            for nb, db, vb in F[b]:
                if abs(abs(na.dot(nb))-1) > 1e-4 or abs(da-(db if na.dot(nb) > 0 else -db)) > 1e-3:
                    continue
                B0, B1 = bb(vb)
                ov = [min(A1[i], B1[i])-max(A0[i], B0[i]) for i in range(3)]
                if sorted(ov)[1] > 1e-3 and min(ov) > -1e-3:
                    hits.add((a, b))
    return sorted(hits)


def check_manifold(names):
    dg = bpy.context.evaluated_depsgraph_get()
    out = {}
    for nm in names:
        e = bpy.data.objects[nm].evaluated_get(dg)
        bm = bmesh.new()
        bm.from_mesh(e.to_mesh())
        out[nm] = sum(1 for ed in bm.edges if not ed.is_manifold)
        bm.free()
        e.to_mesh_clear()
    return out


def check_grid(names, step=0.25, tol=0.006):
    """Axis-aligned planes off the feature grid (hidden seats and recess depths are expected hits)."""
    dg = bpy.context.evaluated_depsgraph_get()
    off = set()
    for nm in names:
        o = bpy.data.objects[nm]
        e = o.evaluated_get(dg)
        me = e.to_mesh()
        mw = o.matrix_world
        for p in me.polygons:
            n = (mw.to_3x3() @ p.normal).normalized()
            for ax in range(3):
                if abs(abs(n[ax])-1) < 1e-4:
                    d = abs(n.dot(mw @ me.vertices[p.vertices[0]].co))
                    if abs(d-round(d/step)*step) > tol:
                        off.add((nm, "xyz"[ax], round(d, 3)))
        e.to_mesh_clear()
    return sorted(off)


# ---------- general sections (any simple polygon in XZ, same point count per station) ----------
def section_spined(hw, top, sw, sd, c, bot, k):
    """12-point section, CCW seen from +Y: raised spine (half width sw, flush at top), shoulders sd lower with a
    c corner to the sides, vertical sides, a 45-degree keel bevel of depth k down to the bottom flat (half width hw-k).
    k = small (e.g. 0.25) gives an ordinary bottom corner. Edge directions stay fixed, so lofts stay planar."""
    bw = hw-k
    return [(sw, top), (-sw, top), (-sw, top-sd), (-(hw-c), top-sd), (-hw, top-sd-c), (-hw, bot+k), (-bw, bot),
            (bw, bot), (hw, bot+k), (hw, top-sd-c), (hw-c, top-sd), (sw, top-sd)]


def lerp_section(a, b, f):
    return [(xa+(xb-xa)*f, za+(zb-za)*f) for (xa, za), (xb, zb) in zip(a, b)]


def offset_poly2d(pts, d):
    """Constant offset of a simple polygon in XZ (d > 0 inward). Works on concave outlines (spine steps)."""
    n = len(pts)
    area = sum(pts[i][0]*pts[(i+1) % n][1]-pts[(i+1) % n][0]*pts[i][1] for i in range(n))
    sgn = 1 if area > 0 else -1                     # CCW: interior on the left of each edge
    lines = []
    for i in range(n):
        (x0, z0), (x1, z1) = pts[i], pts[(i+1) % n]
        ex, ez = x1-x0, z1-z0
        L = math.hypot(ex, ez)
        ex, ez = ex/L, ez/L
        nx, nz = -ez*sgn, ex*sgn                     # inward normal
        lines.append(((x0+nx*d, z0+nz*d), (ex, ez)))
    out = []
    for i in range(n):
        (px, pz), (ax, az) = lines[i-1]
        (qx, qz), (bx, bz) = lines[i]
        den = ax*bz-az*bx
        t = ((qx-px)*bz-(qz-pz)*bx)/den
        out.append((px+ax*t, pz+az*t))
    return out


def add_loft_poly(bm, stations, mat_index=0):
    """stations: [(y, [(x,z), ...])] with equal point counts; capped with n-gons."""
    rings = [[bm.verts.new((x, y, z)) for x, z in pts] for y, pts in stations]
    n = len(rings[0])
    faces = []
    for r0, r1 in zip(rings, rings[1:]):
        for i in range(n):
            j = (i+1) % n
            faces.append(bm.faces.new((r0[i], r0[j], r1[j], r1[i])))
    faces.append(bm.faces.new(rings[0]))
    faces.append(bm.faces.new(list(reversed(rings[-1]))))
    for f in faces:
        f.material_index = mat_index


def build_poly(name, parts, col, mats):
    bm = bmesh.new()
    for st, mi in parts:
        add_loft_poly(bm, st, mi)
    bmesh.ops.recalc_face_normals(bm, faces=bm.faces)
    bmesh.ops.dissolve_limit(bm, angle_limit=math.radians(0.5), use_dissolve_boundaries=False, verts=bm.verts, edges=bm.edges)
    return to_object(name, bm, col, mats)


def check_inside(test_names, shell_names, skip=None):
    """Clipping test: how many evaluated vertices of each test object sit inside any shell (ray parity).
    skip(p) -> True excludes deliberate plug-in points (e.g. a pipe end entering the core)."""
    from mathutils.bvhtree import BVHTree
    dg = bpy.context.evaluated_depsgraph_get()

    def tree(o):
        e = o.evaluated_get(dg)
        bm = bmesh.new()
        bm.from_mesh(e.to_mesh())
        bm.transform(o.matrix_world)
        t = BVHTree.FromBMesh(bm)
        bm.free()
        e.to_mesh_clear()
        return t

    def inside(t, p):
        hits, o, d = 0, p.copy(), Vector((0.0013, 1, 0.0007)).normalized()
        while True:
            h = t.ray_cast(o, d)
            if h[0] is None:
                return hits % 2 == 1
            hits += 1
            o = h[0]+d*1e-5
    trees = {n: tree(bpy.data.objects[n]) for n in shell_names}
    out = {}
    for tn in test_names:
        o = bpy.data.objects[tn]
        e = o.evaluated_get(dg)
        me = e.to_mesh()
        bad = []
        for v in me.vertices:
            p = o.matrix_world @ v.co
            if skip and skip(p):
                continue
            hit = [n for n, t in trees.items() if inside(t, p)]
            if hit:
                bad.append((hit[0], tuple(round(c, 3) for c in p)))
        e.to_mesh_clear()
        out[tn] = bad
    return out
