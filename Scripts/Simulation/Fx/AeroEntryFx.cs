#pragma warning disable CS0649, CS8632, CS1591, CS1573, CS1570, CS1574, CS0419, CS1734
using System;
using Keen.VRage.Core;
using Keen.VRage.Core.Render;
using Keen.VRage.Core.Systems;
using Keen.VRage.DCS.Annotations;
using Keen.VRage.Core.Game.GameSystems.Observers;

namespace AeroMod;

/// <summary>
/// Flight state for the effects and the API (visual only). The server copy of a grid (where aero runs) publishes
/// its FLOW every step - travel, Mach, density, lift, forces, an entry source's strength - and keeps its entry HEAT
/// (Mach-based, lagged); the client copy's AeroEntryFxComponent finds its record and drives the grid's flight field
/// (Fx/FlightField: plasma, vapour, tip trails). Entry sources (other mods, by reflection) tell aero about grids
/// whose motion it cannot see. (The old nose-spray entry effect, AeroEntry_*, is gone: the flight field replaced it.)
/// </summary>
public static class AeroEntryFx
{
    public static bool Enabled = true;
    /// <summary>Every aero visual effect on this client (AeroSettings "Fx.Effects"): off, the flight field and the nose
    /// plasma are dropped and none start.</summary>
    public static bool AllEffects = true;
    /// <summary>Heat where the plasma starts, and where it is full. Heat is by MACH, not density (the user: it starts at
    /// Mach 2 at any height): OnsetHeat (OnsetMach-relative M)^3, so Mach 2 = onset, full at ~Mach 2.5. Density then scales
    /// the strength (DensityScale).</summary>
    public static float OnsetHeat = 0.3f, FullHeat = 0.59f, OnsetMach = 2f;   // (full at ~Mach 2.5: the game's practical top speed - the user)
    /// <summary>The strength x a density factor: 1 at sea level (1.225) down to 0 at DensityFloor kg/m3, on a log scale (a
    /// thin upper atmosphere still glows, fainter; a linear or square-root factor made a real entry invisible).</summary>
    public static float DensityFloor = 1e-6f;
    /// <summary>The plasma strength 0..1 at this Mach and density, unlagged: (heat - onset) / (full - onset), heat =
    /// OnsetHeat (Mach / OnsetMach)^3, x DensityScale. (Server's own glow is the same with the HeatLag.)</summary>
    public static float EntryStrength(float mach, float density)
    {
        float mr = mach / MathF.Max(0.1f, OnsetMach);
        float heat = OnsetHeat * mr * mr * mr;
        return Math.Clamp((heat - OnsetHeat) / (FullHeat - OnsetHeat), 0f, 1f) * DensityScale(density);
    }
    public static float DensityScale(float rho)
    {
        if (!(rho > DensityFloor)) return 0f;
        // (NaN is 0, not passed on - 2026-10-15: a NaN density, or 0/0 with DensityFloor at 1.225, came out NaN and the
        //  glow's lag kept it; otherwise as before: Clamp keeps NaN, this does not)
        float r = (MathF.Log10(rho) - MathF.Log10(DensityFloor)) / (MathF.Log10(1.225f) - MathF.Log10(DensityFloor));
        return r >= 0f ? MathF.Min(r, 1f) : 0f;
    }
    /// <summary>Seconds the hull takes to heat up / cool down (the glow lingers after slowing).</summary>
    public static float HeatLag = 2f;
    /// <summary>TEST (harness: aeroset AeroEntryFx.TestSpeed 2000): every grid as if flying forward at this speed in
    /// sea-level air - the plasma on a parked ship, to look at.</summary>
    public static float TestSpeed;
    /// <summary>TEST: the grid axis TestSpeed flies along: "-z" (forward), "+z", "-x", "+x", "-y", "+y" (a ship built
    /// along another axis flies nose first).</summary>
    public static string TestAxis = "-z";
    /// <summary>TEST (harness: aeroset AeroEntryFx.TestForces 1, with TestSpeed): aero COMPUTES each grid's forces at
    /// the test airflow - hull, wings, control surfaces - and publishes them (the API, gen aero) but does NOT apply
    /// them: a held grid measured as if flying (angle-of-attack sweeps of generated planes).</summary>
    public static bool TestForces;
    internal static bool TestForcesOn => TestForces && TestSpeed > 0f;
    /// <summary>TEST: a WORLD direction ("x,y,z") for the test airflow, the same for every grid - a plane's hinged
    /// flaps are grids of their own, and along each grid's own axis they got spanwise air (Orbital's `gen air N dir`
    /// sets it from the plane's frame). Empty: TestAxis, per grid.</summary>
    public static string TestWorld = "";
    static string _testWorldParsed; static Vector3 _testWorldDir;
    internal static Vector3 TestVelocityWorld(in WorldTransform wt)
    {
        string tw = TestWorld;
        if (!string.IsNullOrEmpty(tw))
        {
            // ("freeze": the first grid's TestAxis as it points NOW, held in the world - spin a grid through its airflow)
            if (!ReferenceEquals(tw, _testWorldParsed)) { _testWorldDir = tw == "freeze" ? WorldTransform.TransformDirection(TestDir(), wt) : ParseAxis(tw); _testWorldParsed = tw; }   // (parsed once per value)
            return _testWorldDir * TestSpeed;
        }
        return WorldTransform.TransformDirection(TestDir(), wt) * TestSpeed;
    }
    static Vector3 TestDir() => TestAxis switch
    { "+z" => Vector3.UnitZ, "-x" => -Vector3.UnitX, "+x" => Vector3.UnitX, "-y" => -Vector3.UnitY, "+y" => Vector3.UnitY, "-z" => -Vector3.UnitZ, _ => ParseAxis(TestAxis) };
    static Vector3 ParseAxis(string s)
    {
        var p = s.Split(',');
        var ci = System.Globalization.CultureInfo.InvariantCulture;
        if (p.Length == 3 && float.TryParse(p[0], System.Globalization.NumberStyles.Float, ci, out float x) && float.TryParse(p[1], System.Globalization.NumberStyles.Float, ci, out float y) && float.TryParse(p[2], System.Globalization.NumberStyles.Float, ci, out float z))
        { var v = new Vector3(x, y, z); if (v.LengthSquared() > 1e-6f) return Vector3.Normalize(v); }
        return -Vector3.UnitZ;
    }
    // ── ENTRY SOURCES: the cross-mod contract (another mod tells aero about grids whose motion it cannot see) ──
    // The Orbital Mod's reentry on rails: its grids sit still in their frame while the frame is braked through the
    // air. It registers a source BY REFLECTION (mods compile separately: only BCL / engine types cross), which aero
    // asks per grid. Contract 2:
    //   int  RegisterEntrySource(string owner, Delegate source)  -> EntrySourceContract, or 0 (wrong delegate type)
    //   bool UnregisterEntrySource(string owner)
    //   bool HasEntrySource(string owner, Delegate source)        (still registered: cheap, no assembly scan)
    //   source: Func<Entity, ValueTuple<Vector3, float>> = (the air's velocity past the grid, world m/s; how hard it
    //           burns 0..1, &lt;= 0: none - the grid's own motion counts).
    // A source with a strength OWNS that grid's entry: its air replaces the grid's motion for the flow and every
    // effect, and its strength replaces aero's own heat model. Registering again under the same owner replaces (a new
    // session, or a reloaded mod); a source that throws is dropped. Checked across both repos by the Orbital Mod's fast
    // gate (tools/check_contract.sh); a change here bumps EntrySourceContract and that check.
    public const int EntrySourceContract = 2;   // (the source's shape, unchanged since API version 2: not AeroApi.Version)
    static (string owner, Func<Keen.VRage.DCS.Components.Entity, ValueTuple<Vector3, float>> fn)[] _sources = Array.Empty<(string, Func<Keen.VRage.DCS.Components.Entity, ValueTuple<Vector3, float>>)>();
    static readonly object _sourcesLock = new();

    public static int RegisterEntrySource(string owner, Delegate source)
    {
        if (string.IsNullOrEmpty(owner) || source is not Func<Keen.VRage.DCS.Components.Entity, ValueTuple<Vector3, float>> fn) return 0;
        lock (_sourcesLock)
        {
            var l = new List<(string, Func<Keen.VRage.DCS.Components.Entity, ValueTuple<Vector3, float>>)>(_sources.Length + 1);
            foreach (var e in _sources) if (e.owner != owner) l.Add(e);
            l.Add((owner, fn));
            _sources = l.ToArray();   // (copy on write: readers take the array without a lock)
        }
        Log.Default?.Info($"[AERO] entry source registered: {owner} (contract {EntrySourceContract})");
        return EntrySourceContract;
    }

    // ── ENTRY STATE (2026-10-14, the user: "i would use an orbital api instead of some glow"): the real re-entry the
    // Orbital mod flies - per grid, the TRUE air velocity past it (world m/s: its real speed, not the rails' capped one) and
    // the air's density there (kg/m3). The hull heat model heats from these (Sutton-Graves at the true speed), so a railed
    // entry heats, weakens and scorches the hull as a real one. Asked of the Orbital mod's API (OrbitalApi.TryGetEntryState,
    // by reflection - mods compile apart: bound in OrbitalLink, the one rule for every member of Orbital's aero reads).
    /// <summary>Diagnostics: why OrbitalApi.TryGetEntryState is (not) bound (OrbitalLink writes it).</summary>
    public static string EntryStateStatus = "not looked up";

    /// <summary>A grid's real re-entry state (the Orbital mod's): the true air velocity (world) and density. False: none.</summary>
    internal static bool EntryStateAt(Keen.VRage.DCS.Components.Entity grid, out Vector3 airWorld, out float density)
        => OrbitalLink.TryGetEntryState(grid, out airWorld, out density);

    /// <summary>Diagnostics: the registered sources' owners.</summary>
    public static string EntrySources { get { var src = _sources; var n = new string[src.Length]; for (int i = 0; i < n.Length; i++) n[i] = src[i].owner; return string.Join(",", n); } }

    public static bool UnregisterEntrySource(string owner)
    {
        lock (_sourcesLock)
        {
            int n = _sources.Length;
            _sources = Array.FindAll(_sources, e => e.owner != owner);
            return _sources.Length != n;
        }
    }

    public static bool HasEntrySource(string owner, Delegate source)
    {
        foreach (var e in _sources) if (e.owner == owner && Equals(e.fn, source)) return true;
        return false;
    }

    /// <summary>The registered sources' word on a grid: the strongest one with a strength. False: none (the grid's own
    /// motion and aero's heat model count).</summary>
    internal static bool ExternalAt(Keen.VRage.DCS.Components.Entity grid, out Vector3 airWorld, out float strength)
    {
        airWorld = default; strength = 0f;
        var src = _sources;
        if (src.Length == 0 || grid == null) return false;
        for (int i = 0; i < src.Length; i++)
        {
            ValueTuple<Vector3, float> r;
            try { r = src[i].fn(grid); }
            catch (Exception e) { Log.Default?.Info($"[AERO] entry source {src[i].owner} threw, dropped: {e.GetType().Name}"); UnregisterEntrySource(src[i].owner); continue; }
            if (r.Item2 > strength) { strength = MathF.Min(1f, r.Item2); airWorld = r.Item1; }
        }
        return strength > 0f;
    }
    /// <summary>Server, each aero step: the grid's entry heat (lagged), and a railed grid's request for its table.</summary>
    internal static void Server(AeroGridComponent aero, in WorldTransform wt, Vector3 vel, float density, float dt)
    {
        if (!Enabled || !aero.IsServerScene) return;   // (the sim job runs on client copies too: they only draw)
        if (TestSpeed > 0f) { vel = TestVelocityWorld(wt); density = MathF.Max(density, 1.2f); }
        float extStrength = 0f;
        // (the sources' word Flow asked for this grid a moment ago, on this thread, this same step: not asked twice - each
        //  source is a call into another mod, per server grid per tick)
        Vector3 extAir; float es; bool ext;
        if (ReferenceEquals(_extFor, aero)) { ext = _extHas; extAir = _extAir; es = _extS; _extFor = null; }
        else ext = ExternalAt(aero.Entity, out extAir, out es);
        if (ext) { vel = extAir; extStrength = es; }
        float speed = vel.Length();
        float a = aero.LastSoundSpeed;   // (the local speed of sound, AeroGridComponent.SetAir - it was the GROUND speed over the model's Mach: wrong in any wind)
        float mr = speed / MathF.Max(1f, a) / MathF.Max(0.1f, OnsetMach);
        float target = density > 0f ? OnsetHeat * mr * mr * mr : 0f;
        float k = MathF.Min(1f, dt / MathF.Max(0.05f, HeatLag));
        aero.EntryHeat += (target - aero.EntryHeat) * k;
        if (extStrength > 0f) aero.EntryHeat = MathF.Max(aero.EntryHeat, OnsetHeat + extStrength * (FullHeat - OnsetHeat));   // (its glow, held; cools by the lag after)
        // (a grid on rails sees no air of its own, so its first table was never built: wanted now - from the table
        //  cache this is a few ms)
        if (extStrength > 0f && !aero.HasTable) aero.EntryWantsTable = true;
    }

    // ── FLOW: every moving grid's air, for other visual effects (the Orbital Mod's vapour cone / plasma spike) ──

    internal sealed class FlowPub
    {
        public Vector3D Pos, Vel;
        public Quaternion Orient;   // (the server grid's attitude: a client grid turned otherwise is not this one)
        public Vector3 BoxMin, BoxMax; public bool HasBox;   // (its local bounds: a client grid's own copy has the same - a sub-grid does not)
        public Vector3 TravelLocal, LiftLocal;
        public float Mach, Density, Speed, LiftCoef;
        internal object MemberwiseCloneFlow() => MemberwiseClone();
        /// <summary>A copy into a record the caller keeps (the flight job's own, AeroEntryFxComponent.FlowCopy): what
        /// MemberwiseCloneFlow gives, without a new record per frame (47 KB/s of a profile, 2026-10-15). Every field - a new
        /// one goes here too.</summary>
        internal FlowPub CopyFrom(FlowPub p)
        {
            Pos = p.Pos; Vel = p.Vel; Orient = p.Orient; BoxMin = p.BoxMin; BoxMax = p.BoxMax; HasBox = p.HasBox;
            TravelLocal = p.TravelLocal; LiftLocal = p.LiftLocal; Mach = p.Mach; Density = p.Density; Speed = p.Speed; LiftCoef = p.LiftCoef;
            ExtStrength = p.ExtStrength; Aero = p.Aero; ForceLocal = p.ForceLocal; TorqueLocal = p.TorqueLocal;
            Drag = p.Drag; Lift = p.Lift; Q = p.Q; FrontalArea = p.FrontalArea; Stamp = p.Stamp;
            return this;
        }
        public float ExtStrength = -1f;   // (an entry source's strength: it owns the plasma; -1 none)
        public AeroGridComponent Aero;    // (its server copy: the force table, the wings)
        public Vector3 ForceLocal, TorqueLocal;   // (the aero result, grid frame: components included)
        public float Drag, Lift, Q, FrontalArea;  // (N, N, Pa, m2)
        public long Stamp;
    }
    static readonly Dictionary<AeroGridComponent, FlowPub> _flow = new();
    /// <summary>The entry sources' answer Flow got (ExternalAt), for the grid's Server call right after on the same thread.</summary>
    [ThreadStatic] static AeroGridComponent _extFor; [ThreadStatic] static bool _extHas; [ThreadStatic] static Vector3 _extAir; [ThreadStatic] static float _extS;

    /// <summary>Server, each aero step, for every grid (not gated by the entry plasma's switch or heat): the air past it -
    /// its travel in its own frame, Mach (speed over the local speed of sound: AeroGridComponent.LastSoundSpeed, the sea-level one where
    /// the model has none), density. TestSpeed applies here too (sea-level air).</summary>
    internal static void Flow(AeroGridComponent aero, in WorldTransform wt, Vector3 vel, float density, Vector3 moving)
    {
        // (vel: the air past it - its velocity less the wind; moving: its real motion, what carries its published position
        //  forward for a client's match)
        if (!aero.IsServerScene) return;
        if (TestSpeed > 0f) { vel = TestVelocityWorld(wt); density = MathF.Max(density, 1.2f); }
        float extS = -1f;
        bool ext = ExternalAt(aero.Entity, out var extAir, out float es);
        _extFor = aero; _extHas = ext; _extAir = extAir; _extS = es;   // (for Server, next: AeroSimJobCore calls it right after)
        if (ext) { vel = extAir; extS = es; }
        // (a railed grid braking through another mod's band is in vacuum as far as the game is concerned: density 0 - it
        //  published nothing and drew no plasma. The source's strength stands for the air: published regardless)
        if (extS > 0f) density = MathF.Max(density, 0.01f);
        float speed = vel.Length();
        // (hysteresis: published from 1 m/s, withdrawn below 0.7 - a ship hovering about 1 m/s, the wind in, flickered in
        //  and out, and every return built its flight field anew)
        if (speed < (aero.FlowPublished ? 0.7f : 1f) || density <= 0f)
        {
            if (aero.FlowPublished) lock (_flow) { _flow.Remove(aero); aero.FlowPublished = false; _flowCount = _flow.Count; }
            return;
        }
        aero.RefreshFxGroup();   // (the ship it is part of: one set of tip trails per ship)
        float a = aero.LastSoundSpeed;   // (the local speed of sound, AeroGridComponent.SetAir - it was the GROUND speed over the model's Mach: wrong in any wind)
        lock (_flow)
        {
            if (!_flow.TryGetValue(aero, out var p)) { _flow[aero] = p = new FlowPub(); aero.FlowPublished = true; _flowCount = _flow.Count; }
            // (Vel is the real motion, not the air: a held test grid's or a railed grid's air speed carried its position
            //  hundreds of metres off, and a client matched its subgrid's record - the wrong hull)
            p.Pos = wt.Position; p.Orient = wt.Orientation; p.Vel = moving;
            if (aero.Entity != null && aero.Entity.Data.TryGet<Keen.VRage.Core.Game.Data.BoundingBoxData>(out var lb))
            { p.BoxMin = lb.BoundingBox.Min; p.BoxMax = lb.BoundingBox.Max; p.HasBox = true; } p.TravelLocal = WorldTransform.TransformDirectionInv(vel / speed, wt);
            p.Speed = speed; p.Mach = speed / MathF.Max(1f, a); p.Density = density; p.Stamp = System.Diagnostics.Stopwatch.GetTimestamp();
            p.ExtStrength = extS; p.Aero = aero;
            // the lift: the aero result's force (grid frame, components included) across the travel; its strength per
            // dynamic pressure x frontal area (a lift coefficient on the frontal area). Zero under TestSpeed (no real air)
            var F = aero.LastResult.Force; var tl = p.TravelLocal;
            Vector3 L = F - Vector3.Dot(F, tl) * tl;
            float qA = (float)(aero.LastResult.DynamicPressure * aero.LastResult.FrontalArea);
            p.LiftLocal = L.LengthSquared() > 1e-6f ? Vector3.Normalize(L) : Vector3.Zero;
            p.LiftCoef = qA > 1e-3f ? L.Length() / qA : 0f;
            p.ForceLocal = F; p.TorqueLocal = aero.LastResult.Torque;
            p.Drag = aero.LastResult.DragMagnitude; p.Lift = aero.LastResult.LiftMagnitude;
            p.Q = (float)aero.LastResult.DynamicPressure; p.FrontalArea = aero.LastResult.FrontalArea;
        }
    }

    /// <summary>Client (any thread): the published air of the grid at this world position (carried forward to now, within
    /// 20 m, under 0.5 s old): its travel in its own frame (the direction INTO the air), Mach, density, speed, and its
    /// lift: direction in its own frame and strength (lift / (dynamic pressure x frontal area)).
    /// PUBLIC: the Orbital Mod reads it by reflection (the Orbital Mod's docs/REFLECTION_AND_WHITELIST.md, §3.10).</summary>
    public static bool TryGetFlow(Vector3D gridPos, out Vector3 travelLocal, out float mach, out float density, out float speed, out Vector3 liftLocal, out float liftCoef)
    {
        travelLocal = liftLocal = default; mach = density = speed = liftCoef = 0f;
        long now = System.Diagnostics.Stopwatch.GetTimestamp(); double freq = System.Diagnostics.Stopwatch.Frequency;
        lock (_flow)
        {
            FlowPub best = null; double bestD = 20.0;
            foreach (var p in _flow.Values)
            {
                double age = (now - p.Stamp) / freq;
                if (age > 0.5) continue;
                double d = (p.Pos + p.Vel * age - gridPos).Length();
                if (d < bestD) { bestD = d; best = p; }
            }
            if (best == null) return false;
            travelLocal = best.TravelLocal; mach = best.Mach; density = best.Density; speed = best.Speed;
            liftLocal = best.LiftLocal; liftCoef = best.LiftCoef;
            return true;
        }
    }

    /// <summary>The wing tips of a grid from the aero mod's detected wings (grid-local metres): each wing's span end(s) far
    /// from the wings' middle (a half wing's outer end; both ends of a wing that spans the fuselage; a fin's top), at that
    /// end's trailing edge (the last tick's flow decides which chord edge trails - 2026-10-18, fix a-wing-edge; none yet:
    /// the high end of the grid axis). normals: each tip's wing lift normal. Returns the count (0: no aero / no wings yet).</summary>
    public static int WingTips(Keen.VRage.DCS.Components.Entity grid, List<Vector3> tips, List<Vector3> normals)
    {
        tips.Clear(); normals.Clear();
        AeroGridComponent aero = null;
        foreach (var c in grid.Components) if (c is AeroGridComponent a) { aero = a; break; }
        var wings = aero?.FxWings;
        if (wings == null || wings.Count == 0) return 0;
        var (cs, co) = aero.FxCellGeo;
        Vector3 Pos(Vector3I c) => new Vector3((c.X + co) * cs, (c.Y + co) * cs, (c.Z + co) * cs);
        // the wings' middle: the mean of every wing cell (on the centre line of a symmetric aircraft)
        Vector3 mid = Vector3.Zero; int n = 0;
        foreach (var w in wings) if (w.Cells != null) foreach (var c in w.Cells) { mid += Pos(c); n++; }
        if (n == 0) return 0;
        mid /= n;
        var forces = aero.FxModel?.LastWingForces;
        for (int wi = 0; wi < wings.Count; wi++)
        {
            var w = wings[wi];
            if (w.Cells == null || w.Cells.Count == 0) continue;
            var trail = w.TrailingFor(forces != null && wi < forces.Count ? forces[wi].Lead : 0f);
            float sMin = float.MaxValue, sMax = float.MinValue;
            foreach (var c in w.Cells) { float s = Vector3.Dot(Pos(c), w.SpanAxis); sMin = Math.Min(sMin, s); sMax = Math.Max(sMax, s); }
            float sMid = Vector3.Dot(mid, w.SpanAxis);
            float dMin = MathF.Abs(sMin - sMid), dMax = MathF.Abs(sMax - sMid), dFar = Math.Max(dMin, dMax);
            if (dFar < 4f * cs) continue;
            for (int end = 0; end < 2; end++)
            {
                float sEnd = end == 0 ? sMin : sMax, d = end == 0 ? dMin : dMax;
                if (d < 0.75f * dFar) continue;   // (a root)
                float sign = end == 0 ? -1f : 1f;
                // the trailing edge at that end: the cell furthest along the chord within 1.5 cells of the end
                Vector3 best = default; float bestCh = float.MinValue;
                foreach (var c in w.Cells)
                {
                    var p = Pos(c);
                    if (MathF.Abs(Vector3.Dot(p, w.SpanAxis) - sEnd) > 1.5f * cs) continue;
                    float ch = Vector3.Dot(p, trail);
                    if (ch > bestCh) { bestCh = ch; best = p; }
                }
                tips.Add(best + w.SpanAxis * (sign * 0.5f * cs) + trail * (0.5f * cs));
                normals.Add(w.Normal);
            }
        }
        return tips.Count;
    }

    /// <summary>Client: the published flow of the grid at pos (the nearest within 20 m, carried forward by its velocity,
    /// under 0.5 s old), or null. The record is the publisher's own (read, never written).</summary>
    internal static FlowPub FindFlow(Vector3D pos) => FindFlow(pos, null, null);
    internal static FlowPub FindFlow(Vector3D pos, AeroGridComponent last) => FindFlow(pos, last, null);
    /// <summary>The client grid's local bounds (set per call by the flight job; none: orientation only).</summary>
    [ThreadStatic] static Vector3 _cMin, _cMax; [ThreadStatic] static bool _cBox;
    /// <summary>into: the caller's own record, the match copied into it (null: a new copy).</summary>
    internal static FlowPub FindFlow(Vector3D pos, AeroGridComponent last, Quaternion? orient, Keen.VRage.DCS.Components.Entity client, FlowPub into = null)
    {
        Keen.VRage.Core.Game.Data.BoundingBoxData cb = default;
        _cBox = client != null && client.Data.TryGet<Keen.VRage.Core.Game.Data.BoundingBoxData>(out cb);
        if (_cBox) { var b = cb.BoundingBox; _cMin = b.Min; _cMax = b.Max; }   // (the value TryGet read: no second lookup)
        try { return FindFlow(pos, last, orient, into); } finally { _cBox = false; }
    }
    /// <summary>Another grid (a pod, a rotor - turned the same way or not): its local bounds differ from the client's.</summary>
    static bool OtherGrid(FlowPub p) => _cBox && p.HasBox && ((p.BoxMin - _cMin).Length() > 0.5f || (p.BoxMax - _cMax).Length() > 0.5f);
    /// <summary>A record turned more than 3 deg from the client grid is another grid (a pod, a rotor) - never this one.</summary>
    static bool Turned(FlowPub p, Quaternion? q, double deg = 3.0)
    {
        if (q == null) return false;
        var d = Quaternion.Normalize(Quaternion.Inverse(p.Orient) * q.Value);
        return 2.0 * Math.Acos(Math.Min(1.0, Math.Abs((double)d.W))) > deg * Math.PI / 180.0;
    }
    /// <summary>
    /// CONSISTENCY: the grid matched last time is kept while it is still published and within reach - reach grows with speed
    /// (20 m + 0.1 s of travel: at 2000 m/s a 10 ms mismatch between the server's step and the client's drawn position is
    /// 20 m, and the field froze or dropped). A sub-grid's nearer record no longer steals a ship's field. A copy, taken under
    /// the lock (the record is written by the sim thread).
    /// </summary>
    internal static FlowPub FindFlow(Vector3D pos, AeroGridComponent last, Quaternion? orient, FlowPub into = null)
    {
        long now = System.Diagnostics.Stopwatch.GetTimestamp(); double freq = System.Diagnostics.Stopwatch.Frequency;
        lock (_flow)
        {
            FlowPub Reach(FlowPub p, double extra)
            {
                double age = (now - p.Stamp) / freq;
                if (age > 0.5) return null;
                double d = (p.Pos + p.Vel * age - pos).Length();
                return d <= 20.0 + 0.1 * p.Vel.Length() + extra ? p : null;
            }
            // (the grid already matched: kept within 20 deg - the record is a step old while the ship turns; at 3 deg a turning
            //  ship lost its own record, its field was dropped and made again, and its trails re-placed five times in 8 s)
            if (last != null && _flow.TryGetValue(last, out var kept) && !OtherGrid(kept) && !Turned(kept, orient, 20.0) && Reach(kept, 20.0) != null)
                return into != null ? into.CopyFrom(kept) : (FlowPub)kept.MemberwiseCloneFlow();
            FlowPub best = null; double bestD = double.MaxValue;
            foreach (var p in _flow.Values)
            {
                if (Reach(p, 0.0) == null || OtherGrid(p) || Turned(p, orient)) continue;
                double d = (p.Pos + p.Vel * ((now - p.Stamp) / freq) - pos).Length();
                if (d < bestD) { bestD = d; best = p; }
            }
            return best == null ? null : into != null ? into.CopyFrom(best) : (FlowPub)best.MemberwiseCloneFlow();
        }
    }
    /// <summary>Any flow published (no lock: every client grid's flight job asks it every frame - they queued on the flow
    /// table's lock, 2026-10-15; the count is written under the lock wherever the table changes).</summary>
    public static bool AnyFlow => _flowCount > 0;
    static volatile int _flowCount;

    /// <summary>A grid's flow record, copied under the lock: its own (server copy) or, for a client copy, the nearest
    /// published one (FindFlow). False: not flying (under 1 m/s or no air), or not published in this process.</summary>
    internal static bool FlowOf(Keen.VRage.DCS.Components.Entity grid, out FlowPub copy)
    {
        copy = null;
        if (grid == null) return false;
        AeroGridComponent aero = null;
        foreach (var c in grid.Components) if (c is AeroGridComponent a) { aero = a; break; }
        FlowPub p = null;
        lock (_flow)
        {
            if (aero != null && aero.IsServerScene) _flow.TryGetValue(aero, out p);
        }
        if (p == null) p = FindFlow(grid.Data.GetWorldTransform().Position);
        if (p == null) return false;
        lock (_flow) copy = (FlowPub)p.MemberwiseCloneFlow();
        return true;
    }

    /// <summary>Server: a grid going away stops publishing.</summary>
    internal static void Forget(AeroGridComponent aero)
    {
        if (aero.FlowPublished) lock (_flow) { _flow.Remove(aero); aero.FlowPublished = false; _flowCount = _flow.Count; }
    }

    static readonly Keen.VRage.Library.Utils.StringId CameraTag = Keen.VRage.Library.Utils.StringId.Get("VisualEffectsObserver");

    /// <summary>Client: flight fields live (FlightField.FlightMax), the last one's status (diagnostics).</summary>
    public static int FlightActive;
    public static string FlightStatus = "";
    internal static readonly Dictionary<AeroEntryFxComponent, string> FlightLines = new();
    /// <summary>Diagnostics: every live flight field's position, camera distance and status.</summary>
    public static string FlightAll { get { lock (FlightLines) return string.Join(" || ", FlightLines.Values); } }
    static readonly Dictionary<AeroEntryFxComponent, (double d, long stamp)> _flightDist = new();
    static double _flightCut = double.MaxValue;
    static long _flightCutAt;

    /// <summary>Client: whether this grid, at distance d from the camera, is among the nearest FlightMax flying.</summary>
    internal static bool NearFlight(AeroEntryFxComponent c, double d) => NearFlight(c, d, false);
    /// <summary>held: the grid already has a field - kept to 1.25x the cut (no swapping fields as the camera moves).</summary>
    internal static bool NearFlight(AeroEntryFxComponent c, double d, bool held)
    {
        long now = System.Diagnostics.Stopwatch.GetTimestamp(), f = System.Diagnostics.Stopwatch.Frequency;
        lock (_flightDist)
        {
            _flightDist[c] = (d, now);
            if (now - _flightCutAt > f / 4)
            {
                _flightCutAt = now;
                var ds = new List<double>();
                List<AeroEntryFxComponent> stale = null;
                foreach (var kv in _flightDist)
                    if (now - kv.Value.stamp < f / 2) ds.Add(kv.Value.d);
                    else (stale ??= new()).Add(kv.Key);
                if (stale != null) foreach (var s in stale) _flightDist.Remove(s);
                int max = Math.Max(1, FlightField.FlightMax);
                if (ds.Count <= max) _flightCut = double.MaxValue;
                else { ds.Sort(); _flightCut = ds[max - 1]; }
            }
            return d <= (held ? 1.25 * _flightCut : _flightCut);
        }
    }

    internal static bool CameraAt(IObservers observers, out Vector3D at)
    {
        at = default;
        if (observers == null || !observers.TryGetFirstTransform(CameraTag, out WorldTransform cam)) return false;
        at = cam.Position; return true;
    }

}

/// <summary>A grid's flight effects, client side (injected into every grid; a server copy draws nothing and goes
/// quiet at once).</summary>
public partial class AeroEntryFxComponent : Component, IInSceneListener
{
    internal int ScorchVersion = -1, ScorchTick;
    internal List<(int step, bool dark)> ScorchStamped;
    internal object RenderParent;
    internal bool NoRender;
    internal int RetryIn;
    internal FlightField Flight;
    internal AeroGridComponent FlowOf;
    /// <summary>The flight job's copy of its grid's flow, written in place each frame (FindFlow's into; read by this job only -
    /// the flight field and the scorch read it within the frame and keep nothing of it).</summary>
    internal AeroEntryFx.FlowPub FlowCopy = new();
    /// <summary>The grid this component's state belongs to (CheckOwner).</summary>
    private Keen.VRage.DCS.Components.Entity _boundTo;

    /// <summary>WHOSE STATE (2026-10-18, a client crash after a grid split: two grids' jobs ran one FlightField at once -
    /// "Collection was modified" in FlightField.ComputeTips). A component that finds itself on another entity than the one
    /// its state was made for (a split-off grid's copy) drops that state WITHOUT destroying it - it is the other grid's - and
    /// starts its own: the flight field, the render parent, the matched flow and its copy, the scorch stamps.</summary>
    internal void CheckOwner()
    {
        var e = Entity;
        if (ReferenceEquals(_boundTo, e) && (Flight == null || ReferenceEquals(Flight.Client, e))) return;
        if (_boundTo != null || (Flight != null && !ReferenceEquals(Flight.Client, e)))
        {
            // (FlightActive untouched: the field is still counted once - by the grid that owns it; ours counts when made)
            Flight = null; RenderParent = null; FlowOf = null; FlowCopy = new(); ScorchStamped = null; ScorchVersion = -1; LostSince = 0;
        }
        _boundTo = e;
    }
    internal long LinesAt;   // (its diagnostics lines next made: FlightJob)
    internal long LostSince;   // (dropped after 1 s lost, and 60 frames: not 60 frames - half a second at 120 fps, two at 30)   // (the server grid matched last frame: kept while in reach)

    void IInSceneListener.OnAddedToScene() { }
    void IInSceneListener.OnBeforeRemovedFromScene()
    {
        if (Flight != null) { Flight.Destroy(); Flight = null; System.Threading.Interlocked.Decrement(ref AeroEntryFx.FlightActive); }
        lock (AeroEntryFx.FlightLines) AeroEntryFx.FlightLines.Remove(this);
        TearSmoke.EndAll(this);   // (its torn-edge smoke: stopped and disposed with it)
    }

    [After(typeof(RenderSubmissionBegin))]
    private class OnAeroEntryFx : JobGroup;

    /// <summary>A field whose flow is lost is kept at least this long (s) before it goes.</summary>
    public static double LostKeepSeconds = 1.5;

    /// <summary>The flight field: kept while the grid publishes a flow, is within FlightRange and among the nearest
    /// FlightMax to the camera; dropped a second after it stops.</summary>
    private static void FlightJob(AeroEntryFxComponent c, IObservers observers)
    {
        if (c.Flight == null && !AeroEntryFx.AnyFlow) return;   // (nothing flying anywhere: cheap)
        var grid = c.Entity;
        if (grid == null || !GridPhysics.Alive(c.Data)) return;
        var wt = c.Data.GetWorldTransform();
        var p = AeroEntryFx.FindFlow(wt.Position, c.FlowOf, wt.Orientation, grid, c.FlowCopy);   // (into its own copy: no record a frame)
        if (p != null) c.FlowOf = p.Aero;   // (one miss kept the remembered grid: it was cleared, and the next match was not sticky)
        bool keep = p != null && p.Aero != null && !AeroApi.Suppressed(grid, wt.Position, p.Aero);
        double d = -1;
        if (keep && AeroEntryFx.CameraAt(observers, out var cam))
        {
            d = (wt.Position - cam).Length();
            // (a ship's trail owner is always kept: with more grids than FlightMax near the camera - the Hercules has 7 - the
            //  owner was cut and the ship drew no trails at all)
            bool owner = p.Aero.FxTipOwner;
            keep = d < FlightField.FlightRange && (AeroEntryFx.NearFlight(c, d, c.Flight != null) || owner);
        }
        if (!keep)
        {
            if (c.Flight != null) FlightField.RecLostFrame();
            // (lost or culled: it FADES out - eased to 0, still written, the spot following - and goes once faded, or after
            //  4 s; it froze, then was cut at full strength after 60 frames and 1 s)
            long nowL = System.Diagnostics.Stopwatch.GetTimestamp();
            if (c.Flight != null && c.LostSince == 0) c.LostSince = nowL;
            bool faded = c.Flight != null && c.Flight.FadeOut(wt);
            // (and never before LostKeepSeconds: a slow ship flickering about the publish speed lost its field and built a new
            //  one - the shock grid, the hull, its hash - every few frames: a profile, 2026-10-14)
            long lostFor = nowL - c.LostSince;
            if (c.Flight != null && ((faded && lostFor > (long)(LostKeepSeconds * System.Diagnostics.Stopwatch.Frequency)) || lostFor > 4 * System.Diagnostics.Stopwatch.Frequency)) { c.LostSince = 0; c.Flight.Destroy(); c.Flight = null; System.Threading.Interlocked.Decrement(ref AeroEntryFx.FlightActive); lock (AeroEntryFx.FlightLines) AeroEntryFx.FlightLines.Remove(c); }
            return;
        }
        c.LostSince = 0;
        if (c.Flight == null)
        {
            if (AeroEntryFx.FlightActive >= FlightField.FlightMax + 1 && !p.Aero.FxTipOwner) return;
            if (c.RenderParent == null)
            {
                // (a client grid just streamed in or moved may not have its render component yet: try again later -
                //  never "no render" for good here, the scene check above already tells a server copy)
                if (c.RetryIn > 0) { c.RetryIn--; return; }
                c.RenderParent = EntryFxBridge.RenderParent(grid);
                if (c.RenderParent == null) { c.RetryIn = 30; return; }
            }
            c.Flight = new FlightField(grid, c.RenderParent);
            System.Threading.Interlocked.Increment(ref AeroEntryFx.FlightActive);
        }
        c.Flight.Update(p, wt);
        // (the diagnostics strings - FlightStatus, NearTipWhy, FlightLines - made four times a second per field, not every
        //  frame: each was a string a frame per flying grid with the field's whole Status in it - a profile, 2026-10-15. Read
        //  by the harness only; Status itself is built when read, FlightField.Status)
        long nowS = System.Diagnostics.Stopwatch.GetTimestamp();
        bool lines = nowS >= c.LinesAt;
        if (lines) { c.LinesAt = nowS + System.Diagnostics.Stopwatch.Frequency / 4; AeroEntryFx.FlightStatus = c.Flight.Status; }
        // (your ship's own: the trail owner's field within 150 m of the camera - the cockpit's grid is often a sub-grid)
        if (lines && d >= 0 && d < 150 && (p.Aero?.FxTipOwner ?? false))
        {
            // (client copy vs the server grid it draws for: the same grid should sit on top of itself)
            // (the server grid's pose as its flow record carries it: the server entity itself, read from here - the client's job -
            //  was the cross-thread read the game's DataGuard asserts on, 2026-10-15)
            string pair = "";
            {
                var dq = Quaternion.Normalize(Quaternion.Inverse(p.Orient) * wt.Orientation);
                double ang = 2.0 * Math.Acos(Math.Min(1.0, Math.Abs((double)dq.W))) * 180.0 / Math.PI;
                var cb = BoundingBoxData.Get(grid.Data);
                pair = $" | client-server {(wt.Position - p.Pos).Length():F1} m {ang:F1} deg, box client {cb.Min}..{cb.Max} server {(p.HasBox ? $"{p.BoxMin}..{p.BoxMax}" : "-")}";
            }
            FlightField.NearTipWhy = $"{c.Flight.TipWhyHere} (cam {d:F0} m){pair} | {c.Flight.Status}";
        }
        if (lines) lock (AeroEntryFx.FlightLines) AeroEntryFx.FlightLines[c] = $"[{wt.Position.X:F0} {wt.Position.Y:F0} {wt.Position.Z:F0} cam {d:F0} m] {c.Flight.Status}";
    }

    /// <summary>The trail tracker's overlay (FlightField.TrailDebug): its own job - one job context each.</summary>
    [OnAeroEntryFx]
    [MustHave(typeof(AeroEntryFxComponent))]
    private static void TrailDrawJob(AeroEntryFxComponent c, Keen.VRage.Core.Game.RuntimeSystems.DebugDraw.IDebugDrawProvider ddp)
    {
        if (FlightField.TrailDebug && c.Flight != null) c.Flight.DrawTrails(c.Data.GetWorldTransform(), ddp);
    }

    [OnAeroEntryFx]
    [MustHave(typeof(AeroEntryFxComponent))]
    private static void EntryJob(AeroEntryFxComponent c, IObservers observers)
    {
        if (c.NoRender) return;
        if (c.Data.Scene?.DebugName != "Client") { c.NoRender = true; return; }   // (the server copy: aero runs there, nothing is drawn)
        c.CheckOwner();
        // (the client's session-level work - settings poll and menu, Mach readout, quick launch's key, autopilot toasts, creaks
        //  and shake - runs from the render camera, Runtime/AeroClientHost.cs, grid or no grid: with no client grid the
        //  settings menu never appeared, 2026-10-19. Here only its fallback: a timestamp compare while the camera host runs)
        AeroClientHost.Frame(c.Entity, fromHost: false);
        // (the autopilot's and the stall protection's CLIENT bridge - the pilot's keys and steering to the server's record, the
        //  server's target and readout back to this copy: AeroGridComponent.ClientAp / ClientAssist. Here, in the client's frame (per grid, so not in AeroClientHost):
        //  the attitude job that called them is the server scene's only - in game 2026-10-19 they never ran, so the seated
        //  pilot's keys and steering never reached the autopilot and the readout stayed empty)
        var aeroGrid = c.Entity?.TryGet<AeroGridComponent>();
        if (aeroGrid != null && !aeroGrid.IsServerScene)
        {
            var bwt = c.Data.GetWorldTransform();
            aeroGrid.AutopilotStep(bwt);
            aeroGrid.AssistStep(bwt);
        }
        QuickLaunch.TurnTarget(c.Entity);   // (this grid's cockpit target, after a quick launch aero turned it: a reference compare)
        TearSmoke.Tick(c);   // (torn-edge smoke from the server's breaks - TearSmoke.cs, 2026-10-14; off with the effects)
        // (the hull's scorch marks, twice a second - off with the effects only: from the flight field's update they were never
        //  stamped with the field off, out of its range or culled, 2026-10-15. The server copy: the field's pairing, else
        //  found here)
        if (AeroEntryFx.AllEffects && ++c.ScorchTick >= 30)
        {
            c.ScorchTick = 0;
            if (c.FlowOf == null && AeroEntryFx.AnyFlow && GridPhysics.Alive(c.Data))
            {
                var swt = c.Data.GetWorldTransform();
                var sp = AeroEntryFx.FindFlow(swt.Position, null, swt.Orientation, c.Entity, c.FlowCopy);
                if (sp != null) c.FlowOf = sp.Aero;
            }
            HeatScorch.Sync(c, c.FlowOf);
        }
        if (!AeroEntryFx.AllEffects || !FlightField.On)
        {
            if (c.Flight != null) { c.Flight.Destroy(); c.Flight = null; System.Threading.Interlocked.Decrement(ref AeroEntryFx.FlightActive); lock (AeroEntryFx.FlightLines) AeroEntryFx.FlightLines.Remove(c); }
            return;
        }
        FlightJob(c, observers);
        if (c.Flight != null) c.Flight.TrackTips(c.Data.GetWorldTransform());   // (the trail tracker)
    }
}
