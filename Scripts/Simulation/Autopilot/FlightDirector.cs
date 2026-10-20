#pragma warning disable CS0649, CS8632, CS1591, CS1573, CS1570, CS1574, CS0419, CS1734
using System;
namespace AeroMod;

/// <summary>
/// THE FLIGHT DIRECTOR - one autopilot core for every user (the player's modes now; fixed-wing modes, waypoints, formation and
/// AI pilots later - through the external goal source), engine-free (Tests/AutopilotSim flies it). Each tick: GOALS (what is
/// held: ApGoal) -> GUIDANCE (outer loops: the speed, place and height errors made into a lean and a vertical command; an
/// external source's track, acceleration or attitude) -> PROTECTION (EnvelopeGuard: stall angle, g, the predicted loss of
/// control; the lean and thrust limits; the terrain - the floor under it and the time to impact ahead; low energy; overspeed)
/// -> the SEAM: an ApCommand - a target attitude, the movement keys and the dampener switch, exactly what a seated pilot writes.
/// The ship's own attitude law (ThrustAllocator.AttitudeAccel in ThrustTorque, aero-aware) and its allocator then fly that
/// through the real gyros, thrusters, rotors' cyclic and control surfaces - the autopilot never applies a force or a torque.
///
/// HOVER (ADS-33's translational rate command): the horizontal velocity error (to the keys' speed, or back to the held place)
/// is an acceleration, the acceleration a lean (atan a / g) of the ship's lift axis toward it - with an integral, which finds
/// by itself the steady lean a single tail rotor's sideways thrust needs (a real helicopter's translating tendency) - level on
/// the heading held, the lean within Autopilot.MaxLeanDeg and what the thrust can lean and still hold the weight. Where the
/// ship has thrusters across (a VTOL), the same acceleration goes to the movement keys too (the dampeners then hold that axis
/// with real thrust; a helicopter's tail rotor stays on its yaw - the allocator's rotation first). The gains come from what the
/// mod measures (AttAccel: how fast the attitude law can tilt this ship), never from a per-ship setting.
/// HEIGHT: over the ground (Autopilot.HoldAboveGround: the terrain's height under the ship, filtered) or along the gravity from
/// where it was caught; the dampeners hold the vertical speed at zero by themselves, the height error and the up / down keys
/// are a climb rate flown with the up key (the collective).
/// A piloted ship: the pilot's movement keys fly it at a speed (the place / height caught again where it stops); steering it
/// (the reticle moved past ReleaseDeg) lets go of the hover (the height hold stays). Unpiloted, the mode stays (Autopilot.Unattended).
/// </summary>
public sealed class FlightDirector
{
    public ApGoal Goals { get; private set; }
    public bool Engaged => Goals != ApGoal.None;
    /// <summary>Why it last let go or refused (None: it never did / it is flying).</summary>
    public ApReason Reason { get; private set; }
    /// <summary>What the protection did this tick.</summary>
    public ApProt Prot { get; private set; }
    public readonly EnvelopeGuard Guard = new EnvelopeGuard();
    /// <summary>The terrain it sees (null: only ApSense.Agl, the ground ray straight down) and its look at it each tick.</summary>
    public ITerrainQuery Terrain;
    public readonly TerrainWatch Ground = new TerrainWatch();
    /// <summary>The obstacles around the ship (null: none known) and the collision avoidance's look at them each tick (ApAvoid).</summary>
    public ApObstacles Obstacles;
    public readonly ApAvoid Avoid = new ApAvoid();
    /// <summary>The clearance the avoidance keeps from the TARGET at most (m): half the behaviour's abort distance (the goal source
    /// sets it; +inf: the plain clearance).</summary>
    public float TargetClearMax = float.PositiveInfinity;
    /// <summary>The goal source's target as an obstacle this tick (set by the source each tick it has one; cleared after the step).</summary>
    public ApObstacle TargetObstacle; public bool HasTargetObstacle;
    /// <summary>The external goal source (ApGoal.External): a mission, a formation slot, a landing pad, a navigation leg.</summary>
    public IApGoalSource Source { get; private set; }
    /// <summary>The height hold's reference (set at engage from Autopilot.HoldAboveGround).</summary>
    public ApHeightRef HeightRef { get; private set; } = ApHeightRef.Ground;
    /// <summary>The height held has no ground under it now: over the ground (Ground) - none in sight (the rays and the planet's
    /// height both miss): the height is held along the gravity, where it was over the last ground seen; level (Inertial) - no
    /// ground was known for its datum: it reads from where the ship was (until a ground is seen). The status line says so.</summary>
    public bool NoGround { get; private set; }
    /// <summary>The hover's lift axis (local unit: the side with the most thrust) - the lean is of this axis.</summary>
    public Vector3 LiftAxis => _upL;
    /// <summary>The flight performance now (FlightPerf.Evaluate over ApSense.Perf, each tick it is given; Valid false: none) - for
    /// any goal source to read.</summary>
    public ApPerf Perf { get; private set; }

    /// <summary>Hover: the steering that lets go (deg, the pilot's leaky sum - the Orbital Mod's AttitudeHold's rule).</summary>
    public const float ReleaseDeg = 4f;
    /// <summary>Engaged faster than this (m/s): refused - hover hold is for low speed (it brakes from up to this).</summary>
    public const float MaxEngageSpeed = 60f;
    /// <summary>Upset: further than this from its target (deg) for UpsetSeconds - the autopilot is not in control: it lets go.</summary>
    public const float UpsetDeg = 45f, UpsetSeconds = 2f;
    /// <summary>The up thrust under this share of the weight: nothing to hover with (NoLift).</summary>
    public const float NoLiftShare = 0.5f;
    /// <summary>Thrust short of the weight's x this: low energy - full up thrust, no lean.</summary>
    public const float LiftMargin = 1.05f;
    /// <summary>Piloted, below its height this far (m) and sinking for CantHoldSeconds at full thrust: it lets go.</summary>
    public const float CantHoldM = 30f, CantHoldSeconds = 8f;
    /// <summary>The hover's speed integral ignores errors under this (m/s), and integrates at most this much (m/s).</summary>
    public const float IntegralDeadband = 0.02f, IntegralMaxError = 0.5f;
    /// <summary>The hover's speed command ramps at most this share of the lean's acceleration.</summary>
    public const float RampShare = 0.5f;
    /// <summary>The movement keys across the lift axis only from this acceleration (m/s2): under it the dampeners' own stiff hold
    /// stays on that axis (a key switches it off there).</summary>
    public const float SideKeyFrom = 0.15f;
    /// <summary>A side with less translation authority than this (SideAuthority) gets no movement key.</summary>
    public const float MinSideAuthority = 0f;   // (0: keys on every side - offline, a single tail keyed holds its game side steady, unkeyed its dampening chatters: SideAuthority stays a measure)
    /// <summary>The vertical speed loop's gain (1/s): the collective is quick - the rotor's and the thrusters' thrust follow at once.</summary>
    public const float VerticalGain = 3f;
    /// <summary>Overspeed: the throttle eases off over this many m/s up to the speed limit.</summary>
    public const float OverspeedBand = 5f;
    /// <summary>Hovering: terrain ahead within this many seconds - climb and stop going toward it.</summary>
    public const float TtiHover = 3f;
    /// <summary>Flying (attitude / external): the pull-up's ask - this much nose-up a tick (deg) - the protection cuts it to
    /// what the stall, g and loss-of-control limits allow; and its reaction time before the turn (s).</summary>
    public const float PullUpDeg = 30f, PullReact = 2f, PullHold = 2f;
    /// <summary>The commanded attitude moves at most this fast (deg/s): hovering, flying.</summary>
    public const float SlewHover = 90f, SlewFlying = 25f;
    /// <summary>A track's heading is turned toward at most this fast (deg/s) - the yaw the hover's frame follows.</summary>
    public const float TrackYawRate = 45f;
    /// <summary>Six-axis: the share of the weakest side's acceleration a translation is planned with (the rest for the attitude
    /// law's thrust and the disturbances), and the movement key kept past the game's input deadzone on every axis it flies (a key
    /// under it hands the axis to the dampeners' brake - a moving target would be braked toward).</summary>
    public const float SixShare = 0.6f, SixKeyMin = 0.0015f;

    // the hover's frame: the local lift axis (the most thrust), the local heading axis across it, the world heading held
    Vector3 _upL = Vector3.UnitY, _fwdL = -Vector3.UnitZ, _headW;
    // the held place (world, centre of mass) and whether it is caught (after a key move or the engage's braking); the height over
    // the ground held (Ground reference)
    Vector3D _hold; bool _holdH, _holdV; float _holdAgl = -1f; bool _aglPending;
    // the level altitude's datum (Inertial: the ground under the ship where the height hold was engaged) and its up
    Vector3D _datum; Vector3 _upDatum = Vector3.UnitY; bool _datumUnknown;
    // the last ground seen under the ship (a point on it: the hold's filtered height below the ship) - the no-ground fallback's base
    Vector3D _lastGround; bool _hasLastGround;
    // the integrals: the horizontal acceleration (world m/s2), its smoothed command, the vertical key (share)
    Vector3 _aI, _aF, _vRamp; float _uI;
    // the attitude goal's target
    Quaternion _att = Quaternion.Identity;
    // the external source's goal this tick
    ApExternal _ext;
    // the watch's clocks
    float _upset, _cantHold;
    bool _floorArmed, _wasPiloted, _dampWanted;
    float _brakeBest = float.MaxValue, _brakeStall;
    Vector3 _vPrev, _aMeas; bool _hasVPrev; float _pullHold;
    // the collision avoidance's push on a lift-vector goal this tick (world m/s2)
    Vector3 _avoidPush;
    Quaternion _lastTarget = Quaternion.Identity; bool _hasLastTarget;
    // six-axis: the attitude held (caught at the engage), its local up and nose (a level frame on a heading)
    Quaternion _sixAtt = Quaternion.Identity; Vector3 _sixUpL = Vector3.UnitY, _sixNoseL = -Vector3.UnitZ;
    /// <summary>Six-axis: the translation acceleration it plans with (m/s2, the weakest side's share) - diagnostics.</summary>
    public float SixAccel;
    /// <summary>The last command's lean (rad), acceleration (world m/s2), speed wanted (world m/s) and climb (m/s) - the traces.</summary>
    public float LeanRad; public Vector3 AccelCmd; public Vector3 VelCmd; public float ClimbCmd;

    /// <summary>The readout: "AP HOVER POS AGL", "AP ALT", "AP ATT" (+ " · " + the protection), "" off. Built only when it
    /// changes (a lookup per tick, no garbage).</summary>
    public string Readout { get; private set; } = "";
    int _readKey = -1;

    /// <summary>Engage these goals (added to the ones held), from the state now. False: refused, why.</summary>
    public bool Engage(ApGoal goals, in ApSense s, out ApReason why) => Engage(goals, in s, out why, null);

    /// <summary>... with an external goal source (ApGoal.External; null keeps the one set).</summary>
    public bool Engage(ApGoal goals, in ApSense s, out ApReason why, IApGoalSource source)
    {
        why = ApReason.None;
        if (!s.Lim.Enabled) { why = ApReason.NotAllowed; Reason = why; return false; }
        if ((goals & ApGoal.External) != 0 && (source ?? Source) == null) { why = ApReason.NoSource; Reason = why; return false; }
        bool hover = (goals & (ApGoal.Hover | ApGoal.Position | ApGoal.Height)) != 0;
        if (hover)
        {
            if (s.Gravity.LengthSquared() < 1e-4f) { why = ApReason.NoGravity; Reason = why; return false; }
            Vector3 up = ApMath.MainAxis(s.ThrustPos, s.ThrustNeg, out float lift);
            if (!(lift > NoLiftShare * s.Mass * s.Gravity.Length())) { why = ApReason.NoLift; Reason = why; return false; }
            if ((goals & ApGoal.Hover) != 0 && s.Vel.Length() > MaxEngageSpeed) { why = ApReason.TooFast; Reason = why; return false; }
            if (!s.Dampeners) { why = ApReason.NeedsDampeners; Reason = why; return false; }
            if ((Goals & (ApGoal.Hover | ApGoal.Height)) == 0)
            {
                // a new hover frame: the lift axis, the heading held now, the integrals from rest
                _upL = up; _fwdL = ApMath.Across(up);
                Vector3 upW = -Vector3.Normalize(s.Gravity);
                Vector3 h = ApMath.Flat(Vector3.Transform(_fwdL, s.Orient), upW);
                if (h.LengthSquared() < 1e-4f) h = ApMath.Flat(Vector3.Transform(Vector3.Cross(_upL, _fwdL), s.Orient), upW);
                _headW = Vector3.Normalize(h);
                _aI = Vector3.Zero; _uI = 0f;
                _floorArmed = s.Agl > s.Lim.FloorM + 2f;
            }
            if ((goals & ApGoal.Hover) != 0) goals |= ApGoal.Position;   // (a hover holds its place, caught where it stops)
            // (the height held from now - AltHold's rule - over the ground or along the gravity; the place caught where it stops)
            _holdH = false; _brakeBest = float.MaxValue; _brakeStall = 0f;
            if ((goals & ApGoal.Height) != 0 && (Goals & ApGoal.Height) == 0)
            {
                HeightRef = s.Lim.HoldAboveGround ? ApHeightRef.Ground : ApHeightRef.Inertial;
                _hold = s.Pos; _holdV = true; _aglPending = HeightRef == ApHeightRef.Ground;
                CatchDatum(in s, -Vector3.Normalize(s.Gravity));
            }
            else if ((goals & ApGoal.Height) == 0 && (Goals & ApGoal.Height) == 0) _holdV = false;
            _aF = Vector3.Zero; _vRamp = ApMath.Flat(s.Vel, -Vector3.Normalize(s.Gravity));
        }
        if ((goals & ApGoal.Attitude) != 0 && (Goals & ApGoal.Attitude) == 0) _att = s.Orient;
        if ((goals & ApGoal.External) != 0 && (Goals & ApGoal.External) == 0)
        {
            // (six-axis: the attitude now held; its local up - the side most against the gravity -, its nose across it)
            _sixAtt = s.Orient;
            Quaternion inv = Quaternion.Inverse(s.Orient);
            Vector3 upLoc = s.Gravity.LengthSquared() > 1e-4f ? Vector3.Transform(-Vector3.Normalize(s.Gravity), inv) : Vector3.UnitY;
            _sixUpL = Nearest(upLoc);
            Vector3 nose = s.Nose.LengthSquared() > 0.5f ? s.Nose : -Vector3.UnitZ;
            nose = ApMath.Flat(nose, _sixUpL);
            _sixNoseL = nose.LengthSquared() > 1e-4f ? Vector3.Normalize(nose) : ApMath.Across(_sixUpL);
            if (!hover)
            {
                Vector3 upW0 = s.Gravity.LengthSquared() > 1e-4f ? -Vector3.Normalize(s.Gravity) : Vector3.UnitY;
                Vector3 h0 = ApMath.Flat(Vector3.Transform(_sixNoseL, s.Orient), upW0);
                if (h0.LengthSquared() > 1e-4f) _headW = Vector3.Normalize(h0);
            }
        }
        if (source != null) Source = source;
        if ((Goals & ~ApGoal.External) == 0) { Ground.Reset(); _hasLastGround = false; }
        Goals |= goals;
        Reason = ApReason.None; _upset = _cantHold = 0f; _wasPiloted = s.Piloted; _dampWanted = false;
        Guard.Reset();
        _lastTarget = s.Orient; _hasLastTarget = true;   // (the first target slewed from the attitude it has)
        return true;
    }

    /// <summary>The attitude goal's target (an API / a later mode sets it; engaging Attitude holds the attitude it had).</summary>
    public void SetAttitude(Quaternion q) { if (q.IsValidAndRotationIsNormalized()) _att = Quaternion.Normalize(q); }

    /// <summary>The height held now (m; false: none held yet - climbing on the keys, or no height goal): over the ground (Ground:
    /// the filtered height under the ship it keeps) or over the DATUM (Inertial: the ground under the ship where the height hold
    /// was engaged - a level altitude, read as a height over that ground). The Autopilot block's panel shows it.</summary>
    public bool HeldHeight(out float h)
    {
        h = 0f;
        if ((Goals & ApGoal.Height) == 0 && (Goals & ApGoal.Hover) == 0 || !_holdV) return false;
        if (HeightRef == ApHeightRef.Ground) { if (_aglPending || _holdAgl < 0f) return false; h = _holdAgl; return true; }
        h = (float)Vector3D.Dot(_hold - _datum, (Vector3D)_upDatum); return true;
    }

    /// <summary>Hold this height (m, in the reference held - HeldHeight's meaning) from now: it climbs or sinks to it at the climb
    /// rate (the panel's setpoint changed). No height goal: nothing.</summary>
    public void SetHeight(float h, in ApSense s)
    {
        if ((Goals & (ApGoal.Height | ApGoal.Hover)) == 0 || !float.IsFinite(h)) return;
        h = MathF.Max(0f, h);
        Vector3 upW = s.Gravity.LengthSquared() > 1e-4f ? -Vector3.Normalize(s.Gravity) : _upDatum;
        if (!_holdV) CatchHeight(s.Pos, upW);
        if (HeightRef == ApHeightRef.Ground)
        {
            _holdAgl = h; _aglPending = false;
            // (the same height along the gravity too - what is flown while no ground is in sight: over the ground known now, the
            //  last seen, or the planet's; none: over the datum. In game 2026-10-19 a typed height with the ground out of the rays'
            //  reach changed the panel's number and nothing else.)
            Vector3D ug = upW;
            double baseUp = GroundUp(in s, upW, out double g) ? g : Vector3D.Dot(_datum, ug);
            _hold += ug * (baseUp + h - Vector3D.Dot(_hold, ug));
            return;
        }
        Vector3D u = _upDatum;
        _hold += u * (h - Vector3D.Dot(_hold - _datum, u));
    }

    /// <summary>Switch the height reference while holding: the height it holds now is kept (caught in the new reference), so the
    /// ship does not move; the panel then shows its number in the new reference.</summary>
    public void SetHeightRef(ApHeightRef r, in ApSense s)
    {
        if (r == HeightRef) return;
        HeightRef = r;
        if ((Goals & (ApGoal.Height | ApGoal.Hover)) == 0) return;
        Vector3 upW = s.Gravity.LengthSquared() > 1e-4f ? -Vector3.Normalize(s.Gravity) : _upDatum;
        CatchHeight(s.Pos, upW);
        CatchDatum(in s, upW);
        _aglPending = r == ApHeightRef.Ground; _holdAgl = -1f;
    }

    /// <summary>The level altitude's datum: the ground under the ship now (ApSense.Agl; none found: where it is).</summary>
    void CatchDatum(in ApSense s, Vector3 upW)
    {
        // (the ground under the ship: the rays, the planet's height, the last seen - none: where it is, said on the status line
        //  and caught again when a ground is seen. In game 2026-10-19: "holding 0.0 m altitude" at 130 m, the ground past the rays.)
        _upDatum = upW;
        Vector3D u = upW;
        if (GroundUp(in s, upW, out double g)) { _datum = s.Pos - u * Math.Max(0.0, Vector3D.Dot(s.Pos, u) - g); _datumUnknown = false; }
        else { _datum = s.Pos; _datumUnknown = true; }
    }

    /// <summary>The ground's height along the up under the ship now (world, along upW): the watch's filtered height, the ground
    /// ray (ApSense.Agl), the terrain asked straight down (the planet's height reaches far), the last ground seen. False: none.</summary>
    bool GroundUp(in ApSense s, Vector3 upW, out double groundUp)
    {
        Vector3D u = upW;
        double pu = Vector3D.Dot(s.Pos, u);
        groundUp = 0;
        // (the watch's and the last ground's only while engaged: at an engage they may be an older flight's, elsewhere)
        if (Engaged && Ground.HasGround && Ground.AglHold >= 0f) { groundUp = pu - Ground.AglHold; return true; }
        if (s.Agl >= 0f && float.IsFinite(s.Agl)) { groundUp = pu - s.Agl; return true; }
        if (Terrain != null && Terrain.Below(s.Pos, -upW, TerrainWatch.BelowReachLong, out float h, out _) && float.IsFinite(h))
        { groundUp = pu - Math.Max(0f, h - s.Belly); return true; }
        if (Engaged && _hasLastGround) { groundUp = Vector3D.Dot(_lastGround, u); return true; }
        return false;
    }

    /// <summary>Let go of some goals (all: every one), saying why.</summary>
    public void Disengage(ApReason why, ApGoal goals = (ApGoal)0xFF)
    {
        Goals &= ~goals;
        if ((Goals & ApGoal.Hover) == 0) Goals &= ~ApGoal.Position;
        if ((Goals & ApGoal.External) == 0) Source = null;
        Reason = why; Prot = ApProt.None;
    }

    /// <summary>One tick: the command for this state (c.Flying false: no attitude held - the ship is its pilot's).</summary>
    public void Step(in ApSense s, ref ApCommand c)
    {
        c = default;
        Prot = ApProt.None; NoGround = false;
        if (!Engaged) { UpdateReadout(); return; }
        if (!s.Lim.Enabled) { Disengage(ApReason.NotAllowed); UpdateReadout(); return; }
        // (the pilot left and the world wants the mode with them; seated, they switched the dampeners off - the hover needs them)
        if (!s.Lim.Unattended && _wasPiloted && !s.Piloted) { _wasPiloted = false; Disengage(ApReason.LeftSeat); UpdateReadout(); return; }
        _wasPiloted = s.Piloted;
        if (_dampWanted && !s.Dampeners && s.Piloted) { _dampWanted = false; Disengage(ApReason.DampenersOff); UpdateReadout(); return; }
        _dampWanted = false;
        if (s.Piloted && s.PilotSteerDeg > ReleaseDeg && (Goals & (ApGoal.Hover | ApGoal.Attitude | ApGoal.External)) != 0)
        {
            Disengage(ApReason.Steered, ApGoal.Hover | ApGoal.Position | ApGoal.Attitude | ApGoal.External);
            if (!Engaged) { UpdateReadout(); return; }
        }
        float dt = s.Dt > 0f ? s.Dt : 1f / 60f;
        Perf = s.Perf != null ? FlightPerf.Evaluate(s.Perf, in s) : default;   // (the read-only performance for goal sources)
        ApProt prot = ApProt.None;
        // ── the external source's goal for this tick (a mission, a slot, a pad) ──
        bool ext = (Goals & ApGoal.External) != 0;
        if (ext)
        {
            _ext = default; _ext.Throttle = float.NaN; _ext.Load = float.NaN;
            if (Source == null || !Source.Step(in s, ref _ext)) { Disengage(Source == null ? ApReason.NoSource : _ext.LetGo != ApReason.None ? _ext.LetGo : ApReason.GoalDone, ApGoal.External); ext = false; if (!Engaged) { UpdateReadout(); return; } }
        }
        bool hover = (Goals & ApGoal.Hover) != 0, height = (Goals & ApGoal.Height) != 0;
        bool track = ext && _ext.Kind == ApExternalKind.Track && hover;
        Quaternion want = s.Orient; bool attitude = false;
        float g = s.Gravity.Length();
        if ((hover || height) && g < 1e-2f) { Disengage(ApReason.NoGravity); UpdateReadout(); return; }
        Vector3 upW = g > 1e-2f ? -s.Gravity / g : Vector3.UnitY;
        float weight = s.Mass * g;
        float upCap = ApMath.Along(s.ThrustPos, s.ThrustNeg, _upL);
        if ((hover || height) && upCap < NoLiftShare * weight) { Disengage(ApReason.NoLift); UpdateReadout(); return; }
        bool lowEnergy = (hover || height) && upCap < LiftMargin * weight;

        // ── the outer loops' bandwidth, from how fast this ship tilts (the attitude law's authority) ──
        float a = s.AttAccel > 0f ? s.AttAccel : 2f;
        float tAtt = MathF.Max(0.15f, 2f * MathF.Sqrt(0.1f / a));   // (s: a 0.1 rad lean at the law's authority, there and stopped)
        // (the velocity loop at about half the tilt bandwidth; the place loop and the integral placed for a well damped
        //  cascade - s^3 + kv s^2 + (kv kp + ki) s + ki kp: a real root near 0.15 w and a pair at zeta ~0.9)
        float w = Math.Clamp(0.5f / tAtt, 0.3f, 2f);
        float kv = 1.6f * w, kp = 0.35f * w, ki = 0.25f * w * w;

        // ── the terrain: the ground below (raw for the floor, filtered for a hold) and the time to impact ahead ──
        Vector3 gTrue = s.GravityTrue.LengthSquared() > 0f ? s.GravityTrue : s.Gravity;
        // (the path bent by the acceleration the ship really has - its velocity's change, smoothed: the gravity and its turn in)
        if (_hasVPrev) _aMeas += ((s.Vel - _vPrev) / dt - _aMeas) * MathF.Min(1f, dt / 0.3f);
        _vPrev = s.Vel; _hasVPrev = true;
        Ground.Step(Terrain, s.Pos, s.Vel, _aMeas, g > 1e-2f ? -upW : -Vector3.UnitY, s.Belly, dt);
        float agl = Ground.HasGround ? Ground.Agl : s.Agl, aglHold = Ground.HasGround ? Ground.AglHold : s.Agl;
        if (aglHold >= 0f && g > 1e-2f)
        {
            _lastGround = s.Pos - (Vector3D)(upW * aglHold); _hasLastGround = true;
            // (a level hold engaged with no ground known: its datum now, the height held kept - the panel's number becomes real)
            if (_datumUnknown) { _upDatum = upW; _datum = _lastGround; _datumUnknown = false; }
        }
        float sink = -Vector3.Dot(s.Vel, upW);
        if (agl > s.Lim.FloorM + 2f) _floorArmed = true;
        bool floor = s.Lim.Protection && s.Lim.FloorM > 0f && _floorArmed && agl >= 0f
            && (agl < s.Lim.FloorM || (sink > 0.5f && agl / sink < 3f));
        float tti = s.Lim.LookAhead ? Ground.Tti : float.PositiveInfinity;   // (the look ahead switched off: the floor alone)
        bool aheadHover = s.Lim.Protection && (hover || height) && tti < TtiHover;

        // ── collision avoidance: the closest approach to each obstacle along the velocity now; a sidestep / pull-up through the
        //    mode's own command (an acceleration, a track's place, the hover's speed) - the protection below as for any goal ──
        bool sixGoal = ext && _ext.Kind == ApExternalKind.Track && !hover, liftGoal = ext && _ext.Kind == ApExternalKind.Lift, flyGoal = ext && (_ext.Kind == ApExternalKind.Accel || liftGoal);
        _avoidPush = Vector3.Zero;
        bool anyObs = (Obstacles != null && Obstacles.Count > 0) || HasTargetObstacle;
        if (s.Lim.Avoid && anyObs && (hover || height || sixGoal || flyGoal))
        {
            float aMax = flyGoal ? MathF.Max(1f, s.Lim.GLimit - 1f) * MathF.Max(g, 1f) * 0.6f
                : sixGoal ? (SixAccel > 0f ? SixAccel : 2f)
                : MathF.Max(g, 1f) * MathF.Tan(MathF.Min(s.Lim.MaxLeanDeg, 60f) * ApMath.Deg) * 0.8f;
            Avoid.React = flyGoal ? 1.2f : 0.3f;
            Avoid.Step(Obstacles, in TargetObstacle, HasTargetObstacle, s.Pos, s.Vel, s.Radius, s.Lim.AvoidS, aMax, TargetClearMax, upW, agl, dt);
        }
        else if (Avoid.Active) Avoid.Reset();
        HasTargetObstacle = false;
        bool avoid = Avoid.Active && Avoid.Accel > 0f;
        if (avoid)
        {
            prot |= ApProt.Avoid;
            float vOut = MathF.Min(Avoid.Accel * MathF.Max(Avoid.Tca, 1f), 40f);
            if (ext && _ext.Kind == ApExternalKind.Track)
            {
                // (the place moved out by the shortfall and a margin, its velocity by the push over the time to the approach)
                _ext.Position += (Vector3D)(Avoid.Dir * (Avoid.Short + 2f * ApAvoid.Margin));
                _ext.Velocity += Avoid.Dir * vOut;
            }
            else if (liftGoal) { _avoidPush = Avoid.Dir * Avoid.Accel; _ext.Blend = 0f; }   // (the lift vector: the push added to its acceleration below, the pull against it out)
            else if (flyGoal)
            {
                // (the goal's own pull against it taken out - a height hold would sink it back into the obstacle - the push added,
                //  and no aim blended in while it lasts: the nose follows the path out, not the target)
                float opp = Vector3.Dot(_ext.Accel, Avoid.Dir);
                if (opp < 0f) _ext.Accel -= Avoid.Dir * opp;
                _ext.Accel += Avoid.Dir * Avoid.Accel; _ext.Blend = 0f;
            }
        }

        Vector3 move = Vector3.Zero; bool setMove = false, sixAxis = false;
        if (hover || height)
        {
            Vector3 v = s.Vel; float vz = Vector3.Dot(v, upW); Vector3 vh = v - upW * vz;
            Vector3 keys = s.Piloted ? s.PilotMove : Vector3.Zero;
            float keyUp = Vector3.Dot(keys, _upL);
            Vector3 keyFlat = ApMath.Flat(keys, _upL);
            if (hover)
            {
                // ── horizontal: the speed wanted (keys, an external track, or back to the place), the acceleration, the lean ──
                Vector3 vCmd = Vector3.Zero;
                if (keyFlat.LengthSquared() > 0.0025f)
                {
                    Vector3 kw = ApMath.Flat(Vector3.Transform(keyFlat, s.Orient), upW);
                    vCmd = ApMath.ClampLen(kw * s.Lim.HoverSpeed, s.Lim.HoverSpeed);
                    _holdH = false; _brakeBest = float.MaxValue; _brakeStall = 0f;
                }
                else if (track)
                {
                    // (a moving place: its velocity fed forward, the gap closed as the hold's - at most the track's speed, or the hover's)
                    _hold = _ext.Position; _holdH = true;
                    Vector3 e = ApMath.Flat((Vector3)(_hold - s.Pos), upW);
                    Vector3 vff = ApMath.Flat(_ext.Velocity, upW);
                    float vLim = _ext.MaxSpeed > 0f ? MathF.Max(_ext.MaxSpeed, vff.Length() * 1.2f) : MathF.Max(s.Lim.HoverSpeed, vff.Length() * 1.5f);
                    vCmd = ApMath.ClampLen(vff + ApMath.ClampLen(e * kp, vLim), vLim);
                    FollowHeading(in s, upW, dt);
                }
                else if ((Goals & ApGoal.Position) != 0)
                {
                    if (!_holdH)
                    {
                        // (caught once stopped - or once the braking stalls: a steady push the speed loop alone cannot take out, a
                        //  single tail's, then the integral finds its lean from the place held)
                        float sp = vh.Length();
                        if (sp < _brakeBest * 0.98f) { _brakeBest = sp; _brakeStall = 0f; } else _brakeStall += dt;
                        if (sp < 0.3f || _brakeStall > 1.5f) CatchFlat(s.Pos, upW);
                    }
                    if (_holdH)
                    {
                        Vector3 e = ApMath.Flat((Vector3)(_hold - s.Pos), upW);
                        vCmd = ApMath.ClampLen(e * kp, s.Lim.HoverSpeed);
                    }
                }
                // (an obstacle: no closer toward it, the sidestep's speed added - a track's place was moved instead)
                if (avoid && !track)
                {
                    Vector3 dh = ApMath.Flat(Avoid.Dir, upW);
                    if (dh.LengthSquared() > 1e-4f)
                    {
                        dh = Vector3.Normalize(dh);
                        float into = -Vector3.Dot(vCmd, dh);
                        if (into > 0f) vCmd += dh * into;
                        vCmd += dh * MathF.Min(Avoid.Accel * MathF.Max(Avoid.Tca, 1f), s.Lim.HoverSpeed);
                    }
                }
                // (terrain ahead: no closer toward it - the speed toward the hit taken out; the climb below)
                if (aheadHover && vh.LengthSquared() > 0.01f)
                {
                    Vector3 toward = Vector3.Normalize(vh);
                    float into = Vector3.Dot(vCmd, toward);
                    if (into > 0f) vCmd -= toward * into;
                    prot |= ApProt.Terrain;
                }
                float tanMax = MathF.Tan(MathF.Min(s.Lim.MaxLeanDeg, 60f) * ApMath.Deg);
                // the lean the thrust can take and still hold the weight (10 % to spare), and none out of energy
                float cosNeed = upCap > 0f ? Math.Clamp(1.1f * weight / upCap, 0f, 1f) : 1f;
                float tanThr = cosNeed >= 1f ? 0f : MathF.Sqrt(1f - cosNeed * cosNeed) / cosNeed;
                float tanCap = MathF.Min(tanMax, tanThr);
                if (floor || aheadHover) tanCap = MathF.Min(tanCap, 0.5f * tanMax);
                float aMax = g * tanCap;
                // (the speed wanted, ramped: at most half the lean's acceleration - a key's step or a track's jump flown as a smooth
                //  change the lean can follow, its own acceleration fed forward - never a bang-bang of the lean against its limits)
                Vector3 dv = vCmd - _vRamp; float dvMax = RampShare * aMax * dt;
                Vector3 step = ApMath.ClampLen(dv, dvMax);
                _vRamp = ApMath.Flat(_vRamp + step, upW);
                Vector3 aFF = step / dt;
                Vector3 err = _vRamp - vh;
                Vector3 aCmd = err * kv + _aI + aFF;
                // the integral, held while the lean is at its limit and the error asks for more (no wind-up)
                // (a soft deadband: a residual the dampeners hold - a leaned axis they cancel - never winds it up)
                Vector3 aTry = aCmd;
                bool sat = aTry.Length() > aMax;
                float el = err.Length();
                Vector3 eI = ApMath.ClampLen(el > IntegralDeadband ? err * ((el - IntegralDeadband) / el) : Vector3.Zero, IntegralMaxError);
                // (and only in a steady hold - not while the keys fly it or it brakes to a stop: there the speed error is the move's,
                //  not a steady push to trim out, and on an axis the dampeners hold it would stay wound up)
                bool steady = keyFlat.LengthSquared() <= 0.0025f && (_holdH || (Goals & ApGoal.Position) == 0) && !(track && _ext.Blend > 0.3f);
                // (and only near the speed wanted: a transient - a track joined, a gust - is the speed loop's, not a trim to learn)
                if (steady && el < IntegralMaxError && !sat) _aI += eI * (ki * dt);
                _aI = ApMath.ClampLen(ApMath.Flat(_aI, upW), aMax);
                // (smoothed over half the tilt time - the attitude law could not follow faster, and a noisy speed reading
                //  shakes nothing); the keys across take it whole, the lean only up to its limits
                _aF += (aCmd - _aF) * MathF.Min(1f, dt / Math.Clamp(0.5f * tAtt, 0.05f, 0.3f));
                Vector3 aKeys = _aF;
                aCmd = ApMath.ClampLen(_aF, aMax);
                if (sat) prot |= tanThr < tanMax ? ApProt.Thrust : ApProt.Lean;
                AccelCmd = aCmd; VelCmd = vCmd;
                float am = aCmd.Length(); LeanRad = MathF.Atan(am / MathF.Max(g, 0.1f));
                // the level attitude on the heading held, leaned toward the acceleration
                Quaternion level = LevelOnly(in s, upW);
                if (am > 1e-4f)
                {
                    Vector3 k = Vector3.Normalize(Vector3.Cross(upW, aCmd / am));
                    want = Quaternion.Normalize(Quaternion.CreateFromAxisAngle(k, LeanRad) * level);
                }
                else want = level;
                // (aimed: the nose turned toward the aim on top of the lean - its push accepted while it lasts, the integral held)
                if (track) want = Aimed(want);
                attitude = true;

                // the movement keys across the lift axis (a VTOL's side thrusters; ineffective, and harmless, on a helicopter)
                Vector3 aL = Vector3.Transform(aKeys, Quaternion.Inverse(s.Orient));
                Vector3 side = ApMath.Flat(aL, _upL);
                if (side.LengthSquared() > SideKeyFrom * SideKeyFrom || (s.Piloted && keyFlat.LengthSquared() > 0.0025f))
                {
                    bool known = s.SidePos != Vector3.Zero || s.SideNeg != Vector3.Zero;
                    Vector3 ap = known ? s.SidePos : Vector3.One, an = known ? s.SideNeg : Vector3.One;
                    move += new Vector3(SideKey(side.X, s.ThrustPos.X, s.ThrustNeg.X, ap.X, an.X, s.Mass), SideKey(side.Y, s.ThrustPos.Y, s.ThrustNeg.Y, ap.Y, an.Y, s.Mass), SideKey(side.Z, s.ThrustPos.Z, s.ThrustNeg.Z, ap.Z, an.Z, s.Mass));
                    move = ApMath.Flat(move, _upL);
                    setMove = true;
                }
            }
            // ── vertical: the height (over the ground or along the gravity; caught where it stops), the climb rate, the up key ──
            float climb = 0f; bool vActive = false;
            bool overGround = HeightRef == ApHeightRef.Ground && aglHold >= 0f;
            if (_aglPending && aglHold >= 0f) { _holdAgl = aglHold; _aglPending = false; }
            if (MathF.Abs(keyUp) > 0.05f) { climb = keyUp * s.Lim.ClimbRate; _holdV = false; vActive = true; }
            else if (track)
            {
                float eh = (float)Vector3D.Dot(_ext.Position - s.Pos, (Vector3D)upW);
                climb = Math.Clamp(Vector3.Dot(_ext.Velocity, upW) + eh * 0.5f, -2f * s.Lim.ClimbRate, 2f * s.Lim.ClimbRate);
                vActive = MathF.Abs(eh) > 0.1f || MathF.Abs(vz - Vector3.Dot(_ext.Velocity, upW)) > 0.1f;
            }
            else if (height || hover)
            {
                if (!_holdV && MathF.Abs(vz) < 0.3f) { CatchHeight(s.Pos, upW); if (overGround) _holdAgl = aglHold; }   // (caught here; the place keeps its own)
                if (_holdV)
                {
                    bool overHeld = overGround && _holdAgl >= 0f && !_aglPending;
                    // (over the ground: the height held kept along the gravity too, at the ground under the ship now - so when the
                    //  ground goes out of sight the hold goes on at that height, no jump to where it was caught)
                    if (overHeld) { Vector3D u = upW; _hold += u * (Vector3D.Dot(s.Pos, u) - aglHold + _holdAgl - Vector3D.Dot(_hold, u)); }
                    // (over the ground: the filtered height under the ship; with no ground found, along the gravity)
                    float eh = overHeld ? _holdAgl - aglHold : (float)Vector3D.Dot(_hold - s.Pos, (Vector3D)upW);
                    NoGround = HeightRef == ApHeightRef.Ground ? !overGround : _datumUnknown;
                    // (over the ground: the terrain's rise under the ship fed forward - it follows the slope, not chases it)
                    float ff = overGround && Ground.HasGround ? Ground.GroundRate : 0f;
                    climb = Math.Clamp(eh * 0.5f + ff, -s.Lim.ClimbRate - MathF.Abs(ff), s.Lim.ClimbRate + MathF.Abs(ff));
                    vActive = MathF.Abs(eh) > 0.1f || MathF.Abs(vz) > 0.1f;
                    // (piloted and sinking far below it at full thrust: it cannot hold the height)
                    _cantHold = eh > CantHoldM && sink > 0.2f && lowEnergy ? _cantHold + dt : 0f;
                }
            }
            if (avoid && !track && Vector3.Dot(Avoid.Dir, upW) > 0.2f)
            { climb = MathF.Max(climb, Vector3.Dot(Avoid.Dir, upW) * MathF.Min(Avoid.Accel * MathF.Max(Avoid.Tca, 1f), 2f * s.Lim.ClimbRate)); vActive = true; }
            if (floor || aheadHover)
            {
                climb = MathF.Max(climb, MathF.Max(1f, s.Lim.ClimbRate) * (aheadHover ? 1.5f : 1f)); vActive = true;
                prot |= floor ? ApProt.Floor : ApProt.Terrain;
            }
            ClimbCmd = climb;
            float uUp = 0f;
            if (lowEnergy && s.Lim.Protection && (sink > 0.2f || climb > vz))
            {
                uUp = 1f; prot |= ApProt.LowEnergy; _uI = 0f;   // (out of energy: all the up thrust there is, no lean)
                if (hover) { want = LevelOnly(in s, upW); prot |= ApProt.Thrust; }
            }
            else if (vActive)
            {
                // the vertical speed error to the up key: up - the thrust left above the hold, down - about twice the weight's
                float ez = climb - vz;
                float per = ez > 0f ? MathF.Max(upCap - weight, 0.05f * weight) : 2f * weight + ApMath.Along(s.ThrustPos, s.ThrustNeg, -_upL);
                float u = s.Mass * VerticalGain * ez / per;
                // (the integral on the small errors only, and small: the steady share the dampeners miss, never a wind-up that
                //  pumps the height after a sink)
                _uI = Math.Clamp(_uI + s.Mass * VerticalGain * 0.5f * Math.Clamp(ez, -0.5f, 0.5f) / per * dt, -0.2f, 0.2f);
                uUp = Math.Clamp(u + _uI, -1f, 1f);
            }
            else _uI *= MathF.Max(0f, 1f - dt);   // (the dampeners hold it: the integral eases out)
            if (uUp != 0f) { move += _upL * uUp; setMove = true; }
            // (height hold alone: the pilot flies - their keys across the lift axis pass through as they are; only the up key is ours)
            if (!hover && setMove && s.Piloted) move += keyFlat;
            c.Dampeners = true; _dampWanted = true;
        }
        // ── the attitude goal, and the external source's acceleration / attitude (not a hover's track) ──
        float throttle = float.NaN;
        if (!hover)
        {
            if (ext && _ext.Kind == ApExternalKind.Track)
            {
                // SIX-AXIS: the place and its velocity flown on the movement keys (every side's real thrust), no lean; the attitude
                // level on the heading asked (or held), the aim on top
                want = SixAxis(in s, dt, ref move);
                setMove = true; attitude = true; sixAxis = true;
                if (s.Dampeners) { c.Dampeners = true; _dampWanted = true; }
            }
            else if (ext && _ext.Kind == ApExternalKind.Accel)
            {
                Vector3 need = _ext.Accel - gTrue;   // (the specific force wanted: the gravity compensated)
                if (LiftFlight(in s, need, _ext.Accel, out Quaternion q, out float thr)) { want = Aimed(q); throttle = float.IsNaN(_ext.Throttle) ? thr : Math.Clamp(_ext.Throttle, 0f, 1f); }
                else
                {
                    // (no lift to fly it with - slow, no air, no stall sweep: the main thrust axis along it, the throttle that force over
                    //  the axis' thrust - a VTOL, a rocket)
                    Vector3 main = ApMath.MainAxis(s.ThrustPos, s.ThrustNeg, out float tmax);
                    want = Aimed(Point(s.Orient, main, need));
                    throttle = float.IsNaN(_ext.Throttle) ? (tmax > 1f ? Math.Clamp(need.Length() * s.Mass / tmax, 0f, 1f) : 0f) : Math.Clamp(_ext.Throttle, 0f, 1f);
                }
                AccelCmd = _ext.Accel; attitude = true;
            }
            else if (ext && _ext.Kind == ApExternalKind.Lift)
            {
                // THE LIFT VECTOR: the lift along the goal's direction (across the path through the air) at its load - the
                // collision avoidance's push added (its pull against it out) - flown on the wings, the direction kept when clipped
                Vector3 vW = s.AirVel.LengthSquared() > 1f ? Vector3.Transform(Vector3.Normalize(s.AirVel), s.Orient)
                    : s.Vel.LengthSquared() > 1f ? Vector3.Normalize(s.Vel) : Vector3.Transform(ApMath.MainAxis(s.ThrustPos, s.ThrustNeg, out _), s.Orient);
                float gT = gTrue.Length();
                float n = float.IsFinite(_ext.Load) ? MathF.Max(0f, _ext.Load) : Perf.Valid ? Perf.NInst : s.Lim.GLimit;
                Vector3 need = ApLift.Across(vW, _ext.Lift) * (n * gT);
                if (_avoidPush != Vector3.Zero)
                {
                    Vector3 pd = Vector3.Normalize(_avoidPush); float opp = Vector3.Dot(need, pd);
                    if (opp < 0f) need -= pd * opp;
                    need = ApMath.Flat(need + _avoidPush, vW);
                }
                if (LiftFlight(in s, need, need + gTrue, out Quaternion q, out float thr, keepDir: true)) { want = Aimed(q); throttle = float.IsNaN(_ext.Throttle) ? thr : Math.Clamp(_ext.Throttle, 0f, 1f); }
                else
                {
                    // (no lift to fly with - slow, no air, a rocket: the main thrust tilted off the path to give the push across it,
                    //  the rest along the path - its throttle the goal's, full when not given)
                    Vector3 main = ApMath.MainAxis(s.ThrustPos, s.ThrustNeg, out float tmax);
                    float thrUse = float.IsNaN(_ext.Throttle) ? 1f : Math.Clamp(_ext.Throttle, 0f, 1f);
                    float aT = tmax > 1f ? thrUse * tmax / MathF.Max(s.Mass, 1f) : 0f, aN = MathF.Min(need.Length(), aT);
                    Vector3 f = need.LengthSquared() > 1e-6f ? Vector3.Normalize(need) * aN + vW * MathF.Sqrt(MathF.Max(aT * aT - aN * aN, 0f)) : vW;
                    want = Aimed(Point(s.Orient, main, f)); throttle = thrUse;
                }
                AccelCmd = need + gTrue; attitude = true;
            }
            else if (ext && _ext.Kind == ApExternalKind.Attitude && _ext.Attitude.IsValidAndRotationIsNormalized())
            {
                want = Aimed(Quaternion.Normalize(_ext.Attitude)); attitude = true;
                if (!float.IsNaN(_ext.Throttle)) throttle = Math.Clamp(_ext.Throttle, 0f, 1f);
            }
            else if ((Goals & ApGoal.Attitude) != 0) { want = _att; attitude = true; }
        }

        // ── protection: the attitude step governed; terrain ahead; low energy / overspeed on the main thrust ──
        if (attitude)
        {
            if (!hover && s.Lim.Protection)
            {
                // TERRAIN AHEAD (flying): within the time the turn takes (the reaction, then the path turned through a radian at
                // the g it has) - pull up: the nose raised about the path's own axis, the governor below cuts it to what the stall,
                // g and the loss of control allow; full thrust
                float speed = s.Vel.Length();
                float nAvail = s.StallFresh && s.StallG > 0f ? MathF.Min(s.StallG, s.Lim.GLimit) : s.Lim.GLimit;
                float tPull = PullReact + speed / (gTrue.Length() * MathF.Max(nAvail - 1f, 0.5f) + 1e-3f);
                // (latched: once pulling, it pulls until the path is clear by half as much again, for at least PullHold s - a look
                //  that flickers clear as the nose comes up never lets it sag back into the hill)
                bool pulling = _pullHold > 0f;
                if (speed > 5f && tti < tPull) _pullHold = PullHold;
                else if (pulling && tti < 1.5f * tPull) _pullHold = MathF.Max(_pullHold, dt);
                _pullHold -= dt;
                if (speed > 5f && (pulling || tti < tPull))
                {
                    Vector3 k = Vector3.Cross(s.Vel, -Vector3.Normalize(gTrue.LengthSquared() > 1e-4f ? gTrue : -Vector3.UnitY));
                    if (k.LengthSquared() > 1e-6f) want = Quaternion.Normalize(Quaternion.CreateFromAxisAngle(Vector3.Normalize(k), PullUpDeg * ApMath.Deg) * s.Orient);
                    throttle = 1f; prot |= ApProt.Terrain;
                }
            }
            // (the path's own turn: the measured acceleration across the velocity over the speed)
            float vs = s.Vel.Length();
            Vector3 pathRate = vs > 5f ? Vector3.Cross(s.Vel, ApMath.Flat(_aMeas, s.Vel / vs)) / (vs * vs) : Vector3.Zero;
            Quaternion gov = Guard.Govern(in s, want, ref prot, pathRate);
            c.Probe = want; c.HasProbe = true;
            want = gov;
            if (!hover && !sixAxis)
            {
                Vector3 main = ApMath.MainAxis(s.ThrustPos, s.ThrustNeg, out _);
                if (!float.IsNaN(throttle)) { move = main * throttle; setMove = true; }
                if (s.Lim.Protection)
                {
                    float over = s.SpeedLimit > 0f ? s.Vel.Length() - (s.SpeedLimit - OverspeedBand) : -1f;
                    if (over > 0f)
                    {
                        // (the throttle eased off over the last OverspeedBand m/s to the limit - the keys scaled, shut past it)
                        float kk = Math.Clamp(1f - over / OverspeedBand, 0f, 1f);
                        move = (setMove ? move : s.Piloted ? s.PilotMove : Vector3.Zero) * kk; setMove = true; prot |= ApProt.Overspeed;
                    }
                    else if (s.StallFresh && s.StallState >= 1 && EnvelopeGuard.AirLimits(in s)) { move = main; setMove = true; prot |= ApProt.LowEnergy; }
                }
            }
            // (the target slewed: at most SlewHover / SlewFlying deg/s from the last one - a mode change or a pull-up ending never
            //  snaps the ship round; the attitude law flies the rest)
            if (_hasLastTarget)
            {
                float maxStep = (hover || sixAxis ? SlewHover : SlewFlying) * ApMath.Deg * dt, ang = hover ? 0f : ApMath.Angle(_lastTarget, want);   // (a hover's lean is smoothed already)
                if (ang > maxStep && ang > 1e-6f) want = Quaternion.Normalize(Quaternion.Slerp(_lastTarget, want, maxStep / ang));
            }
            _lastTarget = want; _hasLastTarget = true;
            c.Flying = true; c.Target = want;
            // the watch: far from its target for long - not in control
            _upset = s.AttErrDeg > UpsetDeg ? _upset + dt : 0f;
            if (_upset > UpsetSeconds) { Disengage(ApReason.Upset); c = default; UpdateReadout(); return; }
        }
        if (!attitude) _hasLastTarget = false;
        if (s.Piloted && _cantHold > CantHoldSeconds) { Disengage(ApReason.CantHoldHeight); c = default; UpdateReadout(); return; }
        if (setMove) { c.SetMove = true; c.Move = Vector3.Clamp(move, -Vector3.One, Vector3.One); }
        c.Prot = prot; Prot = prot;
        UpdateReadout();
    }

    /// <summary>
    /// The attitude and throttle that fly the specific force 'need' (world m/s2, the gravity's hold in) with the ship's LIFT: the
    /// lift rolled about the flight path onto need's part across it, the angle of attack whose lift gives that part (the ship's own
    /// lift curve - PerfSamples, the stall sweep's), the thrust for need's part along the path plus the drag at that angle. The
    /// protection then cuts it (stall, g, the predicted loss of control). False: no lift to fly with (slow, no air, no sweep, or
    /// the best lift short of the weight) - the caller points the thrust instead.
    /// </summary>
    bool LiftFlight(in ApSense s, Vector3 need, Vector3 accel, out Quaternion q, out float throttle, bool keepDir = false)
    {
        q = s.Orient; throttle = 0f;
        var p = s.Perf;
        if (p == null || p.Count < 4 || !EnvelopeGuard.AirLimits(in s) || !Perf.Valid || Perf.NAtAlphaMax < 1.1f) return false;
        Vector3 vL = Vector3.Normalize(s.AirVel), vW = Vector3.Transform(vL, s.Orient);
        float along = Vector3.Dot(need, vW);
        Vector3 across = need - vW * along;
        // (more than the lift can give - the protection's load, NInst: the gravity's hold kept whole and the asked part scaled to
        //  what is left - an over-asked turn is the tightest level one, never a dive)
        float aMax = Perf.NInst * (s.GravityTrue.LengthSquared() > 0f ? s.GravityTrue : s.Gravity).Length();
        if (!keepDir && across.Length() > aMax)   // (a lift vector keeps its direction: the protection clips its load, LiftFlight never turns it)
        {
            Vector3 hold = ApMath.Flat(need - accel, vW), ask = across - hold;
            float a2 = ask.LengthSquared(), b = 2f * Vector3.Dot(hold, ask), c = hold.LengthSquared() - aMax * aMax;
            float kk = a2 > 1e-6f ? Math.Clamp((-b + MathF.Sqrt(MathF.Max(b * b - 4f * a2 * c, 0f))) / (2f * a2), 0f, 1f) : 0f;
            across = hold + ask * kk; accel *= kk;
        }
        float aAcross = across.Length();
        // roll: the lift's way now (world) onto the across part's, about the flight path
        Vector3 liftW = ApMath.Flat(Vector3.Transform(s.LiftDir, s.Orient), vW);
        Quaternion qr = s.Orient;
        if (aAcross > 0.05f && liftW.LengthSquared() > 1e-6f)
        {
            liftW = Vector3.Normalize(liftW); Vector3 t = across / aAcross;
            float ang = MathF.Atan2(Vector3.Dot(Vector3.Cross(liftW, t), vW), Vector3.Dot(liftW, t));
            qr = Quaternion.Normalize(Quaternion.CreateFromAxisAngle(vW, ang) * s.Orient);
        }
        // pitch: the angle whose lift is the across part (from the samples' flow, less the turn since), about the lift's axis
        // (rolling about the path leaves the flow and the lift's way in the ship's own frame as they were)
        float since = EnvelopeGuard.Since(in s);
        float th = FlightPerf.ThetaFor(p, s.Mass * aAcross, p.Theta[p.Count - 1]);
        Vector3 k = Vector3.Cross(vL, s.LiftDir);
        if (k.LengthSquared() < 1e-8f) return false;
        k = Vector3.Normalize(k);
        if (EnvelopeGuard.Pull(in s, Quaternion.Normalize(s.Orient * Quaternion.CreateFromAxisAngle(k, 0.01f))) < 0f) k = -k;
        q = Quaternion.Normalize(qr * Quaternion.CreateFromAxisAngle(k, th - since));
        // (led by the path's own turn: the attitude law closes an error at ThrustAllocator.LinearGain per second and is given no
        //  target rate - a target turning with the path would lag it by rate / gain, an angle of attack short of the one asked)
        float speed = s.AirVel.Length();
        Vector3 wPath = Vector3.Cross(vW, ApMath.Flat(accel, vW)) / MathF.Max(speed, 1f);
        float wl = wPath.Length();
        if (wl > 1e-4f) q = Quaternion.Normalize(Quaternion.CreateFromAxisAngle(wPath / wl, wl / ThrustAllocator.LinearGain) * q);
        float thrust = ApMath.Along(s.ThrustPos, s.ThrustNeg, vL);
        throttle = thrust > 1f ? Math.Clamp((s.Mass * along + FlightPerf.DragAt(p, th)) / thrust, 0f, 1f) : 0f;
        return true;
    }

    /// <summary>The attitude q with the external goal's aim on top: its axis turned toward its direction, Blend of the way.</summary>
    Quaternion Aimed(Quaternion q)
    {
        float b = _ext.Blend;
        if (!(b > 0f) || _ext.AimDir.LengthSquared() < 1e-6f || _ext.AimAxis.LengthSquared() < 0.5f) return q;
        Quaternion a = Point(q, Vector3.Normalize(_ext.AimAxis), _ext.AimDir);
        return b >= 1f ? a : Quaternion.Normalize(Quaternion.Slerp(q, a, b));
    }

    /// <summary>A track's heading: the frame's world heading turned toward the one asked, at most TrackYawRate.</summary>
    void FollowHeading(in ApSense s, Vector3 upW, float dt)
    {
        Vector3 want = ApMath.Flat(_ext.Heading, upW);
        if (want.LengthSquared() < 1e-6f) return;
        want = Vector3.Normalize(want);
        Vector3 cur = ApMath.Flat(_headW, upW);
        if (cur.LengthSquared() < 1e-6f) { _headW = want; return; }
        cur = Vector3.Normalize(cur);
        float ang = MathF.Atan2(Vector3.Dot(Vector3.Cross(cur, want), upW), Vector3.Dot(cur, want));
        float step = Math.Clamp(ang, -TrackYawRate * ApMath.Deg * dt, TrackYawRate * ApMath.Deg * dt);
        _headW = Vector3.Normalize(Vector3.Transform(cur, Quaternion.CreateFromAxisAngle(upW, step)));
    }

    /// <summary>SIX-AXIS translation (engine-free): the place (ApExternal.Position) and its velocity / acceleration fed forward, the
    /// gap closed no faster than the ship can brake (the weakest side's acceleration, SixShare of it), the velocity error an
    /// acceleration, the force for it in the ship's frame - the dampeners' hold of the weight in when they are on, the weight held
    /// by the keys when off - and per axis the movement key that gives that force from the game's own thrust law. The attitude:
    /// level on the heading (gravity) or the one held, the aim on top.</summary>
    Quaternion SixAxis(in ApSense s, float dt, ref Vector3 move)
    {
        Quaternion inv = Quaternion.Inverse(s.Orient);
        Vector3 gL = Vector3.Transform(s.Gravity, inv);
        Vector3 hold = s.Dampeners ? -gL * s.Mass : Vector3.Zero;
        // the weakest side's acceleration left over its share of the weight
        float aMin = float.MaxValue;
        for (int k = 0; k < 6; k++)
        {
            float sg = k < 3 ? 1f : -1f;
            float cap = Comp(k < 3 ? s.ThrustPos : s.ThrustNeg, k % 3), wt = MathF.Max(0f, -Comp(gL, k % 3) * sg) * s.Mass;
            aMin = MathF.Min(aMin, (cap - wt) / MathF.Max(s.Mass, 1f));
        }
        float aUse = MathF.Max(0.3f, SixShare * aMin); SixAccel = aUse;
        float kv = Math.Clamp(aUse / 4f, 0.6f, 3f), kp = 0.3f * kv;
        Vector3 e = (Vector3)(_ext.Position - s.Pos);
        float el = e.Length(), vMax = _ext.MaxSpeed > 0f ? _ext.MaxSpeed : s.Lim.HoverSpeed;
        float close = MathF.Min(vMax, MathF.Min(kp * el, MathF.Sqrt(aUse * el)));
        Vector3 vCmd = _ext.Velocity + (el > 1e-3f ? e * (close / el) : Vector3.Zero);
        Vector3 aCmd = ApMath.ClampLen((vCmd - s.Vel) * kv + _ext.Accel, 2f * aUse);
        AccelCmd = aCmd; VelCmd = vCmd;
        Vector3 f = aCmd * s.Mass;
        if (!s.Dampeners) f -= s.Gravity * s.Mass;   // (no dampeners: the weight is the keys' to hold)
        Vector3 fl = Vector3.Transform(f, inv);
        move = new Vector3(KeyFor(fl.X, hold.X, s.ThrustPos.X, s.ThrustNeg.X), KeyFor(fl.Y, hold.Y, s.ThrustPos.Y, s.ThrustNeg.Y), KeyFor(fl.Z, hold.Z, s.ThrustPos.Z, s.ThrustNeg.Z));
        // the attitude: level on the heading (in gravity), else the one held - the aim on top
        Quaternion q = _sixAtt;
        float g = s.Gravity.Length();
        if (g > 1e-2f)
        {
            Vector3 upW = -s.Gravity / g;
            FollowHeading(in s, upW, dt);
            Vector3 head = ApMath.Flat(_headW, upW);
            if (head.LengthSquared() > 1e-6f) q = ApMath.Frame(_sixUpL, _sixNoseL, upW, Vector3.Normalize(head));
        }
        else if (_ext.Heading.LengthSquared() > 1e-6f) q = Point(_sixAtt, _sixNoseL, _ext.Heading);
        return Aimed(q);
    }

    /// <summary>The movement key (-1..1) on one local axis that gives the extra force f (N) beyond the hold h (N, the dampeners'
    /// gravity hold along +axis) from the side maxima - the game's thrust law: a key along the hold adds key x (max - hold); against
    /// it the hold shrinks by the key too. Kept past the input deadzone (SixKeyMin) so the axis stays the keys'.</summary>
    static float KeyFor(float f, float h, float cp, float cn)
    {
        float u;
        if (f >= 0f) { float den = cp - h - MathF.Min(h, 0f); u = den > 1f ? f / den : (f > 0f ? 1f : 0f); }
        else { float den = cn + h + MathF.Max(h, 0f); u = den > 1f ? f / den : -1f; }
        u = Math.Clamp(u, -1f, 1f);
        if (MathF.Abs(u) < SixKeyMin) u = u < 0f ? -SixKeyMin : SixKeyMin;
        return u;
    }

    static float Comp(Vector3 v, int i) => i == 0 ? v.X : i == 1 ? v.Y : v.Z;

    /// <summary>The local unit axis (+-X, Y, Z) nearest a direction.</summary>
    static Vector3 Nearest(Vector3 d)
    {
        float ax = MathF.Abs(d.X), ay = MathF.Abs(d.Y), az = MathF.Abs(d.Z);
        if (ax >= ay && ax >= az) return new Vector3(d.X >= 0f ? 1f : -1f, 0f, 0f);
        if (ay >= az) return new Vector3(0f, d.Y >= 0f ? 1f : -1f, 0f);
        return new Vector3(0f, 0f, d.Z >= 0f ? 1f : -1f);
    }

    /// <summary>The attitude turned the least so the local axis points along the world direction d (roll about it kept).</summary>
    static Quaternion Point(
Quaternion q, Vector3 axisLocal, Vector3 d)
    {
        if (d.LengthSquared() < 1e-6f) return q;
        Vector3 u = Vector3.Transform(axisLocal, q), b = Vector3.Normalize(d);
        Vector3 k = Vector3.Cross(u, b); float s = k.Length(), cth = Math.Clamp(Vector3.Dot(u, b), -1f, 1f);
        if (s < 1e-6f) return cth > 0f ? q : Quaternion.Normalize(Quaternion.CreateFromAxisAngle(Vector3.Normalize(ApMath.Flat(MathF.Abs(u.X) < 0.9f ? Vector3.UnitX : Vector3.UnitY, u)), MathF.PI) * q);
        return Quaternion.Normalize(Quaternion.CreateFromAxisAngle(k / s, MathF.Atan2(s, cth)) * q);
    }

    /// <summary>The held place caught here across the vertical (its height kept, when held).</summary>
    void CatchFlat(Vector3D p, Vector3 upW)
    {
        Vector3D u = upW;
        _hold = _holdV ? p + u * Vector3D.Dot(_hold - p, u) : p;
        _holdH = true;
    }

    /// <summary>The held height caught here (its place across kept, when held).</summary>
    void CatchHeight(Vector3D p, Vector3 upW)
    {
        Vector3D u = upW;
        _hold = _holdH ? _hold + u * Vector3D.Dot(p - _hold, u) : p;
        _holdV = true;
    }

    /// <summary>The level attitude on the heading held (the lift axis up).</summary>
    Quaternion LevelOnly(in ApSense s, Vector3 upW)
    {
        Vector3 head = ApMath.Flat(_headW, upW);
        if (head.LengthSquared() < 1e-6f) head = ApMath.Flat(Vector3.Transform(_fwdL, s.Orient), upW);
        if (head.LengthSquared() < 1e-8f) head = ApMath.Flat(Vector3.UnitX, upW);
        return ApMath.Frame(_upL, _fwdL, upW, Vector3.Normalize(head));
    }

    /// <summary>A movement key across the lift axis for this acceleration (local m/s2) on one axis: the side's maximum over the mass.</summary>
    static float SideKey(float accel, float pos, float neg, float authPos, float authNeg, float mass)
    {
        if (MathF.Abs(accel) < 0.05f || (accel > 0f ? authPos : authNeg) < MinSideAuthority) return 0f;   // (no clean push that way: the lean's job)
        // (the side asked for; none booked there - a reversible thruster booked the other way turns at once to a key, and a
        //  key toward a side with no thrusters gives nothing)
        float cap = accel > 0f ? pos : neg;
        if (cap < 1f) cap = MathF.Max(pos, neg);
        return cap > 1f ? Math.Clamp(accel * mass / cap, -1f, 1f) : 0f;
    }

    void UpdateReadout()
    {
        int key = (int)Goals | ((int)Prot << 8) | (HeightRef == ApHeightRef.Ground ? 1 << 24 : 0);
        if (key == _readKey) return;
        _readKey = key;
        Readout = ApText.Readout(Goals, Prot, HeightRef);
    }
}

/// <summary>The autopilot's words (engine-free): the readout and the reasons.</summary>
public static class ApText
{
    static readonly System.Collections.Generic.Dictionary<int, string> _readouts = new();

    /// <summary>"AP HOVER POS AGL · LEAN LIMIT" ("" when nothing is held). Cached per combination (a handful in a session).</summary>
    public static string Readout(ApGoal goals, ApProt prot, ApHeightRef href = ApHeightRef.Ground)
    {
        if (goals == ApGoal.None) return "";
        int key = (int)goals | ((int)prot << 8) | (href == ApHeightRef.Ground ? 1 << 24 : 0);
        lock (_readouts)
        {
            if (_readouts.TryGetValue(key, out var s)) return s;
            var sb = new System.Text.StringBuilder("AP");
            if ((goals & ApGoal.Hover) != 0) sb.Append(" HOVER");
            if ((goals & ApGoal.Position) != 0) sb.Append(" POS");
            if ((goals & ApGoal.Height) != 0) sb.Append(href == ApHeightRef.Ground ? " AGL" : " ALT");
            if ((goals & ApGoal.Attitude) != 0) sb.Append(" ATT");
            if ((goals & ApGoal.External) != 0) sb.Append(" EXT");
            string p = Prot(prot);
            if (p.Length > 0) sb.Append(" · ").Append(p);
            s = sb.ToString();
            _readouts[key] = s;
            return s;
        }
    }

    /// <summary>The protections in force, most important first ("" none).</summary>
    public static string Prot(ApProt p)
    {
        if ((p & ApProt.Terrain) != 0) return "TERRAIN";
        if ((p & ApProt.Avoid) != 0) return "AVOID";
        if ((p & ApProt.Floor) != 0) return "FLOOR";
        if ((p & ApProt.LowEnergy) != 0) return "FULL THRUST";
        if ((p & ApProt.Loac) != 0) return "CONTROL LIMIT";
        if ((p & ApProt.Alpha) != 0) return "STALL LIMIT";
        if ((p & ApProt.GLimit) != 0) return "G LIMIT";
        if ((p & ApProt.Overspeed) != 0) return "OVERSPEED";
        if ((p & ApProt.Thrust) != 0) return "THRUST LIMIT";
        if ((p & ApProt.Lean) != 0) return "LEAN LIMIT";
        return "";
    }

    /// <summary>Why it let go or refused - the reason alone, as the player reads it ("" none).</summary>
    public static string Say(ApReason r) => r switch
    {
        ApReason.TurnedOff => "turned off",
        ApReason.Steered => "you steered",
        ApReason.DampenersOff => "the dampeners were switched off",
        ApReason.NeedsDampeners => "switch the dampeners on first",
        ApReason.NotAllowed => "not allowed in this world",
        ApReason.NoGravity => "no gravity to hover in",
        ApReason.NoLift => "not enough upward thrust to hover",
        ApReason.TooFast => "too fast to hover - slow down first",
        ApReason.Upset => "lost control of the attitude",
        ApReason.LeftSeat => "you left the seat",
        ApReason.CantHoldHeight => "can't hold the height - thrust short",
        ApReason.NoAttitude => "nothing to steer with",
        ApReason.NoSource => "no goal source",
        ApReason.GoalDone => "goal reached",
        ApReason.BlockOff => "the autopilot block is off, unpowered or damaged",
        ApReason.TooSlow => "too slow to fly on the wings - needs airspeed",
        ApReason.NoAir => "no air to fly on the wings",
        ApReason.NoWaypoints => "no waypoints - type GPS names or coordinates in Route",
        ApReason.NoTranslation => "not enough thrust across to fly six-axis",
        ApReason.WrongStyle => "the flight style set does not fit this ship",
        ApReason.NoResponse => "the ship never took it up - no working thrusters or gyros to fly it",
        ApReason.NotImplemented => "this behaviour is not written yet",
        ApReason.Bingo => "bingo - fuel or damage",
        ApReason.TimedOut => "the behaviour timed out",
        ApReason.LostTarget => "the target was lost",

        _ => "",
    };

    /// <summary>The toast for a refusal / a let-go / an engage.</summary>
    public static string Refused(ApReason r) => "Autopilot: can't engage - " + Say(r);
    /// <param name="still">The goals still held after the let-go (steering drops the hover, the height stays): said, not "off".</param>
    public static string Off(ApReason r, ApGoal still = ApGoal.None)
    {
        if ((still & (ApGoal.Height | ApGoal.Hover)) == ApGoal.Height)   // (in game 2026-10-19: "Autopilot off - you steered" with height hold on)
            return "Hover off - " + Say(r) + " (height hold on)";
        if (still != ApGoal.None) return "Autopilot: " + Say(r) + " (still on)";
        return r == ApReason.TurnedOff ? "Autopilot off" : "Autopilot off - " + Say(r);
    }
    public static string Engaged(ApGoal goals, ApHeightRef href = ApHeightRef.Ground)
        => (goals & ApGoal.Hover) != 0 ? ((goals & ApGoal.Height) != 0 ? (href == ApHeightRef.Ground ? "Autopilot: hover hold (place, height over the ground)" : "Autopilot: hover hold (place, height)") : "Autopilot: hover hold (place)")
         : (goals & ApGoal.Height) != 0 ? (href == ApHeightRef.Ground ? "Autopilot: height hold over the ground" : "Autopilot: height hold") : "Autopilot on";
}
