#pragma warning disable
using System;

namespace AeroMod;

/// <summary>A part's pose in its block's frame.</summary>
public readonly record struct GearPose(Vector3 Position, Quaternion Orientation);

/// <summary>
/// Where a retractable gear's parts are, in its block's frame (pure; the client animation and the tests use it).
/// Built from the DEPLOYED wheel hub - the point the vanilla suspension spawns its wheel at - so the folded-out leg puts
/// the visual wheel exactly where the real one appears:
///  - the LEG turns about the hinge (HingePoint, HingeAxis) from stowed (fold 0: turned by StowAngle) to down (fold 1);
///  - the ROD (the oleo's chrome piston, carrying the axle) rides the leg and slides up it by the suspension's
///    compression;
///  - the ACTUATOR runs from a fixed anchor on the block to an anchor on the leg - its cylinder at the block end aimed
///    at the leg end, its rod at the leg end aimed back (they telescope as the leg turns);
///  - each DOOR turns about its own hinge by its open angle times the doors' opening (0 shut .. 1 open).
/// Every subpart is spawned at its dummy (its rest pose, the gear down and unloaded, doors shut); a pose here is where it
/// goes, given that rest pose.
/// </summary>
public sealed class GearLayout
{
    public Vector3 HingePoint, HingeAxis;
    /// <summary>Signed: the leg turned by this about HingeAxis is stowed (rad).</summary>
    public float StowAngle;
    /// <summary>The hub with the gear down and the suspension unloaded.</summary>
    public Vector3 DeployedHub;
    /// <summary>A pad-foot gear (2026-10-21): its foot's ankle pin - FootDrop below the rod's hub along the leg (gear down, unloaded),
    /// where the foot subpart's dummy sits (and so how it is told from the rod) and about which it turns (FootIk).</summary>
    public bool HasFoot;
    public const float FootDrop = 0.08f;
    public Vector3 FootPoint => DeployedHub + LegDirection * FootDrop;
    /// <summary>The actuator's anchors (with the gear down): on the block, and on the leg.</summary>
    public Vector3 ActuatorBase, ActuatorLegEnd;
    public DoorLayout[] Doors = Array.Empty<DoorLayout>();
    /// <summary>The block's model has the gear's moving parts as subparts (the client animates them); false: a test
    /// block (the fold is physical only - the server's fold wheel - and nothing is animated).</summary>
    public bool AnimatedParts;

    public readonly record struct DoorLayout(Vector3 HingePoint, Vector3 HingeAxis, float OpenAngle);

    /// <summary>
    /// RAKED SLIDE (the forward-folding gear, 2026-10-13): the real wheel moves in a straight line - along RakeAxis
    /// (unit, from the deployed hub toward the stowed one), by the travel the server publishes (the suspension's
    /// compression down, the retraction's slide up) - and the leg turns about the hinge to point at it, telescoping (the
    /// rod rides the hub). False: the leg turns by the fold (LegTurn) and the rod slides up it.
    /// </summary>
    public bool RakedSlide;
    public Vector3 RakeAxis;

    /// <summary>Raked slide: the hub after sliding s along the rake.</summary>
    public Vector3 RakedHub(float s) => DeployedHub + Vector3.Normalize(RakeAxis) * s;

    /// <summary>Raked slide: the leg's turn about the hinge that points it from the deployed hub at RakedHub(s).</summary>
    public Quaternion RakedTurn(float s)
    {
        Vector3 ax = Vector3.Normalize(HingeAxis);
        Vector3 a = DeployedHub - HingePoint, b = RakedHub(s) - HingePoint;
        a -= ax * Vector3.Dot(a, ax); b -= ax * Vector3.Dot(b, ax);   // (in the plane the leg turns in)
        if (a.LengthSquared() < 1e-12f || b.LengthSquared() < 1e-12f) return Quaternion.Identity;
        a = Vector3.Normalize(a); b = Vector3.Normalize(b);
        float angle = MathF.Atan2(Vector3.Dot(Vector3.Cross(a, b), ax), Vector3.Dot(a, b));
        return Quaternion.CreateFromAxisAngle(ax, angle);
    }

    /// <summary>Raked slide: a part fixed to the leg (rest pose: gear down), with the wheel slid s along the rake.</summary>
    public GearPose RakedLeg(in GearPose rest, float s) => RotateAbout(rest, HingePoint, RakedTurn(s));

    /// <summary>Raked slide: a part fixed to the rod (rest pose: gear down, unloaded) - carried to the hub, turned with
    /// the leg about it.</summary>
    public GearPose RakedRod(in GearPose rest, float s)
    {
        var q = RakedTurn(s); Vector3 hub = RakedHub(s);
        return new GearPose(hub + Vector3.Transform(rest.Position - DeployedHub, q), Quaternion.Normalize(q * rest.Orientation));
    }

    /// <summary>The leg's direction down (hinge to hub, unit) and length.</summary>
    public Vector3 LegDirection => Vector3.Normalize(DeployedHub - HingePoint);
    public float LegLength => (DeployedHub - HingePoint).Length();

    /// <summary>The leg's turn at a fold (0 stowed .. 1 down).</summary>
    public Quaternion LegTurn(float fold) => Quaternion.CreateFromAxisAngle(Vector3.Normalize(HingeAxis), (1f - Math.Clamp(fold, 0f, 1f)) * StowAngle);

    /// <summary>Where the hub is: the leg turned, the rod slid up it by the suspension's compression (m).</summary>
    public Vector3 Hub(float fold, float compression = 0f)
        => HingePoint + Vector3.Transform(DeployedHub - HingePoint - LegDirection * Math.Max(0f, compression), LegTurn(fold));

    /// <summary>A part fixed to the leg (rest pose: gear down), with the leg at this fold.</summary>
    public GearPose OnLeg(in GearPose rest, float fold) => RotateAbout(rest, HingePoint, LegTurn(fold));

    /// <summary>A part fixed to the rod (rest pose: gear down, unloaded): on the leg, slid up by the compression.</summary>
    public GearPose OnRod(in GearPose rest, float fold, float compression)
    {
        var q = LegTurn(fold);
        var slid = new GearPose(rest.Position - LegDirection * Math.Max(0f, compression), rest.Orientation);
        return RotateAbout(slid, HingePoint, q);
    }

    /// <summary>The actuator: its cylinder (at ActuatorBase, aimed at the leg end) and its rod (at the leg end, aimed
    /// back), given their rest poses (gear down), and its length now.</summary>
    public (GearPose cylinder, GearPose rod, float length) Actuator(in GearPose cylinderRest, in GearPose rodRest, float fold)
    {
        Vector3 legEnd = HingePoint + Vector3.Transform(ActuatorLegEnd - HingePoint, LegTurn(fold));
        Vector3 rest = Vector3.Normalize(ActuatorLegEnd - ActuatorBase), now = legEnd - ActuatorBase;
        float len = now.Length();
        var turn = Between(rest, now / Math.Max(1e-6f, len));
        var cyl = RotateAbout(cylinderRest, ActuatorBase, turn);
        // (the rod: carried to the leg end, turned the same - it slides out of the cylinder along the same line)
        var rod = new GearPose(legEnd + Vector3.Transform(rodRest.Position - ActuatorLegEnd, turn), Quaternion.Normalize(turn * rodRest.Orientation));
        return (cyl, rod, len);
    }

    /// <summary>A door (rest pose: shut) opened by doors (0 shut .. 1 open).</summary>
    public GearPose Door(int i, in GearPose rest, float doors)
    {
        var d = Doors[i];
        return RotateAbout(rest, d.HingePoint, Quaternion.CreateFromAxisAngle(Vector3.Normalize(d.HingeAxis), Math.Clamp(doors, 0f, 1f) * d.OpenAngle));
    }

    public static GearPose RotateAbout(in GearPose p, Vector3 pivot, Quaternion q)
        => new GearPose(pivot + Vector3.Transform(p.Position - pivot, q), Quaternion.Normalize(q * p.Orientation));

    /// <summary>The shortest turn taking unit a onto unit b.</summary>
    public static Quaternion Between(Vector3 a, Vector3 b)
    {
        float d = Vector3.Dot(a, b);
        if (d > 0.999999f) return Quaternion.Identity;
        if (d < -0.999999f)
        {
            var ax = Vector3.Cross(Vector3.UnitX, a); if (ax.LengthSquared() < 1e-6f) ax = Vector3.Cross(Vector3.UnitY, a);
            return Quaternion.CreateFromAxisAngle(Vector3.Normalize(ax), MathF.PI);
        }
        var c = Vector3.Cross(a, b);
        return Quaternion.Normalize(new Quaternion(c.X, c.Y, c.Z, 1f + d));
    }
}
