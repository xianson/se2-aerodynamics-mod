#pragma warning disable CS0649, CS8632, CS1591, CS1573, CS1570, CS1574, CS0419, CS1734
using System;
using System.Threading;
using Keen.Game2.Simulation.WorldObjects.Movement;
using Keen.VRage.Core;
using Keen.VRage.Physics.Data;

namespace AeroMod;

/// <summary>
/// THE AUTOPILOT ON A GRID (the game side of Autopilot/FlightDirector.cs). Every tick, in the attitude job before
/// ThrustTorque.Apply:
///  SERVER: the flight director (made on the first engage) gets what the instruments show (ApSenseNow: the grid's pose and
///   rates, the gravity it must hold, its thrusters' maxima, the dampeners, the stall and loss-of-control evaluations, the
///   attitude law's authority, the pilot's input from the client copy) and gives what a seated pilot writes: its TARGET
///   ATTITUDE (ApTarget - AttitudeTarget hands it to the aero-aware law, which flies it through the real gyros, thrusters,
///   rotors' cyclic and control surfaces: ThrustTorque), the MOVEMENT KEYS (ControlData.Movement, written in place - the game's
///   thrust law reads it next tick, as it reads a cockpit's) and the dampeners it needs (never switched by the autopilot: a hover
///   needs them on - engaging asks the pilot to, switching them off lets go). Its state is published in the grid's record
///   (ThrustTorque.ApPub) for the client copy and the HUD; why it let go is said to the seated pilot.
///  CLIENT: the client copy flies the server's target too (its own attitude law predicts the same motion), shows the readout,
///   and hands the server the pilot's movement keys and steering (the cockpit writes only this copy).
/// Nothing runs while no autopilot is engaged anywhere (Engaged: a counter) beyond a null check and a read.
/// </summary>
public partial class AeroGridComponent
{
    /// <summary>Server: this grid's flight director (null until first engaged).</summary>
    internal FlightDirector Fd;
    /// <summary>The autopilot flies this copy now, and its target attitude (world). Server: its own; client: the record's.</summary>
    internal volatile bool ApFlying;
    internal Quaternion ApTarget = Quaternion.Identity;
    /// <summary>Client: the readout of the server's autopilot for this grid ("" none) - the HUD (MachHud).</summary>
    internal volatile string ApReadoutClient = "";
    /// <summary>A pending API ask (AeroApi.RequestAutopilot): exactly these goals engaged (ApAskOff 0: every goal off; -1 none).</summary>
    internal int ApAsk = -1;
    internal const int ApAskOff = 0;
    /// <summary>The attitude the guidance wants (unclipped) - the loss-of-control evaluation's probe (StructureServer).</summary>
    internal Quaternion ApProbe; internal bool ApHasProbe;
    /// <summary>The probe's prediction (Loac.cs EvaluateApProbe): the worst need / reach on the way and holding now, for which
    /// attitude, when (Stopwatch ticks).</summary>
    internal float ApLoacProbe, ApLoacHere; internal Quaternion ApLoacAt; internal long ApLoacStamp;
    /// <summary>The stall evaluation's lift direction (local) and when it ran (StallWarning.cs EvaluateStall).</summary>
    internal Vector3 StallLiftDir; internal long StallStamp;

    /// <summary>Engaged flight directors in this process (both scenes' code paths gate on it: nothing to do at 0).</summary>
    internal static int ApEngaged;
    /// <summary>An autopilot is on this grid or asked of it (the quiet gate lets it run).</summary>
    internal bool ApActive => ApFlying || ApBlockWants || Volatile.Read(ref ApAsk) >= 0 || (Fd != null && Fd.Engaged);

    ApCommand _apCmd;
    GridTerrain _apTerrain;
    // (the stall sweep's lift and drag, published by the background evaluation - a swap of two buffers: StallWarning.cs)
    internal volatile PerfSamples ApPerf;
    internal PerfSamples ApPerfBack = new(), ApPerfFront = new();
    internal Vector3 StallFlowDir;
    bool _apMoved, _apWasEngaged;
    ApReason _apSaid;
    // client: the matched record, its recheck, the pilot's steering sum and last reticle
    ThrustTorque.Published _apRec; long _apRecheck; float _apSteer; Quaternion _apLastReticle; bool _apHadReticle;

    /// <summary>The attitude job's autopilot step (AeroSimJob.AttitudeJob, before ThrustTorque.Apply).</summary>
    internal void AutopilotStep(in WorldTransform wt)
    {
        if (IsServerScene) ServerAp(wt);
        else if (Volatile.Read(ref ApEngaged) > 0 || ApFlying) ClientAp(wt);
    }

    // the Autopilot block that flies this grid (ApArbiter), its link to the flight director, the panel's write-back
    AeroAutopilotComponent _apCtl; ApBlockLink _apLink; ApBlockLink.Out _apOut; int _apReportIn;
    // the flight pilot's game senses: its target source, the fixed guns, the route's text resolved (every few seconds), the restarts seen
    internal ApGameTargets ApTargets; ApGunScan _apGuns; ApGameObstacles _apObs; string _apRouteText; long _apRouteNext; int _apRestarts;
    /// <summary>The flight pilot of the block that flies (null: none) - the API's reads.</summary>
    internal ApPilot ApPilotNow => _apLink?.Pilot;
    /// <summary>The Autopilot block that flies this grid (null: none chosen yet) - the API's reads.</summary>
    internal AeroAutopilotComponent ApCtlNow => _apCtl;
    /// <summary>The target source (made on first use: the grid's step, or another mod's API call).</summary>
    internal ApGameTargets ApTargetsMade { get { var t = ApTargets; if (t != null) return t; Interlocked.CompareExchange(ref ApTargets, new ApGameTargets(this), null); return ApTargets; } }

    /// <summary>The limits the autopilot flies with now: the flying block's panel (the world's caps in), or the defaults (any thread:
    /// a reference read and the block's locked copy).</summary>
    internal ApLimits ApLimitsNow { get { var c = _apCtl; return c != null ? ApLimits.Of(c.Panel) : ApLimits.Defaults; } }

    void ServerAp(in WorldTransform wt)
    {
        if (TerrainDump.Go && Data.Has<TargetControlData>()) TerrainDump.Sample(this, wt);   // (the harness's terrain dump: the seated ship's step)
        int ask = Volatile.Read(ref ApAsk);
        var fd = Fd;
        // ── which block flies (the first working, switched-on one by position; none: the API's asks only) ──
        AeroAutopilotComponent[] blocks = ApBlocks;
        var ctl = PickApBlock(blocks, out bool anyWants);
        bool ctlChanged = !ReferenceEquals(ctl, _apCtl);
        bool want = ctl != null && ctl.Panel.Mode != ApMode.Off;
        if (ask < 0 && (fd == null || !fd.Engaged) && !ctlChanged && !want)
        {
            if (ApFlying || _apMoved || _apWasEngaged) StopAp(wt);
            if (!anyWants) ApBlockWants = false;
            ReportBlocks(blocks, ctl, fd, force: false);
            return;
        }
        var s = ApSenseNow(wt, fd);
        s.Lim = ctl != null ? ApLimits.Of(ctl.Panel) : ApLimits.Defaults;
        if (fd == null && (ask >= 0 || ctl != null)) { fd = Fd = new FlightDirector(); _apTerrain ??= new GridTerrain(this); fd.Terrain = new CombinedTerrain(_apTerrain, new PlanetTerrainQuery(new GamePlanetHeight())); }
        if (ctlChanged)
        {
            // (the block that flew is gone, dark or outranked: let go - a dark one's panel back to Off, as a lone block's, so it
            //  does not engage again by itself when its power returns; the new one's panel engages its own mode)
            var old = _apCtl;
            if (fd != null && fd.Engaged && old != null) { fd.Disengage(ApReason.BlockOff); Say(ApReason.BlockOff, s.Piloted, force: true); }
            if (old != null && !old.Working && old.Panel.Mode != ApMode.Off)
                old.WriteBack(new ApBlockLink.Out { ModeChanged = true, Mode = ApMode.Off, Reason = ApReason.BlockOff });
            _apCtl = ctl; _apLink = ctl != null ? new ApBlockLink() : null;
        }
        if (fd == null) return;
        // (the obstacles for the collision avoidance: a flying autopilot's, every 10th tick - ApGameObstacles)
        if (s.Lim.Avoid && fd.Engaged) { _apObs ??= new ApGameObstacles(); _apObs.Update(this, in s, ApTargets?.TargetTop); fd.Obstacles = _apObs.Set; }
        else fd.Obstacles = null;
        if (ctl != null)
        {
            var panel = ctl.Panel;
            PilotSenses(in wt, ctl, in panel, ref s);
            bool wasEngaged = fd.Engaged;
            _apLink.Before(fd, in panel, ctl.Working, in s, ref _apOut);
            if (_apOut.ModeChanged || _apOut.HeightChanged) ctl.WriteBack(in _apOut);
            if (!wasEngaged && fd.Engaged) { _apSaid = ApReason.None; if (s.Piloted) ApNotes.Say(ApText.Engaged(fd.Goals, fd.HeightRef)); }
            else if (_apOut.ModeChanged && _apOut.Reason != ApReason.None) Say(_apOut.Reason, s.Piloted, force: true);
        }
        if (ask >= 0 && Interlocked.Exchange(ref ApAsk, -1) >= 0)
        {
            if (ask == ApAskOff) { if (fd.Engaged) { fd.Disengage(ApReason.TurnedOff); Say(ApReason.TurnedOff, s.Piloted, force: true); } }
            else { if (fd.Engaged) fd.Disengage(ApReason.TurnedOff); Engage(fd, (ApGoal)ask, in s); }   // (an API ask: exactly these)
        }
        bool was = fd.Engaged;
        fd.Step(in s, ref _apCmd);
        if (ctl != null)
        {
            var panel = ctl.Panel;
            _apOut = default;
            _apLink.After(fd, in panel, ref _apOut);
            if (_apOut.ModeChanged || _apOut.HeightChanged) ctl.WriteBack(in _apOut);
        }
        if (was && !fd.Engaged || fd.Reason != ApReason.None && fd.Reason != _apSaid) Say(fd.Reason, s.Piloted);
        if (fd.Engaged != _apWasEngaged) { Interlocked.Add(ref ApEngaged, fd.Engaged ? 1 : -1); _apWasEngaged = fd.Engaged; }
        ReportBlocks(blocks, ctl, fd, force: was != fd.Engaged);
        if (!fd.Engaged) { StopAp(wt); return; }
        // what a pilot writes: the target (for ThrustTorque, this tick), the movement keys (the game's thrust law, next tick)
        ApTarget = _apCmd.Target; ApFlying = _apCmd.Flying;
        ApProbe = _apCmd.Probe; ApHasProbe = _apCmd.HasProbe;
        if (_apCmd.SetMove) { WriteMove(_apCmd.Move); _apMoved = true; }
        else if (_apMoved) { WriteMove(PilotKeys(s)); _apMoved = false; }   // (the keys handed back: the pilot's, or none)
        Publish(wt, fd);
    }

    /// <summary>The block that flies: the first ready one by block position (ApArbiter) - none ready: the first that is not (so its
    /// panel's let-go shows on it). anyWants: some block's panel has a mode on.</summary>
    static AeroAutopilotComponent PickApBlock(AeroAutopilotComponent[] blocks, out bool anyWants)
    {
        anyWants = false;
        int n = blocks.Length;
        if (n == 0) return null;
        if (n == 1) { anyWants = blocks[0].Panel.Mode != ApMode.Off; return blocks[0]; }
        Span<bool> ready = n <= 16 ? stackalloc bool[n] : new bool[n];
        Span<int> x = n <= 16 ? stackalloc int[n] : new int[n], y = n <= 16 ? stackalloc int[n] : new int[n], z = n <= 16 ? stackalloc int[n] : new int[n];
        for (int i = 0; i < n; i++)
        {
            var b = blocks[i];
            ready[i] = b.Working && b.HasCell; var c = b.Cell; x[i] = c.X; y[i] = c.Y; z[i] = c.Z;
            if (b.Panel.Mode != ApMode.Off) anyWants = true;
        }
        int k = ApArbiter.Pick(ready, x, y, z);
        if (k >= 0) return blocks[k];
        for (int i = 0; i < n; i++) ready[i] = true;
        return blocks[ApArbiter.Pick(ready, x, y, z)];
    }

    /// <summary>The flight pilot's senses before the link's step: the guns and the nose (every 3 s), the target source as the panel
    /// sets it, the route's text resolved (when it changes; every 5 s for markers that move), a level-off asked (the mode begun again).</summary>
    void PilotSenses(in WorldTransform wt, AeroAutopilotComponent ctl, in ApPanel panel, ref ApSense s)
    {
        if (!ApPanel.IsPilotMode(panel.Mode)) return;
        _apGuns ??= new ApGunScan();
        _apGuns.Update(this, wt, ctl);
        s.GunAxis = _apGuns.Axis; s.MuzzleSpeed = _apGuns.Muzzle; s.GunRange = _apGuns.Range;
        s.Nose = _apGuns.Guns > 0 ? _apGuns.Axis : _apGuns.Nose;
        var tg = ApTargetsMade;
        tg.Src = panel.TargetSrc; tg.Text = panel.TargetText ?? ""; tg.Filter = panel.TargetFilter;
        var pilot = _apLink.Pilot;
        pilot.Targets = tg;

        long now = System.Diagnostics.Stopwatch.GetTimestamp();
        if (!ReferenceEquals(panel.Route, _apRouteText) || now >= _apRouteNext)
        {
            _apRouteText = panel.Route; _apRouteNext = now + 5 * System.Diagnostics.Stopwatch.Frequency;
            var grid = Entity;
            pilot.SetRoute(ApRoute.Parse(panel.Route, name => (ApGps.Find(grid, name, out var p), p)));
        }
        int r = Volatile.Read(ref ctl.Restarts);
        if (r != _apRestarts) { _apRestarts = r; _apLink.Restart(); }
    }

    /// <summary>The blocks' status lines' inputs (four times a second, or at once when the mode changed): the one that flies its
    /// mode, reason and height; the others "standby".</summary>
    void ReportBlocks(AeroAutopilotComponent[] blocks, AeroAutopilotComponent ctl, FlightDirector fd, bool force)
    {
        if (blocks.Length == 0 || (!force && --_apReportIn > 0)) return;
        _apReportIn = 15;
        bool flying = fd != null && fd.Engaged; float h = float.NaN;
        bool hasH = fd != null && fd.HeldHeight(out h);
        for (int i = 0; i < blocks.Length; i++)
        {
            var b = blocks[i];
            if (ReferenceEquals(b, ctl) && _apLink != null && ApPanel.IsPilotMode(b.Panel.Mode))
            {
                _apLink.Pilot.View(out var v, fd?.Prot ?? ApProt.None);
                v.Refused = ApTargets?.Refused;   // (a target the filter refused: "no target - ... is neutral")
                b.ReportPilot(b.Panel.Mode, fd?.Reason ?? ApReason.None, flying, in v);
            }
            else if (ReferenceEquals(b, ctl)) b.Report(b.Panel.Mode, fd?.Reason ?? ApReason.None, flying, false, hasH, h, fd?.HeightRef ?? b.Panel.HeightRef, fd?.Prot ?? ApProt.None);

            else b.Report(b.Panel.Mode, ApReason.None, false, true, false, 0f, b.Panel.HeightRef, ApProt.None);
        }
    }

    /// <summary>Let go: the target and the keys handed back (once), the record told.</summary>
    void StopAp(in WorldTransform wt)
    {
        ApFlying = false; ApHasProbe = false;
        if (_apMoved) { WriteMove(_apRecPilotKeys()); _apMoved = false; }
        if (_apWasEngaged) { Interlocked.Decrement(ref ApEngaged); _apWasEngaged = false; }
        var p = _flamePub?.Ap;
        if (p != null) lock (p) { p.Flying = false; p.Goals = 0; p.Readout = ""; p.Stamp = System.Diagnostics.Stopwatch.GetTimestamp(); }
    }

    Vector3 _apRecPilotKeys()
    {
        var p = _flamePub?.Ap;
        if (p == null) return Vector3.Zero;
        lock (p) return p.Seated && System.Diagnostics.Stopwatch.GetTimestamp() - p.PilotStamp < PilotFresh ? p.Keys : Vector3.Zero;
    }

    static Vector3 PilotKeys(in ApSense s) => s.Piloted ? s.PilotMove : Vector3.Zero;

    /// <summary>The movement keys, in place (the cockpit's own ControlData on this server grid: the thrust law reads it next tick;
    /// a pilot's own key press from the client replaces it for a tick - the autopilot's next write carries their keys in).</summary>
    void WriteMove(Vector3 move)
    {
        ref ControlData cd = ref Data.TryGetWritePtr<ControlData>();
        if (System.Runtime.CompilerServices.Unsafe.IsNullRef(ref cd)) return;
        cd.Movement = move;
    }

    /// <summary>Why it let go, said once to the seated pilot (an unattended ship says nothing).</summary>
    void Say(ApReason why, bool piloted, bool force = false)
    {
        if (why == ApReason.None || (!force && why == _apSaid)) return;
        _apSaid = why;
        if (piloted) ApNotes.Say(ApText.Off(why));
    }

    /// <summary>Engage, the pilot told what is held or why not.</summary>
    void Engage(FlightDirector fd, ApGoal goals, in ApSense s)
    {
        if (fd.Engage(goals, in s, out var why)) { _apSaid = ApReason.None; if (s.Piloted) ApNotes.Say(ApText.Engaged(fd.Goals, fd.HeightRef)); }
        else { _apSaid = why; if (s.Piloted) ApNotes.Say(ApText.Refused(why)); }
    }

    void Publish(in WorldTransform wt, FlightDirector fd)
    {
        var p = ThrustTorque.ApRecord(this, wt);
        lock (p)
        {
            p.Flying = ApFlying; p.Target = ApTarget; p.Readout = fd.Readout; p.Goals = (int)fd.Goals; p.Prot = (int)fd.Prot; p.Reason = (int)fd.Reason;
            p.Stamp = System.Diagnostics.Stopwatch.GetTimestamp();
        }
    }

    /// <summary>The pilot's input in the record this fresh is theirs (Stopwatch ticks: a quarter second).</summary>
    static readonly long PilotFresh = System.Diagnostics.Stopwatch.Frequency / 4;

    /// <summary>What the instruments show the autopilot this tick (server; no allocation).</summary>
    ApSense ApSenseNow(in WorldTransform wt, FlightDirector fd)
    {
        var s = new ApSense { Dt = AeroPhysics.Dt };
        var data = Data;
        GridPhysics.TryGetVelocity(data, out Vector3 lin, out Vector3 ang);
        s.Orient = wt.Orientation; s.AngVel = WorldTransform.TransformDirectionInv(ang, wt); s.Vel = lin;
        bool hasMass = data.TryGet<RigidBodyMassProperties>(out var mass) && mass.InvMass > 0f;
        s.Mass = hasMass ? 1f / mass.InvMass : LastMass;
        s.Pos = wt.Position + (Vector3D)Vector3.Transform(hasMass ? mass.CenterOfMass : LastCoM, wt.Orientation);
        // (the gravity the thrust must hold with dampeners on: honest - the world's multiplier in; the game's own - one weight)
        Vector3 g = GridPhysics.GetGravityDirection(data);
        s.Gravity = HonestHold.Enabled && AeroSwitch.Enabled ? g * GridPhysics.GravityScale : g;
        s.GravityTrue = g * GridPhysics.GravityScale;   // (what the body falls with: the engine's multiplier always)
        s.Agl = GroundHeight;
        if (data.TryGet<Keen.VRage.Core.Game.Data.BoundingBoxData>(out var bbR)) s.Radius = bbR.BoundingBox.HalfExtents.Length();   // (the collision avoidance's size)
        if (data.TryGet<Keen.Game2.Simulation.WorldObjects.Shared.Movement.MaxThrustData>(out var mt)) { s.ThrustPos = mt.Regular.Positive; s.ThrustNeg = mt.Regular.Negative; }
        s.Dampeners = data.Has<DampeningData>();
        // (the translation authority per side: the thrusters' arms and maxima as the attitude law last had them)
        if (LastThrustCount > 0) SideAuthority.Of(_rcsArm, _rcsDir, _rcsCap, _rcsRev, Math.Min(LastThrustCount, _rcsArm.Length), out s.SidePos, out s.SideNeg);
        // the pilot: the client copy's input (fresh), or a reticle synced here
        var p = _flamePub?.Ap;
        if (p != null)
            lock (p)
                if (p.Seated && System.Diagnostics.Stopwatch.GetTimestamp() - p.PilotStamp < PilotFresh) { s.Piloted = true; s.PilotMove = p.Keys; s.PilotSteerDeg = p.Steer; }
        if (!s.Piloted && data.Has<TargetControlData>()) s.Piloted = true;
        // the attitude law's authority about the lean axes (its last tick: gyros + thrusters, + the rotors' cyclic in-plane)
        var rep = LastThrust;
        if (hasMass)
        {
            Vector3 up = fd?.LiftAxis ?? Vector3.UnitY, a1 = ApMath.Across(up), a2 = Vector3.Cross(up, a1);
            float Acc(Vector3 axis, float cyc)
            {
                float t = MathF.Abs(Vector3.Dot(rep.Reach, Vector3.Abs(axis))) + cyc;
                Vector3 dw = Vector3.Transform(mass.InvInertiaMajorAxisRotation * (axis * t) * mass.InvInertiaTensor, mass.InertiaMajorAxisRotation);
                return MathF.Abs(Vector3.Dot(dw, axis));
            }
            s.AttAccel = MathF.Min(Acc(a1, rep.CyclicReach), Acc(a2, rep.CyclicReach));
            s.AttAccelPlan = MathF.Min(Acc(a1, 0f), Acc(a2, 0f));
            // (the ship's reach below its centre of mass along the gravity: its belly, from its bounds)
            if (data.TryGet<Keen.VRage.Core.Game.Data.BoundingBoxData>(out var bb) && g.LengthSquared() > 1e-6f)
            {
                Vector3 downL = WorldTransform.TransformDirectionInv(Vector3.Normalize(g), wt), c = mass.CenterOfMass, lo = bb.BoundingBox.Min, hi = bb.BoundingBox.Max;
                s.Belly = MathF.Max(0f, Vector3.Dot(new Vector3(downL.X >= 0f ? hi.X : lo.X, downL.Y >= 0f ? hi.Y : lo.Y, downL.Z >= 0f ? hi.Z : lo.Z) - c, downL));
            }
        }
        if (rep.HasTarget)
        {
            s.AttErrDeg = rep.Error.Length() / ApMath.Deg;
            Vector3 r = Vector3.Max(rep.Reach + new Vector3(rep.CyclicReach), new Vector3(1f));
            s.CtrlShare = MathF.Max(MathF.Abs(rep.Need.X) / r.X, MathF.Max(MathF.Abs(rep.Need.Y) / r.Y, MathF.Abs(rep.Need.Z) / r.Z));
        }
        s.AirVel = WorldTransform.TransformDirectionInv(LastAirVel, wt); s.Density = LastDensity;
        // the stall and the predicted loss of control (the structure's background evaluation, a few times a second)
        long now = System.Diagnostics.Stopwatch.GetTimestamp(), f = System.Diagnostics.Stopwatch.Frequency;
        if (now - Volatile.Read(ref StallStamp) < f * 3 / 2)
        {
            s.StallFresh = true; s.StallState = StallState; s.StallMarginDeg = StallMarginDeg; s.StallLiftInUse = StallLiftInUse; s.StallG = StallGAvailable; s.LiftDir = StallLiftDir;
            s.StallFlow = StallFlowDir; s.Perf = ApPerf;
        }
        if (now - Volatile.Read(ref ApLoacStamp) < f * 3 / 2) { s.LoacFresh = true; s.LoacProbe = ApLoacProbe; s.LoacHere = ApLoacHere; s.LoacProbeAt = ApLoacAt; }
        float cap = StructureContactLoads.PhysicsSpeedCap;
        s.SpeedLimit = cap > 0f && float.IsFinite(cap) ? 0.97f * cap : 0f;
        return s;
    }

    /// <summary>Client: the server's autopilot for this copy (its target, its readout), and the pilot's input for it.</summary>
    void ClientAp(in WorldTransform wt)
    {
        long now = System.Diagnostics.Stopwatch.GetTimestamp(), f = System.Diagnostics.Stopwatch.Frequency;
        var rec = _apRec;
        if (rec == null || rec.Gone || now >= _apRecheck)
        {
            _apRecheck = now + f / 4;
            if (!ThrustTorque.FindAp(Entity, rec, now, out var m)) m = null;
            _apRec = rec = m;
        }
        var ap = rec?.Ap;
        if (ap == null) { ApFlying = false; ApReadoutClient = ""; return; }
        bool flying; Quaternion target; string readout;
        lock (ap) { flying = ap.Flying && now - ap.Stamp < f; target = ap.Target; readout = flying || ap.Goals != 0 ? ap.Readout : ""; }
        ApTarget = target; ApFlying = flying && target.IsValidAndRotationIsNormalized(); ApReadoutClient = readout ?? "";
        // the pilot (this copy is the cockpit's): seated with a reticle, their keys, their steering while they turn it (a leaky sum
        // of the reticle's moves while the mouse or the roll keys are in - the Orbital Mod's AttitudeHold's rule: the game moves the
        // reticle with the ship too)
        bool seated = Data.TryGet<TargetControlData>(out var tcd) && tcd.TargetOrientation.IsValidAndRotationIsNormalized();
        Vector3 keys = Vector3.Zero; bool turning = false;
        if (Data.TryGet<ControlData>(out var cd)) { keys = cd.Movement; turning = cd.Rotation.LengthSquared() > 1e-10f; }
        _apSteer *= MathF.Exp(-AeroPhysics.Dt / 0.5f);
        if (seated)
        {
            if (_apHadReticle && turning) { float moved = ApMath.Angle(_apLastReticle, tcd.TargetOrientation) / ApMath.Deg; if (float.IsFinite(moved)) _apSteer += moved; }
            _apLastReticle = tcd.TargetOrientation; _apHadReticle = true;
        }
        else _apHadReticle = false;
        lock (ap) { ap.Seated = seated; ap.Keys = keys; ap.Steer = _apSteer; ap.PilotStamp = now; }
    }

    /// <summary>A grid leaving the scene: its engaged count given back.</summary>
    internal void ForgetAutopilot()
    {
        if (_apWasEngaged) { Interlocked.Decrement(ref ApEngaged); _apWasEngaged = false; }
        ApFlying = false; _apTerrain?.Release(); _apTerrain = null; _apObs?.Release(); _apObs = null; Fd = null;
        _apCtl = null; _apLink = null;   // (back in a scene: its block's panel engages its mode again, as at a load)
    }
}

/// <summary>The autopilot's words to the seated pilot (a toast; the client says it - ApToasts.ClientTick).</summary>
internal static class ApNotes
{
    /// <summary>The line waiting to be said (null none).</summary>
    public static volatile string Pending;
    public static void Say(string line) { if (!string.IsNullOrEmpty(line)) Pending = line; }
}

public partial class AeroGridComponent
{
    /// <summary>The flight performance published by the stall evaluation (two boxes swapped: the API reads the newest whole).</summary>
    internal sealed class PerfBox { public ApPerf P; public float Speed; public long Stamp; }
    internal volatile PerfBox PerfBoxNow = new PerfBox();
    internal PerfBox PerfBoxBack = new PerfBox();
}
