#pragma warning disable CS0649, CS8632, CS1591, CS1573, CS1570, CS1574, CS0419, CS1734
using System;
using Keen.Game2.Simulation.WorldObjects.CubeBlocks;
using Keen.VRage.Core.Game.Components;

namespace AeroMod;

/// <summary>
/// A 3x3x1 control surface's settings (server; injected into those blocks' server prefabs): which of the cockpit's
/// axes it helps turn about, how far it deflects, a fixed deflection (flap / trim). Its terminal is
/// AeroSurfaceBlockDetailModel; the grid's control reads it each step through the block's ControlSurface. On / off and
/// power are the game's own (PowerableBlockComponent: the block's functional toggle, its supply): off, unpowered or
/// broken, the surface trails at neutral and the control leaves it out. Saved with the block (AeroBlockSave, 2026-10-15;
/// before: lines in the world's aero settings text matched by where the block is - still read, AeroSurfaceSaved), host /
/// single player only - mod settings do not replicate.
/// </summary>
public partial class AeroSurfaceSettingsComponent : Component, IInSceneListener, IControlSurfaceSettings
{
    public const float DefaultMaxDeflection = 20f, MaxDeflectionLimit = 35f;

    public bool Pitch { get; set; } = true;
    public bool Yaw { get; set; } = true;
    public bool Roll { get; set; } = true;
    float _max = DefaultMaxDeflection, _trim;
    public float MaxDeflection { get => _max; set => _max = float.IsFinite(value) ? Math.Clamp(value, 0f, MaxDeflectionLimit) : _max; }
    public float Trim { get => _trim; set => _trim = float.IsFinite(value) ? Math.Clamp(value, -MaxDeflectionLimit, MaxDeflectionLimit) : _trim; }
    /// <summary>The settings differ from a new block's (only those are saved).</summary>
    public bool Changed => !(Pitch && Yaw && Roll && _max == DefaultMaxDeflection && _trim == 0f);

    PowerableBlockComponent _power; bool _powerLooked;
    /// <summary>Harness: count it working without power (test blocks with no battery).</summary>
    public static bool DevIgnorePower;

    public bool Working
    {
        get
        {
            if (!_powerLooked) { _powerLooked = true; _power = Entity?.TryGet<PowerableBlockComponent>(); }
            var p = _power;
            if (p == null) return true;   // (a block defined without power: always on)
            try { return p.Functional && p.Enabled && (p.Supplied || DevIgnorePower); } catch { return false; }
        }
    }

    /// <summary>Every settings component in the scene (the save and the harness walk them).</summary>
    internal static readonly List<AeroSurfaceSettingsComponent> All = new();
    void IInSceneListener.OnAddedToScene() { lock (All) All.Add(this); }
    void IInSceneListener.OnBeforeRemovedFromScene() { lock (All) All.Remove(this); }

    /// <summary>Harness (aeroget AeroSurfaceSettingsComponent.Summary): every surface's state, power included.</summary>
    public static string Summary
    {
        get
        {
            var sb = new System.Text.StringBuilder();
            lock (All)
            {
                sb.Append(All.Count).Append(" surfaces");
                foreach (var s in All)
                {
                    var p = s.Entity?.TryGet<PowerableBlockComponent>();
                    sb.Append(" | ").Append(s.Describe()).Append(p == null ? " (no power component)" : $" (enabled {p.Enabled} supplied {p.Supplied} functional {p.Functional})");
                }
            }
            return sb.ToString();
        }
    }

    internal string Describe() => $"{(Working ? "working" : "OFF")} pitch {Pitch} yaw {Yaw} roll {Roll} max {_max:F0} trim {_trim:F0}";
}

/// <summary>
/// The OLD form of the blocks' saved state, READ ONLY since 2026-10-15 (each block now saves its own: AeroBlockSave):
/// "surface x y z pitch yaw roll max trim", "gear x y z 1|0", "airbrake x y z max", "thrust x y z f" lines after the world's
/// aero settings in their text (AeroWorldSettingsComponent; AeroSettings.Apply skips lines without '='). On load they wait
/// until a block is within a metre of where it was (a save keeps grids where they were), then go onto it; one nobody
/// claims in 60 s is dropped. A block that took its value from its own saved data is never claimed. Nothing writes these
/// lines any more: the first save after loading an old world writes the block data, and no lines.
/// REMOVE the reader (the four line lists, Load, the legacy loops of ApplyPending, AeroBlockSaveForm.ParseLegacy, the
/// AeroSettings.WorldLoaded call) once one release has shipped with the block data: a world last saved before that release
/// would then load with its aero blocks at their defaults. Keep Follow (the thrusters' client copies) and Clear.
/// </summary>
internal static class AeroSurfaceSaved
{
    /// <summary>A thruster's saved fraction goes onto the thrusters this close to where it was (m: blocks sit at least a
    /// 0.25 m cell apart; both scenes' copies of the one block).</summary>
    internal const double ThrustReach = 0.1;
    /// <summary>The thrusters' saved attitude fractions ("thrust x y z f" - only those changed).</summary>
    static readonly List<(Vector3D at, float frac, double since)> _thrusts = new();
    /// <summary>The airbrakes' saved max openings ("airbrake x y z max" - only those changed).</summary>
    static readonly List<(Vector3D at, float max, double since)> _brakes = new();
    static readonly List<(Vector3D at, bool p, bool y, bool r, float max, float trim, double since)> _pending = new();
    /// <summary>The retractable gears' saved positions ("gear x y z 1|0": down / up - every gear).</summary>
    static readonly List<(Vector3D at, bool down, double since)> _gears = new();
    /// <summary>Thrusters restored from their own data (their [Init]) whose client-scene copy still needs the value.</summary>
    static readonly List<(AeroThrustSettingsComponent th, double since)> _follow = new();
    static volatile int _count;

    static void Recount() => _count = _pending.Count + _gears.Count + _brakes.Count + _thrusts.Count + _follow.Count;

    internal static void Load(string text)
    {
        int n;
        lock (_pending)
        {
            _pending.Clear(); _gears.Clear(); _brakes.Clear(); _thrusts.Clear();
            n = AeroBlockSaveForm.ParseLegacy(text, _pending, _gears, _brakes, _thrusts);
            Recount();
        }
        if (n > 0) Log.Default?.Info($"[AERO] control surface / gear settings loaded: {n} (applied as their blocks appear)");
    }

    // (_follow too: the world's components are gone with it)
    internal static void Clear() { lock (_pending) { _pending.Clear(); _gears.Clear(); _brakes.Clear(); _thrusts.Clear(); _follow.Clear(); _count = 0; } }

    /// <summary>A thruster that took a changed fraction from its own data: its client-scene copy gets it too (ApplyPending).</summary>
    internal static void Follow(AeroThrustSettingsComponent th) { lock (_pending) { _follow.Add((th, -1)); Recount(); } }

    /// <summary>Server, each step: the saved settings onto their blocks (nothing to do: one read).</summary>
    internal static void ApplyPending()
    {
        if (_count == 0) return;
        double now = System.Diagnostics.Stopwatch.GetTimestamp() / (double)System.Diagnostics.Stopwatch.Frequency;
        lock (_pending)
        {
            for (int i = _pending.Count - 1; i >= 0; i--)
            {
                var e = _pending[i];
                if (e.since < 0) { e.since = now; _pending[i] = e; }
                AeroSurfaceSettingsComponent best = null; double bd = 1.0;
                lock (AeroSurfaceSettingsComponent.All)
                    foreach (var s in AeroSurfaceSettingsComponent.All)
                    {
                        if (s.Entity == null || s.RestoredFromBlockData) continue;
                        double d = (s.Entity.Data.GetWorldTransform().Position - e.at).Length();
                        if (d < bd) { bd = d; best = s; }
                    }
                if (best == null)
                {
                    if (now - e.since > 60) { _pending.RemoveAt(i); Log.Default?.Info("[AERO] control surface setting dropped: no surface where it was"); }
                    continue;
                }
                best.Pitch = e.p; best.Yaw = e.y; best.Roll = e.r; best.MaxDeflection = e.max; best.Trim = e.trim;
                _pending.RemoveAt(i);
                Log.Default?.Info($"[AERO] control surface setting restored: {best.Describe()}");
            }
            for (int i = _gears.Count - 1; i >= 0; i--)
            {
                var e = _gears[i];
                if (e.since < 0) { e.since = now; _gears[i] = e; }
                AeroGearComponent best = null; double bd = 1.0;
                lock (AeroGearComponent.All)
                    foreach (var g in AeroGearComponent.All)
                    {
                        if (g.Entity == null || g.RestoredFromBlockData) continue;
                        double d = (g.Entity.Data.GetWorldTransform().Position - e.at).Length();
                        if (d < bd) { bd = d; best = g; }
                    }
                if (best == null)
                {
                    if (now - e.since > 60) { _gears.RemoveAt(i); Log.Default?.Info("[AERO] gear setting dropped: no gear where it was"); }
                    continue;
                }
                best.Seq.Restore(e.down);   // (down: as built; up: its vanilla wheel goes on its next step)
                _gears.RemoveAt(i);
                Log.Default?.Info($"[AERO] gear restored: {(e.down ? "down" : "up")}");
            }
            for (int i = _brakes.Count - 1; i >= 0; i--)
            {
                var e = _brakes[i];
                if (e.since < 0) { e.since = now; _brakes[i] = e; }
                AeroAirbrakeComponent best = null; double bd = 1.0;
                lock (AeroAirbrakeComponent.All)
                    foreach (var b in AeroAirbrakeComponent.All)
                    {
                        if (b.Entity == null || b.RestoredFromBlockData) continue;
                        double d = (b.Entity.Data.GetWorldTransform().Position - e.at).Length();
                        if (d < bd) { bd = d; best = b; }
                    }
                if (best == null) { if (now - e.since > 60) _brakes.RemoveAt(i); continue; }
                best.MaxOpening = e.max; _brakes.RemoveAt(i);
                Log.Default?.Info($"[AERO] airbrake restored: max opening {e.max:F0} deg");
            }
            for (int i = _thrusts.Count - 1; i >= 0; i--)
            {
                var e = _thrusts[i];
                if (e.since < 0) { e.since = now; _thrusts[i] = e; }
                int hit = 0;
                lock (AeroThrustSettingsComponent.All)
                    foreach (var th in AeroThrustSettingsComponent.All)
                        if (th.Entity != null && !th.RestoredFromBlockData && (th.Entity.Data.GetWorldTransform().Position - e.at).Length() < ThrustReach) { th.AttitudeFraction = e.frac; hit++; }
                if (hit == 0) { if (now - e.since > 60) _thrusts.RemoveAt(i); continue; }
                _thrusts.RemoveAt(i);
                Log.Default?.Info($"[AERO] thruster restored: attitude fraction {e.frac:F2}");
            }
            // the client-scene copies of thrusters restored from their own data (as the old lines gave every copy within reach)
            for (int i = _follow.Count - 1; i >= 0; i--)
            {
                var e = _follow[i];
                if (e.since < 0) { e.since = now; _follow[i] = e; }
                var src = e.th;
                if (src.Entity == null) { _follow.RemoveAt(i); continue; }
                bool srcIn = false; int hit = 0;
                lock (AeroThrustSettingsComponent.All)
                {
                    foreach (var th in AeroThrustSettingsComponent.All) if (ReferenceEquals(th, src)) { srcIn = true; break; }
                    if (srcIn && src.Server)
                    {
                        var at = src.Entity.Data.GetWorldTransform().Position;
                        foreach (var th in AeroThrustSettingsComponent.All)
                            if (!th.Server && !th.RestoredFromBlockData && th.Entity != null && (th.Entity.Data.GetWorldTransform().Position - at).Length() < ThrustReach) { th.AttitudeFraction = src.AttitudeFraction; hit++; }
                    }
                }
                // (done: given to its client copy; a client-scene source - its own builder brought the value; no client copy
                //  within the wait - a dedicated server)
                if (hit > 0 || (srcIn && !src.Server) || now - e.since > 60) _follow.RemoveAt(i);
            }
            Recount();
        }
    }
}
