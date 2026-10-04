#pragma warning disable
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
    public static float DensityScale(float rho) =>
        rho <= DensityFloor ? 0f : Math.Clamp((MathF.Log10(rho) - MathF.Log10(DensityFloor)) / (MathF.Log10(1.225f) - MathF.Log10(DensityFloor)), 0f, 1f);
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
    internal static Vector3 TestVelocityWorld(in WorldTransform wt) => WorldTransform.TransformDirection(TestDir(), wt) * TestSpeed;
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
        // (a grid on rails sees no air of its own, so its first table was never built: wanted now - from the table
        //  cache this is a few ms)
        if (extStrength > 0f && !aero.HasTable) aero.EntryWantsTable = true;
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
        // (a railed grid braking through another mod's band is in vacuum as far as the game is concerned: density 0 - it
        //  published nothing and drew no plasma. The source's strength stands for the air: published regardless)
        if (extS > 0f) density = MathF.Max(density, 0.01f);
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

}

/// <summary>A grid's flight effects, client side (injected into every grid; a server copy draws nothing and goes
/// quiet at once).</summary>
public partial class AeroEntryFxComponent : Component, IInSceneListener
{
    internal object RenderParent;
    internal bool NoRender;
    internal int RetryIn;
    internal FlightField Flight;
    internal int FlightLost;

    void IInSceneListener.OnAddedToScene() { }
    void IInSceneListener.OnBeforeRemovedFromScene()
    {
        if (Flight != null) { Flight.DropAll(); Flight = null; System.Threading.Interlocked.Decrement(ref AeroEntryFx.FlightActive); }
        lock (AeroEntryFx.FlightLines) AeroEntryFx.FlightLines.Remove(this);
    }

    [After(typeof(RenderSubmissionBegin))]
    private class OnAeroEntryFx : JobGroup;

    /// <summary>The flight field: kept while the grid publishes a flow, is within FlightRange and among the nearest
    /// FlightMax to the camera; dropped a second after it stops.</summary>
    private static void FlightJob(AeroEntryFxComponent c, IObservers observers)
    {
        MachHud.Tick(c.Entity);   // (Mach in the game's speed readout: every 0.1 s, whichever grid ticks first)
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
        if (!AeroSettings.SawClient) AeroSettings.SawClient = true;
        AeroSettings.Poll();   // (the settings files, every ~2 s)
        if (!AeroEntryFx.AllEffects || !FlightField.On)
        {
            if (c.Flight != null) { c.Flight.DropAll(); c.Flight = null; System.Threading.Interlocked.Decrement(ref AeroEntryFx.FlightActive); lock (AeroEntryFx.FlightLines) AeroEntryFx.FlightLines.Remove(c); }
            return;
        }
        FlightJob(c, observers);
    }
}
