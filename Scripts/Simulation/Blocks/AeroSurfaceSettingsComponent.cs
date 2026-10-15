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
/// broken, the surface trails at neutral and the control leaves it out. Saved with the world in the aero settings
/// text (AeroSettings: by where the block is), host / single player only - mod settings do not replicate.
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

    /// <summary>One line of the save: "x y z pitch yaw roll max trim" (world position of the block, the settings).</summary>
    internal string SaveLine()
    {
        var p = Entity.Data.GetWorldTransform().Position;
        var c = System.Globalization.CultureInfo.InvariantCulture;
        return string.Join(" ", p.X.ToString("R", c), p.Y.ToString("R", c), p.Z.ToString("R", c),
            Pitch ? "1" : "0", Yaw ? "1" : "0", Roll ? "1" : "0", _max.ToString("R", c), _trim.ToString("R", c));
    }

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
/// The surfaces' settings in the save: "surface x y z pitch yaw roll max trim" lines (only those changed from a new
/// block's) after the world's aero settings in their text (AeroWorldSettingsComponent; AeroSettings.Apply skips lines
/// without '='). On load they wait until a surface is within a metre of where it was (a save keeps grids where they were),
/// then go onto it; one nobody claims in 60 s is dropped. The thrusters' attitude fractions likewise ("thrust x y z f",
/// 2026-10-15; a save without them loads as before, and an older mod skips the unknown lines).
/// </summary>
internal static class AeroSurfaceSaved
{
    const string Tag = "surface ", GearTag = "gear ", BrakeTag = "airbrake ", ThrustTag = "thrust ";
    /// <summary>A thruster's saved fraction goes onto the thrusters this close to where it was (m: blocks sit at least a
    /// 0.25 m cell apart; both scenes' copies of the one block).</summary>
    const double ThrustReach = 0.1;
    /// <summary>The thrusters' saved attitude fractions ("thrust x y z f" - only those changed).</summary>
    static readonly List<(Vector3D at, float frac, double since)> _thrusts = new();
    /// <summary>The airbrakes' saved max openings ("airbrake x y z max" - only those changed).</summary>
    static readonly List<(Vector3D at, float max, double since)> _brakes = new();
    static readonly List<(Vector3D at, bool p, bool y, bool r, float max, float trim, double since)> _pending = new();
    /// <summary>The retractable gears' saved positions ("gear x y z 1|0": down / up - every gear).</summary>
    static readonly List<(Vector3D at, bool down, double since)> _gears = new();
    static volatile int _count;

    internal static string Text()
    {
        var sb = new System.Text.StringBuilder();
        lock (AeroSurfaceSettingsComponent.All)
            foreach (var s in AeroSurfaceSettingsComponent.All)
                if (s.Changed && s.Entity != null) sb.Append('\n').Append(Tag).Append(s.SaveLine());
        var c = System.Globalization.CultureInfo.InvariantCulture;
        lock (AeroAirbrakeComponent.All)
            foreach (var b in AeroAirbrakeComponent.All)
                if (b.Changed && b.Entity != null) sb.Append('\n').Append(BrakeTag).Append(b.SaveLine());
        lock (AeroGearComponent.All)
            foreach (var g in AeroGearComponent.All)
            {
                if (g.Entity == null) continue;
                var p = g.Entity.Data.GetWorldTransform().Position;
                sb.Append('\n').Append(GearTag).Append(p.X.ToString("R", c)).Append(' ').Append(p.Y.ToString("R", c)).Append(' ')
                  .Append(p.Z.ToString("R", c)).Append(' ').Append(g.Seq.Down ? "1" : "0");
            }
        // (the client scene's copies first: where both scenes' copies changed, the server's line is last and wins on load)
        lock (AeroThrustSettingsComponent.All)
            for (int pass = 0; pass < 2; pass++)
                foreach (var th in AeroThrustSettingsComponent.All)
                    if (th.Server == (pass == 1) && th.Changed && th.Entity != null) sb.Append('\n').Append(ThrustTag).Append(th.SaveLine());
        return sb.ToString();
    }

    internal static void Load(string text)
    {
        var c = System.Globalization.CultureInfo.InvariantCulture; int n = 0;
        lock (_pending)
        {
            _pending.Clear(); _gears.Clear(); _brakes.Clear(); _thrusts.Clear();
            foreach (var raw in text.Split('\n'))
            {
                var line = raw.Trim();
                if (line.StartsWith(ThrustTag))
                {
                    var tw = line.Substring(ThrustTag.Length).Split(' ', StringSplitOptions.RemoveEmptyEntries);
                    if (tw.Length >= 4 && double.TryParse(tw[0], System.Globalization.NumberStyles.Float, c, out double tx)
                        && double.TryParse(tw[1], System.Globalization.NumberStyles.Float, c, out double ty)
                        && double.TryParse(tw[2], System.Globalization.NumberStyles.Float, c, out double tz)
                        && float.TryParse(tw[3], System.Globalization.NumberStyles.Float, c, out float tf) && float.IsFinite(tf))
                    { _thrusts.Add((new Vector3D(tx, ty, tz), Math.Clamp(tf, 0f, 1f), -1)); n++; }
                    continue;
                }
                if (line.StartsWith(BrakeTag))
                {
                    var bw = line.Substring(BrakeTag.Length).Split(' ', StringSplitOptions.RemoveEmptyEntries);
                    if (bw.Length >= 4 && double.TryParse(bw[0], System.Globalization.NumberStyles.Float, c, out double bx)
                        && double.TryParse(bw[1], System.Globalization.NumberStyles.Float, c, out double by)
                        && double.TryParse(bw[2], System.Globalization.NumberStyles.Float, c, out double bz)
                        && float.TryParse(bw[3], System.Globalization.NumberStyles.Float, c, out float bm))
                    { _brakes.Add((new Vector3D(bx, by, bz), bm, -1)); n++; }
                    continue;
                }
                if (line.StartsWith(GearTag))
                {
                    var gw = line.Substring(GearTag.Length).Split(' ', StringSplitOptions.RemoveEmptyEntries);
                    if (gw.Length >= 4 && double.TryParse(gw[0], System.Globalization.NumberStyles.Float, c, out double gx)
                        && double.TryParse(gw[1], System.Globalization.NumberStyles.Float, c, out double gy)
                        && double.TryParse(gw[2], System.Globalization.NumberStyles.Float, c, out double gz))
                    { _gears.Add((new Vector3D(gx, gy, gz), gw[3] == "1", -1)); n++; }
                    continue;
                }
                if (!line.StartsWith(Tag)) continue;
                var w = line.Substring(Tag.Length).Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (w.Length < 8 || !double.TryParse(w[0], System.Globalization.NumberStyles.Float, c, out double x)
                    || !double.TryParse(w[1], System.Globalization.NumberStyles.Float, c, out double y)
                    || !double.TryParse(w[2], System.Globalization.NumberStyles.Float, c, out double z)
                    || !float.TryParse(w[6], System.Globalization.NumberStyles.Float, c, out float max)
                    || !float.TryParse(w[7], System.Globalization.NumberStyles.Float, c, out float trim)) continue;
                _pending.Add((new Vector3D(x, y, z), w[3] == "1", w[4] == "1", w[5] == "1", max, trim, -1));
                n++;
            }
            _count = _pending.Count + _gears.Count + _brakes.Count + _thrusts.Count;
        }
        if (n > 0) Log.Default?.Info($"[AERO] control surface / gear settings loaded: {n} (applied as their blocks appear)");
    }

    internal static void Clear() { lock (_pending) { _pending.Clear(); _gears.Clear(); _brakes.Clear(); _thrusts.Clear(); _count = 0; } }

    /// <summary>Server, each step: the saved settings onto their surfaces (nothing to do: one read).</summary>
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
                        if (s.Entity == null) continue;
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
                        if (g.Entity == null) continue;
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
                        if (b.Entity == null) continue;
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
                        if (th.Entity != null && (th.Entity.Data.GetWorldTransform().Position - e.at).Length() < ThrustReach) { th.AttitudeFraction = e.frac; hit++; }
                if (hit == 0) { if (now - e.since > 60) _thrusts.RemoveAt(i); continue; }
                _thrusts.RemoveAt(i);
                Log.Default?.Info($"[AERO] thruster restored: attitude fraction {e.frac:F2}");
            }
            _count = _pending.Count + _gears.Count + _brakes.Count + _thrusts.Count;
        }
    }
}
