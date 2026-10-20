#pragma warning disable CS0649, CS8632, CS1591, CS1573, CS1570, CS1574, CS0419, CS1734
using System;
namespace AeroMod;

/// <summary>The dogfight slot's panel values (ApPanel's, clamped) - what the author's ApDogfight reads. See AUTHORING.md for the
/// field -> panel label table and how to add one.</summary>
public struct ApDogfightParams
{
    /// <summary>Aggressiveness 0..1 (the panel's "Aggressiveness", shared with the other behaviours).</summary>
    public float Aggression;
    /// <summary>"Preferred Fight": Auto / Angles / Energy.</summary>
    public ApFightPref Fight;
    /// <summary>"Head-on Passes" allowed.</summary>
    public bool HeadOn;
    /// <summary>"Hard Deck (m over ground)" and "Combat Ceiling (m)" (0: off) - the core's height already keeps them for a nav goal
    /// (ApSlotOut.UseNav); a direct goal is the author's to keep inside them.</summary>
    public float DeckM, CeilingM;
    /// <summary>"Bingo Fuel (%)" and "Bingo Damage (%)" (0: off) - compare with ApSlotCtx.S.FuelShare / IntegrityShare.</summary>
    public float BingoFuelPct, BingoDamagePct;
    /// <summary>"Respect Enemy Guns" 0..1 and "Skill" 0..1.</summary>
    public float RespectGuns, Skill;

    public static ApDogfightParams Of(in ApPanel p) => new ApDogfightParams
    {
        Aggression = p.Aggression, Fight = p.DfFight, HeadOn = p.DfHeadOn, DeckM = p.DeckM, CeilingM = p.CeilingM,
        BingoFuelPct = p.DfBingoFuelPct, BingoDamagePct = p.DfBingoDamagePct, RespectGuns = p.DfRespectGuns, Skill = p.DfSkill,
    };
}

/// <summary>The rocket-guidance slot's panel values (ApPanel's, clamped) - what the author's ApRocketGuidance reads.</summary>
public struct ApRocketParams
{
    /// <summary>"Guidance Law": ProNav / WhipNav / HybridNav / QuadraticIntercept / AugmentedPN / Pursuit (an enum to switch on).</summary>
    public ApGuidanceLaw Law;
    /// <summary>"Navigation Constant" (1..8, default 3), "Target Accel Gain" (0..3, default 1.5).</summary>
    public float NavConst, TargetAccelGain;
    /// <summary>"Max Lateral g": the cap the core applies to the slot's Lift load / Accel across the path (the protection clips
    /// further).</summary>
    public float MaxLatG;
    /// <summary>"Gravity Compensation" on.</summary>
    public bool GravityComp;
    /// <summary>"Arm Distance (m)" and "Let-go Timeout (s)" (0: off) - values only; the core does nothing with them.</summary>
    public float ArmM, TimeoutS;

    public static ApRocketParams Of(in ApPanel p) => new ApRocketParams
    {
        Law = p.RgLaw, NavConst = p.RgNavConst, TargetAccelGain = p.RgTargetAccelGain, MaxLatG = p.RgMaxLatG, GravityComp = p.RgGravityComp,
        ArmM = p.RgArmM, TimeoutS = p.RgTimeoutS,
    };
}

/// <summary>
/// EVERYTHING A SLOT'S ALGORITHM GETS, in one place (engine-free; one instance per pilot, filled by ApPilot each step before the
/// slot's Score / Step - read it, never keep a reference to its insides across steps but the track's). All world frame, SI.
/// </summary>
public sealed class ApSlotCtx
{
    /// <summary>The tick (s) and the time since this behaviour took over (s).</summary>
    public float Dt, Time;
    /// <summary>OWN SHIP: the instruments (pose Orient, Pos, Vel, AngVel - local rad/s -, Gravity / GravityTrue, Mass, AirVel - local -,
    /// Density, SpeedOfSound, ThrustPos / ThrustNeg - N per local side -, Nose, GunAxis, MuzzleSpeed, GunRange, FuelShare /
    /// IntegrityShare (HasShipStatus), Lim - the protection's limits -, the stall evaluation).</summary>
    public ApSense S;
    /// <summary>The flight style (Airplane / Helicopter / SixAxis), up (unit, against the gravity), the gravity the body falls with
    /// (m/s2), the pilot's altimeter height (m over the engage datum), height over the ground (m; &lt; 0 not known), airspeed (m/s),
    /// Mach (0: no speed of sound known).</summary>
    public ApStyle Style; public Vector3 Up; public float G, Height, Agl, AirSpeed, Mach;
    /// <summary>The main thrust axis (world unit), its full thrust (N), that over the weight.</summary>
    public Vector3 MainAxisW; public float MainThrust, ThrustToWeight;
    /// <summary>OWN PERFORMANCE now (FlightPerf.Evaluate: NInst / NSust, turn rates, CornerSpeed, TurnRateCorner, Ps, drag, AlphaMax;
    /// Valid false: not known - slow, no air) and its samples (for FlightPerf.NInstAt / TurnRateInstAt / NSustAt / PsAt at any speed).</summary>
    public ApPerf Perf; public PerfSamples Samples;
    /// <summary>The LIFT GOAL's limits: the most load the protection lets a Lift goal pull now (g; Perf.NInst, else the g limit), the
    /// most nose-up from the flow (rad; Perf.AlphaMax; NaN not known), the g limit (g).</summary>
    public float LiftNMax, AlphaMax, GLimit;
    /// <summary>THE TARGET: valid, and its track - Target.Pos / Vel / Acc (the source's), Range, Closing, LosRate, AngleOff, Los,
    /// Name, Id, Radius, Lost (s); Target.Motion (= Track): estimated from its observed positions alone - Pos, Vel, Acc, TurnRate,
    /// EnergyHeight(refPos), SpecificPower, LoadFactor, Confidence (0..1), Age (s), Predict(t).</summary>
    public bool HasTarget; public ApTargetTrack Target;
    public ApMotionTrack Track => Target?.Motion;
    /// <summary>The terrain (null: none known): Terrain.Below(p, down, reach, out height, out normal), Terrain.Cast(p, dir, len, out
    /// dist, out normal); and the flight director's watch of it: Ground.Agl, Ground.Tti (s to impact), Ground.AheadDist (m).</summary>
    public ITerrainQuery Terrain; public TerrainWatch Ground;
    /// <summary>The whole panel (clamped) and the slots' own values from it.</summary>
    public ApPanel Panel; public ApDogfightParams Dogfight; public ApRocketParams Rocket;

    /// <summary>THE LEAD POINT for a projectile at muzzle speed u (m/s; 0: the panel's, else the grid's guns') against the target -
    /// ApAim.Lead (the intercept iterated, the shooter's velocity in, SE2 4.0's straight shots): the aim (world unit) and the time
    /// of flight (s). useTrack: the observed-motion track's velocity / acceleration instead of the source's. False: no solution.</summary>
    public bool Lead(float u, out Vector3 dir, out float tof, bool useTrack = false)
    {
        dir = Vector3.UnitX; tof = 0f;
        if (!HasTarget || Target == null) return false;
        if (!(u > 0f)) u = Panel.MuzzleSpeed > 0f ? Panel.MuzzleSpeed : S.MuzzleSpeed;
        var m = Target.Motion;
        bool tr = useTrack && m.Valid;
        Vector3D p = tr ? m.Pos : Target.Pos; Vector3 v = tr ? m.Vel : Target.Vel, a = tr ? m.Acc : Target.Acc;
        return ApAim.Lead((Vector3)(p - S.Pos), v - S.Vel, a, Vector3.Zero, u, out dir, out tof);
    }

    /// <summary>The angle (deg) between a world direction and the guns' axis (the nose without guns) now.</summary>
    public float GunErrorDeg(Vector3 dirW)
    {
        Vector3 axis = S.GunAxis.LengthSquared() > 0.5f ? S.GunAxis : S.Nose.LengthSquared() > 0.5f ? S.Nose : -Vector3.UnitZ;
        return ApAim.ErrorDeg(Vector3.Transform(axis, S.Orient), dirW);
    }

    /// <summary>The ground's height under a point (m along the up, from the point down; false: none found within reach m).</summary>
    public bool GroundBelow(Vector3D p, float reach, out float height)
    {
        height = float.NaN;
        return Terrain != null && Terrain.Below(p, -Up, reach, out height, out _);
    }
}

/// <summary>
/// WHAT A SLOT RETURNS for a step. Either a NAV goal (UseNav: an ApNavGoal - a point, a circle, a course, a height, a speed - flown
/// by the style's own executor, the terrain minimum and the altitude band kept, the aim and jinking as for the built-in behaviours)
/// or a DIRECT goal (Goal: any ApExternalKind - Track, Accel, Attitude, Lift - with its Throttle and its aim fields), flown as it
/// is by the flight director (every protection, the terrain, the collision avoidance on top). LetGo: not None - the behaviour lets
/// go (the mode ends with that reason: ApText.Say). Status: the slot's own words for the status line ("Dogfight - Status on X
/// 1.2 km"; keep it a cached string - no allocation a tick). AimReport: the pilot's aim report (ApPilot.AimErrDeg / InCone /
/// InRange, AeroApi.TryGetAutopilotAim) made against the target's lead each tick; AimNose: and the nose blended onto it as the
/// built-in behaviours do (a nav goal or a direct goal without its own aim). TargetClearM: the clearance the collision avoidance
/// keeps from the target (m; NaN: half the range - a slot that rams sets it small, never below the target's own size).
/// </summary>
public struct ApSlotOut
{
    public bool UseNav; public ApNavGoal Nav;
    public ApExternal Goal;
    public ApReason LetGo;
    public string Status;
    public bool AimReport, AimNose;
    public float TargetClearM;

    /// <summary>A fresh output: nothing asked (Goal.Kind None, its throttle and load NaN - the autopilot's own), no let-go.</summary>
    public static ApSlotOut None
    {
        get
        {
            var o = new ApSlotOut { Nav = ApNavGoal.None, TargetClearM = float.NaN };
            o.Goal.Throttle = float.NaN; o.Goal.Load = float.NaN;
            return o;
        }
    }
}
