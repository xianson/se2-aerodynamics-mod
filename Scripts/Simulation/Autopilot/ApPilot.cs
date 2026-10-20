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
/// leant toward the motion, its lean and speed limits); SIX-AXIS the same place flown on the thrusters, the nose free -> the AIM
/// (the gun axis on the target's lead point, Blend of the way; the protection still clips) -> jinking. Every flying number comes
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

    readonly List<ApRoute.Point> _route = new();
    public int Wp, WpCount; Vector3D _wpFrom; bool _loiter; Vector3D _loiterAt;
    Vector3D _home, _station; bool _hasStation;
    Vector3 _upL = Vector3.UnitY, _noseL = -Vector3.UnitZ;
    Vector3 _upW = Vector3.UnitY;
    float _cruise, _time, _jinkIn, _gunEl;
 int _jx = 1, _jy = 1; uint _rng = 2463534242u;

    // ── what it caught at the engage (the link writes it to the panel) ──
    public bool CapturedHolds; public float CapHeading, CapAlt, CapSpeed;
    public bool CapturedHome; public Vector3D Home => _home;

    // ── the readouts (the status line, the API, the tests) ──
    public ApBehaviour ActiveBh; public bool Recovering, Loitering;
    public float AimErrDeg = float.NaN, Tof; public bool AimOn, InCone, InRange;
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
        v.AimOn = AimOn; v.InCone = InCone; v.InRange = InRange; v.AimErrDeg = AimErrDeg;
        v.Wp = Wp; v.WpCount = Cfg.ReturnHome ? 1 : _route.Count; v.WpName = WpName; v.WpDist = WpDist; v.Eta = Eta;
        v.HeadingNow = HeadingNow; v.HeightNow = HeightNow; v.SpeedNow = SpeedNow;
        v.HeadingOn = Cfg.HeadingOn; v.AltOn = Cfg.AltOn; v.SpeedOn = Cfg.SpeedOn;
        v.HeadingWant = Cfg.HeadingDeg; v.HeightWant = Cfg.AltM; v.SpeedWant = Cfg.SpeedMs > 0f ? Cfg.SpeedMs : _cruise;
        v.Prot = prot;
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
        if (st == ApStyle.Airplane)
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
        float agl = s.Agl;
        Alt.Reset(s.Pos, up, agl >= 0f ? agl : 0f);
        Tecs.Reset(); Bh.Reset(); Arb.Reset(); T.Clear();
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
            CapHeading = MathF.Round(ApCompass.Heading(dir, up)); CapAlt = MathF.Round(MathF.Max(0f, h)); CapSpeed = st == ApStyle.Airplane ? MathF.Round(air) : 0f;
            Cfg.HeadingDeg = CapHeading; Cfg.AltM = CapAlt; Cfg.SpeedMs = CapSpeed; _cruise = CapSpeed;
            CapturedHolds = true;
        }
        else if (Cfg.AltM <= 0f) { CapAlt = MathF.Round(MathF.Max(0f, h)); Cfg.AltM = CapAlt; CapHeading = Cfg.HeadingDeg; CapSpeed = Cfg.SpeedMs; CapturedHolds = true; }
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
    float Agl(in ApSense s) => Fd != null && Fd.Ground.HasGround ? Fd.Ground.Agl : s.Agl;

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
        var goal = ApNavGoal.None;
        ActiveBh = ApBehaviour.Auto; Loitering = false;
        if (Mode == ApMode.FlightHold) Holds(in s, up, cruise, ref goal);
        else if (Mode == ApMode.Navigate) RouteGoal(in s, up, cruise, chase, ref goal);
        else
        {
            if (T.Valid)
            {
                var c = new ApBhCtx { Style = Style, P = Cfg, Pos = s.Pos, Vel = s.Vel, Up = up, Speed = Style == ApStyle.Airplane ? air : vg, Cruise = cruise, Chase = chase,
                    TurnR = TurnR, Height = alt, Time = _time, T = T, THeight = Alt.Height(T.Pos, up), Decel = perf.Valid ? perf.DragDecelNow : 1f, NMax = perf.Valid ? perf.NAtAlphaMax : 0f };
                ActiveBh = Arb.Pick(Cfg.Behaviour, in c, dt);
                Bh.Plan(ActiveBh, in c, dt, ref goal);
                h = alt;   // (the target's heights are the altimeter's)
                // (a helicopter aiming fixed guns: level with the target - it cannot pitch onto it without moving)
                //  - and its gun's own pitch now (the lean, the trim; filtered) taken in: the height where that line meets the lead)
                if (Style == ApStyle.Helicopter && goal.Aim && Cfg.AimOn)
                {
                    Vector3 axis = s.GunAxis.LengthSquared() > 0.5f ? s.GunAxis : s.Nose.LengthSquared() > 0.5f ? s.Nose : _noseL;
                    float el = MathF.Asin(Math.Clamp(Vector3.Dot(Vector3.Transform(axis, s.Orient), up), -1f, 1f));
                    _gunEl += (el - _gunEl) * MathF.Min(1f, dt / 1.5f);
                    float flat = ApMath.Flat((Vector3)(T.Pos - s.Pos), up).Length();
                    goal.Height = c.THeight + Vector3.Dot(T.Vel, up) * Tof - flat * MathF.Tan(Math.Clamp(_gunEl, -0.5f, 0.5f));
                }


            }
            else
            {
                Arb.Reset();
                if (_route.Count > 0) RouteGoal(in s, up, cruise, chase, ref goal);
                else { Loiter(_home, up, ref goal); goal.Height = float.IsFinite(Cfg.AltM) && Cfg.AltM > 0f ? Cfg.AltM : h; goal.Speed = cruise; h = HeightIn(in s, up, alt); }
            }
        }
        // ── the terrain under it: never below MinAglM over the ground below, and up over what is ahead ──
        float hDem = goal.Height;
        float need = Cfg.MinAglM;
        if (need > 0f && agl >= 0f && agl < 1.5f * need) hDem = MathF.Max(float.IsFinite(hDem) ? hDem : h, h + (need - agl));
        if (need > 0f && Fd != null && Fd.Ground.Tti < 12f && Style == ApStyle.Airplane) hDem = MathF.Max(float.IsFinite(hDem) ? hDem : h, h + need);
        HeightWanted = hDem; Goal = goal;
        // ── the style's executor ──
        x = default; x.Throttle = float.NaN;
        Recovering = false;
        if (Style == ApStyle.Airplane) Airplane(in s, up, gT, h, hDem, air, aLatMax, nAvail, perf, ref goal, ref x);
        else Hover(in s, up, h, hDem, chase, ref goal, ref x);
        // ── the aim, jinking ──
        Aim(in s, up, in goal, ref x);
        if (Cfg.Evasive && Mode == ApMode.Engage && T.Valid) Jink(in s, up, aLatMax, dt, ref x);
        return true;
    }

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
        float hdot = Vector3.Dot(s.Vel, up);
        float vDem = goal.Max ? float.NaN : goal.Speed;
        Tecs.Step(h, hdot, hDem, Cfg.VsMs, air, vDem, thrust, drag, s.Mass, gT, aVertMax, s.Dt > 0f ? s.Dt : 1f / 60f, out float aVert, out float thr);
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
        if (face.LengthSquared() < 1e-6f) face = ApMath.Flat(v, up).LengthSquared() > 9f ? v : ApMath.Flat(rel, up).LengthSquared() > 400f ? rel : Vector3.Zero;
        x.Heading = face;
    }

    void Aim(in ApSense s, Vector3 up, in ApNavGoal goal, ref ApExternal x)
    {
        AimOn = goal.Aim && Cfg.AimOn && T.Valid;
        if (!AimOn) { AimErrDeg = float.NaN; InCone = InRange = false; LeadBlend = 0f; return; }
        Vector3 axis = s.GunAxis.LengthSquared() > 0.5f ? s.GunAxis : s.Nose.LengthSquared() > 0.5f ? s.Nose : _noseL;
        float u = Cfg.MuzzleSpeed > 0f ? Cfg.MuzzleSpeed : s.MuzzleSpeed;
        ApAim.Lead((Vector3)(T.Pos - s.Pos), T.Vel - s.Vel, T.Acc, Vector3.Zero, u, out Vector3 dir, out Tof);
        Vector3 gunW = Vector3.Transform(axis, s.Orient);
        AimErrDeg = ApAim.ErrorDeg(gunW, dir);
        float reach = s.GunRange > 0f ? s.GunRange : MathF.Max(1.5f * Cfg.RangeM, 1500f);
        InRange = T.Range <= reach;
        InCone = InRange && ApAim.InCone(AimErrDeg, Cfg.ConeDeg, T.Range, T.Radius);
        float w;
        if (Style == ApStyle.Airplane)
        {
            // (the nose off the flight path toward the lead, within a window the lift can still fly - beyond it the path's turn
            //  brings it round; only inside the gun's reach)
            Vector3 path = s.AirVel.LengthSquared() > 1f ? Vector3.Transform(Vector3.Normalize(s.AirVel), s.Orient) : gunW;
            float off = ApAim.ErrorDeg(path, dir);
            w = Math.Clamp((30f - off) / 15f, 0f, 1f) * (T.Range <= 1.2f * reach ? 1f : 0f);
            // (and off it before the target: the nose given back to the path from 7 s before reaching it, all of it by 4 s - the pull-out's time)
            if (T.Closing > 1f) w *= Math.Clamp((T.Range / T.Closing - 4f) / 3f, 0f, 1f);

        }
        else if (Style == ApStyle.Helicopter)
        {
            // (a helicopter yaws onto the lead; it does not pitch onto it - its lift axis is its nose's pitch, tilting it moves the
            //  ship: the behaviour holds it level with the lead instead - ApPilot.Step - so the nose is on it in height too)
            w = 0f; x.Heading = ApMath.Flat(dir, up);
        }

        else
        {
            // (six-axis: the nose anywhere - but in gravity tilted only as far as its side thrusters still hold their share of the
            //  weight: past it the ship could not hold its place - the elevation clipped, the error reported as it is)
            w = 1f;
            float g = s.Gravity.Length();
            Vector3 fh = ApMath.Flat(dir, up);
            if (g > 0.01f && fh.LengthSquared() > 1e-6f)
            {
                Quaternion inv = Quaternion.Inverse(s.Orient); Vector3 uL = Near(Vector3.Transform(up, inv));
                float side = float.MaxValue;
                for (int k = 0; k < 6; k++) if (MathF.Abs(C(uL, k % 3)) < 0.5f) side = MathF.Min(side, C(k < 3 ? s.ThrustPos : s.ThrustNeg, k % 3));
                float elMax = MathF.Asin(Math.Clamp(0.6f * side / MathF.Max(s.Mass * g, 1f), 0f, 1f));
                float el = Math.Clamp(MathF.Asin(Math.Clamp(Vector3.Dot(dir, up), -1f, 1f)), -elMax, elMax);
                dir = Vector3.Normalize(fh) * MathF.Cos(el) + up * MathF.Sin(el);
            }
        }
        LeadBlend = w;

        x.AimAxis = axis; x.AimDir = dir; x.Blend = w;

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
