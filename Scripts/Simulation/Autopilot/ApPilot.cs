#pragma warning disable CS0649, CS8632, CS1591, CS1573, CS1570, CS1574, CS0419, CS1734
using System;
namespace AeroMod;

/// <summary>
/// THE FLIGHT PILOT (engine-free; an external goal source on the flight director's core - IApGoalSource): the in-air modes, the
/// flight styles and the behaviours. Each tick: the instruments (the altimeter, the compass, the target's track) -> the MODE's goal
/// (FLIGHT HOLD: heading or wings level, altitude with a climb rate, speed; NAVIGATE: the route's legs on L1, a loiter at its end
/// or round again, the return point; ENGAGE: the arbiter's behaviour on the target, else the route / a loiter) -> the terrain kept
/// under it (MinAglM over the ground below and ahead) -> the STYLE's executor: AIRPLANE an acceleration (L1 across, TECS's
/// vertical) flown on the lift with TECS's throttle; HELICOPTER a moving place for the hover's lean (tilt movement: the lift axis
/// leant toward the motion, its lean and speed limits); SIX-AXIS the same place flown on the thrusters, the nose free -> the FACING
/// (2026-10-20, a desire: the panel's Facing - the nose on the lead, a side on the target - blended into the movement's attitude by
/// its Aim Priority within the style's caps; movement wins where they conflict, the protection still clips) -> jinking. Every flying number comes
/// from the ship (FlightPerf's load and drag, the thrust, the bank limit); the panel gives only tactical ones.
/// </summary>
public sealed class ApPilot : IApGoalSource
{
    /// <summary>The panel it flies (the link copies the block's each tick).</summary>
    public ApPanel Cfg = ApPanel.Defaults;
    public ApMode Mode { get; private set; }
    public ApStyle Style { get; private set; }
    /// <summary>The target's source (the grid's targeting in game; a SimTarget offline) and the track kept of it.</summary>
    public IApTargetSource Targets;
    public readonly ApTargetTrack T = new();
    public readonly ApAltimeter Alt = new();
    public readonly ApTecs Tecs = new();
    public readonly ApBehaviours Bh = new();
    public readonly ApArbiterBh Arb = new();
    /// <summary>The flight director it feeds (its terrain watch, its flight performance).</summary>
    public FlightDirector Fd;
    /// <summary>(2026-10-19) The built-in formation, and the two combat SLOTS - the author's files (ApDogfight.cs, ApRocketGuidance.cs):
    /// each step the context (Slot - everything they get) is filled, their Step asked, its goal flown. SlotText: the active slot's
    /// own status words (null none).</summary>
    public readonly ApFormation Formation = new();
    public readonly ApDogfight Dogfight = new();
    public readonly ApRocketGuidance Rocket = new();
    public readonly ApSlotCtx Slot = new();
    public string SlotText; public ApSlotOut SlotOut;
    /// <summary>A slot's saved state to write back to the panel (DfSaved / RgSaved; the link copies it).</summary>
    public bool SlotSavedChanged; public string DfSavedOut, RgSavedOut;
    ApBehaviour _slotWas = (ApBehaviour)255; float _slotTime, _slotSaveIn; bool _dfBan, _rgBan;
    public static bool IsSlot(ApBehaviour b) => b == ApBehaviour.Dogfight || b == ApBehaviour.RocketGuidance;
    /// <summary>A slot whose logic is there (its Implemented).</summary>
    public static bool SlotImplemented(ApBehaviour b) => b == ApBehaviour.Dogfight ? ApDogfight.Implemented : b == ApBehaviour.RocketGuidance ? ApRocketGuidance.Implemented : true;

    readonly List<ApRoute.Point> _route = new();
    public int Wp, WpCount; Vector3D _wpFrom; bool _loiter; Vector3D _loiterAt;
    Vector3D _home, _station; bool _hasStation;
    Vector3 _upL = Vector3.UnitY, _noseL = -Vector3.UnitZ;
    Vector3 _upW = Vector3.UnitY; bool _terrainUp;
    /// <summary>The last ground seen, as a height along the up (world): the height over the ground while none is in sight (Agl).</summary>
    double _gUp; bool _hasG;
    /// <summary>(2026-10-21) Engaged under this height over the ground (m), or at rest with no ground in sight: on the ground - the
    /// height caught is a climb off it (TakeoffM, at least the terrain minimum), not the 0 m it sits at.</summary>
    public const float GroundedM = 3f, RestSpeed = 0.5f, TakeoffM = 20f;

    float _cruise, _time, _jinkIn, _gunEl;
 int _jx = 1, _jy = 1; uint _rng = 2463534242u;

    // ── what it caught at the engage (the link writes it to the panel) ──
    public bool CapturedHolds; public float CapHeading, CapAlt, CapSpeed;
    public bool CapturedHome; public Vector3D Home => _home;

    // ── the readouts (the status line, the API, the tests) ──
    public ApBehaviour ActiveBh; public bool Recovering, Loitering;
    public float AimErrDeg = float.NaN, Tof; public bool AimOn, InCone, InRange;
    /// <summary>(2026-10-20) The facing flown this tick (Travel: none - AimOn then means a slot's aim report only).</summary>
    public ApFacing FacingNow;
    public float CrossTrack, RadiusErr, WpDist, Eta, HeightNow, HeightWanted = float.NaN, TurnR, LeadBlend;
    public float HeadingNow, SpeedNow;
    /// <summary>Jinks begun (each a new pseudo-random direction) - diagnostics.</summary>
    public int Jinks;
    public ApNavGoal Goal;

    /// <summary>The panel now (each tick): a return asked or taken back starts the route's legs again from here.</summary>
    public void SyncCfg(in ApPanel p, in ApSense s)
    {
        bool ret = Cfg.ReturnHome;
        Cfg = p;
        if (p.ReturnHome != ret) { Wp = 0; _loiter = false; _wpFrom = s.Pos; }
    }

    /// <summary>What it shows (the status line's values; no allocation).</summary>
    public void View(out ApPilotView v, ApProt prot)
    {
        v = default;
        v.Mode = Mode; v.Style = Style; v.Behaviour = ActiveBh; v.Phase = Bh.Phase; v.Loitering = Loitering; v.Recovering = Recovering; v.Returning = Cfg.ReturnHome;
        v.HasTarget = T.Valid && Mode == ApMode.Engage; v.TargetName = T.Name; v.Range = T.Range;
        v.BearingDeg = T.Valid ? ApCompass.Diff(ApCompass.Heading(T.Los, _upW), HeadingNow) : 0f;
        v.AimOn = AimOn; v.InCone = InCone; v.InRange = InRange; v.AimErrDeg = AimErrDeg; v.Facing = FacingNow;
        v.Wp = Wp; v.WpCount = Cfg.ReturnHome ? 1 : _route.Count; v.WpName = WpName; v.WpDist = WpDist; v.Eta = Eta;
        v.HeadingNow = HeadingNow; v.HeightNow = HeightNow; v.SpeedNow = SpeedNow;
        v.HeadingOn = Cfg.HeadingOn; v.AltOn = Cfg.AltOn; v.SpeedOn = Cfg.SpeedOn;
        v.HeadingWant = Cfg.HeadingDeg; v.HeightWant = Cfg.AltM; v.SpeedWant = Cfg.SpeedMs > 0f ? Cfg.SpeedMs : _cruise;
        v.Prot = prot;
        v.SlotText = IsSlot(ActiveBh) ? SlotText : null; v.FormErr = Formation.Err; v.FormAlong = Formation.Along; v.FormOver = Formation.Overshoot; v.LeaderSpeed = T.Motion.Valid ? T.Motion.Speed : T.Vel.Length();
    }

    /// <summary>The flight director's goals a style flies with: the hover's lean for a helicopter, the external goal alone otherwise.</summary>
    public static ApGoal GoalsFor(ApStyle st) => st == ApStyle.Helicopter ? ApGoal.Hover | ApGoal.External : ApGoal.External;

    /// <summary>The style it would fly now (the panel's, or detected from the ship).</summary>
    public ApStyle Resolve(in ApSense s) => Cfg.Style != ApStyle.Auto ? Cfg.Style : ApStyleDetect.Of(in s);

    /// <summary>The route (resolved points: GPS markers found, coordinates), copied - a call only when it changed.</summary>
    public void SetRoute(List<ApRoute.Point> pts)
    {
        _route.Clear();
        if (pts != null) foreach (var p in pts) if (p.Found) _route.Add(p);
        if (Wp >= _route.Count) Wp = 0;
        WpCount = _route.Count;
    }
    public int RouteCount => _route.Count;
    public string WpName => Cfg.ReturnHome ? "return" : Wp < _route.Count ? _route[Wp].Name : "";

    /// <summary>Start a mode from the state now. False: refused (why). FlightHold catches the heading, height and speed it has.</summary>
    public bool Begin(ApMode mode, in ApSense s, out ApReason why)
    {
        why = ApReason.None;
        var st = Resolve(in s);
        float g = s.Gravity.Length();
        Vector3 up = g > 1e-2f ? -s.Gravity / g : Vector3.UnitY;
        float air = s.AirVel.Length();
        // (a combat slot chosen whose logic is not written yet: refused, said)
        if (mode == ApMode.Engage && IsSlot(Cfg.Behaviour) && !SlotImplemented(Cfg.Behaviour)) { why = ApReason.NotImplemented; return false; }
        // (a rocket steered onto its target launches from rest: no airspeed asked of it)
        bool rocket = mode == ApMode.Engage && Cfg.Behaviour == ApBehaviour.RocketGuidance;
        if (st == ApStyle.Airplane && !rocket)
        {
            if (!(s.Density > EnvelopeGuard.MinDensity)) { why = ApReason.NoAir; return false; }
            if (air < EnvelopeGuard.MinSpeed + 5f) { why = ApReason.TooSlow; return false; }
        }
        if (st == ApStyle.SixAxis)
        {
            // (the four sides across the up: each pushes a share of the main thrust, through the centre of mass)
            Vector3 upLoc = Vector3.Transform(up, Quaternion.Inverse(s.Orient));
            ApMath.MainAxis(s.ThrustPos, s.ThrustNeg, out float mainF);
            Vector3 u0 = Near(upLoc);
            bool known = s.SidePos != Vector3.Zero || s.SideNeg != Vector3.Zero;
            for (int k = 0; k < 6; k++)
            {
                if (MathF.Abs(C(u0, k % 3)) > 0.5f) continue;
                float f = C(k < 3 ? s.ThrustPos : s.ThrustNeg, k % 3), auth = known ? C(k < 3 ? s.SidePos : s.SideNeg, k % 3) : 1f;
                if (f < ApStyleDetect.SideShare * mainF || auth < 0.3f) { why = ApReason.NoTranslation; return false; }
            }

            if (g > 1e-2f && ApMath.Along(s.ThrustPos, s.ThrustNeg, Near(upLoc)) < 1.05f * s.Mass * g) { why = ApReason.NoLift; return false; }
        }
        if (mode == ApMode.Navigate && _route.Count == 0 && !Cfg.ReturnHome) { why = ApReason.NoWaypoints; return false; }
        Mode = mode; Style = st;
        // (the height over the ground now: the watch's, the ground ray's, else the terrain asked straight down - the planet's height
        //  reaches far; none, at rest in gravity: on the ground, its ground caught under it. In game 2026-10-20 Cruise engaged
        //  over a ground out of sight caught "ALT 0 m" and the ship never left the ground)
        _upW = up; _hasG = false;
        float agl = RealAgl(in s);
        if (agl < 0f && Fd?.Terrain != null && g > 1e-2f && Fd.Terrain.Below(s.Pos, -up, TerrainWatch.BelowReachLong, out float tb, out _) && float.IsFinite(tb)) agl = MathF.Max(0f, tb - s.Belly);
        bool grounded = agl >= 0f ? agl < GroundedM : g > 1e-2f && s.Vel.Length() < RestSpeed;
        if (agl < 0f && grounded) agl = 0f;
        if (agl >= 0f) { _gUp = Vector3D.Dot(s.Pos, (Vector3D)up) - agl; _hasG = true; }
        Alt.Reset(s.Pos, up, agl >= 0f ? agl : 0f);
        Tecs.Reset(); Bh.Reset(); Arb.Reset(); T.Clear(); Formation.Reset(); _slotWas = (ApBehaviour)255; SlotText = null; _dfBan = _rgBan = false;
        Wp = 0; _wpFrom = s.Pos; _loiter = false; _hasStation = false; _time = 0f;
        Quaternion inv = Quaternion.Inverse(s.Orient);
        _upL = Near(Vector3.Transform(up, inv));
        Vector3 nose = s.Nose.LengthSquared() > 0.5f ? s.Nose : st == ApStyle.Airplane ? ApMath.MainAxis(s.ThrustPos, s.ThrustNeg, out _) : -Vector3.UnitZ;
        nose = ApMath.Flat(nose, _upL); _noseL = nose.LengthSquared() > 1e-4f ? Vector3.Normalize(nose) : ApMath.Across(_upL);
        float h = HeightIn(in s, up, Alt.Height(s.Pos, up));
        // (the speed: the panel's, or the one it has - a hovering style's chase speed when none is set)
        _cruise = st == ApStyle.Airplane ? (Cfg.SpeedMs > 0f ? Cfg.SpeedMs : air) : (Cfg.SpeedMs > 0f ? Cfg.SpeedMs : 0f);
        CapturedHolds = false;
        if (mode == ApMode.FlightHold)
        {
            Vector3 vh = ApMath.Flat(s.Vel, up);
            Vector3 dir = vh.LengthSquared() > 4f ? vh : ApMath.Flat(Vector3.Transform(_noseL, s.Orient), up);
            CapHeading = MathF.Round(ApCompass.Heading(dir, up)); CapAlt = MathF.Round(CatchAlt(h, agl, grounded)); CapSpeed = st == ApStyle.Airplane ? MathF.Round(air) : 0f;
            Cfg.HeadingDeg = CapHeading; Cfg.AltM = CapAlt; Cfg.SpeedMs = CapSpeed; _cruise = CapSpeed;
            CapturedHolds = true;
        }
        else if (Cfg.AltM <= 0f) { CapAlt = MathF.Round(CatchAlt(h, agl, grounded)); Cfg.AltM = CapAlt; CapHeading = Cfg.HeadingDeg; CapSpeed = Cfg.SpeedMs; CapturedHolds = true; }
        CapturedHome = false;
        if (Cfg.HasHome) _home = new Vector3D(Cfg.HomeX, Cfg.HomeY, Cfg.HomeZ);
        else { _home = s.Pos; CapturedHome = true; }
        return true;
    }

    /// <summary>The height in the panel's reference: over the ground under the ship (when known), else the altimeter's.</summary>
    float HeightIn(in ApSense s, Vector3 up, float alt)
    {
        float agl = Agl(in s);
        return Mode != ApMode.Engage && Cfg.HeightRef == ApHeightRef.Ground && agl >= 0f ? agl : alt;
    }
    /// <summary>The ground under the ship as seen now (the watch's, else the ground ray's; -1 none).</summary>
    float RealAgl(in ApSense s) => Fd != null && Fd.Ground.HasGround ? Fd.Ground.Agl : s.Agl;
    /// <summary>The height over the ground: as seen now (and kept as the last ground seen), else over the last ground seen (the
    /// flight director's own rule for its holds: GroundUp), else -1.</summary>
    float Agl(in ApSense s)
    {
        float a = RealAgl(in s);
        double pu = Vector3D.Dot(s.Pos, (Vector3D)_upW);
        if (a >= 0f) { _gUp = pu - a; _hasG = true; return a; }
        return _hasG ? (float)Math.Max(0.0, pu - _gUp) : -1f;
    }

    /// <summary>The height caught at an engage (in the panel's reference): the one it has - on the ground a climb off it (TakeoffM,
    /// at least the terrain minimum); over a known ground never under the terrain minimum (which it would climb to anyway).</summary>
    float CatchAlt(float h, float agl, bool grounded)
    {
        float c = MathF.Max(0f, h), min = Cfg.MinAglM > 0f ? Cfg.MinAglM : 0f;
        if (grounded) return MathF.Max(c, MathF.Max(min, TakeoffM));
        return agl >= 0f ? MathF.Max(c, min) : c;
    }

    public bool Step(in ApSense s, ref ApExternal x)
    {
        float dt = s.Dt > 0f ? s.Dt : 1f / 60f; _time += dt;
        float g = s.Gravity.Length(), gT = (s.GravityTrue.LengthSquared() > 0f ? s.GravityTrue : s.Gravity).Length();
        Vector3 up = g > 1e-2f ? -s.Gravity / g : Vector3.UnitY; _upW = up;
        float alt = Alt.Height(s.Pos, up), h = HeightIn(in s, up, alt), agl = Agl(in s);
        float air = s.AirVel.Length();
        HeightNow = h; SpeedNow = Style == ApStyle.Airplane ? air : ApMath.Flat(s.Vel, up).Length();
        HeadingNow = ApCompass.Heading(ApMath.Flat(s.Vel, up).LengthSquared() > 4f ? s.Vel : Vector3.Transform(_noseL, s.Orient), up);
        // ── the target ──
        ApTargetFix fix = default;
        if (Targets == null || !Targets.Get(in s, out fix)) fix.Valid = false;
        T.Update(in fix, in s, dt);
        // ── the ship's own turn: the most bank the lift and the panel allow, the radius at this speed ──
        var perf = Fd != null ? Fd.Perf : default;
        float nAvail = perf.Valid ? MathF.Max(perf.NInst, 1.01f) : MathF.Min(s.Lim.GLimit, 2f);
        float bank = MathF.Min(Cfg.BankDeg * ApMath.Deg, MathF.Acos(1f / nAvail));
        float aLatMax = MathF.Max(0.5f, gT * MathF.Tan(bank));
        float vg = ApMath.Flat(s.Vel, up).Length();
        TurnR = Style == ApStyle.Airplane ? vg * vg / aLatMax : 0f;
        float cruise = Cfg.SpeedMs > 0f ? Cfg.SpeedMs : _cruise;
        float chase = Style == ApStyle.Airplane ? cruise : (cruise > 0f ? cruise : 30f);
        // ── the mode's goal ──
        var goal = ApNavGoal.None; bool direct = false;
        ActiveBh = ApBehaviour.Auto; Loitering = false;
        if (Mode == ApMode.FlightHold) Holds(in s, up, cruise, ref goal);
        else if (Mode == ApMode.Navigate) RouteGoal(in s, up, cruise, chase, ref goal);
        else
        {
            if (T.Valid)
            {
                var c = new ApBhCtx { Style = Style, P = Cfg, Pos = s.Pos, Vel = s.Vel, Up = up, Speed = Style == ApStyle.Airplane ? air : vg, Cruise = cruise, Chase = chase,
                    TurnR = TurnR, Height = alt, Time = _time, T = T, THeight = Alt.Height(T.Pos, up), Decel = perf.Valid ? perf.DragDecelNow : 1f, NMax = perf.Valid ? perf.NAtAlphaMax : 0f };
                // (the combat slots: their context filled when they may fly - chosen, or Auto asking their scores)
                bool auto = Cfg.Behaviour == ApBehaviour.Auto;
                if (auto || IsSlot(Cfg.Behaviour)) FillSlot(in s, up, gT, alt, agl, air, in perf, dt);
                Arb.DogfightScore = auto && ApDogfight.Implemented && !_dfBan ? Dogfight.Score(Slot) : float.NaN;
                Arb.RocketScore = auto && ApRocketGuidance.Implemented && !_rgBan ? Rocket.Score(Slot) : float.NaN;
                ActiveBh = Arb.Pick(Cfg.Behaviour, in c, dt);
                float clear = float.NaN;
                if (IsSlot(ActiveBh))
                {
                    if (!RunSlot(ActiveBh, dt, ref goal, out direct, out var letGo))
                    {
                        // (declined: chosen - the mode lets go with the slot's reason; picked by Auto - not asked again this engage)
                        if (!auto) { x = default; x.LetGo = letGo; return false; }
                        if (ActiveBh == ApBehaviour.Dogfight) _dfBan = true; else _rgBan = true;
                        ActiveBh = Style == ApStyle.Airplane ? ApBehaviour.PursuitPure : ApBehaviour.Standoff; direct = false;
                        Bh.Plan(ActiveBh, in c, dt, ref goal);
                    }
                    else clear = SlotOut.TargetClearM;
                }
                else
                {
                    _slotWas = ActiveBh; SlotText = null;
                    if (ActiveBh == ApBehaviour.Formation) Formation.Plan(in c, ApFormationParams.Of(in Cfg), LeastSpeed(in perf), ref goal);
                    else
                    {
                        Bh.Plan(ActiveBh, in c, dt, ref goal);
                        // (a hovering ship's heading: the way it travels - the facing, a desire, turns it toward the target in Aim)
                        if (Style != ApStyle.Airplane) goal.Face = Vector3.Zero;
                    }
                }
                // (the target as an obstacle for the collision avoidance, kept at most half the behaviour's own closest distance - a
                //  slot's own clearance when it gives one, never under the target's size)
                if (Fd != null)
                {
                    Fd.TargetObstacle = new ApObstacle { Pos = T.Pos, Vel = T.Vel, Radius = MathF.Max(1f, T.Radius), Target = true };
                    Fd.HasTargetObstacle = true; Fd.TargetClearMax = float.IsFinite(clear) ? MathF.Max(clear, T.Radius) : ApAvoid.TargetClear(in Cfg, ActiveBh);
                }
                h = alt;   // (the target's heights are the altimeter's)
                // (a helicopter aiming fixed guns - the nose facing: level with the target - it cannot pitch onto it without moving)
                //  - and its gun's own pitch now (the lean, the trim; filtered) taken in: the height where that line meets the lead; the
                //  aim priority of the way from the movement's height, and only where the movement lets the guns bear - goal.Aim)
                if (Style == ApStyle.Helicopter && goal.Aim && Cfg.Facing == ApFacing.Nose && Cfg.AimPriority > 0f && !IsSlot(ActiveBh) && ActiveBh != ApBehaviour.Formation)
                {
                    Vector3 axis = s.GunAxis.LengthSquared() > 0.5f ? s.GunAxis : s.Nose.LengthSquared() > 0.5f ? s.Nose : _noseL;
                    float el = MathF.Asin(Math.Clamp(Vector3.Dot(Vector3.Transform(axis, s.Orient), up), -1f, 1f));
                    _gunEl += (el - _gunEl) * MathF.Min(1f, dt / 1.5f);
                    float flat = ApMath.Flat((Vector3)(T.Pos - s.Pos), up).Length();
                    float hAim = c.THeight + Vector3.Dot(T.Vel, up) * Tof - flat * MathF.Tan(Math.Clamp(_gunEl, -0.5f, 0.5f));
                    float hMove = float.IsFinite(goal.Height) ? goal.Height : alt;
                    goal.Height = hMove + (hAim - hMove) * Cfg.AimPriority;
                }


            }
            else
            {
                Arb.Reset(); _slotWas = (ApBehaviour)255; SlotText = null;   // (no target: a slot begins again on the next)
                if (_route.Count > 0) RouteGoal(in s, up, cruise, chase, ref goal);
                else { Loiter(_home, up, ref goal); goal.Height = float.IsFinite(Cfg.AltM) && Cfg.AltM > 0f ? Cfg.AltM : h; goal.Speed = cruise; h = HeightIn(in s, up, alt); }
            }
        }
        // ── the terrain under it: never below MinAglM over the ground below, and up over what is ahead; Engage's altitude band
        //    (the combat ceiling in the altimeter, the hard deck over the ground - the deck wins) ──
        float hDem = goal.Height;
        float need = Cfg.MinAglM;
        // (2026-10-21) FOLLOW SHIP: the terrain minimum never above the leader's own height over the ground (the ground under the
        // follower stands for the leader's) - a 60 m minimum kept a follower out of a low leader's slot. The floor (FloorM) still holds.
        if (Mode == ApMode.Engage && T.Valid && ActiveBh == ApBehaviour.Formation && agl >= 0f && need > 0f)
            need = MathF.Min(need, MathF.Max(0f, agl + Vector3.Dot((Vector3)(T.Pos - s.Pos), up)));
        float hd0 = hDem;
        if (Mode == ApMode.Engage && T.Valid && !direct)
        {
            if (Cfg.CeilingM > 0f && float.IsFinite(hDem)) hDem = MathF.Min(hDem, Cfg.CeilingM);
            if (Cfg.DeckM > 0f && agl >= 0f) hDem = MathF.Max(float.IsFinite(hDem) ? hDem : h, h + (Cfg.DeckM - agl));
        }
        if (need > 0f && agl >= 0f && agl < 1.5f * need) hDem = MathF.Max(float.IsFinite(hDem) ? hDem : h, h + (need - agl));
        if (need > 0f && Fd != null && Fd.Ground.Tti < 12f && Style == ApStyle.Airplane) hDem = MathF.Max(float.IsFinite(hDem) ? hDem : h, h + need);
        _terrainUp = float.IsFinite(hDem) && (!float.IsFinite(hd0) || hDem > hd0 + 1f);
        HeightWanted = hDem; Goal = goal;
        // ── the style's executor (a slot's direct goal: flown as it is - the rocket's lateral cap on it) ──
        x = default; x.Throttle = float.NaN;
        Recovering = false;
        if (direct) { x = SlotOut.Goal; x.LetGo = ApReason.None; if (ActiveBh == ApBehaviour.RocketGuidance) CapLateral(in s, gT, ref x); }
        else if (Style == ApStyle.Airplane) Airplane(in s, up, gT, h, hDem, air, aLatMax, nAvail, perf, ref goal, ref x);
        else Hover(in s, up, h, hDem, chase, ref goal, ref x);
        // ── the facing (a desire): the built-in movements - the panel's Facing at its Aim Priority; a slot - its aim report, and the nose on
        //    the lead when it asks and the panel's facing is the nose (the author's call: all of it the caps allow); none for a formation,
        //    a rocket, a mode without a target. Jinking (the built-in behaviours) ──
        bool slot = IsSlot(ActiveBh) && Mode == ApMode.Engage && T.Valid;
        bool engaged = Mode == ApMode.Engage && T.Valid && ActiveBh != ApBehaviour.Formation && ActiveBh != ApBehaviour.RocketGuidance;
        if (slot)
        {
            bool nose = (SlotOut.AimNose || (!direct && goal.Aim)) && Cfg.Facing == ApFacing.Nose;
            Aim(in s, up, nose ? ApFacing.Nose : ApFacing.Travel, 1f, SlotOut.AimReport || SlotOut.AimNose || (!direct && goal.Aim), ref x);
        }
        else Aim(in s, up, engaged ? Cfg.Facing : ApFacing.Travel, Cfg.AimPriority, false, ref x);
        if (Cfg.Evasive && Mode == ApMode.Engage && T.Valid && !slot && ActiveBh != ApBehaviour.Formation) Jink(in s, up, aLatMax, dt, ref x);
        return true;
    }

    /// <summary>Everything a slot gets this step (ApSlotCtx) - copies, no allocation.</summary>
    void FillSlot(in ApSense s, Vector3 up, float gT, float alt, float agl, float air, in ApPerf perf, float dt)
    {
        var c = Slot;
        c.Dt = dt; c.S = s; c.Style = Style; c.Up = up; c.G = gT; c.Height = alt; c.Agl = agl; c.AirSpeed = air;
        c.Mach = s.SpeedOfSound > 1f ? air / s.SpeedOfSound : 0f;
        Vector3 main = ApMath.MainAxis(s.ThrustPos, s.ThrustNeg, out float mf);
        c.MainAxisW = Vector3.Transform(main, s.Orient); c.MainThrust = mf; c.ThrustToWeight = s.Mass > 0f && gT > 0f ? mf / (s.Mass * gT) : 0f;
        c.Perf = perf; c.Samples = s.Perf; c.GLimit = s.Lim.GLimit;
        c.LiftNMax = perf.Valid ? perf.NInst : s.Lim.GLimit; c.AlphaMax = perf.Valid ? perf.AlphaMax : float.NaN;
        c.HasTarget = T.Valid; c.Target = T;
        c.Terrain = Fd?.Terrain; c.Ground = Fd?.Ground;
        c.Panel = Cfg.Clamped(); c.Dogfight = ApDogfightParams.Of(in c.Panel); c.Rocket = ApRocketParams.Of(in c.Panel);
    }

    /// <summary>One step of a slot (b): begun when it takes over, its Step asked; its saved state copied out twice a second. False:
    /// declined (letGo: its reason - NotImplemented when it gave none). direct: its goal is a direct one (SlotOut.Goal).</summary>
    bool RunSlot(ApBehaviour b, float dt, ref ApNavGoal goal, out bool direct, out ApReason letGo)
    {
        direct = false; letGo = ApReason.None;
        if (!SlotImplemented(b)) { letGo = ApReason.NotImplemented; SlotText = null; return false; }
        if (b != _slotWas)
        {
            _slotWas = b; _slotTime = 0f; _slotSaveIn = 0.5f;
            if (b == ApBehaviour.Dogfight) Dogfight.Begin(Slot, Cfg.DfSaved); else Rocket.Begin(Slot, Cfg.RgSaved);
        }
        _slotTime += dt; Slot.Time = _slotTime;
        var o = ApSlotOut.None;
        bool ok = b == ApBehaviour.Dogfight ? Dogfight.Step(Slot, ref o) : Rocket.Step(Slot, ref o);
        SlotOut = o; SlotText = o.Status;
        if (!ok || o.LetGo != ApReason.None) { letGo = o.LetGo != ApReason.None ? o.LetGo : ApReason.NotImplemented; return false; }
        if ((_slotSaveIn -= dt) <= 0f)
        {
            _slotSaveIn = 0.5f;
            if (b == ApBehaviour.Dogfight && Dogfight.State.Dirty) { Dogfight.State.Dirty = false; DfSavedOut = Dogfight.State.Save() ?? ""; SlotSavedChanged = true; }
            if (b == ApBehaviour.RocketGuidance && Rocket.State.Dirty) { Rocket.State.Dirty = false; RgSavedOut = Rocket.State.Save() ?? ""; SlotSavedChanged = true; }
        }
        if (o.UseNav) { goal = o.Nav; return true; }
        direct = true;
        return true;
    }

    /// <summary>The rocket slot's Max Lateral g on its direct goal: a Lift goal's load, an Accel goal's part across the path.</summary>
    void CapLateral(in ApSense s, float gT, ref ApExternal x)
    {
        float cap = Cfg.RgMaxLatG;
        if (x.Kind == ApExternalKind.Lift) x.Load = float.IsFinite(x.Load) ? MathF.Min(x.Load, cap) : cap;
        else if (x.Kind == ApExternalKind.Accel && s.Vel.LengthSquared() > 1f)
        {
            Vector3 vh = Vector3.Normalize(s.Vel), across = ApMath.Flat(x.Accel, vh);
            float a = across.Length(), m = cap * gT;
            if (a > m) x.Accel += across * (m / a - 1f);
        }
    }

    /// <summary>The least airspeed a formation's airplane drops back at: where the lift at the stall margin gives 1.3 g (the
    /// performance's chart), else EnvelopeGuard's floor and 10 m/s.</summary>
    static float LeastSpeed(in ApPerf p) => p.Valid && p.NAtAlphaMax > 0.1f ? p.SpeedRef * MathF.Sqrt(1.3f / p.NAtAlphaMax) : EnvelopeGuard.MinSpeed + 10f;
    // ── the modes ──

    void Holds(in ApSense s, Vector3 up, float cruise, ref ApNavGoal g)
    {
        g.Height = Cfg.AltOn ? Cfg.AltM : float.NaN;
        g.Speed = Cfg.SpeedOn ? cruise : float.NaN;
        if (Cfg.HeadingOn) { g.Kind = ApNavKind.Course; g.Course = ApCompass.Dir(Cfg.HeadingDeg, up); g.Face = g.Course; }
        else g.Kind = ApNavKind.None;   // (wings level: no turn asked)
    }

    void RouteGoal(in ApSense s, Vector3 up, float cruise, float chase, ref ApNavGoal g)
    {
        g.Height = Cfg.AltOn ? Cfg.AltM : float.NaN;
        g.Speed = Cfg.SpeedOn ? cruise : float.NaN;
        int n = Cfg.ReturnHome ? 1 : _route.Count;
        if (n == 0) { Loiter(_home, up, ref g); return; }
        if (_loiter) { Loiter(_loiterAt, up, ref g); WpDist = 0f; Eta = 0f; return; }
        if (Wp >= n) Wp = 0;
        Vector3D wp = Cfg.ReturnHome ? _home : _route[Wp].Pos;
        Vector3 to = ApMath.Flat((Vector3)(wp - s.Pos), up);
        float dist = to.Length(); WpDist = dist;
        float gs = MathF.Max(ApMath.Flat(s.Vel, up).Length(), 1f); Eta = dist / gs;
        Vector3 leg = ApMath.Flat((Vector3)(wp - _wpFrom), up);
        bool past = leg.LengthSquared() > 1f && Vector3.Dot(ApMath.Flat((Vector3)(s.Pos - wp), up), leg) > 0f;
        // (the turn led: an airplane takes the next leg a turn's lead early - R tan(half the leg change) - so it rolls out on it)
        float accept = MathF.Max(8f, 0.15f * chase);
        if (Style == ApStyle.Airplane)
        {
            float turn = 0f;
            int nx = Wp + 1 < n ? Wp + 1 : (Cfg.RouteLoop && n > 1 ? 0 : -1);
            if (nx >= 0 && !Cfg.ReturnHome)
            {
                Vector3 next = ApMath.Flat((Vector3)(_route[nx].Pos - wp), up);
                if (next.LengthSquared() > 1f && leg.LengthSquared() > 1f) turn = MathF.Acos(Math.Clamp(Vector3.Dot(Vector3.Normalize(next), Vector3.Normalize(leg)), -1f, 1f));
            }
            accept = Math.Clamp(TurnR * MathF.Tan(MathF.Min(turn, 2.6f) * 0.5f), 120f, 3f * MathF.Max(TurnR, 120f));
        }

        if (dist < accept || (past && Style == ApStyle.Airplane))
        {
            _wpFrom = wp; Wp++;
            if (Wp >= n)
            {
                if (Cfg.RouteLoop && n > 1 && !Cfg.ReturnHome) Wp = 0;
                else { _loiter = true; _loiterAt = wp; Loiter(wp, up, ref g); return; }
            }
            wp = Cfg.ReturnHome ? _home : _route[Wp].Pos;
        }
        g.Kind = ApNavKind.Point; g.Point = wp; g.From = _wpFrom; g.Line = Style == ApStyle.Airplane;
        g.Face = ApMath.Flat((Vector3)(wp - s.Pos), up);
        if (Style != ApStyle.Airplane) g.Speed = chase;
    }

    void Loiter(Vector3D at, Vector3 up, ref ApNavGoal g)
    {
        Loitering = true;
        if (Style == ApStyle.Airplane) { g.Kind = ApNavKind.Circle; g.Point = at; g.Radius = MathF.Max(Cfg.LoiterM, 1.3f * TurnR); g.Dir = 1; }
        else { g.Kind = ApNavKind.Point; g.Point = at; }
    }

    // ── the executors ──

    void Airplane(in ApSense s, Vector3 up, float gT, float h, float hDem, float air, float aLatMax, float nAvail, ApPerf perf, ref ApNavGoal goal, ref ApExternal x)
    {
        if (!EnvelopeGuard.AirLimits(in s) || !perf.Valid)
        {
            // (no lift known yet - slow, the sweep not in: wings level along the path, full throttle; the protection's own)
            Recovering = true;
            Vector3 head = ApMath.Flat(s.Vel, up);
            if (head.LengthSquared() < 1f) head = ApMath.Flat(Vector3.Transform(_noseL, s.Orient), up);
            if (head.LengthSquared() < 1e-6f) head = ApCompass.Dir(0f, up);
            x.Kind = ApExternalKind.Attitude; x.Attitude = ApMath.Frame(_upL, _noseL, up, Vector3.Normalize(head)); x.Throttle = 1f;
            return;
        }
        Vector3 vg = s.Vel;
        float l1 = ApL1.Distance(ApMath.Flat(vg, up).Length(), aLatMax);
        Vector3 aLat = Vector3.Zero; CrossTrack = RadiusErr = 0f;
        switch (goal.Kind)
        {
            case ApNavKind.Point:
                aLat = goal.Line ? ApL1.Line(s.Pos, vg, up, goal.From, goal.Point, l1, aLatMax, out CrossTrack) : ApL1.Toward(s.Pos, vg, up, goal.Point, l1, aLatMax);
                if (ActiveBh == ApBehaviour.Formation && Mode == ApMode.Engage)
                {
                    // (the slot's own turn fed forward: L1 alone toward a moving point turns only once the point has moved off the
                    //  nose - a wingman 9 deg behind its leader's 30 deg-bank turn ended 180 m outside the slot; ApFormation.TurnFeed of it,
                    //  the L1 point being on the curved path already - Tests/AutopilotSim FormationPack)
                    Vector3 vh = ApMath.Flat(vg, up);
                    if (vh.LengthSquared() > 1f)
                    {
                        Vector3 rt = Vector3.Normalize(Vector3.Cross(vh, up));
                        float a = Math.Clamp(Vector3.Dot(aLat, rt) + ApFormation.TurnFeed * Vector3.Dot(goal.PointAcc, rt), -aLatMax, aLatMax);
                        aLat = rt * a;
                    }
                }
                break;
            case ApNavKind.Circle:
                // (a moving centre: flown in its frame - the velocity relative to it)
                aLat = ApL1.Circle(s.Pos, vg - ApMath.Flat(goal.PointVel, up), up, goal.Point, goal.Radius, goal.Dir, l1, aLatMax, out RadiusErr);
                break;
            case ApNavKind.Course:
                aLat = ApL1.Course(vg, up, goal.Course, l1, aLatMax);
                break;
        }
        float thrust = ApMath.Along(s.ThrustPos, s.ThrustNeg, Vector3.Normalize(s.AirVel));
        float drag = MathF.Max(0f, perf.DragDecelNow * s.Mass);
        float aVertMax = gT * MathF.Max(0.3f, nAvail - 1f) * 0.7f;
        // (over the ground the height's rate is the climb less the ground's own rise under the ship; terrain ahead or under the
        //  minimum: climb as hard as the excess thrust lets it, not at the panel's rate)
        bool overGround = Mode != ApMode.Engage && Cfg.HeightRef == ApHeightRef.Ground && Agl(in s) >= 0f;
        float hdot = Vector3.Dot(s.Vel, up) - (overGround && Fd != null && Fd.Ground.HasGround ? Fd.Ground.GroundRate : 0f);
        float vDem = goal.Max ? float.NaN : goal.Speed;
        float vs = _terrainUp ? MathF.Max(Cfg.VsMs, 0.4f * air) : Cfg.VsMs;
        if (ActiveBh == ApBehaviour.Formation) vs = MathF.Max(vs, MathF.Abs(Vector3.Dot(Formation.SlotVel, up)) + 3f);   // (the leader's climb, and some to close)
        Tecs.Step(h, hdot, hDem, vs, air, vDem, thrust, drag, s.Mass, gT, aVertMax, s.Dt > 0f ? s.Dt : 1f / 60f, out float aVert, out float thr);
        x.Kind = ApExternalKind.Accel; x.Accel = aLat + up * aVert; x.Throttle = goal.Max ? 1f : thr;
    }

    void Hover(in ApSense s, Vector3 up, float h, float hDem, float chase, ref ApNavGoal goal, ref ApExternal x)
    {
        Vector3D p = s.Pos; Vector3 v = goal.PointVel;
        float speed = goal.Max ? chase : float.IsFinite(goal.Speed) && goal.Speed > 0f ? goal.Speed : chase;
        switch (goal.Kind)
        {
            case ApNavKind.Point: case ApNavKind.Circle: p = goal.Point; _hasStation = false; break;
            case ApNavKind.Course:
            {
                // (a station marched along the course at the speed - kept near the ship, so a held-back ship is not chased by it)
                Vector3 d = ApMath.Flat(goal.Course, up);
                d = d.LengthSquared() > 1e-6f ? Vector3.Normalize(d) : Vector3.Zero;
                float sp = goal.Max ? chase : float.IsFinite(goal.Speed) ? goal.Speed : 0f;
                if (!_hasStation) { _station = s.Pos; _hasStation = true; }
                _station += (Vector3D)(d * (sp * (s.Dt > 0f ? s.Dt : 1f / 60f)));
                Vector3 lag = (Vector3)(_station - s.Pos); float lim = sp * 2f + 5f;
                if (lag.Length() > lim) _station = s.Pos + (Vector3D)(lag * (lim / lag.Length()));
                p = _station; v = d * sp; speed = MathF.Max(sp, 1f);
                break;
            }
            default:
                if (!_hasStation) { _station = s.Pos; _hasStation = true; }
                p = _station; break;
        }
        // the height: the goal's (in the pilot's reference), else the one it has
        Vector3 rel = (Vector3)(p - s.Pos);
        float hWant = float.IsFinite(hDem) ? hDem : h;
        p = s.Pos + (Vector3D)(ApMath.Flat(rel, up) + up * (hWant - h));
        x.Kind = ApExternalKind.Track; x.Position = p; x.Velocity = v; x.Accel = goal.PointAcc; x.MaxSpeed = MathF.Max(speed, 1f);
        Vector3 face = goal.Face;
        // (the way it travels: the place's motion, else the way to the place - FLAT: a six-axis ship points its nose along the heading
        //  as given (FlightDirector), and the way to a place 750 m up stood it on its tail - FacingPack, 2026-10-20)
        if (face.LengthSquared() < 1e-6f) face = ApMath.Flat(v, up).LengthSquared() > 9f ? ApMath.Flat(v, up) : ApMath.Flat(rel, up).LengthSquared() > 400f ? ApMath.Flat(rel, up) : Vector3.Zero;
        x.Heading = face;
    }

    /// <summary>THE FACING (2026-10-20): the aim report (the lead, the error, in the cone, in range) and the chosen face blended toward
    /// the target, Aim Priority (p, 0..1) of what the style's caps allow - movement wins where they conflict:
    ///  - NOSE (fixed guns) on the target's lead point. Airplane: off its flight path only inside a 30 deg window the lift can still
    ///    fly, within the gun's reach, given back 4..7 s before the target (the pull-out) - Blend = window x p. Helicopter: by yaw
    ///    (its lift axis is its pitch: tilting it would move it), the heading turned from the way it travels toward the lead by p.
    ///    Six-axis: the nose anywhere its side thrusters still hold their share of the weight (the elevation clipped) - Blend = p.
    ///  - SIDE (left / right: turrets, a broadside) on the target itself (turrets lead their own shots). Airplane: by the way it turns
    ///    round (ApBehaviours.SideFor) - its attitude stays the turn's, no blend. Helicopter: the heading that puts that side on the
    ///    target, by p. Six-axis: that side's axis onto the target, elevation clipped, Blend = p.
    ///  - TRAVEL: nothing blended; report: the nose's report alone (a slot's AimReport).
    /// The protection still clips (the flight director's guard on the attitude this makes).</summary>
    void Aim(in ApSense s, Vector3 up, ApFacing face, float p, bool report, ref ApExternal x)
    {
        FacingNow = T.Valid ? face : ApFacing.Travel;
        AimOn = T.Valid && (face != ApFacing.Travel || report);
        if (!AimOn) { AimErrDeg = float.NaN; InCone = InRange = false; LeadBlend = 0f; return; }
        p = float.IsFinite(p) ? Math.Clamp(p, 0f, 1f) : 0f;
        Vector3 gun = s.GunAxis.LengthSquared() > 0.5f ? s.GunAxis : s.Nose.LengthSquared() > 0.5f ? s.Nose : _noseL;
        float u = Cfg.MuzzleSpeed > 0f ? Cfg.MuzzleSpeed : s.MuzzleSpeed;
        ApAim.Lead((Vector3)(T.Pos - s.Pos), T.Vel - s.Vel, T.Acc, Vector3.Zero, u, out Vector3 dir, out Tof);
        bool side = face == ApFacing.SideLeft || face == ApFacing.SideRight;
        Vector3 axis = gun, want = dir;
        if (side)
        {
            Vector3 noseL = s.Nose.LengthSquared() > 0.5f ? s.Nose : _noseL;
            Vector3 rightL = Vector3.Cross(noseL, _upL);   // (fwd x up: right - the behaviours' convention; -Z, +Y give +X)
            rightL = rightL.LengthSquared() > 1e-4f ? Vector3.Normalize(rightL) : Vector3.UnitX;
            axis = face == ApFacing.SideRight ? rightL : -rightL;
            Vector3 los = (Vector3)(T.Pos - s.Pos);
            want = los.LengthSquared() > 1e-4f ? Vector3.Normalize(los) : dir;
        }
        Vector3 axisW = Vector3.Transform(axis, s.Orient);
        AimErrDeg = ApAim.ErrorDeg(axisW, want);
        float reach = s.GunRange > 0f ? s.GunRange : MathF.Max(1.5f * Cfg.RangeM, 1500f);
        InRange = T.Range <= reach;
        InCone = !side && InRange && ApAim.InCone(AimErrDeg, Cfg.ConeDeg, T.Range, T.Radius);
        if (face == ApFacing.Travel || p <= 0f) { LeadBlend = 0f; return; }   // (the report alone; priority 0: the path only)
        float w;
        if (Style == ApStyle.Airplane)
        {
            if (side) w = 0f;   // (a plane's side: the way it circles - ApBehaviours.SideFor; banking it level onto the target would undo the turn)
            else
            {
                // (the nose off the flight path toward the lead, within a window the lift can still fly - beyond it the path's turn
                //  brings it round; only inside the gun's reach)
                Vector3 path = s.AirVel.LengthSquared() > 1f ? Vector3.Transform(Vector3.Normalize(s.AirVel), s.Orient) : axisW;
                float off = ApAim.ErrorDeg(path, dir);
                w = Math.Clamp((30f - off) / 15f, 0f, 1f) * (T.Range <= 1.2f * reach ? 1f : 0f);
                // (and off it before the target: the nose given back to the path from 7 s before reaching it, all of it by 4 s - the pull-out's time)
                if (T.Closing > 1f) w *= Math.Clamp((T.Range / T.Closing - 4f) / 3f, 0f, 1f);
            }
            if (w > 0f) { x.AimAxis = axis; x.AimDir = want; x.Blend = w * p; }
        }
        else if (Style == ApStyle.Helicopter)
        {
            // (a helicopter yaws the face on; it does not pitch onto it - its lift axis is its nose's pitch, tilting it moves the
            //  ship: for the nose the behaviour holds it level with the lead instead - ApPilot.Step)
            w = 1f;
            YawToward(want, up, face, p, ref x);
        }
        else
        {
            // (six-axis: the face anywhere - but in gravity tilted only as far as its side thrusters still hold their share of the
            //  weight: past it the ship could not hold its place - the elevation clipped, the error reported as it is)
            w = 1f;
            float g = s.Gravity.Length();
            Vector3 fh = ApMath.Flat(want, up);
            if (g > 0.01f && fh.LengthSquared() > 1e-6f)
            {
                Quaternion inv = Quaternion.Inverse(s.Orient); Vector3 uL = Near(Vector3.Transform(up, inv));
                float sideF = float.MaxValue;
                for (int k = 0; k < 6; k++) if (MathF.Abs(C(uL, k % 3)) < 0.5f) sideF = MathF.Min(sideF, C(k < 3 ? s.ThrustPos : s.ThrustNeg, k % 3));
                float elMax = MathF.Asin(Math.Clamp(0.6f * sideF / MathF.Max(s.Mass * g, 1f), 0f, 1f));
                float el = Math.Clamp(MathF.Asin(Math.Clamp(Vector3.Dot(want, up), -1f, 1f)), -elMax, elMax);
                want = Vector3.Normalize(fh) * MathF.Cos(el) + up * MathF.Sin(el);
            }
            // (its heading turned toward the face first, as a helicopter's: the aim's shortest arc from a heading the other way
            //  went over the top - a target above and behind stood it on its back, FacingPack 2026-10-20; the blend then the rest)
            YawToward(want, up, face, p, ref x);
            x.AimAxis = axis; x.AimDir = want; x.Blend = w * p;
        }
        LeadBlend = w * p;
    }

    /// <summary>The heading turned from the way it travels (x.Heading) toward the one that puts the face on 'want' (the nose: its flat
    /// direction; a side: the heading with that side toward it), by the share p.</summary>
    static void YawToward(Vector3 want, Vector3 up, ApFacing face, float p, ref ApExternal x)
    {
        Vector3 d = ApMath.Flat(want, up);
        if (d.LengthSquared() < 1e-6f) return;
        d = Vector3.Normalize(d);
        Vector3 aimHead = face == ApFacing.SideRight ? Vector3.Cross(up, d) : face == ApFacing.SideLeft ? Vector3.Cross(d, up) : d;
        x.Heading = TurnToward(ApMath.Flat(x.Heading, up), aimHead, up, p);
    }

    /// <summary>A flat heading turned toward another by the share p of the angle between them (no heading yet: the other one, or none
    /// at p 0).</summary>
    static Vector3 TurnToward(Vector3 from, Vector3 to, Vector3 up, float p)
    {
        if (from.LengthSquared() < 1e-6f) return p > 0f ? to : from;
        from = Vector3.Normalize(from);
        float ang = MathF.Atan2(Vector3.Dot(Vector3.Cross(from, to), up), Vector3.Dot(from, to));
        return Vector3.Transform(from, Quaternion.CreateFromAxisAngle(up, ang * p));
    }

    void Jink(in ApSense s, Vector3 up, float aLatMax, float dt, ref ApExternal x)
    {
        _jinkIn -= dt;
        if (_jinkIn <= 0f)
        {
            _rng ^= _rng << 13; _rng ^= _rng >> 17; _rng ^= _rng << 5;
            _jinkIn = 1.2f + (_rng % 1000u) / 1000f * 1.6f;
            _jx = (_rng & 2u) != 0 ? 1 : -1; _jy = (_rng & 4u) != 0 ? 1 : -1; Jinks++;
        }
        float k = Cfg.Aggression;
        Vector3 vh = ApMath.Flat(s.Vel, up);
        Vector3 right = vh.LengthSquared() > 1f ? Vector3.Cross(Vector3.Normalize(vh), up) : Vector3.Cross(T.Los, up);
        if (right.LengthSquared() > 1e-6f) right = Vector3.Normalize(right);
        if (x.Kind == ApExternalKind.Accel) x.Accel += right * (_jx * k * 0.5f * aLatMax) + up * (_jy * k * 2f);
        else if (x.Kind == ApExternalKind.Track) x.Position += (Vector3D)(right * (_jx * k * 20f) + up * (_jy * k * 5f));
    }

    static float C(Vector3 v, int i) => i == 0 ? v.X : i == 1 ? v.Y : v.Z;
    static Vector3 Near(Vector3 d)
    {
        float ax = MathF.Abs(d.X), ay = MathF.Abs(d.Y), az = MathF.Abs(d.Z);
        if (ax >= ay && ax >= az) return new Vector3(d.X >= 0f ? 1f : -1f, 0f, 0f);
        if (ay >= az) return new Vector3(0f, d.Y >= 0f ? 1f : -1f, 0f);
        return new Vector3(0f, 0f, d.Z >= 0f ? 1f : -1f);
    }
}
