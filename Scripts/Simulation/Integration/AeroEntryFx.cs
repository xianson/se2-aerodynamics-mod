#pragma warning disable
using System;
using Keen.VRage.Core;
using Keen.VRage.Core.Render;
using Keen.VRage.Core.Systems;
using Keen.VRage.DCS.Annotations;
using Keen.VRage.Core.Game.GameSystems.Observers;

namespace AeroMod;

/// <summary>
/// Reentry plasma (visual only). A fast grid in air sprays a sheath of tiny streaking sparks off its nose that hit
/// the hull and slide along it - the ship's own shape makes the sheath - with an orange light (the Flip and Burn
/// mod's SE1 entry effect, Assets/Particles/AeroEntry_*, made by tools/particles/gen_entry.py).
///
/// The server copy of a grid (where aero runs) publishes, per grid: where it is and how it moves, the flow in the
/// grid's frame, the nose (the most upstream part of the hull, from the chunks of its force table) and its frontal
/// radius (the table's frontal area), and its heat: a thermal-lagged sqrt(rho/rho0) (V/1 km/s)^3 (the stagnation
/// heating law). The client copy finds its entry (the nearest published grid, positions carried forward by their
/// velocities - at 2 km/s a frame apart is 30 m) and drives one effect at its nose: strength (spawn rate, light)
/// from heat, size from the frontal radius, streak length from speed. The effect sits in the grid's frame: it moves
/// only when the flow turns. At most MaxActive grids at once.
/// </summary>
public static class AeroEntryFx
{
    public static readonly Guid EffectGuid = new Guid("a3e0c7d1-5b2f-4e8a-9c61-0d7e2f4a1b01");
    public static bool Enabled = true;
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
    public static float DensityScale(float rho) =>
        rho <= DensityFloor ? 0f : Math.Clamp((MathF.Log10(rho) - MathF.Log10(DensityFloor)) / (MathF.Log10(1.225f) - MathF.Log10(DensityFloor)), 0f, 1f);
    /// <summary>Seconds the hull takes to heat up / cool down (the glow lingers after slowing).</summary>
    public static float HeatLag = 2f;
    public static int MaxActive = 16;
    /// <summary>TEST (harness: aeroset AeroEntryFx.TestSpeed 2000): every grid as if flying forward at this speed in
    /// sea-level air - the plasma on a parked ship, to look at.</summary>
    public static float TestSpeed;
    /// <summary>TEST: the grid axis TestSpeed flies along: "-z" (forward), "+z", "-x", "+x", "-y", "+y" (a ship built
    /// along another axis flies nose first).</summary>
    public static string TestAxis = "-z";
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
    /// <summary>TEST: particle speed from the frontal radius (the effect's whole shape per metre of radius, speed
    /// independent - the renderer scales sizes by the effect's scale, but speeds only by the velocity multiplier).</summary>
    public static bool VelocityPerRadius;
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
    public const int EntrySourceContract = AeroApi.Version;
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
    /// <summary>TEST knobs: strength as the effect's fixed time (else the timeline runs); the size, x.</summary>
    public static bool FixTime = true;
    public static float ScaleMul = 1f;
    /// <summary>TEST: another effect's Guid in place of the entry effect (side-by-side looks); "" the entry effect.</summary>
    public static string TestEffect = "";
    public static int Active, Published;
    /// <summary>Diagnostics: client grids that looked, found an entry, had no render parent; spawns tried / failed.</summary>
    public static int ClientLooks, ClientFound, ClientNoRender, SpawnTries, SpawnFails;
    /// <summary>Diagnostics: the heaviest moving glowing grid's nose (world), frontal radius, strength.</summary>
    public static string Diag = "";
    static float _diagMass;

    internal sealed class Pub
    {
        public Vector3D Pos, Vel;
        public Vector3 TravelLocal, NoseLocal;
        public float Radius, Strength, Speed;
        public long Stamp;
        public bool Gone;   // (no longer published: a client's remembered match must search again)
    }
    static readonly Dictionary<AeroGridComponent, Pub> _pub = new();
    static volatile int _count;
    public static bool Any => _count > 0;

    /// <summary>Server, each aero step: the grid's heat, and its entry while it glows.</summary>
    internal static void Server(AeroGridComponent aero, in WorldTransform wt, Vector3 vel, float density, float dt)
    {
        if (!Enabled || !aero.IsServerScene) return;   // (the sim job runs on client copies too: they only draw)
        Vector3 moving = vel;   // (the real motion: carries the record forward for a client's match - not the air)
        if (TestSpeed > 0f) { vel = WorldTransform.TransformDirection(TestDir(), wt) * TestSpeed; density = MathF.Max(density, 1.2f); }
        float extStrength = 0f;
        if (ExternalAt(aero.Entity, out var extAir, out float es)) { vel = extAir; extStrength = es; }
        float speed = vel.Length();
        double m0 = aero.LastResult.Mach;
        float a = m0 > 0.01 && aero.LastSpeed > 1f ? (float)(aero.LastSpeed / m0) : 340f;
        float mr = speed / MathF.Max(1f, a) / MathF.Max(0.1f, OnsetMach);
        float target = density > 0f ? OnsetHeat * mr * mr * mr : 0f;
        float k = MathF.Min(1f, dt / MathF.Max(0.05f, HeatLag));
        aero.EntryHeat += (target - aero.EntryHeat) * k;
        if (extStrength > 0f) aero.EntryHeat = MathF.Max(aero.EntryHeat, OnsetHeat + extStrength * (FullHeat - OnsetHeat));   // (its glow, held; cools by the lag after)
        float strength = Math.Clamp((aero.EntryHeat - OnsetHeat) / (FullHeat - OnsetHeat), 0f, 1f) * (extStrength > 0f ? 1f : DensityScale(density));
        // (a grid on rails sees no air of its own, so its first table was never built: wanted now - from the table
        //  cache this is a few ms - and it glows once it is in)
        if (extStrength > 0f && !aero.HasTable) aero.EntryWantsTable = true;
        if (strength <= 0f || speed < 1f || !aero.HasTable)
        {
            if (aero.EntryPublished) lock (_pub) { if (_pub.Remove(aero, out var gone)) gone.Gone = true; _count = _pub.Count; aero.EntryPublished = false; }
            return;
        }
        var travel = WorldTransform.TransformDirectionInv(vel / speed, wt);
        aero.EntryShape(travel, out var nose, out float radius);
        if (!aero.IsStatic && aero.LastMass >= _diagMass)
        {
            _diagMass = aero.LastMass;
            var nw = wt.Position + (Vector3D)WorldTransform.TransformDirection(nose, wt);
            var tw = WorldTransform.TransformDirection(travel, wt);
            Diag = $"mass {aero.LastMass / 1000f:F0} t nose {nw.X:F1} {nw.Y:F1} {nw.Z:F1} travel {tw.X:F2} {tw.Y:F2} {tw.Z:F2} local nose {nose.X:F1} {nose.Y:F1} {nose.Z:F1} radius {radius:F1} heat {aero.EntryHeat:F2}";
        }
        lock (_pub)
        {
            if (!_pub.TryGetValue(aero, out var p)) { _pub[aero] = p = new Pub(); _count = _pub.Count; aero.EntryPublished = true; }
            p.Pos = wt.Position; p.Vel = moving; p.TravelLocal = travel; p.NoseLocal = nose; p.Radius = radius;
            p.Strength = strength; p.Speed = speed; p.Stamp = System.Diagnostics.Stopwatch.GetTimestamp();
            Published = _count;
        }
    }

    // ── FLOW: every moving grid's air, for other visual effects (the Orbital Mod's vapour cone / plasma spike) ──

    internal sealed class FlowPub
    {
        public Vector3D Pos, Vel;
        public Vector3 TravelLocal, LiftLocal;
        public float Mach, Density, Speed, LiftCoef;
        internal object MemberwiseCloneFlow() => MemberwiseClone();
        public float ExtStrength = -1f;   // (an entry source's strength: it owns the plasma; -1 none)
        public AeroGridComponent Aero;    // (its server copy: the force table, the wings)
        public Vector3 ForceLocal, TorqueLocal;   // (the aero result, grid frame: components included)
        public float Drag, Lift, Q, FrontalArea;  // (N, N, Pa, m2)
        public long Stamp;
    }
    static readonly Dictionary<AeroGridComponent, FlowPub> _flow = new();

    /// <summary>Server, each aero step, for every grid (not gated by the entry plasma's switch or heat): the air past it -
    /// its travel in its own frame, Mach (speed over the speed of sound the aero model last used; 340 m/s before it has
    /// one), density. TestSpeed applies here too (sea-level air).</summary>
    internal static void Flow(AeroGridComponent aero, in WorldTransform wt, Vector3 vel, float density)
    {
        if (!aero.IsServerScene) return;
        Vector3 moving = vel;   // (the grid's real motion: what carries its published position forward for a client's match)
        if (TestSpeed > 0f) { vel = WorldTransform.TransformDirection(TestDir(), wt) * TestSpeed; density = MathF.Max(density, 1.2f); }
        float extS = -1f;
        if (ExternalAt(aero.Entity, out var extAir, out float es)) { vel = extAir; extS = es; }
        float speed = vel.Length();
        if (speed < 1f || density <= 0f)
        {
            if (aero.FlowPublished) lock (_flow) { _flow.Remove(aero); aero.FlowPublished = false; }
            return;
        }
        double m0 = aero.LastResult.Mach;
        float a = m0 > 0.01 && aero.LastSpeed > 1f ? (float)(aero.LastSpeed / m0) : 340f;
        lock (_flow)
        {
            if (!_flow.TryGetValue(aero, out var p)) { _flow[aero] = p = new FlowPub(); aero.FlowPublished = true; }
            // (Vel is the real motion, not the air: a held test grid's or a railed grid's air speed carried its position
            //  hundreds of metres off, and a client matched its subgrid's record - the wrong hull)
            p.Pos = wt.Position; p.Vel = moving; p.TravelLocal = WorldTransform.TransformDirectionInv(vel / speed, wt);
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
    /// PUBLIC: the Orbital Mod reads it by reflection (D:\aero\docs\REFLECTION_AND_WHITELIST.md).</summary>
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
    /// end's trailing edge. normals: each tip's wing lift normal. Returns the count (0: no aero / no wings yet).</summary>
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
        foreach (var w in wings)
        {
            if (w.Cells == null || w.Cells.Count == 0) continue;
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
                    float ch = Vector3.Dot(p, w.ChordAxis);
                    if (ch > bestCh) { bestCh = ch; best = p; }
                }
                tips.Add(best + w.SpanAxis * (sign * 0.5f * cs) + w.ChordAxis * (0.5f * cs));
                normals.Add(w.Normal);
            }
        }
        return tips.Count;
    }

    /// <summary>Client: the published flow of the grid at pos (the nearest within 20 m, carried forward by its velocity,
    /// under 0.5 s old), or null. The record is the publisher's own (read, never written).</summary>
    internal static FlowPub FindFlow(Vector3D pos)
    {
        long now = System.Diagnostics.Stopwatch.GetTimestamp(); double freq = System.Diagnostics.Stopwatch.Frequency;
        lock (_flow)
        {
            FlowPub best = null; double bestD = 20.0;
            foreach (var p in _flow.Values)
            {
                double age = (now - p.Stamp) / freq;
                if (age > 0.5) continue;
                double d = (p.Pos + p.Vel * age - pos).Length();
                if (d < bestD) { bestD = d; best = p; }
            }
            return best;
        }
    }
    public static bool AnyFlow { get { lock (_flow) return _flow.Count > 0; } }

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
        if (aero.FlowPublished) lock (_flow) { _flow.Remove(aero); aero.FlowPublished = false; }
        if (!aero.EntryPublished) return;
        lock (_pub) { if (_pub.Remove(aero, out var gone)) gone.Gone = true; _count = _pub.Count; aero.EntryPublished = false; }
    }

    /// <summary>Client: the published entry of the grid at this place (carried forward to now), if any.</summary>
    internal static bool Find(in WorldTransform grid, ref object hint, out Vector3 travel, out Vector3 nose, out float radius, out float strength, out float speed)
    {
        travel = nose = default; radius = strength = speed = 0f;
        long now = System.Diagnostics.Stopwatch.GetTimestamp(); double freq = System.Diagnostics.Stopwatch.Frequency;
        lock (_pub)
        {
            // the entry matched last time, while it is still published, fresh and here: no search (a search per
            // client grid per frame over every entry was O(n^2) with many grids glowing)
            if (hint is Pub h && !h.Gone)
            {
                double ha = (now - h.Stamp) / freq;
                if (ha <= 0.5 && (h.Pos + h.Vel * ha - grid.Position).LengthSquared() < 20.0 * 20.0)
                {
                    travel = h.TravelLocal; nose = h.NoseLocal; radius = h.Radius; strength = h.Strength; speed = h.Speed;
                    return true;
                }
            }
            Pub best = null; double bestD = 60.0;
            foreach (var kv in _pub)
            {
                var p = kv.Value;
                double age = (now - p.Stamp) / freq;
                if (age > 0.5) continue;
                double d = (p.Pos + p.Vel * age - grid.Position).Length();
                if (d < bestD) { bestD = d; best = p; }
            }
            hint = best;
            if (best == null) return false;
            travel = best.TravelLocal; nose = best.NoseLocal; radius = best.Radius; strength = best.Strength; speed = best.Speed;
            return true;
        }
    }

    // nearest MaxActive to the camera: each glowing client grid notes its distance; four times a second the cutoff is
    // the MaxActive-th nearest (grids past it fade out and do not spawn)
    static readonly Dictionary<AeroEntryFxComponent, (double d, long stamp)> _dist = new();
    static double _cutoff = double.MaxValue;
    static long _cutoffAt;
    static readonly List<double> _ds = new();
    static readonly Keen.VRage.Library.Utils.StringId CameraTag = Keen.VRage.Library.Utils.StringId.Get("VisualEffectsObserver");

    /// <summary>Client: whether this grid, at distance d from the camera, is among the nearest MaxActive glowing.</summary>
    internal static bool Near(AeroEntryFxComponent c, double d)
    {
        long now = System.Diagnostics.Stopwatch.GetTimestamp(), f = System.Diagnostics.Stopwatch.Frequency;
        lock (_dist)
        {
            _dist[c] = (d, now);
            if (now - _cutoffAt > f / 4)
            {
                _cutoffAt = now;
                _ds.Clear();
                List<AeroEntryFxComponent> stale = null;
                foreach (var kv in _dist)
                    if (now - kv.Value.stamp < f / 2) _ds.Add(kv.Value.d);
                    else (stale ??= new()).Add(kv.Key);
                if (stale != null) foreach (var s in stale) _dist.Remove(s);
                if (_ds.Count <= MaxActive) _cutoff = double.MaxValue;
                else { _ds.Sort(); _cutoff = _ds[MaxActive - 1]; }
            }
            return d <= _cutoff;
        }
    }

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
    internal static bool NearFlight(AeroEntryFxComponent c, double d)
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
            return d <= _flightCut;
        }
    }

    internal static bool CameraAt(IObservers observers, out Vector3D at)
    {
        at = default;
        if (observers == null || !observers.TryGetFirstTransform(CameraTag, out WorldTransform cam)) return false;
        at = cam.Position; return true;
    }

    /// <summary>The emitter's frame: its +Z (the spray's axis) downwind, at the nose.</summary>
    internal static RelativeTransform At(Vector3 travel, Vector3 nose)
        => new RelativeTransform(nose, Quaternion.CreateFromTwoVectors(Vector3.UnitZ, -travel));
}

/// <summary>A grid's entry plasma, client side (injected into every grid; a server copy has no render parent and
/// goes quiet at once).</summary>
public partial class AeroEntryFxComponent : Component, IInSceneListener
{
    internal EntryFxBridge.Fx Fx;
    internal object RenderParent;
    internal bool NoRender;
    internal Vector3 ShownTravel, ShownNose;
    internal float ShownStrength = -1f, ShownScale = -1f, Fade;
    internal int RetryIn;
    internal object Match;   // (the published entry found last frame)
    internal FlightField Flight;
    internal int FlightLost;

    void IInSceneListener.OnAddedToScene() { }
    void IInSceneListener.OnBeforeRemovedFromScene()
    {
        if (Flight != null) { Flight.DropAll(); Flight = null; System.Threading.Interlocked.Decrement(ref AeroEntryFx.FlightActive); }
        lock (AeroEntryFx.FlightLines) AeroEntryFx.FlightLines.Remove(this);
        if (Fx != null) { EntryFxBridge.Stop(Fx); Fx = null; System.Threading.Interlocked.Decrement(ref AeroEntryFx.Active); }
    }

    [After(typeof(RenderSubmissionBegin))]
    private class OnAeroEntryFx : JobGroup;

    /// <summary>The flight field: kept while the grid publishes a flow, is within FlightRange and among the nearest
    /// FlightMax to the camera; dropped a second after it stops.</summary>
    private static void FlightJob(AeroEntryFxComponent c, IObservers observers)
    {
        if (c.Flight == null && !AeroEntryFx.AnyFlow) return;   // (nothing flying anywhere: cheap)
        var grid = c.Entity;
        if (grid == null || !PhysicsHack.Alive(c.Data)) return;
        var wt = c.Data.GetWorldTransform();
        var p = AeroEntryFx.FindFlow(wt.Position);
        bool keep = p != null && p.Aero != null && !AeroApi.Suppressed(grid, wt.Position);
        double d = -1;
        if (keep && AeroEntryFx.CameraAt(observers, out var cam))
        {
            d = (wt.Position - cam).Length();
            keep = d < FlightField.FlightRange && AeroEntryFx.NearFlight(c, d);
        }
        if (!keep)
        {
            if (c.Flight != null && ++c.FlightLost > 60) { c.Flight.DropAll(); c.Flight = null; System.Threading.Interlocked.Decrement(ref AeroEntryFx.FlightActive); lock (AeroEntryFx.FlightLines) AeroEntryFx.FlightLines.Remove(c); }
            return;
        }
        c.FlightLost = 0;
        if (c.Flight == null)
        {
            if (AeroEntryFx.FlightActive >= FlightField.FlightMax + 1) return;
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
        AeroEntryFx.FlightStatus = c.Flight.Status;
        lock (AeroEntryFx.FlightLines) AeroEntryFx.FlightLines[c] = $"[{wt.Position.X:F0} {wt.Position.Y:F0} {wt.Position.Z:F0} cam {d:F0} m] {c.Flight.Status}";
    }

    [OnAeroEntryFx]
    [MustHave(typeof(AeroEntryFxComponent))]
    private static void EntryJob(AeroEntryFxComponent c, IObservers observers)
    {
        if (c.NoRender) return;
        if (c.Data.Scene?.DebugName != "Client") { c.NoRender = true; return; }   // (the server copy: aero runs there, nothing is drawn)
        if (FlightField.On) { FlightJob(c, observers); return; }
        if (c.Flight != null) { c.Flight.DropAll(); c.Flight = null; System.Threading.Interlocked.Decrement(ref AeroEntryFx.FlightActive); lock (AeroEntryFx.FlightLines) AeroEntryFx.FlightLines.Remove(c); }   // (the flight field: plasma, vapour, tip trails - the nose effect below is the old look)
        if (c.Fx == null && (!AeroEntryFx.Any || !AeroEntryFx.Enabled || !EntryFxBridge.Usable)) return;   // (nothing glowing anywhere: cheap)
        var grid = c.Entity;
        if (grid == null || !PhysicsHack.Alive(c.Data)) return;
        var wt = c.Data.GetWorldTransform();
        System.Threading.Interlocked.Increment(ref AeroEntryFx.ClientLooks);
        Vector3 travel = default, nose = default; float radius = 0f, strength = 0f, speed = 0f;
        bool found = AeroEntryFx.Enabled && AeroEntryFx.Find(wt, ref c.Match, out travel, out nose, out radius, out strength, out speed);
        if (found) System.Threading.Interlocked.Increment(ref AeroEntryFx.ClientFound);
        // (only the nearest MaxActive to the camera glow: the rest fade as if cooled)
        if (found && AeroEntryFx.CameraAt(observers, out var cam) && !AeroEntryFx.Near(c, (wt.Position - cam).Length())) found = false;
        // eased in and out (no popping at the onset): ~0.5 s
        c.Fade += ((found ? 1f : 0f) - c.Fade) * 0.12f;
        if (!found && c.Fade < 0.02f)
        {
            if (c.Fx != null) { EntryFxBridge.Stop(c.Fx); c.Fx = null; c.ShownStrength = -1f; System.Threading.Interlocked.Decrement(ref AeroEntryFx.Active); }
            return;
        }
        if (!found) { travel = c.ShownTravel; nose = c.ShownNose; strength = MathF.Max(0f, c.ShownStrength); radius = c.ShownScale; speed = 0f; }
        if (c.Fx != null && !ReferenceEquals(c.Fx.Key, AeroEntryFx.TestEffect))   // (test effect switched: start again with it)
        { EntryFxBridge.Stop(c.Fx); c.Fx = null; c.ShownStrength = -1f; System.Threading.Interlocked.Decrement(ref AeroEntryFx.Active); }
        if (c.Fx == null)
        {
            if (AeroEntryFx.Active >= AeroEntryFx.MaxActive + 4 || !found) return;   // (a few spare while the farthest fade)
            if (c.RetryIn > 0) { c.RetryIn--; return; }
            if (c.RenderParent == null)
            {
                c.RenderParent = EntryFxBridge.RenderParent(grid);
                if (c.RenderParent == null) { c.RetryIn = 30; System.Threading.Interlocked.Increment(ref AeroEntryFx.ClientNoRender); return; }   // (not drawn yet: again later - the scene check tells a server copy)
            }
            System.Threading.Interlocked.Increment(ref AeroEntryFx.SpawnTries);
            c.Fx = EntryFxBridge.Spawn(grid, c.RenderParent, AeroEntryFx.At(travel, nose));
            if (c.Fx == null) { System.Threading.Interlocked.Increment(ref AeroEntryFx.SpawnFails); c.RetryIn = 60; return; }   // (not every frame)
            System.Threading.Interlocked.Increment(ref AeroEntryFx.Active);
            c.ShownTravel = travel; c.ShownNose = nose;
        }
        else if (Vector3.Dot(travel, c.ShownTravel) < 0.9986f || (nose - c.ShownNose).LengthSquared() > 0.25f)   // (3 degrees, 0.5 m)
        {
            EntryFxBridge.Move(c.Fx, AeroEntryFx.At(travel, nose));
            c.ShownTravel = travel; c.ShownNose = nose;
        }
        float s = strength * c.Fade;
        float scale = MathF.Max(0.5f, radius) * AeroEntryFx.ScaleMul;   // (the effect is authored per metre of frontal radius)
        if (MathF.Abs(s - c.ShownStrength) < 0.02f && MathF.Abs(scale - c.ShownScale) < 0.02f * scale) return;
        // hotter: orange to yellow-white; faster: longer streaks; pushed downwind (world)
        float vr = Math.Clamp((speed - 800f) / 2200f, 0f, 1f);
        var push = WorldTransform.TransformDirection(-travel, wt) * 150f;
        if (AeroEntryFx.VelocityPerRadius) push = Vector3.Zero;   // (the authored plasma flows by itself: no world push)
        EntryFxBridge.Set(c.Fx, scale, push, 1f, 0.75f + 0.25f * s, 0.6f + 0.4f * s, AeroEntryFx.VelocityPerRadius ? scale : 0.7f + 0.8f * vr, s);
        c.ShownStrength = s; c.ShownScale = scale;
    }
}
