using AeroMod;

// THE PLANT: one ship, 6-dof at 60 Hz, flown the way the game and the mod fly it, in the game's order each tick:
//   1. the reversible thrusters' game sides (ReversibleThrust.Demand / Step - the movement keys turn them at once)
//   2. the game's thrust law per grid axis (ThrustComponent.ComputeThrust): the gravity hold (HonestHold.GameHold, shrunk by
//      a key against it), the inertia dampening (-m v 60, cleared on an axis with a key), the keys' thrust (the side's
//      maximum left after the hold), clamped to the side; the hold taken off the gravity in place; the HONEST hold
//      (HonestHold.Thrust / Gravity) after it
//   3. the air (AeroSimJob: before the attitude job) - its force and torque, the control surfaces' share of last tick's ask
//   4. THE AUTOPILOT (the real FlightDirector, from what the instruments show - Sense) - its command takes effect as a pilot's
//      would: the attitude target now, the movement keys from the next tick's thrust law (our job runs after the game's)
//   5. ThrustTorque.Apply's aware branch, step for step (the game's share - GameShare, the rotor's reaction, the attitude law -
//      RateCommand's error into ThrustAllocator.AttitudeAccel with the reach, the cyclic's plan - RotorTrim.Plan, the solve -
//      ThrustAllocator.Solve on RotorTrim.ProjectArms, the cyclic given - RotorTrim.Give, the gyros the rest up to their reach);
//      without the autopilot, unpiloted with dampeners on: its "damp" branch (RateCommand to the attitude now)
//   6. the step: every force and torque from an actuator above, nothing else - the LEDGER checks it every tick
// What is NOT the game: the inertia dampening's high-speed curve (the low-speed law throughout), the rotor's rpm (always
// governed), the game's gyro law in the "damp" branch (a rate damper within the budget). See Ships.cs for the ships.
sealed class Thr
{
    public string Name; public Vector3 Pos, Dir; public float Cap; public bool Rev;
    public Thr(string name, Vector3 pos, Vector3 dir, float cap, bool rev = false) { Name = name; Pos = pos; Dir = dir; Cap = cap; Rev = rev; }
}

/// <summary>The air on a ship: force and torque (local, about the centre of mass) for its motion through the air (local m/s),
/// rate (local rad/s) and density.</summary>
interface IAir
{
    void Forces(Vector3 airLocal, Vector3 w, float rho, out Vector3 force, out Vector3 torque);
    /// <summary>The control surfaces' reach either way per local axis at this dynamic pressure (N m; zero: none).</summary>
    Vector3 SurfaceReach(float q);
    /// <summary>A real lift polar (the stall evaluation applies).</summary>
    bool Lifts { get; }
}

/// <summary>A blunt body: drag per local axis (Cd A, m2) and rotation damping (N m s / rad) - helicopters, VTOLs.</summary>
sealed class BoxAir : IAir
{
    public Vector3 CdA = new(30f, 60f, 15f); public Vector3 Damping = new(2e4f, 2e4f, 2e4f);
    public bool Lifts => false;
    public Vector3 SurfaceReach(float q) => Vector3.Zero;
    public void Forces(Vector3 a, Vector3 w, float rho, out Vector3 f, out Vector3 t)
    {
        f = -0.5f * rho * new Vector3(CdA.X * MathF.Abs(a.X) * a.X, CdA.Y * MathF.Abs(a.Y) * a.Y, CdA.Z * MathF.Abs(a.Z) * a.Z);
        t = -Damping * w;
    }
}

sealed class SimShip
{
    public string Name = "ship"; public float Mass = 10000f; public Vector3 I = new(1e5f);
    public readonly List<Thr> T = new();
    /// <summary>The main rotor (index into T; -1 none), its radius, hub height over the centre of mass along its axis, cyclic tilt.</summary>
    public int Rotor = -1; public float RotorR = 7.5f, HubH = 4.5f, MaxTiltDeg = 10f, HubMoment = 0.15f;
    /// <summary>The gyros' reach per local axis (N m): the game's target-law budget, after Gyros.TorqueScale.</summary>
    public Vector3 Gyro;
    public IAir Air = new BoxAir();
    public float PlanetG = 9.81f, Gm = 2f; public bool Honest = true;
    /// <summary>Height of the centre of mass over the ship's lowest point (m): it stands on the ground there.</summary>
    public float Bottom = 2f;
    /// <summary>The ship's radius (m; 0: from its thrusters' reach) - ApSense.Radius.</summary>
    public float Radius;
    /// <summary>The nose (local), the fixed guns' axis (local; zero none), their muzzle speed (m/s) and range (m).</summary>
    public Vector3 Nose = -Vector3.UnitZ, GunAxis; public float Muzzle, GunRange;
    public Thr Add(string name, Vector3 pos, Vector3 dir, float cap, bool rev = false) { var t = new Thr(name, pos, dir, cap, rev); T.Add(t); return t; }
    /// <summary>(2026-10-19) Gimballed thrusters (ThrustVector): the thruster's index, its cone (deg) and rate (deg/s).</summary>
    public readonly List<(int Thr, float MaxDeg, float RateDeg)> Gimbals = new();
    public void Gimbal(int thr, float maxDeg, float rateDeg = ThrustVector.DefaultRateDeg) => Gimbals.Add((thr, maxDeg, rateDeg));
}

/// <summary>A violation the ledger found (every tick: torques and forces only from the actuators, within their limits).</summary>
sealed class Ledger
{
    public int Ticks, Violations; public string First;
    public double MaxGyroShare, MaxCyclicShare, MaxThrustShare;
    public void Fail(string what, float t) { Violations++; First ??= $"t {t:F2}: {what}"; }
}

sealed class Plant
{
    public readonly SimShip S;
    public readonly FlightDirector Fd = new();
    public ApCommand Cmd;
    // the state (world: Y up, the ground at Y = 0)
    public Quaternion Q = Quaternion.Identity; public Vector3 W, V; public Vector3D P = new(0, 200, 0);
    public float Time;
    /// <summary>The movement keys in effect (ControlData.Movement: the autopilot's last write, else the pilot's) and the dampeners.</summary>
    public Vector3 Move; public bool Damp = true;
    /// <summary>A seated pilot: their keys and their steering (deg, the leaky sum the client publishes).</summary>
    public bool Piloted; public Vector3 PilotKeys; public float PilotSteerDeg;
    /// <summary>The wind (world m/s) - set by the scenario each tick.</summary>
    public Vector3 Wind;
    /// <summary>The air density and the world's speed limit.</summary>
    public float Rho = 1.0f, SpeedLimit;
    // disturbances
    public float NoiseVel, NoisePos, NoiseDeg, LagSeconds; public Random Rng = new(1);
    public float StallEvalHz = 4f;
    /// <summary>The ship's fuel and integrity as the instruments show them (ApSense.HasShipStatus; a scenario sets them).</summary>
    public bool ShipStatus; public float Fuel = float.NaN, Integrity = 1f;
    /// <summary>ThrustTorque's aware law plans its braking with the rotors' cyclic reach too while the autopilot flies (CyclicPlanShare of it).</summary>
    public bool PlanWithCyclic = true;
    public readonly Ledger Book = new();
    // the reports a tick leaves (the traces and metrics)
    public Vector3 LastGameF, LastAeroT, LastGyroT, LastCyclicT, LastThrTau, LastReaction, LastTilt; public float LastCycReach, LastPrio;
    public string Mode = "-";
    public bool Crashed, OnGround;
    public Vector3 LastAeroF;
    /// <summary>The load factor: the air force across the flow over the real weight.</summary>
    public float LoadFactor { get { var a = AirLocal(); float s = a.Length(); if (s < 1f) return 0f; var d = a / s; return (LastAeroF - d * Vector3.Dot(LastAeroF, d)).Length() / (S.Mass * S.PlanetG * S.Gm); } }
    public float LastShare => _lastShare;
    public float TailSideForce;   // (the anti-torque tail's X force this tick, N: the translating tendency's)

    int _n; Vector3[] _arms, _dirs, _proj; float[] _caps, _f, _f0, _prev, _y, _side, _capDir = new float[6]; bool[] _rev; float _armSq; bool _warm;
    ThrustVector.Nozzle[] _gim = Array.Empty<ThrustVector.Nozzle>(); Vector3[] _gimPrev = Array.Empty<Vector3>();
    /// <summary>The gimbals now (their deflections) and the torque they gave last tick (local N m).</summary>
    public ThrustVector.Nozzle[] GimbalState => _gim; public Vector3 LastTvcT;
    /// <summary>The gimbals' reach at their thrusters' last forces (the attitude law's planning, the instruments' AttAccel).</summary>
    Vector3 GimbalReach() { if (_gim.Length == 0) return Vector3.Zero; for (int j = 0; j < _gim.Length; j++) _gim[j].Thrust = MathF.Max(0f, _f[S.Gimbals[j].Thr]); return ThrustVector.Reach(_gim, _gim.Length); }
    ReversibleThrust.State[] _st;
    Vector3 _lastLawW, _lastDemand, _lagTau;
    float _evalClock;
    // the evaluation's outputs (the stall and loss-of-control, a few times a second, as the structure's background job)
    bool _stallFresh, _loacFresh; int _stallState; float _stallMargin, _stallInUse, _stallG, _loacProbe, _loacHere; Vector3 _liftDir, _stallFlow; Quaternion _loacAt;
    PerfSamples _perf = new(), _perfBack = new();
    public float LastLoacProbe => _loacProbe;
    public float LastStallMargin => _stallMargin;

    public Plant(SimShip s) { S = s; Rebuild(); Fd.Terrain = _terrain; }
    SimTerrain _terrain = SimTerrain.Flat();
    /// <summary>The ground (the plant stands on it; the autopilot sees it through its ITerrainQuery).</summary>
    /// <summary>The limits flown with (a panel's: ApLimits.Of); null the static defaults.</summary>
    public ApLimits? LimOverride;
    /// <summary>The ground ray's reach (ApSense.Agl: -1 past it, as the game's - the voxel colliders are built near the ship only).</summary>
    public float AglReach = float.PositiveInfinity;
    public SimTerrain Terrain { get => _terrain; set { _terrain = value ?? SimTerrain.Flat(); Fd.Terrain = _terrain; } }
    /// <summary>The ground's height under the ship now.</summary>
    public double GroundY => _terrain.H(P.X, P.Z);

    /// <summary>The thruster set changed (an engine out, a new ship): the solver's arrays again.</summary>
    public void Rebuild()
    {
        _n = S.T.Count;
        _arms = new Vector3[_n]; _dirs = new Vector3[_n]; _proj = new Vector3[_n]; _caps = new float[_n]; _f = new float[_n]; _f0 = new float[_n];
        _prev = new float[_n]; _y = new float[_n]; _side = new float[_n]; _rev = new bool[_n];
        _st ??= new ReversibleThrust.State[_n];
        if (_st.Length != _n) _st = new ReversibleThrust.State[_n];
        for (int i = 0; i < _n; i++) { if (_st[i].Sign == 0) _st[i].Sign = 1; _side[i] = _st[i].Sign; }
        _armSq = 0f;
        for (int i = 0; i < _n; i++) { var t = S.T[i]; _dirs[i] = t.Dir; _arms[i] = Vector3.Cross(t.Pos, t.Dir); _rev[i] = t.Rev; _armSq += t.Pos.LengthSquared(); }
        _warm = false;
        _gim = new ThrustVector.Nozzle[S.Gimbals.Count]; _gimPrev = new Vector3[S.Gimbals.Count];
        for (int j = 0; j < S.Gimbals.Count; j++) { var gb = S.Gimbals[j]; var t = S.T[gb.Thr]; _gim[j] = new ThrustVector.Nozzle { Pos = t.Pos, Dir = t.Dir, MaxRad = gb.MaxDeg * ApMath.Deg, RateRad = gb.RateDeg * ApMath.Deg }; }
    }

    public float TiltDeg => MathF.Acos(Math.Clamp(Vector3.Transform(Vector3.UnitY, Q).Y, -1f, 1f)) / ApMath.Deg;
    public Vector3 HorizontalV => new(V.X, 0f, V.Z);
    public float Agl => (float)(P.Y - GroundY) - S.Bottom;

    Vector3 Gravity => new(0f, -S.PlanetG, 0f);
    /// <summary>The gravity the thrust must hold with dampeners on (the autopilot's weight): honest, the world's multiplier in.</summary>
    Vector3 HoldGravity => Gravity * (S.Honest && HonestHold.Enabled ? S.Gm : 1f);

    // ── the instruments: what the autopilot may know (with the scenario's sensor noise) ──
    ApSense Sense(float dt)
    {
        var s = new ApSense { Lim = LimOverride ?? ApLimits.Defaults, Dt = dt, Mass = S.Mass, Gravity = HoldGravity, GravityTrue = Gravity * S.Gm, Belly = S.Bottom, Dampeners = Damp, Piloted = Piloted, PilotMove = Piloted ? PilotKeys : Vector3.Zero, PilotSteerDeg = PilotSteerDeg };
        s.Orient = Q; s.AngVel = W; s.Pos = P; s.Vel = V;
        s.Radius = S.Radius > 0f ? S.Radius : S.T.Count > 0 ? S.T.Max(t => t.Pos.Length()) + 1f : 2f * S.Bottom;   // (the collision avoidance's size)
        if (NoiseVel > 0f) s.Vel += Gauss3(NoiseVel);
        if (NoisePos > 0f) s.Pos += (Vector3D)Gauss3(NoisePos);
        if (NoiseDeg > 0f) { var e = Gauss3(NoiseDeg * ApMath.Deg); s.Orient = Quaternion.Normalize(Q * Quaternion.CreateFromAxisAngle(Vector3.Normalize(e + new Vector3(1e-6f)), e.Length())); }
        s.Agl = Agl <= AglReach ? Agl : -1f;   // (the game's ground ray reaches only so far: GroundHeight)
        // the side maxima (MaxThrustData.Regular: every thruster's live maximum on the side the game books it)
        Vector3 pos = Vector3.Zero, neg = Vector3.Zero;
        for (int i = 0; i < _n; i++)
        {
            Vector3 d = _dirs[i] * _side[i]; float c = S.T[i].Cap;
            pos += Vector3.Max(d, Vector3.Zero) * c; neg += Vector3.Max(-d, Vector3.Zero) * c;
        }
        s.ThrustPos = pos; s.ThrustNeg = neg;
        SideAuthority.Of(_arms, _dirs, _caps, _rev, _n, out s.SidePos, out s.SideNeg);
        // the attitude law's authority about the lean axes (X, Z for a +Y lift axis): gyros + thrusters + the cyclic, over I
        // (AttAccelPlan: without the cyclic - what the law plans its braking with, the game's ApSenseNow the same)
        Vector3 reach = S.Gyro + ThrusterReach() + new Vector3(LastCycReach, 0f, LastCycReach) + 0.75f * S.Air.SurfaceReach(0.5f * Rho * AirLocal().LengthSquared()) + GimbalReach();
        s.AttAccel = MathF.Min(reach.X / S.I.X, reach.Z / S.I.Z);
        Vector3 plan = reach - new Vector3(LastCycReach, 0f, LastCycReach);
        s.AttAccelPlan = MathF.Min(plan.X / S.I.X, plan.Z / S.I.Z);
        s.AttErrDeg = _lastErr * 180f / MathF.PI; s.CtrlShare = _lastShare;
        s.AirVel = AirLocal(); s.Density = Rho;
        s.StallFresh = _stallFresh; s.StallState = _stallState; s.StallMarginDeg = _stallMargin; s.StallLiftInUse = _stallInUse; s.StallG = _stallG; s.LiftDir = _liftDir; s.StallFlow = _stallFlow; s.Perf = _stallFresh ? _perf : null;
        s.LoacFresh = _loacFresh; s.LoacProbe = _loacProbe; s.LoacHere = _loacHere; s.LoacProbeAt = _loacAt;
        s.SpeedLimit = SpeedLimit;
        s.Nose = S.Nose; s.GunAxis = S.GunAxis; s.MuzzleSpeed = S.Muzzle; s.GunRange = S.GunRange;
        s.SpeedOfSound = 340f; s.HasShipStatus = ShipStatus; s.FuelShare = Fuel; s.IntegrityShare = Integrity;
        return s;
    }
    float _lastErr, _lastShare;

    Vector3 Gauss3(float sigma) => new(Gauss() * sigma, Gauss() * sigma, Gauss() * sigma);
    float Gauss() { double u1 = 1.0 - Rng.NextDouble(), u2 = Rng.NextDouble(); return (float)(Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Cos(2 * Math.PI * u2)); }

    public Vector3 AirLocal() => Vector3.Transform(V - Wind, Quaternion.Inverse(Q));

    Vector3 ThrusterReach()
    {
        Vector3 r = Vector3.Zero;
        for (int i = 0; i < _n; i++) r += Vector3.Abs(_arms[i]) * S.T[i].Cap * (_rev[i] ? 2f : 1f);
        return r * 0.5f;
    }

    // ── the background evaluation (the structure's job, a few times a second): the stall and the predicted loss of control ──
    void Evaluate()
    {
        _stallFresh = false; _loacFresh = false;
        var air = AirLocal(); float speed = air.Length();
        if (!S.Air.Lifts || speed < EnvelopeGuard.MinSpeed || Rho < EnvelopeGuard.MinDensity) return;
        var v = air / speed;
        float weight = S.Mass * S.PlanetG * S.Gm;   // (the real weight: the world's multiplier in - EvaluateStall's snap.Weight)
        Vector3 F(Vector3 dir) { S.Air.Forces(dir * speed, Vector3.Zero, Rho, out var f, out _); return f; }
        var up = Vector3.Transform(Vector3.UnitY, Quaternion.Inverse(Q));
        var l = LiftPeak.LiftDir(v, F(v), up);
        // (the lift and drag of every angle the sweep looks at, recorded: the flight performance - into the back buffer, swapped in)
        var ps = _perfBack; ps.Clear(); ps.Flow = v; ps.LiftDir = l; ps.Speed = speed; ps.Density = Rho;
        LiftPeak.Find(t => { var ft = F(LiftPeak.Air(v, l, t)); ps.Record(t, ft, v, l); return Vector3.Dot(ft, LiftPeak.Normal(v, l, t)); }, out float tp, out float lp, out float l0);
        ps.Sort(); _perfBack = _perf; _perf = ps;
        _stallState = LiftPeak.State(tp, lp, l0, 0.05f * weight);
        _stallMargin = tp * 180f / MathF.PI; _stallInUse = lp > 0f ? l0 / lp : 0f; _stallG = lp / weight; _liftDir = l; _stallFlow = v; _stallFresh = true;
        // the loss of control at the autopilot's probe (Loac.cs: PathPoints 3 on the way, the attitude now too)
        if (!Cmd.HasProbe) return;
        float q = 0.5f * Rho * speed * speed;
        Vector3 reach = S.Gyro + ThrusterReach(), surf = S.Air.SurfaceReach(q);
        var airW = Vector3.Transform(air, Q);
        float Need(Quaternion at) { var al = Vector3.Transform(airW, Quaternion.Inverse(at)); S.Air.Forces(al, Vector3.Zero, Rho, out _, out var tq); return LoacMath.Worst(tq, reach, surf, surf); }
        _loacHere = Need(Q);
        float worst = 0f;
        for (int k = 1; k <= 3; k++) worst = MathF.Max(worst, Need(Quaternion.Slerp(Q, Cmd.Probe, k / 3f)));
        _loacProbe = worst; _loacAt = Cmd.Probe; _loacFresh = true;
    }

    /// <summary>One 60 Hz tick.</summary>
    public void Tick()
    {
        const float dt = 1f / 60f;
        Time += dt;
        int n = _n;
        var qi = Quaternion.Inverse(Q);
        Vector3 g = Vector3.Transform(Gravity, qi), v = Vector3.Transform(V, qi);
        float m = S.Mass;
        for (int i = 0; i < n; i++) _caps[i] = S.T[i].Cap;
        // 1. the reversible thrusters' sides (the keys turn them at once)
        for (int i = 0; i < n; i++)
        {
            if (!_rev[i]) { _side[i] = 1f; continue; }
            float input = Vector3.Dot(Move, _dirs[i]);
            float dem = ReversibleThrust.Demand(input, Damp, Vector3.Dot(v, _dirs[i]), Vector3.Dot(g, _dirs[i]) * (S.Honest ? S.Gm : 1f), m, _caps[i]);
            ReversibleThrust.Step(ref _st[i], dem, _caps[i], MathF.Abs(input) > ReversibleThrust.InputDeadband, dt);
            _side[i] = _st[i].Sign;
        }
        Array.Clear(_capDir, 0, 6);
        for (int i = 0; i < n; i++) _capDir[ThrustAllocator.DirIndex(_dirs[i] * _side[i])] += _caps[i];
        // 2. the game's thrust law, per axis
        Vector3 hold = Damp ? HonestHold.GameHold(g, m, Move) : Vector3.Zero;
        Vector3 gameF = Vector3.Zero, cancel = Vector3.Zero;
        for (int ax = 0; ax < 3; ax++)
        {
            float cp = _capDir[2 * ax], cn = _capDir[2 * ax + 1];
            float u = Comp(Move, ax), h = Comp(hold, ax), va = Comp(v, ax), ga = Comp(g, ax);
            bool key = MathF.Abs(u) > HonestHold.InputDeadzone;
            float damp = Damp && !key ? -m * va * 60f : 0f;
            float input = u > 0f ? u * MathF.Max(cp - h, 0f) : u < 0f ? u * MathF.Max(cn + h, 0f) : 0f;
            float fa = Math.Clamp(h + damp + input, -cn, cp);
            float ca = h != 0f && ga != 0f ? MathF.Sign(h) * MathF.Min(MathF.Abs(Math.Clamp(h, -cn, cp)) / m, MathF.Abs(ga)) : 0f;
            Set(ref gameF, ax, fa); Set(ref cancel, ax, ca);
        }
        Vector3 applied = gameF - cancel * m, gravApplied = (g + cancel) * S.Gm;
        if (S.Honest && HonestHold.Enabled && Damp && cancel != Vector3.Zero)
        {
            Vector3 th = HonestHold.Thrust(hold, S.Gm, applied, new Vector3(_capDir[0], _capDir[2], _capDir[4]), new Vector3(_capDir[1], _capDir[3], _capDir[5]));
            gravApplied = HonestHold.Gravity(g, th - applied, m, S.Gm) * S.Gm;
            applied = th; gameF = th;
            // (the honest step: the thrust th applied, the gravity the body gets x the multiplier: the hold part's thrust is real)
            gravApplied = g * S.Gm;
        }
        LastGameF = gameF;
        // 3. the air (before the attitude job): the airframe's torque, and the surfaces' share of last tick's ask
        var airL = AirLocal();
        S.Air.Forces(airL, W, Rho, out Vector3 aeroF, out Vector3 aeroT);
        LastAeroF = aeroF;
        float qd = 0.5f * Rho * airL.LengthSquared();
        Vector3 sReach = S.Air.SurfaceReach(qd);
        Vector3 surfT = Vector3.Clamp(_lastDemand, -sReach, sReach);
        V += Vector3.Transform(aeroF, Q) * (dt / m);
        W += (aeroT + surfT) / S.I * dt;
        LastAeroT = aeroT;
        // 4. the autopilot (the evaluation a few times a second first)
        _evalClock += dt;
        if (_evalClock >= 1f / StallEvalHz) { _evalClock = 0f; Evaluate(); }
        var sense = Sense(dt);
        bool wasFlying = Fd.Engaged;
        // (an Autopilot block flying it: the game's order - AeroGridComponent.ServerAp - its panel's limits, the link before and after)
        if (Link != null)
        {
            sense.Lim = ApLimits.Of(Panel);
            Link.Before(Fd, in Panel, BlockReady, in sense, ref LinkOut); Apply(in LinkOut);
        }
        Fd.Step(in sense, ref Cmd);
        if (Link != null) { LinkOut = default; Link.After(Fd, in Panel, ref LinkOut); Apply(in LinkOut); }
        if (Fd.Engaged)
        {
            if (Cmd.SetMove) Move = Cmd.Move; else Move = Piloted ? PilotKeys : Vector3.Zero;
            if (Cmd.Dampeners) Damp = true;
        }
        else Move = Piloted ? PilotKeys : Vector3.Zero;
        if (Cmd.SetMove && (MathF.Abs(Cmd.Move.X) > 1f || MathF.Abs(Cmd.Move.Y) > 1f || MathF.Abs(Cmd.Move.Z) > 1f)) Book.Fail("a movement key past 1", Time);
        // 5. ThrustTorque.Apply
        Vector3 shareTau = ThrustAllocator.GameShare(gameF, _arms, _dirs, _caps, _capDir, _f0, n, _side);
        Vector3 reaction = S.Rotor >= 0 ? _dirs[S.Rotor] * RotorKinematics.ReactionTorque(_f0[S.Rotor], S.RotorR, 1f, Rho) : Vector3.Zero;
        Vector3 axis = S.Rotor >= 0 ? _dirs[S.Rotor] : Vector3.UnitY;
        float sinT = MathF.Sin(S.MaxTiltDeg * ApMath.Deg);
        float cycReach = S.Rotor >= 0 ? RotorTrim.Reach(_f0[S.Rotor], S.HubH, S.RotorR, sinT, S.HubMoment) : 0f;
        LastCycReach = cycReach; LastReaction = reaction;
        Vector3 netA = (applied + _lastLawW) / m + gravApplied;
        bool trim = S.Rotor >= 0 && !RotorTrim.Resting(V.Length(), W.Length(), netA.Length());
        bool aware = Cmd.Flying;
        Vector3 tauNeed = Vector3.Zero, tauTarget, cycAsk, plan = Vector3.Zero; bool project = false;
        Vector3 inv = new(1f / S.I.X, 1f / S.I.Y, 1f / S.I.Z);
        if (aware)
        {
            Mode = "autopilot (aero-aware)";
            ThrustAllocator.RateCommand(Q, Cmd.Target, W, 1.2f, 0.8f, 0.3f, 0.002f, 0.002f, out Vector3 err);
            Vector3 reach = S.Gyro + ThrusterReach() + 0.75f * sReach + GimbalReach();
            if (PlanWithCyclic && S.Rotor >= 0) reach += ApTuning.CyclicPlanShare * cycReach * (Vector3.One - Vector3.Abs(axis));   // (ThrustTorque.CyclicPlanShare: the autopilot's law plans with the cyclic too)
            Vector3 d = (aeroT + surfT) * inv;
            Vector3 acc = ThrustAllocator.AttitudeAccel(err, W - d * dt, reach * inv, aeroT * inv, 0.002f, 0.002f);
            Vector3 demand = acc * S.I - aeroT;
            _lastDemand = demand;
            tauNeed = demand - surfT - reaction;
            _lastErr = err.Length();
            cycAsk = tauNeed;
            if (S.Rotor >= 0) { plan = RotorTrim.Plan(tauNeed, shareTau, axis, cycReach, trim, out project); tauNeed -= plan; }
            Vector3 beyond = tauNeed;
            tauTarget = beyond - Vector3.Clamp(beyond, -S.Gyro, S.Gyro);
            Vector3 full = Vector3.Max(S.Gyro + ThrusterReach() + sReach + new Vector3(cycReach), new Vector3(1f));   // (all of every actuator: the surfaces whole, as the loss-of-control prediction counts them)
            _lastShare = MathF.Max(MathF.Abs(demand.X) / full.X, MathF.Max(MathF.Abs(demand.Y) / full.Y, MathF.Abs(demand.Z) / full.Z));
        }
        else
        {
            // unpiloted, dampeners on: the damp branch (no attitude held); off: free (the solve still answers the rotor)
            Mode = Damp ? "damp" : "free";
            Vector3 accel = Damp ? ThrustAllocator.RateCommand(Q, Q, W, 1.2f, 0.8f, 0.3f, 0.002f, 0.0002f, out _) : -W / 0.3f;
            tauTarget = (accel == Vector3.Zero ? Vector3.Zero : accel * S.I) - reaction;
            cycAsk = tauTarget;
            if (S.Rotor >= 0) { plan = RotorTrim.Plan(tauTarget, shareTau, axis, cycReach, trim, out project); tauTarget -= plan; }
            _lastDemand = Vector3.Zero; _lastErr = 0f; _lastShare = 0f;
        }
        if (!_warm) { Array.Copy(_f0, _f, n); _warm = true; }
        if (project) RotorTrim.ProjectArms(_arms, axis, _proj, n);
        ThrustAllocator.Solve(project ? RotorTrim.ProjectTorque(tauTarget, axis) : tauTarget, gameF, project ? _proj : _arms, _dirs, _caps, _f0, _f, _prev, _y, _armSq, n, 60, out float prio, _rev);
        LastPrio = prio;
        Vector3 thrTau = Vector3.Zero, total = Vector3.Zero;
        for (int i = 0; i < n; i++) { thrTau += _arms[i] * _f[i]; total += _dirs[i] * _f[i]; }
        Vector3 given = S.Rotor >= 0 ? RotorTrim.Give(cycAsk, thrTau, plan, axis, cycReach, trim) : Vector3.Zero;
        if (aware) tauNeed += plan - given;
        Vector3 gyroT = aware ? Vector3.Clamp(tauNeed - thrTau, -S.Gyro, S.Gyro) : Vector3.Clamp(-W * S.I / 0.3f, -S.Gyro, S.Gyro);
        // (THRUST VECTORING: the gimballed nozzles give what the thrusters and gyros left of the need - ThrustVector, the cone and the
        //  rate theirs; unflown: back to straight at their rate)
        Vector3 tvcF = Vector3.Zero, tvcT = Vector3.Zero;
        if (_gim.Length > 0)
        {
            for (int j = 0; j < _gim.Length; j++) { _gimPrev[j] = _gim[j].Deflect; _gim[j].Thrust = MathF.Max(0f, _f[S.Gimbals[j].Thr]); }
            ThrustVector.Solve(aware ? tauNeed - thrTau - gyroT : Vector3.Zero, _gim, _gim.Length, dt, out tvcF, out tvcT);
            LastTvcT = tvcT;
        }
        Vector3 extra = prio > 0f ? total - gameF : Vector3.Zero;
        Vector3 tilt = S.Rotor >= 0 ? RotorTrim.TiltForce(given, axis, _f0[S.Rotor], S.HubH, cycReach, sinT) : Vector3.Zero;
        _lastLawW = extra + tilt;
        LastThrTau = thrTau; LastGyroT = gyroT; LastCyclicT = given; LastTilt = tilt;
        TailSideForce = 0f; for (int i = 0; i < n; i++) if (_rev[i] && MathF.Abs(_dirs[i].X) > 0.9f) TailSideForce += _f[i] * _dirs[i].X;
        // 6. THE LEDGER: each actuator within its own limits, the torque applied exactly the sum of theirs
        Book.Ticks++;
        for (int i = 0; i < n; i++)
        {
            float lo = _rev[i] ? -_caps[i] : 0f;
            if (_f[i] < lo - 1f || _f[i] > _caps[i] + 1f) Book.Fail($"thruster {S.T[i].Name} at {_f[i]:F0} N outside {lo:F0}..{_caps[i]:F0}", Time);
            if (_caps[i] > 0f) Book.MaxThrustShare = Math.Max(Book.MaxThrustShare, MathF.Abs(_f[i]) / _caps[i]);
        }
        if (MathF.Abs(gyroT.X) > S.Gyro.X * 1.0001f + 1e-3f || MathF.Abs(gyroT.Y) > S.Gyro.Y * 1.0001f + 1e-3f || MathF.Abs(gyroT.Z) > S.Gyro.Z * 1.0001f + 1e-3f) Book.Fail("gyros past their reach", Time);
        if (S.Gyro.Length() > 0f) Book.MaxGyroShare = Math.Max(Book.MaxGyroShare, MathF.Max(MathF.Abs(gyroT.X) / MathF.Max(S.Gyro.X, 1e-6f), MathF.Max(MathF.Abs(gyroT.Y) / MathF.Max(S.Gyro.Y, 1e-6f), MathF.Abs(gyroT.Z) / MathF.Max(S.Gyro.Z, 1e-6f))));
        if (_gim.Length > 0) for (int j = 0; j < _gim.Length; j++) { float dl = (_gim[j].Deflect - _gimPrev[j]).Length(); if (_gim[j].Deflect.Length() > _gim[j].MaxRad * 1.0001f + 1e-5f || dl > _gim[j].RateRad / 60f * 1.0001f + 1e-5f) Book.Fail($"gimbal {j} past its cone or rate ({_gim[j].Deflect.Length() / ApMath.Deg:F2} deg, {dl / ApMath.Deg * 60f:F1} deg/s)", Time); }
        if (S.Rotor >= 0)
        {
            if (RotorTrim.InPlane(given, axis).Length() > cycReach * 1.0001f + 1f) Book.Fail("cyclic past its reach", Time);
            if (cycReach > 0f) Book.MaxCyclicShare = Math.Max(Book.MaxCyclicShare, given.Length() / cycReach);
        }
        if (MathF.Abs(Vector3.Dot(given, axis)) > 1f) Book.Fail("cyclic torque about the rotor axis", Time);
        if (extra.LengthSquared() > 1f && prio <= 0f) Book.Fail("a thrust change with no attitude priority", Time);
        if (surfT.X > sReach.X + 1f || surfT.X < -sReach.X - 1f || surfT.Y > sReach.Y + 1f || surfT.Y < -sReach.Y - 1f || surfT.Z > sReach.Z + 1f || surfT.Z < -sReach.Z - 1f) Book.Fail("control surfaces past their reach", Time);
        // (an actuator lag: the control torque reaches the ship through a first-order lag - a rotor's spool, a slow gyro)
        Vector3 ctrl = thrTau - shareTau + given + gyroT + tvcT;
        if (LagSeconds > 0f) { _lagTau += (ctrl - _lagTau) * MathF.Min(1f, dt / LagSeconds); ctrl = _lagTau; }
        Vector3 torque = shareTau + reaction + ctrl;
        // 7. the step: the game's thrust, the attitude law's force changes (a real thrust change, the tilt), the gravity it gets
        Vector3 a = (applied + extra + tilt + tvcF) / m + gravApplied;
        V += Vector3.Transform(a, Q) * dt;
        W += (torque - Vector3.Cross(W, S.I * W)) * inv * dt;
        Q = Quaternion.Normalize(Q + Q * new Quaternion(W * (0.5f * dt), 0f));
        P += (Vector3D)(V * dt);
        // the ground: the ship stands on it (a hard landing - over 3 m/s down - is a crash)
        OnGround = false;
        double gy = GroundY;
        if (P.Y < gy + S.Bottom)
        {
            // (into the ground faster than 3 m/s along its normal - a cliff face too - is a crash; else it stands / slides on it)
            var gn = _terrain.NormalAt(P.X, P.Z); float into = -Vector3.Dot(V, gn);
            if (into > 3f) Crashed = true;
            P = new Vector3D(P.X, gy + S.Bottom, P.Z); if (into > 0f) V = (V + gn * into) * 0.5f; OnGround = true;
        }
    }

    static float Comp(Vector3 v, int ax) => ax == 0 ? v.X : ax == 1 ? v.Y : v.Z;
    static void Set(ref Vector3 v, int ax, float x) { if (ax == 0) v.X = x; else if (ax == 1) v.Y = x; else v.Z = x; }

    /// <summary>The autopilot's instruments now (for an engage at a scripted moment).</summary>
    public ApSense SenseNow() => Sense(1f / 60f);

    // ── an Autopilot block's panel flying it (Scenarios/BlockPack.cs): the panel, its link, the block working, the last write-back ──
    public ApBlockLink Link; public ApPanel Panel = ApPanel.Defaults; public bool BlockReady = true; public ApBlockLink.Out LinkOut;
    public ApReason LastPanelReason; public int PanelWrites;
    /// <summary>A block on this plant: the link made (as the grid makes it when a block takes over).</summary>
    public void AddBlock(in ApPanel p) { Panel = p; Link = new ApBlockLink(); }
    void Apply(in ApBlockLink.Out o)
    {
        if (o.ModeChanged) { Panel.Mode = o.Mode; LastPanelReason = o.Reason; PanelWrites++; }
        if (o.HeightChanged) { Panel.HeightM = o.HeightM; PanelWrites++; }
        if (o.HoldsChanged) { Panel.HeadingDeg = o.HeadingDeg; Panel.AltM = o.AltM; Panel.SpeedMs = o.SpeedMs; PanelWrites++; }
        if (o.HomeChanged) { Panel.HasHome = true; Panel.HomeX = o.Home.X; Panel.HomeY = o.Home.Y; Panel.HomeZ = o.Home.Z; PanelWrites++; }
        if (o.SlotSavedChanged) { if (o.DfSaved != null) Panel.DfSaved = o.DfSaved; if (o.RgSaved != null) Panel.RgSaved = o.RgSaved; PanelWrites++; }
    }
}
