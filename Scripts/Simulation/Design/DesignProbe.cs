#pragma warning disable
using System;
using Keen.VRage.Core;
namespace AeroMod;

/// <summary>
/// THE DESIGN PROBE (2026-10-10, for the build-mode hints: "something while you're building", the CoP that "loops
/// through", "what about Mach", "a calculated top speed at sea level" - the user): the whole ship's air force for ANY
/// flow direction and Mach, as if flying - the hull from its force table, the wings from a wing model of the probe's own
/// (the live one's scratch and last results are untouched) - and from it:
///  - the centre of pressure: the point on the force's line of action nearest the centre of mass;
///  - the static stability about the two axes across the flow: the ship turned a little, does the air turn it back
///    (positive: restoring, per radian, over q x frontal area x size - so ships compare);
///  - the streamlining along it (ForceTable.ShapeStreamlining) and the Mach wall it gives;
///  - the top speed at sea level along a direction: the forward thrust (sea-level air, each thruster's profile at the
///    Mach) against the drag, the speed where they meet.
/// Pure reads of the grid's current shape; client callers are paired with the server copy (position, orientation and
/// bounds), as the flame code pairs them. Sea level: 1.225 kg/m3, 340.3 m/s.
/// </summary>
internal static class DesignProbe
{
    public const float SeaRho = 1.225f, SeaSos = 340.3f;
    static readonly Dictionary<AeroGridComponent, CompressibleWingModel> _wingModels = new();

    /// <summary>The server aero of a grid: its own when it is the server copy; else the server copy at the same place,
    /// turned the same way, with the same local bounds (a client grid; the published flow when it is flying).</summary>
    internal static AeroGridComponent ServerAero(Keen.VRage.DCS.Components.Entity grid)
    {
        if (grid == null) return null;
        foreach (var c in grid.Components) if (c is AeroGridComponent a && a.IsServerScene) return a;
        if (AeroEntryFx.FlowOf(grid, out var p) && p.Aero != null) return p.Aero;
        if (!PhysicsHack.Alive(grid.Data)) return null;
        var wt = grid.Data.GetWorldTransform();
        bool hasBox = grid.Data.TryGet<BoundingBoxData>(out var bb);
        AeroGridComponent best = null; double bestD = 1.0;
        lock (AeroGridComponent.Live)
            foreach (var a in AeroGridComponent.Live)
            {
                if (!a.IsServerScene || a.Entity == null || !PhysicsHack.Alive(a.Entity.Data)) continue;
                var awt = a.Entity.Data.GetWorldTransform();
                double d = (awt.Position - wt.Position).Length();
                if (d >= bestD) continue;
                if (MathF.Abs(Quaternion.Dot(awt.Orientation, wt.Orientation)) < 0.9998f) continue;
                if (hasBox && a.Entity.Data.TryGet<BoundingBoxData>(out var ab) && (ab.BoundingBox.Min - bb.BoundingBox.Min).LengthSquared() + (ab.BoundingBox.Max - bb.BoundingBox.Max).LengthSquared() > 0.05f) continue;
                best = a; bestD = d;
            }
        return best;
    }

    // ── THE SHIP (2026-10-11, the user: "it needs to handle subgrids"): the grid and every grid jointed to it (rotors,
    //    hinges, pistons, connectors - AeroGridComponent.FxGroup, the server's connection search once a second), each
    //    member's figures turned into the aimed grid's axes and summed: forces, torques about the ship's centre of mass,
    //    thrust, mass, the overlay's faces, the control torque. Everything below the per-grid helpers works on a ship.
    internal sealed class Member { public AeroGridComponent A; public WorldTransform W; }
    internal sealed class Ship { public AeroGridComponent Root; public WorldTransform RW; public List<Member> M = new(); public long At; }
    static readonly Dictionary<AeroGridComponent, Ship> _ships = new();

    internal static Ship ShipOf(AeroGridComponent root)
    {
        if (root?.Entity == null) return null;
        long now = System.Diagnostics.Stopwatch.GetTimestamp();
        lock (_ships)
            if (_ships.TryGetValue(root, out var cached) && now - cached.At < System.Diagnostics.Stopwatch.Frequency / 4) return cached;
        root.RefreshFxGroup();
        var sh = new Ship { Root = root, RW = root.Entity.Data.GetWorldTransform(), At = now };
        sh.M.Add(new Member { A = root, W = sh.RW });
        foreach (var a in root.FxGroup)
        {
            if (a == null || ReferenceEquals(a, root) || a.Entity == null || !a.IsServerScene || !PhysicsHack.Alive(a.Entity.Data)) continue;
            sh.M.Add(new Member { A = a, W = a.Entity.Data.GetWorldTransform() });
        }
        lock (_ships) if (InScene(root)) _ships[root] = sh;
        return sh;
    }
    static Vector3 ToRootDir(Ship s, Member m, Vector3 d) => ReferenceEquals(m.A, s.Root) ? d : WorldTransform.TransformDirectionInv(WorldTransform.TransformDirection(d, m.W), s.RW);
    static Vector3 ToMemberDir(Ship s, Member m, Vector3 d) => ReferenceEquals(m.A, s.Root) ? d : WorldTransform.TransformDirectionInv(WorldTransform.TransformDirection(d, s.RW), m.W);
    static Vector3 ToRootPoint(Ship s, Member m, Vector3 p) => ReferenceEquals(m.A, s.Root) ? p
        : WorldTransform.TransformDirectionInv((Vector3)(WorldTransform.Transform((Vector3D)p, in m.W) - s.RW.Position), s.RW);

    /// <summary>The ship's mass (kg) and centre of mass (the aimed grid's axes).</summary>
    internal static float ShipMass(Ship s, out Vector3 com)
    {
        com = default; float total = 0f; Vector3 moment = Vector3.Zero;
        if (s == null) return 0f;
        foreach (var m in s.M)
        {
            if (!PhysicsHack.TryGetMassProperties(m.A.Data, out float mass, out var c) || !(mass > 0f)) continue;
            total += mass; moment += ToRootPoint(s, m, c) * mass;
        }
        if (total > 0f) com = moment / total;
        return total;
    }

    /// <summary>The ship's air force and torque about its centre of mass (the aimed grid's axes): each member's (Forces) in
    /// its own flow, turned and summed. False: the aimed grid has no table yet; a member without one is left out.</summary>
    internal static bool ShipForces(Ship s, Vector3 travelRoot, float mach, float rho, float sos, out Vector3 force, out Vector3 torque, out Vector3 com, out float frontal)
    {
        force = torque = default; frontal = 0f;
        ShipMass(s, out com);
        if (s == null) return false;
        bool any = false;
        foreach (var m in s.M)
        {
            if (!Forces(m.A, ToMemberDir(s, m, travelRoot), mach, rho, sos, out var f, out var t, out var c, out float fr))
            {
                if (ReferenceEquals(m.A, s.Root)) return false;
                continue;
            }
            var fR = ToRootDir(s, m, f);
            force += fR; torque += ToRootDir(s, m, t) + Vector3.Cross(ToRootPoint(s, m, c) - com, fR); frontal += fr; any = true;
        }
        return any;
    }

    /// <summary>The ship's air force and torque (about its centre of mass) moving along travelLocal at this Mach, sea-level
    /// air unless given. False: no force table yet (asked for).</summary>
    internal static bool Forces(AeroGridComponent aero, Vector3 travelLocal, float mach, float rho, float sos, out Vector3 force, out Vector3 torque, out Vector3 com, out float frontal)
    {
        force = torque = com = default; frontal = 0f;
        // (a grid just split or removed: its entity gone under the build hints' feet - a null reference here faulted
        //  BuildHints.Draw mid-pull, 2026-10-13)
        if (aero?.Entity == null) return false;
        var dd = aero.Data;
        if (dd.Scene == null || !dd.Scene.IsEntityAlive(dd.Entity)) return false;
        var model = aero.FxModel; var table = model?.Table;
        if (table == null || model.InnerModel is not DampedShadowedDragModel) { if (aero != null) aero.EntryWantsTable = true; return false; }
        com = aero.Data.TryGet<Keen.VRage.Physics.Data.RigidBodyMassProperties>(out var mp) ? mp.CenterOfMass : table.BuildCom;
        return ForcesAt(aero, travelLocal, mach, rho, sos, com, out force, out torque, out frontal);
    }

    /// <summary>Forces with the centre of mass given - no entity data read, so safe off the simulation thread (the stall
    /// warning's background evaluation): the table is read only, the wings' model is this probe's own, locked.</summary>
    internal static bool ForcesAt(AeroGridComponent aero, Vector3 travelLocal, float mach, float rho, float sos, Vector3 com, out Vector3 force, out Vector3 torque, out float frontal)
    {
        force = torque = default; frontal = 0f;
        var model = aero?.FxModel; var table = model?.Table;
        if (table == null || model.InnerModel is not DampedShadowedDragModel d) return false;
        Vector3 v = Vector3.Normalize(travelLocal) * Math.Max(1f, mach * sos);
        var atm = new AtmosphereState { Density = rho, SpeedOfSound = sos, Pressure = 101325, Temperature = 288.15, DynamicViscosity = 1.8e-5 };
        var ctx = new AeroContext(null, null, v, atm, com);
        var r = table.Evaluate(ctx, d.SubsonicLimit, d.SupersonicLimit, d.Streamlining);
        force = r.Force; torque = r.Torque; frontal = r.FrontalArea;
        var wings = model.Wings;
        if (wings != null && wings.Count > 0)
        {
            CompressibleWingModel wm;
            lock (_wingModels) { if (!_wingModels.TryGetValue(aero, out wm)) { wm = new CompressibleWingModel(); if (InScene(aero)) _wingModels[aero] = wm; } }
            List<WingForceResult> wf;
            lock (wm) wf = wm.ComputeWingForces(System.Runtime.InteropServices.CollectionsMarshal.AsSpan(wings), ctx);
            if (wf != null)
                foreach (var w in wf)
                {
                    var fw = w.LiftForce + w.InducedDrag;
                    force += fw; torque += Vector3.Cross(w.ApplicationPoint - com, fw);
                }
        }
        return float.IsFinite(force.X + force.Y + force.Z + torque.X + torque.Y + torque.Z);
    }

    /// <summary>Everything the build hints show for one flow direction (grid-local, the way the ship travels).</summary>
    internal static bool Probe(AeroGridComponent aero, Vector3 travelLocal, float mach, out Vector3 cop, out Vector3 force, out Vector3 torque,
        out float stabA, out float stabB, out float streamlining)
    {
        cop = force = torque = default; stabA = stabB = 0f; streamlining = -1f;
        if (travelLocal.LengthSquared() < 1e-8f) return false;
        var dHat = Vector3.Normalize(travelLocal);
        var ship = ShipOf(aero);
        if (!ShipForces(ship, dHat, mach, SeaRho, SeaSos, out force, out torque, out var com, out float frontal)) return false;
        float f2 = force.LengthSquared();
        cop = f2 > 1e-6f ? com + Vector3.Cross(force, torque) / f2 : com;
        // static stability about the two axes across the flow: the ship turned by +e about axis a sees the flow turned by -e
        Vector3 a1 = Vector3.Normalize(Vector3.Cross(dHat, MathF.Abs(dHat.Y) < 0.9f ? Vector3.UnitY : Vector3.UnitX)), a2 = Vector3.Cross(dHat, a1);
        const float e = 0.035f;   // (2 degrees)
        float size = Math.Max(1f, aero.FxModel?.Table != null ? MathF.Sqrt(Math.Max(1f, frontal)) : 1f);
        float qRef = 0.5f * SeaRho * MathF.Pow(Math.Max(1f, mach * SeaSos), 2) * Math.Max(1f, frontal) * size;
        float Stab(Vector3 a)
        {
            if (!ShipForces(ship, Vector3.Transform(dHat, Quaternion.CreateFromAxisAngle(a, -e)), mach, SeaRho, SeaSos, out _, out var tp, out _, out _)) return 0f;
            if (!ShipForces(ship, Vector3.Transform(dHat, Quaternion.CreateFromAxisAngle(a, +e)), mach, SeaRho, SeaSos, out _, out var tm, out _, out _)) return 0f;
            float dTdTheta = Vector3.Dot(tp - tm, a) / (2f * e);   // (the body turned +theta about a: the flow turned -theta)
            return -dTdTheta / qRef;                                // positive: the torque turns it back
        }
        stabA = Stab(a1); stabB = Stab(a2);
        var table = aero.FxModel.Table;
        streamlining = table.StreamliningAlong(dHat, aero.FxModel.InnerModel is DampedShadowedDragModel dm ? dm.Streamlining : 0.4f);
        return true;
    }

    /// <summary>The thrust along a direction at sea level and a Mach (N): each thruster pushing that way, at its live
    /// maximum x its class's sea-level efficiency x its profile at the Mach, by how much it points that way.</summary>
    internal static float ThrustAlong(AeroGridComponent aero, Vector3 dirLocal, float mach, out Vector3 centre)
    {
        // (the ship's: each member's thrusters along the direction turned into its axes, their centre back into the aimed grid's)
        var s = ShipOf(aero);
        if (s != null && s.M.Count > 1)
        {
            float sum = 0f; Vector3 moment = Vector3.Zero;
            foreach (var m in s.M)
            {
                float f = ThrustAlongOne(m.A, ToMemberDir(s, m, dirLocal), mach, out var c);
                if (f > 0f) { sum += f; moment += ToRootPoint(s, m, c) * f; }
            }
            centre = sum > 0f ? moment / sum : default;
            return sum;
        }
        return ThrustAlongOne(aero, dirLocal, mach, out centre);
    }

    static float ThrustAlongOne(AeroGridComponent aero, Vector3 dirLocal, float mach, out Vector3 centre)
    {
        centre = default;
        var cache = aero?._thrusterCache; if (cache == null) return 0f;
        var dHat = Vector3.Normalize(dirLocal);
        float sum = 0f; Vector3 moment = Vector3.Zero;
        for (int i = 0; i < cache.Count; i++)
        {
            var t = cache[i];
            float along = Vector3.Dot(t.ThrustDirection, dHat);
            if (along < 0.5f) continue;
            float prof = t.Profile == null ? 1f : (float)t.Profile.Evaluate(mach);
            float f = SeaLevelMax(t) * Math.Max(0f, prof) * along;
            sum += f; moment += t.GridLocalPosition * f;
        }
        if (sum > 0f) centre = moment / sum;
        return sum;
    }

    /// <summary>A thruster's maximum at sea level (N): its live maximum (its definition's when unreadable) x its class's
    /// sea-level efficiency.</summary>
    static float SeaLevelMax(in ThrusterInfo t)
    {
        float max = ThrustTorque.LiveMax(t.ThrusterEntity, true); if (max < 0f) max = t.MaxPower;
        return max * ThrustTorque.SeaLevelEfficiency(t.ThrusterEntity, SeaRho);
    }

    static readonly DampedShadowedDragModel.TableWork _pressureWork = new();
    internal static string PressureWhy = "-";
    static readonly List<Vector3> _mp = new(), _mn = new(); static readonly List<float> _ma = new(), _mc = new();

    /// <summary>The drag overlay's faces: DampedShadowedDragModel.FacePressures on this grid's model (one at a time).</summary>
    internal static int Pressures(AeroGridComponent aero, Vector3 travelLocal, float mach, List<Vector3> pos, List<Vector3> nrm, List<float> area, List<float> cp, int max)
    {
        string why;
        int n = 0;
        if (aero == null) why = "no server aero";
        else if (travelLocal.LengthSquared() < 1e-8f) why = "no direction";
        else
        {
            // (each member of the ship its own faces, in its own flow, turned into the aimed grid's axes - shadowing is
            //  per grid: a subgrid behind the main one is not hidden by it)
            var s = ShipOf(aero);
            pos.Clear(); nrm.Clear(); area.Clear(); cp.Clear();
            why = "not built";
            if (s != null)
                foreach (var m in s.M)
                {
                    var ov = OverlayOf(m.A, out string w1);
                    if (ReferenceEquals(m.A, s.Root)) why = w1;
                    if (ov == null) continue;
                    lock (_pressureWork)
                        try
                        {
                            int k = ov.Model.FacePressures(ov.Surface, ov.Manifold, ToMemberDir(s, m, travelLocal), mach, _pressureWork, _mp, _mn, _ma, _mc, Math.Max(1, max / s.M.Count));
                            for (int i = 0; i < k; i++) { pos.Add(ToRootPoint(s, m, _mp[i])); nrm.Add(ToRootDir(s, m, _mn[i])); area.Add(_ma[i]); cp.Add(_mc[i]); }
                        }
                        catch (Exception e) { why = "threw " + e.GetType().Name + ": " + e.Message; }
                }
            n = pos.Count;
            if (n > 0) why = "ok"; else if (why == "ok") why = "0 faces";
        }
        if (n == 0) { pos.Clear(); nrm.Clear(); area.Clear(); cp.Clear(); }
        if (why != PressureWhy) { PressureWhy = why; Log.Default?.Info($"[AERO] drag overlay: {why}"); }
        return n;
    }

    /// <summary>THE OVERLAY'S OWN FACES (2026-10-11): the live model often has none - a table from the disk cache never
    /// builds the surface, and a big grid's is released after its build (the overlay found "surface 0") - so the probe
    /// builds a surface of its own from the grid's block boxes, on an aero worker, once per shape: again a second after
    /// the shape last changed (mass or block boxes), while you build. Wing skins included (it shows pressure, not only
    /// the face model's share). Grids over OverlayMaxCells: none (a surface that size is hundreds of MB).</summary>
    sealed class Overlay { public DampedShadowedDragModel Model; public SmoothSurfaceProvider Surface; public ManifoldClassifier Manifold; public long Key; }
    sealed class OverlayState { public Overlay Ready; public bool Building; public long WantKey, BuiltKey; public long KeySince; }
    static readonly Dictionary<AeroGridComponent, OverlayState> _overlays = new();
    public static long OverlayMaxCells = 4_000_000;

    static Overlay OverlayOf(AeroGridComponent aero, out string why)
    {
        OverlayState st;
        lock (_overlays) if (!_overlays.TryGetValue(aero, out st)) { st = new OverlayState(); if (InScene(aero)) _overlays[aero] = st; }
        // (the shape's key: its mass and its live surface's version - both move as blocks go on or off)
        long key = (long)MathF.Round(aero.LastMass) * 31 + (aero.FxSurface?.Version ?? 0);
        long now = System.Diagnostics.Stopwatch.GetTimestamp();
        if (key != st.WantKey) { st.WantKey = key; st.KeySince = now; }
        bool settled = now - st.KeySince > System.Diagnostics.Stopwatch.Frequency;
        if (!st.Building && st.BuiltKey != st.WantKey && (settled || st.Ready == null))
        {
            var boxes = aero.CaptureBoxes();
            long cells = 0; foreach (var (lo, hi) in boxes) cells += (long)(hi.X - lo.X + 1) * (hi.Y - lo.Y + 1) * (hi.Z - lo.Z + 1);
            if (boxes.Count == 0) { why = "no blocks"; return st.Ready; }
            if (cells > OverlayMaxCells) { why = $"too big for the overlay ({cells:N0} cells)"; st.BuiltKey = st.WantKey; return null; }
            st.Building = true; long building = st.WantKey; float bs = aero.FxBlockSize > 0f ? aero.FxBlockSize : 2.5f;
            AeroWork.Enqueue(() => 0f, () =>
            {
                try
                {
                    int k = SnapshotGridAccessor.CellScale(bs); var geo = SnapshotGridAccessor.CellGeometry(k);
                    var snap = k > 1 ? new SnapshotGridAccessor(boxes).CoarsenMajority(k) : new SnapshotGridAccessor(boxes);
                    var surf = new SmoothSurfaceProvider { CellSize = geo.size, CellOffset = geo.offset };
                    surf.BeginBuild(snap, bs); while (!surf.AddCellBatch(int.MaxValue)) { } surf.FinalizeBuild();
                    var man = new ManifoldClassifier(); man.Classify(surf);
                    st.Ready = new Overlay { Model = new DampedShadowedDragModel(), Surface = surf, Manifold = man, Key = building };
                }
                catch (Exception e) { Log.Default?.Info($"[AERO] drag overlay build failed: {e.GetType().Name}: {e.Message}"); }
                finally { st.BuiltKey = building; st.Building = false; }
            });
        }
        why = st.Ready != null ? "ok" : st.Building ? "building the overlay's faces" : "not built";
        return st.Ready;
    }

    /// <summary>The grid's mass (kg; 0 unknown).</summary>
    internal static float Mass(AeroGridComponent aero) => ShipMass(ShipOf(aero), out _);

    /// <summary>The ship's centre of mass (the aimed grid's axes). False: no mass data.</summary>
    internal static bool CenterOfMass(AeroGridComponent aero, out Vector3 com) => ShipMass(ShipOf(aero), out com) > 0f;

    /// <summary>The upset (deg) ControlRatio judges the ship's control at.</summary>
    public static float UpsetDeg = 5f;
    /// <summary>CONTROLLABLE (2026-10-11, the user: "controllable if the rcs can overcome the torques"): the torque the ship
    /// can hold against an upset across dirLocal - its gyros' maximum plus what its thrusters give about each of the two
    /// axes across the flow, the weaker sign of the weaker axis (it must hold either way) - over the air's torque with the
    /// ship UpsetDeg off the flow at this speed (sea level), the larger of the two axes. Over 1: it can hold it. NaN: no table.</summary>
    internal static float ControlRatio(AeroGridComponent aero, Vector3 dirLocal, float speed)
    {
        var s = ShipOf(aero);
        if (s == null || dirLocal.LengthSquared() < 1e-8f) return float.NaN;
        var d = Vector3.Normalize(dirLocal);
        ShipMass(s, out var com);
        Vector3 a1 = Vector3.Normalize(Vector3.Cross(d, MathF.Abs(d.Y) < 0.9f ? Vector3.UnitY : Vector3.UnitX)), a2 = Vector3.Cross(d, a1);
        float mach = Math.Max(0.01f, speed / SeaSos), e = UpsetDeg * MathF.PI / 180f;
        float air = 0f;
        foreach (var a in new[] { a1, a2 })
        {
            if (!ShipForces(s, Vector3.Transform(d, Quaternion.CreateFromAxisAngle(a, e)), mach, SeaRho, SeaSos, out _, out var t, out _, out _)) return float.NaN;
            air = Math.Max(air, MathF.Abs(Vector3.Dot(t, a)));
        }
        if (air < 1f) return float.PositiveInfinity;
        float gyro = 0f, hold = float.MaxValue;
        foreach (var a in new[] { a1, a2 })
        {
            float pos = 0f, neg = 0f;
            foreach (var m in s.M)
            {
                var cache = m.A._thrusterCache; if (cache == null) continue;
                for (int i = 0; i < cache.Count; i++)
                {
                    var t = cache[i];
                    float max = SeaLevelMax(t);
                    var r = ToRootPoint(s, m, t.GridLocalPosition) - com;
                    float tq = Vector3.Dot(Vector3.Cross(r, ToRootDir(s, m, t.ThrustDirection) * max), a);
                    if (tq > 0f) pos += tq; else neg -= tq;
                }
            }
            hold = Math.Min(hold, Math.Min(pos, neg));
        }
        foreach (var m in s.M) { float g = PhysicsHack.TryGetGyroMaxTorque(m.A.Data); if (g > 0f) gyro += g; }
        return (gyro + (hold == float.MaxValue ? 0f : hold)) / air;
    }

    /// <summary>The top speed at sea level travelling along a direction: where the forward thrust (at that Mach) meets the
    /// drag along it. NaN: no table; +inf: thrust beats drag past 3 km/s.</summary>
    internal static float TopSpeed(AeroGridComponent aero, Vector3 dirLocal) => TopSpeed(aero, dirLocal, 1f);

    /// <summary>The sea-level speed along dirLocal where drag meets throttle x the forward thrust (1: the top speed).</summary>
    internal static float TopSpeed(AeroGridComponent aero, Vector3 dirLocal, float throttle)
    {
        var dHat = Vector3.Normalize(dirLocal);
        var ship = ShipOf(aero);
        float Net(float v)
        {
            float m = v / SeaSos;
            if (!ShipForces(ship, dHat, m, SeaRho, SeaSos, out var f, out _, out _, out _)) return float.NaN;
            // (Forces evaluates at speed max(1, m sos): the drag scales from there)
            float drag = -Vector3.Dot(f, dHat);
            return throttle * ThrustAlong(aero, dHat, m, out _) - drag;
        }
        float lo = 1f, hi = 3000f, nlo = Net(lo);
        if (float.IsNaN(nlo)) return float.NaN;
        if (nlo <= 0f) return 0f;
        // the first speed where drag catches up: step up, then bisect
        float prev = lo;
        for (float v = 25f; v <= hi; v *= 1.25f)
        {
            float n = Net(v);
            if (float.IsNaN(n)) return float.NaN;
            if (n <= 0f)
            {
                float a = prev, b = v;
                for (int k = 0; k < 24; k++) { float mid = 0.5f * (a + b); if (Net(mid) > 0f) a = mid; else b = mid; }
                return 0.5f * (a + b);
            }
            prev = v;
        }
        return float.PositiveInfinity;
    }

    /// <summary>Still in a scene (AeroGridComponent.Live) - asked under the cache's own lock before an entry is added: a
    /// background ForcesAt finishing after the grid left put its entries back after Forget, and they kept the grid (and its
    /// tables) alive for the session, 2026-10-15. (Live loses it before Forget takes the lock: no entry slips in after.)</summary>
    static bool InScene(AeroGridComponent a) { lock (AeroGridComponent.Live) return AeroGridComponent.Live.Contains(a); }
    internal static void Forget(AeroGridComponent aero) { lock (_wingModels) _wingModels.Remove(aero); lock (_overlays) _overlays.Remove(aero); lock (_ships) _ships.Remove(aero); }
}
