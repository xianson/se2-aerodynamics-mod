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
            if (p == null) return true;
            return p.Functional && p.Enabled && (p.Supplied || DevIgnorePower);
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

    // ── the ENEMY toggle (ApEnemy's rules; the change itself on the block's server job: ApEnemyGame) ──
    /// <summary>The block's grid is the game's hostile NPC's now (the block's job reads it every second; the panel shows it).</summary>
    internal volatile bool GridIsEnemy;
    volatile int _enemyAsk;   // (0 none, 1 on, 2 off)
    /// <summary>The toggle's last word ("" none): a refusal, or what was done.</summary>
    internal volatile string EnemyNote = "";
    long _enemyLook;

    /// <summary>The panel may be changed by whoever asks now: any ship that is not an enemy; an enemy's by creative or an admin.</summary>
    bool MayEdit()
    {
        if (!GridIsEnemy) return true;
        var grid = ApEnemyGame.GridOfBlock(Entity);
        var who = ApEnemyGame.Who(grid, out _);
        if (ApEnemy.CanEdit(ApEnemyGame.IsEnemy(grid), in who)) return true;
        EnemyNote = ApEnemy.Say(ApEnemyRefusal.Locked);
        return false;
    }

    /// <summary>The panel's Enemy switch: checked now (the words at once), done by the block's next server step.</summary>
    internal void AskEnemy(bool on)
    {
        var grid = ApEnemyGame.GridOfBlock(Entity);
        if (grid == null) { EnemyNote = ApEnemy.Say(ApEnemyRefusal.NoGrid); return; }
        var who = ApEnemyGame.Who(grid, out _);
        bool enemy = ApEnemyGame.IsEnemy(grid);
        var why = on ? ApEnemy.CanTurnOn(Autopilot.EnemyRule, in who, enemy) : ApEnemy.CanTurnOff(in who, enemy);
        if (why != ApEnemyRefusal.None) { if (why != ApEnemyRefusal.AlreadyThat) EnemyNote = ApEnemy.Say(why); return; }
        _enemyAsk = on ? 1 : 2; EnemyNote = on ? "Enemy: switching..." : "Enemy off: switching...";
    }

    /// <summary>The block's server step: the grid's state read (each second), an asked switch done (checked again: the owner may
    /// have changed since), the block's saved state kept with the grid's.</summary>
    void EnemyStep(Entity grid, long now)
    {
        int ask = _enemyAsk;
        if (ask == 0 && now < _enemyLook) return;
        _enemyLook = now + System.Diagnostics.Stopwatch.Frequency;
        bool enemy = ApEnemyGame.IsEnemy(grid);
        if (ask != 0)
        {
            _enemyAsk = 0;
            var who = ApEnemyGame.Who(grid, out var requester);
            if (ask == 1)
            {
                var why = ApEnemy.CanTurnOn(Autopilot.EnemyRule, in who, enemy);
                if (why != ApEnemyRefusal.None) { if (why != ApEnemyRefusal.AlreadyThat) EnemyNote = ApEnemy.Say(why); }
                else if (ApEnemyGame.MakeEnemy(grid, out long prev))
                {
                    lock (_lock) { _panel.Enemy = true; _panel.EnemyPrevOwner = prev; }
                    enemy = true; EnemyNote = "";
                    Log.Default?.Info($"[AERO] autopilot: '{grid.DebugName}' made an enemy (hostile NPC; owner before {prev})");
                }
                else EnemyNote = ApEnemy.Say(ApEnemyRefusal.TransferFailed);
            }
            else
            {
                var why = ApEnemy.CanTurnOff(in who, enemy);
                long prev; lock (_lock) prev = _panel.EnemyPrevOwner;
                if (why != ApEnemyRefusal.None) { if (why != ApEnemyRefusal.AlreadyThat) EnemyNote = ApEnemy.Say(why); }
                else if (ApEnemyGame.Restore(grid, prev, requester, out long to))
                {
                    lock (_lock) { _panel.Enemy = false; _panel.EnemyPrevOwner = 0; }
                    enemy = false;
                    EnemyNote = to == prev ? "" : "Enemy off: the owner before is gone - the ship is yours";
                    Log.Default?.Info($"[AERO] autopilot: '{grid.DebugName}' is no enemy any more (owner {to})");
                }
                else EnemyNote = ApEnemy.Say(ApEnemyRefusal.TransferFailed);
            }
        }
        else if (!enemy) { bool was; lock (_lock) { was = _panel.Enemy; if (was) { _panel.Enemy = false; _panel.EnemyPrevOwner = 0; } } }
        // (an enemy grid not made by this block - an encounter's, the admin screen's - keeps its panel's "before" empty: switching it
        //  off gives it to the one who does)
        else { lock (_lock) _panel.Enemy = true; }
        GridIsEnemy = enemy;
    }

    /// <summary>The Enemy line on the panel: what the ship is to players, or the toggle's last word.</summary>
    public string EnemyText
    {
        get
        {
            string note = EnemyNote;
            if (!string.IsNullOrEmpty(note)) return note;
            return GridIsEnemy ? "Enemy ship (the game's hostile NPC): its turrets and Engage fight players" : "Not an enemy (its owner's ship)";
        }
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
            if (b == null) return ApBlockLink.Status(Panel.Mode, ApReason.None, false, ready, false, float.NaN, ApHeightRef.Ground, ApProt.None);
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
        var g = cb?.Grid?.Entity;
        if (g != null) a.EnemyStep(g, now);
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
