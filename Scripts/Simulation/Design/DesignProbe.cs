#pragma warning disable CS0649, CS8632, CS1591, CS1573, CS1570, CS1574, CS0419, CS1734
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
/// What the probes need of the server grid's engine data (pose, mass, gyros, thrusters, joints, block boxes) comes from its
/// DesignSnapshot, taken by its own step while it is asked (PublishDesign; DesignSnapshot.cs) - never read here from the
/// caller's thread (2026-10-15 scene audit). The first ask after a quiet spell answers "not ready" (no table / NaN / 0).
/// </summary>
internal static class DesignProbe
{
    public const float SeaRho = 1.225f, SeaSos = ThrustProfiles.SeaLevelSoundSpeed;   // (340.3 m/s: the one sea-level value)
    static readonly Dictionary<AeroGridComponent, CompressibleWingModel> _wingModels = new();

    /// <summary>The server aero of a grid: its own when it is the server copy; else the server copy at the same place,
    /// turned the same way, with the same local bounds (a client grid; the published flow when it is flying).</summary>
    internal static AeroGridComponent ServerAero(Keen.VRage.DCS.Components.Entity grid)
    {
        if (grid == null) return null;
        foreach (var c in grid.Components) if (c is AeroGridComponent a && a.IsServerScene) return a;
        if (AeroEntryFx.FlowOf(grid, out var p) && p.Aero != null) return p.Aero;
        if (!GridPhysics.Alive(grid.Data)) return null;
        var wt = grid.Data.GetWorldTransform();
        bool hasBox = grid.Data.TryGet<BoundingBoxData>(out var bb);
        // (the server grids' poses as their own steps publish them (PublishPose) - never their entities: Live holds both scenes'
        //  grids, the caller is the client's job, and a server grid's alive / transform / bounds read from it was the
        //  cross-thread read the game's DataGuard asserts on ("Concurrent access (read) ... IsEntityAlive" - the Orbital Mod's
        //  StationKeepCharge list, in game, 2026-10-15). None fresh yet - the first ask after a quiet spell: null, and the
        //  poses are there for the next one)
        long now = System.Diagnostics.Stopwatch.GetTimestamp(), fresh = System.Diagnostics.Stopwatch.Frequency / 2;
        System.Threading.Volatile.Write(ref _poseWanted, now + 2 * System.Diagnostics.Stopwatch.Frequency);
        AeroGridComponent best = null; double bestD = 1.0;
        lock (AeroGridComponent.Live)
            foreach (var a in AeroGridComponent.Live)
            {
                if (!a.IsServerScene) continue;
                var ps = a.DesignPose;
                Vector3D apos; Quaternion aor; Vector3 amin, amax; bool abox;
                lock (ps) { if (ps.At == 0 || now - ps.At > fresh) continue; apos = ps.Pos; aor = ps.Orient; amin = ps.BoxMin; amax = ps.BoxMax; abox = ps.HasBox; }
                double d = (apos - wt.Position).Length();
                if (d >= bestD) continue;
                if (MathF.Abs(Quaternion.Dot(aor, wt.Orientation)) < 0.9998f) continue;
                if (hasBox && abox && (amin - bb.BoundingBox.Min).LengthSquared() + (amax - bb.BoundingBox.Max).LengthSquared() > 0.05f) continue;
                best = a; bestD = d;
            }
        return best;
    }

    /// <summary>Until when (Stopwatch ticks) the server grids publish their poses for ServerAero: two seconds past its last ask.</summary>
    static long _poseWanted;
    internal sealed class Pose { public Vector3D Pos; public Quaternion Orient; public Vector3 BoxMin, BoxMax; public bool HasBox; public long At; }

    /// <summary>Server, a grid's own step: its pose for ServerAero's pairing (at most 10 a second; only while it is asked - nothing
    /// otherwise: one read).</summary>
    internal static void PublishPose(AeroGridComponent aero, in WorldTransform wt)
    {
        long now = System.Diagnostics.Stopwatch.GetTimestamp();
        if (now > System.Threading.Volatile.Read(ref _poseWanted)) return;
        var ps = aero.DesignPose;
        if (now - System.Threading.Volatile.Read(ref ps.At) < System.Diagnostics.Stopwatch.Frequency / 10) return;
        bool box = aero.Data.TryGet<BoundingBoxData>(out var ab);
        lock (ps) { ps.Pos = wt.Position; ps.Orient = wt.Orientation; ps.HasBox = box; ps.BoxMin = ab.BoundingBox.Min; ps.BoxMax = ab.BoundingBox.Max; ps.At = now; }
    }

    // ── THE SHIP (2026-10-11, the user: "it needs to handle subgrids"): the grid and every grid jointed to it (rotors,
    //    hinges, pistons, connectors - AeroGridComponent.FxGroup, the server's connection search once a second), each
    //    member's figures turned into the aimed grid's axes and summed: forces, torques about the ship's centre of mass,
    //    thrust, mass, the overlay's faces, the control torque. Everything below the per-grid helpers works on a ship.
    //    (From the members' DesignSnapshots only - each server grid's own step reads its engine data while the probe asks,
    //    PublishDesign: the callers are the client's frame and background threads, and their reads of the server grid's
    //    transform, mass, thrusters and joint search were the cross-thread reads the game's DataGuard asserts on, 2026-10-15)
    static readonly Dictionary<AeroGridComponent, DesignShip> _ships = new();
    static long Freq => System.Diagnostics.Stopwatch.Frequency;

    /// <summary>The ship aimed at root (a server grid), from the snapshots. Null: no snapshot fresh yet - the first ask after a
    /// quiet spell (the next step publishes one: ask again), the grid gone, or its aero not stepping (switched off).</summary>
    internal static DesignShip ShipOf(AeroGridComponent root)
    {
        if (root?.Entity == null) return null;
        long now = System.Diagnostics.Stopwatch.GetTimestamp(), f = Freq;
        root.DesignWatch.Ask(now, f);
        lock (_ships)
            if (_ships.TryGetValue(root, out var cached) && now - cached.At < f / 4) return cached;
        var rs = root.DesignWatch.Fresh(now, f);
        if (rs == null || !InScene(root)) return null;
        var sh = new DesignShip { Root = root, At = now };
        sh.Add(root, rs);
        int gn = rs.Group?.Length ?? 0;
        for (int gi = 0; gi < gn; gi++)
        {
            var a = rs.Group[gi];
            if (a == null || ReferenceEquals(a, root) || a.Entity == null || !a.IsServerScene || !InScene(a)) continue;
            a.DesignWatch.Ask(now, f);
            var ms = a.DesignWatch.Fresh(now, f);
            // (a subgrid's first snapshot not in yet - asked just now: the whole ship is not ready, not one without it)
            if (ms == null) { if (root.DesignWatch.Settling(now, f)) return null; continue; }
            sh.Add(a, ms);
        }
        lock (_ships) if (InScene(root)) _ships[root] = sh;
        return sh;
    }

    /// <summary>Server, a grid's own step (AeroSimJobCore, beside PublishPose): its DesignSnapshot while the probe asks (at
    /// most 4 a second; nothing otherwise: one read), and the overlay's block boxes when it asks for a shape.</summary>
    internal static void PublishDesign(AeroGridComponent aero, in WorldTransform wt)
    {
        var w = aero.DesignWatch;
        long now = System.Diagnostics.Stopwatch.GetTimestamp(), f = Freq;
        if (!w.On(now)) return;
        if (w.BoxesDue(out long key)) w.PublishBoxes(key, aero.CaptureBoxes());
        if (!w.Due(now, f)) return;
        aero.RefreshFxGroup();   // (the joint search, here on the server grid's own thread - it ran from the caller's)
        var s = new DesignSnapshot { At = now, Pos = wt.Position, Orient = wt.Orientation, Group = aero.FxGroup };
        s.HasMass = GridPhysics.TryGetMassProperties(aero.Data, out s.Mass, out s.Com);
        // (the gyros' budget as scaled - Gyros.TorqueScale - but not as saturated this second: a design figure, GyroBalance)
        { float gm = GridPhysics.TryGetGyroMaxTorque(aero.Data); s.GyroMax = gm > 0f ? GyroBalance.Unfaded(gm, aero.GyroFade) : gm; }
        var cache = aero._thrusterCache;
        int n = cache.Count;
        if (n > 0)
        {
            s.ThrPos = new Vector3[n]; s.ThrDir = new Vector3[n]; s.ThrSeaMax = new float[n]; s.ThrProfile = new ThrustProfile[n]; s.ThrRev = new bool[n];
            for (int i = 0; i < n; i++)
            {
                var t = cache[i];
                s.ThrPos[i] = t.GridLocalPosition; s.ThrDir[i] = t.ThrustDirection; s.ThrSeaMax[i] = SeaLevelMax(t, out var curve); s.ThrRev[i] = t.Reversible;
                s.ThrProfile[i] = curve;   // (the curve the simulation applies, taken at the Mach the probe asks: SeaLevelMax)
            }
        }
        w.Snap = s;
    }

    /// <summary>The ship's mass (kg) and centre of mass (the aimed grid's axes).</summary>
    internal static float ShipMass(DesignShip s, out Vector3 com)
    {
        com = default;
        return s == null ? 0f : s.Mass(out com);
    }

    /// <summary>The ship's air force and torque about its centre of mass (the aimed grid's axes): each member's (Forces) in
    /// its own flow, turned and summed. False: the aimed grid has no table yet; a member without one is left out.</summary>
    internal static bool ShipForces(DesignShip s, Vector3 travelRoot, float mach, float rho, float sos, out Vector3 force, out Vector3 torque, out Vector3 com, out float frontal)
    {
        force = torque = default; frontal = 0f;
        ShipMass(s, out com);
        if (s == null) return false;
        bool any = false;
        for (int m = 0; m < s.S.Count; m++)
        {
            if (!Forces(s.A[m], s.S[m], s.ToMemberDir(m, travelRoot), mach, rho, sos, out var f, out var t, out var c, out float fr))
            {
                if (m == 0) return false;
                continue;
            }
            var fR = s.ToRootDir(m, f);
            force += fR; torque += s.ToRootDir(m, t) + Vector3.Cross(s.ToRootPoint(m, c) - com, fR); frontal += fr; any = true;
        }
        return any;
    }

    /// <summary>A grid's air force and torque (about its centre of mass: its snapshot's, else the table's) moving along
    /// travelLocal at this Mach, sea-level air unless given. False: no force table yet (asked for).</summary>
    internal static bool Forces(AeroGridComponent aero, DesignSnapshot snap, Vector3 travelLocal, float mach, float rho, float sos, out Vector3 force, out Vector3 torque, out Vector3 com, out float frontal)
    {
        force = torque = com = default; frontal = 0f;
        // (a grid just split or removed: its entity gone under the build hints' feet - a null reference here faulted
        //  BuildHints.Draw mid-pull, 2026-10-13. Gone: out of Live - not asked of the engine from here, 2026-10-15)
        if (aero?.Entity == null || snap == null || !InScene(aero)) return false;
        var model = aero.FxModel; var table = model?.Table;
        if (table == null || model.InnerModel is not DampedShadowedDragModel) { aero.EntryWantsTable = true; return false; }
        com = snap.HasMass ? snap.Com : table.BuildCom;
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
            lock (wm) { model.HandHullEdges(wm, ctx, table); wf = wm.ComputeWingForces(System.Runtime.InteropServices.CollectionsMarshal.AsSpan(wings), ctx); }   // (the wave-drag guard as in flight, 2026-10-18)
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
    /// maximum x its class's sea-level efficiency x its profile at the Mach, by how much it points that way (the ship's:
    /// DesignShip.ThrustAlong, from the snapshots). 0: no snapshot yet.</summary>
    internal static float ThrustAlong(AeroGridComponent aero, Vector3 dirLocal, float mach, out Vector3 centre)
    {
        centre = default;
        var s = ShipOf(aero);
        return s == null ? 0f : s.ThrustAlong(dirLocal, mach, out centre);
    }

    /// <summary>A thruster's maximum at sea level and Mach 0 (N), and the curve the probe applies to it at the Mach it asks:
    /// - a vanilla atmospheric: its live maximum with its Jet curve AND its inlet-angle factor taken out (JetCurve: the live
    ///   value carries both at the grid's Mach and air angle now; the probe assumes straight-in air - InletAngle 1) x its
    ///   class's sea-level efficiency, and the Jet curve (OffsetThrustJob.AppliedProfile);
    /// - one of our PROPULSOR blocks (2026-10-17): its RATING (AeroPropulsorComponent.Rated, the definition's ThrustPower) x
    ///   its density factor at sea level, and its kind's curve (PropulsorKinds.CurveOf) - its live maximum carries its
    ///   airspeed factor, the game's efficiency share, its rpm and its torque cap NOW, which a design figure must not
    ///   (a hovering helicopter's rotors probed at their hover load; a turboprop at 0.2 of its rating in cruise);
    /// - anything else: its live maximum (its definition's when unreadable) x the efficiency, flat or its applied profile.
    /// (Server, the grid's own step: PublishDesign - it reads the thruster's entity.)</summary>
    static float SeaLevelMax(in ThrusterInfo t, out ThrustProfile curve)
    {
        if (t.Propulsor && t.ThrusterEntity?.TryGet<AeroPropulsorComponent>() is { } p)
        {
            curve = AeroPropulsorComponent.CurveOf(p.Kind);
            float rated = p.Rated > 0f ? p.Rated : t.MaxPower;
            return rated * AeroPropulsorComponent.DensityFactor(SeaRho);
        }
        curve = OffsetThrustJob.AppliedProfile(t);
        float max = ThrustTorque.LiveMax(t.ThrusterEntity, true);
        if (max < 0f) max = t.MaxPower;
        else if (t.Settings != null) max = t.Settings.JetUnshaped(max);
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
                for (int m = 0; m < s.S.Count; m++)
                {
                    var ov = OverlayOf(s.A[m], out string w1);
                    if (m == 0) why = w1;
                    if (ov == null) continue;
                    lock (_pressureWork)
                        try
                        {
                            int k = ov.Model.FacePressures(ov.Surface, ov.Manifold, s.ToMemberDir(m, travelLocal), mach, _pressureWork, _mp, _mn, _ma, _mc, Math.Max(1, max / s.S.Count));
                            for (int i = 0; i < k; i++) { pos.Add(s.ToRootPoint(m, _mp[i])); nrm.Add(s.ToRootDir(m, _mn[i])); area.Add(_ma[i]); cp.Add(_mc[i]); }
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
            // (the block boxes as the server grid's own step reads them - PublishDesign, asked here: CaptureBoxes walks the grid's
            //  blocks, and it ran on the caller's thread - the client's frame - 2026-10-15. Until they are in: the faces so far)
            var got = aero.DesignWatch.BoxesFor(st.WantKey);
            if (got == null) { why = st.Ready != null ? "ok" : "reading the overlay's blocks"; return st.Ready; }
            var boxes = got.List;
            long cells = 0; foreach (var (lo, hi) in boxes) cells += (long)(hi.X - lo.X + 1) * (hi.Y - lo.Y + 1) * (hi.Z - lo.Z + 1);
            if (boxes.Count == 0) { why = "no blocks"; return st.Ready; }
            if (cells > OverlayMaxCells) { why = $"too big for the overlay ({cells:N0} cells)"; st.BuiltKey = st.WantKey; aero.DesignWatch.ReleaseBoxes(got); return null; }
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
                finally { st.BuiltKey = building; st.Building = false; aero.DesignWatch.ReleaseBoxes(got); }
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
            s.ThrustHold(a, com, out float pos, out float neg);
            hold = Math.Min(hold, Math.Min(pos, neg));
        }
        gyro = s.GyroMax();
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
    internal static bool InScene(AeroGridComponent a) { lock (AeroGridComponent.Live) return AeroGridComponent.Live.Contains(a); }
    internal static void Forget(AeroGridComponent aero) { aero.DesignWatch.Snap = null; System.Threading.Volatile.Write(ref aero.DesignWatch.BoxesPub, null); lock (_wingModels) _wingModels.Remove(aero); lock (_overlays) _overlays.Remove(aero); lock (_ships) _ships.Remove(aero); }
}

public partial class AeroGridComponent
{
    /// <summary>This server grid's pose for DesignProbe.ServerAero (DesignProbe.PublishPose, its own step; locked on itself).</summary>
    internal readonly DesignProbe.Pose DesignPose = new();
    /// <summary>This server grid's design watch and snapshot (DesignProbe.PublishDesign, its own step; ShipOf reads it).</summary>
    internal readonly DesignWatch DesignWatch = new();
}

/// <summary>The engine side of a snapshot: the grids jointed to it (its FxGroup, on its step).</summary>
internal sealed partial class DesignSnapshot { public AeroGridComponent[] Group; }

/// <summary>The engine side of a ship: each member's aero (index 0: the aimed grid), parallel to its snapshots.</summary>
internal sealed partial class DesignShip
{
    public AeroGridComponent Root;
    public readonly List<AeroGridComponent> A = new();
    public void Add(AeroGridComponent a, DesignSnapshot s) { A.Add(a); S.Add(s); }
}
