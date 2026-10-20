#pragma warning disable CS0649, CS8632, CS1591, CS1573, CS1570, CS1574, CS0419, CS1734
using System;
namespace AeroMod;

/// <summary>What a navigation goal asks (ApNavGoal.Kind).</summary>
public enum ApNavKind : byte
{
    None,
    /// <summary>To a point (moving at PointVel): an airplane steers at it (L1), a helicopter / six-axis ship flies there and holds
    /// it (a station). Line: along From -> Point.</summary>
    Point,
    /// <summary>Round a circle about Point (moving at PointVel), Radius, Dir (+1 right turns).</summary>
    Circle,
    /// <summary>Along a course (Course, world): an airplane's heading; a hover's station moved along it at the speed.</summary>
    Course,
}

/// <summary>
/// ONE BEHAVIOUR'S GOAL for a tick (engine-free) - a parameterised goal on the shared core: where (a point, a circle, a course), at
/// what height (in the pilot's altimeter; NaN: the path held), at what speed (NaN: not held; Max: full throttle), the nose on the
/// target's lead or not, and a hover's facing. The style's executor (ApPilot) makes it the flight director's external goal.
/// </summary>
public struct ApNavGoal
{
    public ApNavKind Kind;
    public Vector3D Point, From; public bool Line; public Vector3 PointVel, PointAcc;
    public float Radius; public int Dir;
    public Vector3 Course;
    public float Height, Speed; public bool Max;
    public bool Aim;
    public Vector3 Face;
    public static ApNavGoal None => new ApNavGoal { Height = float.NaN, Speed = float.NaN };
}

/// <summary>What a behaviour sees each tick (engine-free; ApPilot fills it): the ship, its own measured turn (the radius at its
/// speed, from FlightPerf / the bank limit), the speeds it flies, the target's track, the panel's tactical numbers.</summary>
public struct ApBhCtx
{
    public ApStyle Style; public ApPanel P;
    public Vector3D Pos; public Vector3 Vel, Up; public float Speed, Cruise, Chase, TurnR, Height, Time;
    /// <summary>The deceleration it has with the throttle shut (m/s2: the drag's, FlightPerf) - a closure is planned with it.</summary>
    public float Decel;
    /// <summary>The load the lift gives at the stall margin now (g; FlightPerf.NAtAlphaMax - 0 not known): the energy to manoeuvre.</summary>
    public float NMax;
    public ApTargetTrack T; public float THeight;
}

/// <summary>
/// THE BEHAVIOURS (From the Depths' attack modes; engine-free): each a parameterised goal (ApNavGoal) from tactical numbers only
/// - ranges, angles, heights, aggressiveness; the turning circle, the speeds and the g are the ship's own (ApBhCtx: FlightPerf, the
/// thrust). Attack run (flyover) and its U-turn variant (begin / abort distance, a forced re-run after the re-run time), circle,
/// hover above / below, standoff (hold the range, back off when it closes), pursuit lead / pure / lag, and the arbiter's own extend
/// (energy back). Phases are kept here; a new behaviour starts its phase over. (2026-10-20: MOVEMENTS ONLY - where the ship goes; which
/// face it turns to the target is the panel's Facing, a desire ApPilot.Aim blends in. Point At and Broadside, which were both, are
/// Standoff and Circle now - ApPanel.Migrate; a circle or a standoff's crossing leg turns the way that keeps a chosen side on the target.)
/// </summary>
public sealed class ApBehaviours
{
    /// <summary>The attack run's phase (and the others' where they have one): 0 approach, 1 pass, 2 extend.</summary>
    public int Phase; public float PhaseTime, RunTime;
    /// <summary>Attack runs flown (a pass over the target counted), and passes' closest range (m) - the tests' geometry.</summary>
    public int Runs; public float LastPass = float.PositiveInfinity, PassMin = float.PositiveInfinity;
    ApBehaviour _was = (ApBehaviour)255;
    int _side = 1; Vector3 _runDir; float _orbitAng; bool _hasOrbit;

    public void Reset() { _was = (ApBehaviour)255; Phase = 0; PhaseTime = RunTime = 0f; _hasOrbit = false; }

    static Vector3 Flat(Vector3 v, Vector3 up) => ApMath.Flat(v, up);
    static Vector3 Unit(Vector3 v, Vector3 fallback) => v.LengthSquared() > 1e-6f ? Vector3.Normalize(v) : fallback;

    /// <summary>The goal of behaviour b now.</summary>
    public void Plan(ApBehaviour b, in ApBhCtx c, float dt, ref ApNavGoal g)
    {
        var T = c.T;
        if (b == ApBehaviour.PointAt) b = ApBehaviour.Standoff; else if (b == ApBehaviour.Broadside) b = ApBehaviour.Circle;   // (ApPanel.Migrate's: never chosen now)
        if (b != _was) { Begin(b, in c); _was = b; }
        PhaseTime += dt; RunTime += dt;
        bool plane = c.Style == ApStyle.Airplane;
        Vector3 up = c.Up, los = T.Los, losF = Unit(Flat(los, up), Unit(Flat(c.Vel, up), Vector3.UnitX));
        float range = T.Range, D = c.P.RangeM, hT = c.THeight + c.P.AltOffsetM;
        g = ApNavGoal.None;
        g.Height = hT; g.Speed = plane ? c.Cruise : c.Chase; g.Face = losF;
        PassMin = MathF.Min(PassMin, range);
        switch (b)
        {
            case ApBehaviour.AttackRun:
            case ApBehaviour.AttackRunUTurn:
            {
                bool uturn = b == ApBehaviour.AttackRunUTurn;
                float abort = c.P.AbortM, begin = MathF.Max(D, abort * 1.5f);
                if (Phase == 0)
                {
                    // approach: at the target's lead point, the nose on it inside the gun window
                    float tgo = Math.Clamp(range / MathF.Max(c.Speed + MathF.Max(T.Closing - c.Speed, 0f), 30f), 0f, 8f);
                    g.Kind = ApNavKind.Point; g.Point = T.Pos + (Vector3D)(T.Vel * tgo); g.PointVel = T.Vel; g.Aim = true;
                    if (!plane) { g.Point = T.Pos + (Vector3D)(_runDir * begin); g.Face = _runDir; }   // (a hover's strafe: on through, over the target)
                    bool passed = plane ? range < abort || (T.Closing < 0f && range < begin * 0.6f) : Vector3.Dot(Flat((Vector3)(T.Pos - c.Pos), up), _runDir) < 0f;
                    if (passed) { Phase = 1; PhaseTime = 0f; Runs++; LastPass = PassMin; PassMin = float.PositiveInfinity; }
                    else if (RunTime > c.P.RerunS) { Phase = 2; PhaseTime = 0f; }   // (the run took too long: off, and in again)
                }
                else if (Phase == 1)
                {
                    // the pass: straight on, guns off; then away (or a U-turn straight back in)
                    g.Kind = ApNavKind.Course; g.Course = Unit(Flat(c.Vel, up), losF); g.Max = true;
                    if (!plane) { g.Kind = ApNavKind.Point; g.Point = T.Pos + (Vector3D)(_runDir * begin); g.Face = _runDir; }
                    float outR = uturn ? abort + 0.8f * c.TurnR : abort * 1.5f;
                    bool clear = plane ? range > outR && T.Closing < 0f : Vector3.Distance((Vector3)g.Point, (Vector3)c.Pos) < 0.2f * begin || PhaseTime > 20f;
                    if (clear) { Phase = uturn ? 0 : 2; PhaseTime = 0f; if (uturn) { RunTime = 0f; _runDir = -_runDir; } }
                }
                else
                {
                    // extend: away to the begin distance, then turn back in
                    g.Kind = ApNavKind.Course; g.Course = -losF; g.Max = true;
                    if (!plane) { g.Kind = ApNavKind.Point; g.Point = T.Pos + (Vector3D)(_runDir * begin); g.Face = -losF; }
                    if (range > begin || PhaseTime > 60f) { Phase = 0; PhaseTime = 0f; RunTime = 0f; _runDir = -_runDir; }
                }
                break;
            }
            case ApBehaviour.Circle:
                _side = SideFor(in c, plane, _side);
                if (plane) { g.Kind = ApNavKind.Circle; g.Point = T.Pos; g.PointVel = T.Vel; g.Radius = MathF.Max(D, 1.3f * c.TurnR); g.Dir = _side; }
                else { Orbit(in c, D, 1f, dt, ref g); }
                break;
            case ApBehaviour.HoverAbove:
            case ApBehaviour.HoverBelow:
            {
                float dh = MathF.Abs(c.P.AltOffsetM) * (b == ApBehaviour.HoverBelow ? -1f : 1f);
                g.Height = c.THeight + dh;
                if (plane) { g.Kind = ApNavKind.Circle; g.Point = T.Pos; g.PointVel = T.Vel; g.Radius = MathF.Max(D * 0.5f, 1.3f * c.TurnR); g.Dir = _side; }
                else { g.Kind = ApNavKind.Point; g.Point = T.Pos; g.PointVel = T.Vel; g.PointAcc = T.Acc; g.Aim = c.Style == ApStyle.SixAxis; }   // (a helicopter's forward guns cannot bear straight down)

                break;
            }
            case ApBehaviour.Standoff:
                if (plane)
                {
                    // kite: away when it closes inside the band, in when it is far, across (circling) in the band
                    if (Phase == 0 && range < 0.7f * D) Phase = 1; else if (Phase == 1 && range > D) Phase = 0;
                    if (Phase == 0 && range > 1.3f * D) Phase = 2; else if (Phase == 2 && range < D) Phase = 0;
                    _side = SideFor(in c, true, _side);
                    if (Phase == 1) { g.Kind = ApNavKind.Course; g.Course = -losF; g.Max = true; }
                    else if (Phase == 2) { g.Kind = ApNavKind.Point; g.Point = T.Pos; g.PointVel = T.Vel; g.Aim = true; }
                    else { g.Kind = ApNavKind.Course; g.Course = Vector3.Transform(losF, Quaternion.CreateFromAxisAngle(up, -90f * ApMath.Deg * _side)); }
                }
                else
                {
                    g.Kind = ApNavKind.Point; g.Point = T.Pos - (Vector3D)(losF * D); g.PointVel = T.Vel; g.PointAcc = T.Acc; g.Aim = true;
                    g.Height = c.THeight + c.P.AltOffsetM;
                }
                break;
            case ApBehaviour.PursuitLead:
            case ApBehaviour.PursuitPure:
            case ApBehaviour.PursuitLag:
            {
                float k = b == ApBehaviour.PursuitLead ? 1f : b == ApBehaviour.PursuitLag ? -0.5f : 0f;
                float tgo = Math.Clamp(range / MathF.Max(MathF.Abs(T.Closing), 40f), 0f, 6f);
                Vector3D aimP = T.Pos + (Vector3D)(T.Vel * (k * tgo));
                if (plane)
                {
                    g.Kind = ApNavKind.Point; g.Point = aimP; g.PointVel = T.Vel; g.Aim = true;
                    g.Height = c.THeight + Vector3.Dot((Vector3)(aimP - T.Pos), up);   // (3D: the aim point's own height)
                    // closure: full speed far out, the target's speed plus the gap's at the gun range
                    // closure: no faster than the drag can take out by the gun range (sqrt(2 a d), a margin), the target's own speed
                    // along the line of sight under it
                    float tAlong = Vector3.Dot(T.Vel, los), dec = MathF.Max(0.3f, c.Decel);
                    float close = range > D ? MathF.Sqrt(2f * 0.6f * dec * (range - D)) : -0.15f * (D - range);
                    g.Speed = Math.Clamp(tAlong + close, 0.55f * c.Cruise, c.Cruise);
                }
                else
                {
                    Vector3 behind = Unit(Flat(-T.Vel, up), -losF);
                    Vector3 off = k < 0f ? behind : -losF;
                    g.Kind = ApNavKind.Point; g.Point = T.Pos + (Vector3D)(off * D); g.PointVel = T.Vel; g.PointAcc = T.Acc; g.Aim = true;
                }
                break;
            }
            case ApBehaviour.Extend:
                g.Kind = ApNavKind.Course; g.Course = -losF; g.Max = true; g.Height = MathF.Max(c.Height, hT);
                break;
            default:
                g.Kind = ApNavKind.Point; g.Point = T.Pos; g.PointVel = T.Vel; g.Aim = true;
                break;
        }
        if (!plane && g.Kind == ApNavKind.Point && b != ApBehaviour.AttackRun && b != ApBehaviour.AttackRunUTurn) g.Face = losF;
    }

    /// <summary>The way round that keeps the chosen side on the target (the panel's Facing): a plane's circle turning right
    /// (Dir +1, clockwise from above) has the target on its right; a hover's orbit (Orbit: its tangent is cross(out, up) x -side)
    /// flies with its right side in at side -1. Any other facing: the side it already turns (Begin).</summary>
    static int SideFor(in ApBhCtx c, bool plane, int side) => c.P.Facing switch
    {
        ApFacing.SideRight => plane ? 1 : -1,
        ApFacing.SideLeft => plane ? -1 : 1,
        _ => side,
    };

    /// <summary>A hover's orbit: a station on the circle of radius r about the target at the current bearing, moved round at the
    /// chase speed x share (0: held where it is on the circle).</summary>
    void Orbit(in ApBhCtx c, float r, float share, float dt, ref ApNavGoal g)
    {
        var T = c.T; Vector3 up = c.Up;
        Vector3 rel = Flat((Vector3)(c.Pos - T.Pos), up);
        ApCompass.Frame(up, out var n, out var e);
        if (!_hasOrbit) { _orbitAng = MathF.Atan2(Vector3.Dot(rel, e), Vector3.Dot(rel, n)); _hasOrbit = true; }
        float w = share * c.Chase * 0.5f / MathF.Max(r, 1f);
        _orbitAng += w * _side * dt;
        Vector3 rad = n * MathF.Cos(_orbitAng) + e * MathF.Sin(_orbitAng), tan = Vector3.Cross(rad, up) * -_side;
        g.Kind = ApNavKind.Point; g.Point = T.Pos + (Vector3D)(rad * r); g.PointVel = T.Vel + tan * (w * r); g.PointAcc = T.Acc;
    }

    void Begin(ApBehaviour b, in ApBhCtx c)
    {
        Phase = 0; PhaseTime = 0f; RunTime = 0f; _hasOrbit = false; PassMin = float.PositiveInfinity;
        Vector3 up = c.Up;
        Vector3 rel = Flat((Vector3)(c.Pos - c.T.Pos), up), v = Flat(c.Vel, up);
        // (the side: the way the ship already turns round the target - the cross of its velocity and the line to it)
        _side = Vector3.Dot(Vector3.Cross(v, -rel), up) >= 0f ? -1 : 1;
        _runDir = Unit(-rel, Vector3.UnitX);
    }
}

/// <summary>
/// THE ARBITER (engine-free): one behaviour flies at a time - utility scores (0..1) over the situation (range, the target's speed
/// against ours, our energy, the style), the active one kept with a bonus (Hysteresis) and for at least MinDwell seconds, so two
/// behaviours never fight and a boundary never makes it flip-flop. A behaviour chosen on the panel is the only candidate beside the
/// airplane's energy recovery (Extend).
/// </summary>
public sealed class ApArbiterBh
{
    public const float Hysteresis = 0.15f, MinDwell = 3f;
    public ApBehaviour Active = ApBehaviour.Auto; public float Dwell; public int Switches;
    /// <summary>(2026-10-19) The combat slots' own utility this tick (ApDogfight.Score / ApRocketGuidance.Score - the pilot asks them
    /// in Auto while they are Implemented; NaN: not available - skipped).</summary>
    public float DogfightScore = float.NaN, RocketScore = float.NaN;
    public void Reset() { Active = ApBehaviour.Auto; Dwell = 0f; Switches = 0; }

    public ApBehaviour Pick(ApBehaviour chosen, in ApBhCtx c, float dt)
    {
        Dwell += dt;
        ApBehaviour best = ApBehaviour.Auto; float bestS = float.NegativeInfinity;
        void Try(ApBehaviour b, float s) { if (b == Active) s += Hysteresis; if (s > bestS) { bestS = s; best = b; } }
        bool plane = c.Style == ApStyle.Airplane;
        var T = c.T;
        float tv = T.Vel.Length(), my = MathF.Max(c.Speed, 1f), D = c.P.RangeM;
        // (the energy recovery is the built-in behaviours': a slot or a formation chosen keeps its own energy)
        bool own = chosen == ApBehaviour.Formation || chosen == ApBehaviour.Dogfight || chosen == ApBehaviour.RocketGuidance;
        if (plane && !own)
        {
            // energy: slow and close - away and back up to speed first
            // (out of energy: the lift at the stall margin gives under 1.6 g - it could not turn with the target; back once over 2.2)
            float lowE = c.NMax > 0f && c.NMax < 1.6f && T.Range < 3f * D ? 0.9f : 0f;
            if (Active == ApBehaviour.Extend && c.NMax > 0f && c.NMax < 2.2f) lowE = MathF.Max(lowE, 0.8f);

            Try(ApBehaviour.Extend, lowE);
        }
        if (chosen != ApBehaviour.Auto) Try(chosen, 0.6f);
        else
        {
            // (the combat slots: only with a score - NaN, not written yet or not available, skips them)
            if (float.IsFinite(DogfightScore)) Try(ApBehaviour.Dogfight, DogfightScore);
            if (float.IsFinite(RocketScore)) Try(ApBehaviour.RocketGuidance, RocketScore);
            if (plane)
            {
                bool slow = tv < 0.35f * my;
                Try(ApBehaviour.AttackRun, slow ? 0.7f : 0.2f);
                Try(ApBehaviour.PursuitLead, slow ? 0.25f : 0.6f + (T.Range < 3f * D ? 0.2f : 0f));
                Try(ApBehaviour.Circle, 0.3f);
            }
            else
            {
                bool below = c.THeight < c.Height - 20f, slow = tv < 5f;
                Try(ApBehaviour.Standoff, 0.6f);
                Try(ApBehaviour.HoverAbove, below && slow && T.Range < 2f * D ? 0.55f : 0.1f);
                Try(ApBehaviour.Circle, c.Style == ApStyle.SixAxis && !slow ? 0.5f : 0.2f);   // (2026-10-20: was Broadside - a movement and a facing; the facing is the panel's now)
            }
        }
        if (best != Active && (Dwell >= MinDwell || Active == ApBehaviour.Auto)) { if (Active != ApBehaviour.Auto) Switches++; Active = best; Dwell = 0f; }
        return Active;
    }
}
