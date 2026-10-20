#pragma warning disable CS0649, CS8632, CS1591, CS1573, CS1570, CS1574, CS0419, CS1734
using System;
namespace AeroMod;

/// <summary>
/// THE ROCKET-GUIDANCE SLOT - THE AUTHOR'S FILE. The core (ApPilot, Engage mode, Behaviour "Rocket Guidance" or Auto) calls these
/// each step with everything in one place (ApSlotCtx: own instruments - a small unpiloted rocket grid's pose, velocity, rates,
/// thrust, Mach -, its performance where it has lift, the target's track and its observed motion, the lead point, the terrain,
/// the panel's guidance values: law, navigation constant, target-acceleration gain, the lateral g cap, gravity compensation, arm
/// distance, timeout) and flies what Step returns (ApSlotOut: any goal kind - a Lift goal is the usual lateral command, an Accel
/// goal the same in m/s2 -, its throttle, a let-go reason, the status text) through the protection, steered by the real fins
/// (control surfaces) and the thrust-vectoring nozzles (ThrustVector) - pilot inputs only. The core caps a Lift goal's load and
/// an Accel goal's part across the path at the panel's Max Lateral g. A rocket in Engage with this behaviour is not refused for
/// low airspeed (it launches from rest). Until Implemented: refused when chosen, never picked by Auto.
/// No allocation and no try/catch in Step / Score. AUTHORING.md: the checklist, the data flow, the test.
/// </summary>
public sealed class ApRocketGuidance
{
    /// <summary>TODO (author): true once Step flies it.</summary>
    public static bool Implemented => false;

    /// <summary>The author's own state (Reset when the behaviour takes over; optionally saved with the block - RgSaved).</summary>
    public readonly ApRocketState State = new();

    /// <summary>When the behaviour takes over: the state from rest, its saved text read back.</summary>
    public void Begin(ApSlotCtx c, string saved)
    {
        State.Reset();
        if (!string.IsNullOrEmpty(saved)) State.Load(saved);
        // TODO (author): anything to catch at launch.
    }

    /// <summary>Auto's utility now (0..1; NaN: not available - Auto skips it). TODO (author).</summary>
    public float Score(ApSlotCtx c) => float.NaN;

    /// <summary>ONE STEP: fill o and return true; false: declined - the behaviour lets go (NotImplemented, or o.LetGo).
    /// TODO (author): the guidance - read c (c.Rocket.Law / NavConst / ..., c.Track, c.Lead(...)), write o.Goal (Kind Lift: o.Goal.Lift,
    /// o.Goal.Load; or Accel: o.Goal.Accel), o.Goal.Throttle, o.Status, o.LetGo (e.g. ApReason.TimedOut, ApReason.LostTarget).</summary>
    public bool Step(ApSlotCtx c, ref ApSlotOut o)
    {
        return false;
    }
}

/// <summary>
/// THE ROCKET-GUIDANCE SLOT'S STATE - THE AUTHOR'S (fields, Reset, Save / Load, Dirty: as ApDogfightState; saved in the panel's
/// RgSaved, the key's value 67).
/// </summary>
public sealed class ApRocketState
{
    public bool Dirty;
    public void Reset() { Dirty = false; /* TODO (author) */ }
    public string Save() => "";
    public void Load(string s) { }
}
