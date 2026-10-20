#pragma warning disable CS0649, CS8632, CS1591, CS1573, CS1570, CS1574, CS0419, CS1734
using System;
using System.Collections.Generic;
using System.Threading;
using Keen.Game2.Simulation.WorldObjects.CubeBlocks;
using Keen.VRage.Core;
using Keen.VRage.DCS.Annotations;

namespace AeroMod;

/// <summary>
/// THE AUTOPILOT BLOCK (server; injected into its blocks' server prefabs - AeroBlockGuids.AutopilotServerPrefabs, 2026-10-19, the
/// user: "control it through a SINGLE BLOCK, not in-game keys"). Its panel (AeroAutopilotBlockDetailModel) is the autopilot's whole
/// control surface: the mode, the height and its reference, the hover speed, the protection's limits, what it does unattended,
/// and a status line. The block flies its OWN grid: it registers itself with that grid's aero component each tick, whose server
/// step picks the block that flies (ApArbiter: the first working, switched-on one by block position; the others stand by) and
/// keeps it and the flight director in step (ApBlockLink: a mode chosen engages, a let-go or a held height is written back here).
/// Switched off, unpowered, damaged or removed: the autopilot lets go ("the autopilot block is off ..."). The panel is saved with
/// the block (AeroBlockSave: "aero-autopilot:1 ..."), so a ship left in hover hold holds it again after a load.
/// </summary>
public partial class AeroAutopilotComponent : Component, IInSceneListener
{
    /// <summary>Every server autopilot block (the harness's dump; the server prefabs only - SceneTests).</summary>
    internal static readonly List<AeroAutopilotComponent> All = new();

    readonly object _lock = new();
    ApPanel _panel = ApPanel.Defaults;
    bool _server; bool _hasCell; Vector3I _cell;
    AeroGridComponent _aero; long _aeroLook;
    PowerableBlockComponent _power; bool _powerLooked;
    public static bool DevIgnorePower;

    /// <summary>The panel now (a copy, under the block's lock: the terminal writes it on its thread, the grid reads it on the
    /// simulation's).</summary>
    public ApPanel Panel { get { lock (_lock) return _panel; } }

    /// <summary>The block's lowest cell (its place in the arbitration order; known after its first step).</summary>
    internal bool HasCell => _hasCell;
    internal Vector3I Cell => _cell;

    void IInSceneListener.OnAddedToScene()
    {
        try { _server = !string.Equals(Data.Scene?.DebugName, "Client", StringComparison.Ordinal); } catch { _server = true; }
        lock (All) All.Add(this);
    }

    void IInSceneListener.OnBeforeRemovedFromScene()
    {
        lock (All) All.Remove(this);
        var a = _aero; _aero = null;
        a?.RemoveApBlock(this);   // (the grid's next step lets go if it flew: BlockOff)
    }

    /// <summary>Built, switched on and powered (the game's own power and on / off: PowerableBlockComponent).</summary>
    public bool Working
    {
        get
        {
            if (!_powerLooked) { _powerLooked = true; _power = Entity?.TryGet<PowerableBlockComponent>(); }
            var p = _power;
            return p == null ? ApBlockLink.BlockReady(false, false, false, false, DevIgnorePower)
                : ApBlockLink.BlockReady(true, p.Functional, p.Enabled, p.Supplied, DevIgnorePower);
        }
    }

    // ── the panel's writes (the terminal; an API or the harness) ──
    internal void SetMode(ApMode m) { if (!MayEdit()) return; lock (_lock) _panel.Mode = m; Wake(); }
    internal void ToggleMode(ApMode m) { if (!MayEdit()) return; lock (_lock) _panel.Mode = _panel.Mode == m ? ApMode.Off : m; Wake(); }
    internal void SetHeight(float h) { if (!float.IsFinite(h) || !MayEdit()) return; lock (_lock) _panel.HeightM = Math.Clamp(h, 0f, ApPanel.HeightMax); Wake(); }
    internal void SetHeightRef(ApHeightRef r) { if (!MayEdit()) return; lock (_lock) _panel.HeightRef = r; Wake(); }
    internal void SetHoverSpeed(float v) { if (float.IsFinite(v) && MayEdit()) lock (_lock) _panel.HoverSpeed = Math.Clamp(v, ApPanel.HoverSpeedMin, ApPanel.HoverSpeedMax); }
    internal void SetProtection(bool on) { if (!MayEdit()) return; lock (_lock) _panel.Protection = on; }
    internal void SetGLimit(float v) { if (float.IsFinite(v) && MayEdit()) lock (_lock) _panel.GLimit = Math.Clamp(v, ApPanel.GLimitMin, ApPanel.GLimitMax); }
    internal void SetAoaMargin(float v) { if (float.IsFinite(v) && MayEdit()) lock (_lock) _panel.AoaMarginDeg = Math.Clamp(v, ApPanel.AoaMarginMin, ApPanel.AoaMarginMax); }
    internal void SetFloor(float v) { if (float.IsFinite(v) && MayEdit()) lock (_lock) _panel.FloorM = Math.Clamp(v, ApPanel.FloorMin, ApPanel.FloorMax); }
    internal void SetLookAhead(bool on) { if (!MayEdit()) return; lock (_lock) _panel.LookAhead = on; }
    internal void SetUnattended(bool on) { if (!MayEdit()) return; lock (_lock) _panel.Unattended = on; }
    /// <summary>A whole panel (the save's restore).</summary>
    internal void SetPanel(in ApPanel p) { var c = p.Clamped(); lock (_lock) _panel = c; }

    /// <summary>An edit of the panel (the terminal's setters - the flight pilot's values), clamped, under the lock.</summary>
    internal delegate void PanelEdit(ref ApPanel p);
    internal void Edit(PanelEdit e) { if (e == null || !MayEdit()) return; lock (_lock) { e(ref _panel); _panel = _panel.Clamped(); } Wake(); }

    /// <summary>LEVEL OFF / RECOVER (a button): flight hold with every hold on, caught again from the attitude, height and speed now
    /// (the grid's step begins the mode again even if it was in it: Restarts).</summary>
    internal void LevelOff() { if (!MayEdit()) return; lock (_lock) { _panel.Mode = ApMode.FlightHold; _panel.HeadingOn = _panel.AltOn = _panel.SpeedOn = true; _panel.ReturnHome = false; } Interlocked.Increment(ref Restarts); Wake(); }
    internal int Restarts;

    /// <summary>RETURN (a button): fly back to where a mode was first engaged and loiter there (navigate); again: the route resumed.</summary>
    internal void ToggleReturn()
    {
        if (!MayEdit()) return;
        lock (_lock)
        {
            _panel.ReturnHome = !_panel.ReturnHome;
            if (_panel.ReturnHome && _panel.Mode != ApMode.Navigate && _panel.Mode != ApMode.Engage) _panel.Mode = ApMode.Navigate;
        }
        Wake();
    }

    /// <summary>The grid's step: what the panel should now show (a let-go's mode, the height held).</summary>
    internal void WriteBack(in ApBlockLink.Out o)
    {
        if (!o.ModeChanged && !o.HeightChanged && !o.HoldsChanged && !o.HomeChanged) return;
        lock (_lock)
        {
            if (o.ModeChanged) _panel.Mode = o.Mode;
            if (o.HeightChanged) _panel.HeightM = o.HeightM;
            if (o.HoldsChanged) { _panel.HeadingDeg = o.HeadingDeg; _panel.AltM = o.AltM; _panel.SpeedMs = o.SpeedMs; }
            if (o.HomeChanged) { _panel.HasHome = true; _panel.HomeX = o.Home.X; _panel.HomeY = o.Home.Y; _panel.HomeZ = o.Home.Z; }
        }
    }

    void Wake() { var a = _aero; if (a != null) a.ApBlockWants = true; }

    // ── the ENEMY toggle (ApEnemySwitch's rules and state; the game's ownership: ApEnemyOwnership) ──
    readonly ApEnemySwitch _enemy = new();
    readonly object _enemyLock = new();   // (the switch's Prev: the block's step writes it, the save reads it)
    ApEnemyOwnership _own;
    long _enemyLook;
    internal readonly List<(Entity grid, long owner)> _enemyLoaded = new();
    ApEnemyOwnership Own() { var o = _own ??= new ApEnemyOwnership(); o.Block = Entity; return o; }

    /// <summary>The block's grid is the game's hostile NPC's now (the block's job reads it every second; the panel shows it).</summary>
    internal bool GridIsEnemy => _enemy.GridIsEnemy;
    /// <summary>The panel may be changed by whoever asks now: any ship that is not an enemy; an enemy's by creative or an admin.</summary>
    bool MayEdit() { if (!_enemy.GridIsEnemy) return true; lock (_enemyLock) return _enemy.MayEdit(Own()); }
    /// <summary>The panel's Enemy switch: checked now (the words at once), done by the block's next server step.</summary>
    internal void AskEnemy(bool on) { lock (_enemyLock) _enemy.Request(on, Own(), Autopilot.EnemyRule); }
    /// <summary>The Enemy line on the panel: what the ship is to players, or the switch's last word.</summary>
    public string EnemyText => _enemy.Text();

    /// <summary>The block's server step: an asked switch done, the ship kept whole (each second), the saved state kept with it.</summary>
    void EnemyStep(long now)
    {
        if (_enemy.Ask == 0 && now < _enemyLook) return;
        _enemyLook = now + System.Diagnostics.Stopwatch.Frequency;
        bool en; long prev;
        lock (_lock) { en = _panel.Enemy; prev = _panel.EnemyPrevOwner; }
        bool wasEnemy = en;
        bool wrote;
        lock (_enemyLock)
        {
            var o = Own();
            if (_enemyLoaded.Count > 0) { foreach (var (g, ow) in _enemyLoaded) _enemy.Prev[o.KeyOf(g)] = ow; _enemyLoaded.Clear(); }
            wrote = _enemy.Step(o, Autopilot.EnemyRule, ref en, ref prev);
        }
        if (wrote)
        {
            lock (_lock) { _panel.Enemy = en; _panel.EnemyPrevOwner = prev; }
            if (en != wasEnemy) Log.Default?.Info($"[AERO] autopilot: '{Entity?.DebugName}' - its ship {(en ? $"is an enemy (the hostile NPC's; {_enemy.Flipped} grid(s))" : $"is no enemy any more ({_enemy.Restored} grid(s) given back)")}");
        }
    }

    /// <summary>The save's copy: each living grid of the ship with its owner before (the switch's Prev, or as loaded and not yet mapped).</summary>
    List<(Entity grid, long owner)> EnemyPrevForSave()
    {
        var list = new List<(Entity, long)>();
        lock (_enemyLock)
        {
            if (_enemyLoaded.Count > 0) { foreach (var x in _enemyLoaded) if (x.grid != null && GridPhysics.Alive(x.grid.Data)) list.Add(x); return list; }
            var o = _own;
            if (o == null) return list;
            foreach (var kv in _enemy.Prev) { var g = o.GridOf(kv.Key); if (g != null && GridPhysics.Alive(g.Data)) list.Add((g, kv.Value)); }
        }
        return list;
    }

    // ── the status line (the grid's step writes its inputs; the terminal builds the text when it reads it) ──
    internal sealed class StatusBox
    {
        public ApMode Mode; public ApReason Reason; public bool Flying, Standby, HasHeight; public float HeightM; public ApHeightRef Ref; public ApProt Prot;
        /// <summary>A pilot mode flying: its view (the status line built from it).</summary>
        public bool Pilot; public ApPilotView View;
        public bool Same(StatusBox o) => o != null && o.Mode == Mode && o.Reason == Reason && o.Flying == Flying && o.Standby == Standby
            && o.HasHeight == HasHeight && MathF.Abs(o.HeightM - HeightM) < 0.05f && o.Ref == Ref && o.Prot == Prot && o.Pilot == Pilot && (!Pilot || o.View.Same(in View));
    }
    volatile StatusBox _status;
    StatusBox _spare;

    /// <summary>The grid's step: the status's inputs (a new box only when they changed - no garbage per tick).</summary>
    internal void Report(ApMode mode, ApReason reason, bool flying, bool standby, bool hasHeight, float heightM, ApHeightRef href, ApProt prot)
    {
        var b = _spare ?? new StatusBox();
        b.Mode = mode; b.Reason = reason; b.Flying = flying; b.Standby = standby; b.HasHeight = hasHeight; b.HeightM = heightM; b.Ref = href; b.Prot = prot;
        b.Pilot = false;
        Publish(b);
    }

    /// <summary>The grid's step: a pilot mode's status (its view).</summary>
    internal void ReportPilot(ApMode mode, ApReason reason, bool flying, in ApPilotView v)
    {
        var b = _spare ?? new StatusBox();
        b.Mode = mode; b.Reason = reason; b.Flying = flying; b.Standby = false; b.HasHeight = false; b.HeightM = 0f; b.Ref = ApHeightRef.Ground; b.Prot = v.Prot;
        b.Pilot = flying; b.View = v;
        Publish(b);
    }

    void Publish(StatusBox b)
    {
        var now = _status;
        if (b.Same(now)) { _spare = b; return; }
        _spare = null; _status = b;
    }

    /// <summary>The panel's status line ("Hover hold - holding 25.0 m over the ground", "Off - you steered", "Standby - ...").</summary>
    public string Status
    {
        get
        {
            var b = _status;
            bool ready = Working;
            if (!Autopilot.Enabled) return "Off - not allowed in this world";
            var mode = Panel.Mode;
            // (no report yet, or the panel's mode changed since the grid's last one - a button just pressed: the terminal reads the
            //  line once, at the press, before the grid's step has run - the new mode, not the old line: "Hover hold - engaging",
            //  "Off". Shown one press behind before, 2026-10-19 in game.)
            if (b == null || b.Mode != mode && !b.Standby) return ApBlockLink.Status(mode, ApReason.None, false, ready, b?.Standby ?? false, float.NaN, Panel.HeightRef, ApProt.None);
            if (b.Pilot && ready && !b.Standby && b.Mode != ApMode.Off) return b.View.Text();
            return ApBlockLink.Status(b.Mode, b.Reason, b.Flying, ready, b.Standby, b.HasHeight ? b.HeightM : float.NaN, b.Ref, b.Prot);
        }
    }

    // ── the block's step: registered with its own grid ──
    [Before(typeof(Keen.VRage.Core.Systems.Simulation))]
    private class OnAutopilotBlock : JobGroup;

    [OnAutopilotBlock]
    [MustHave(typeof(AeroAutopilotComponent))]
    private static void BlockJob(AeroAutopilotComponent a)
    {
        if (!a._server || a.Entity == null) return;
        var cb = a.Entity.TryGet<CubeBlockComponent>();
        if (!a._hasCell && cb != null) { a._cell = cb.AABB.Min; a._hasCell = true; }
        long now = System.Diagnostics.Stopwatch.GetTimestamp();
        if (a._aero == null || now >= a._aeroLook)
        {
            // (its own grid - looked up again each second: a split or a merge moves the block to another grid)
            a._aeroLook = now + System.Diagnostics.Stopwatch.Frequency;
            var grid = cb?.Grid?.Entity;
            var aero = grid != null && GridPhysics.Alive(grid.Data) ? grid.TryGet<AeroGridComponent>() : null;
            if (!ReferenceEquals(aero, a._aero)) { a._aero?.RemoveApBlock(a); a._aero = aero; aero?.AddApBlock(a); }
        }
        // (a mode on the panel keeps its grid's attitude step awake - a parked ship's quiet path skips it otherwise)
        if (a._aero != null && a.Panel.Mode != ApMode.Off) a._aero.ApBlockWants = true;
        a.EnemyStep(now);
    }

    /// <summary>Harness (aeroget AeroAutopilotComponent.Summary): every server autopilot block's panel and status.</summary>
    public static string Summary
    {
        get
        {
            var sb = new System.Text.StringBuilder();
            lock (All)
            {
                sb.Append(All.Count).Append(" autopilot blocks");
                foreach (var a in All) { var p = a.Panel; sb.Append($" | {ApModes.ModeName(p.Mode)} h {p.HeightM:F1} {(p.HeightRef == ApHeightRef.Ground ? "AGL" : "ALT")} - {a.Status}"); }
            }
            return sb.ToString();
        }
    }

    /// <summary>Harness (aeroset AeroAutopilotComponent.DevMode N): every server autopilot block's mode (0 off, 1 hover, 2 height, 3 flight hold, 4 navigate, 5 engage).</summary>
    public static int DevMode { get => -1; set { lock (All) foreach (var a in All) a.SetMode(value >= 1 && value <= (int)ApMode.Engage ? (ApMode)value : ApMode.Off); } }
}

public partial class AeroGridComponent
{
    /// <summary>This grid's autopilot blocks (server; copy-on-write - the blocks' steps add and remove, the grid's step reads).</summary>
    internal AeroAutopilotComponent[] ApBlocks = Array.Empty<AeroAutopilotComponent>();
    readonly object _apBlocksLock = new();
    /// <summary>A block's panel has a mode on (or was just changed): the attitude step stays awake for it.</summary>
    internal volatile bool ApBlockWants;

    internal void AddApBlock(AeroAutopilotComponent b)
    {
        lock (_apBlocksLock)
        {
            if (Array.IndexOf(ApBlocks, b) >= 0) return;
            AeroAutopilotComponent[] n = new AeroAutopilotComponent[ApBlocks.Length + 1]; Array.Copy(ApBlocks, n, ApBlocks.Length); n[^1] = b; ApBlocks = n;
        }
        ApBlockWants = true;
    }

    internal void RemoveApBlock(AeroAutopilotComponent b)
    {
        lock (_apBlocksLock)
        {
            int i = Array.IndexOf(ApBlocks, b);
            if (i < 0) return;
            AeroAutopilotComponent[] n = new AeroAutopilotComponent[ApBlocks.Length - 1];
            Array.Copy(ApBlocks, 0, n, 0, i); Array.Copy(ApBlocks, i + 1, n, i, ApBlocks.Length - i - 1); ApBlocks = n;
        }
        ApBlockWants = true;   // (the next step lets go if that block flew)
    }
}
