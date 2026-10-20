#pragma warning disable CS0649, CS8632, CS1591, CS1573, CS1570, CS1574, CS0419, CS1734
using System;
namespace AeroMod;

/// <summary>
/// THE DOGFIGHT SLOT - THE AUTHOR'S FILE. The core (ApPilot, Engage mode, Behaviour "Dogfight" or Auto) calls these each step on
/// the simulation thread with everything in one place (ApSlotCtx: own instruments, own performance and the lift goal's limits,
/// the target's track - its observed motion too -, the lead point, the terrain, the panel's dogfight values) and flies what Step
/// returns (ApSlotOut: a nav goal or any direct goal - Track / Accel / Attitude / Lift - with its throttle, a let-go reason, the
/// status text, the aim report) through the envelope protection, the terrain and the collision avoidance, pilot inputs only.
/// Until Implemented is true: chosen on the panel it refuses ("this behaviour is not written yet"); Auto never picks it.
/// No allocation and no try/catch in Step / Score (they run every tick). AUTHORING.md: the checklist, the data flow, the test.
/// </summary>
public sealed class ApDogfight
{
    /// <summary>TODO (author): true once Step flies it - the panel takes the behaviour and Auto may pick it.</summary>
    public static bool Implemented => false;

    /// <summary>The author's own state, kept across steps (Reset when the behaviour takes over; optionally saved with the block).</summary>
    public readonly ApDogfightState State = new();

    /// <summary>When the behaviour takes over (chosen, or Auto switched to it): the state from rest, its saved text (the panel's
    /// DfSaved - "" none) read back.</summary>
    public void Begin(ApSlotCtx c, string saved)
    {
        State.Reset();
        if (!string.IsNullOrEmpty(saved)) State.Load(saved);
        // TODO (author): anything to catch at the start of a fight.
    }

    /// <summary>Auto's utility for this behaviour now (0..1, like the built-in ones: AttackRun 0.2..0.7, PursuitLead 0.25..0.8,
    /// Circle 0.3; the active one gets +0.15 hysteresis). NaN: not available - Auto skips it.
    /// TODO (author): score the situation (only asked while Implemented).</summary>
    public float Score(ApSlotCtx c) => float.NaN;

    /// <summary>ONE STEP: fill o (ApSlotOut.None on entry) and return true; false: declined - the behaviour lets go
    /// (ApReason.NotImplemented, or o.LetGo when set).
    /// TODO (author): the decision logic - read c, write o.Goal (e.g. o.Goal.Kind = ApExternalKind.Lift; o.Goal.Lift = ...;
    /// o.Goal.Load = ...; o.Goal.Throttle = ...) or o.UseNav + o.Nav, o.Status, o.AimReport / o.AimNose, o.LetGo.</summary>
    public bool Step(ApSlotCtx c, ref ApSlotOut o)
    {
        return false;
    }
}

/// <summary>
/// THE DOGFIGHT SLOT'S STATE - THE AUTHOR'S: whatever the logic keeps between steps (a phase, timers, the last decision...). Add
/// fields here; Reset them in Reset. To keep some across a save: write them in Save (one line of text, no newlines needed - it is
/// escaped) and read them in Load; set Dirty when they change and the core copies Save() to the panel's DfSaved (saved with the
/// block, the key "aero-autopilot:2 ..." value 66) at most twice a second.
/// </summary>
public sealed class ApDogfightState
{
    /// <summary>Set when Save()'s text changed (the core clears it after copying).</summary>
    public bool Dirty;

    public void Reset() { Dirty = false; /* TODO (author): the fields back to rest */ }

    /// <summary>The state worth keeping across a save ("" nothing).</summary>
    public string Save() => "";

    /// <summary>The state back from Save's text.</summary>
    public void Load(string s) { }
}
