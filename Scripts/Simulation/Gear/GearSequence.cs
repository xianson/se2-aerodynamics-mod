#pragma warning disable
using System;

namespace AeroMod;

/// <summary>
/// A retractable gear's motion as one progress value: 0 = up (stowed, doors shut, no wheel), 1 = down (wheel on its
/// vanilla suspension, main doors shut again). One timeline, both ways - retracting runs it backwards, so the order
/// reverses by itself:
///   [0, DoorsOpen]          the bay doors open                  (leg stowed)
///   [DoorsOpen, LegDown]    the leg folds out                   (doors open)
///   [LegDown, 1]            the main doors close behind the leg (leg down; the REAL wheel exists from LegDown on)
/// The real (vanilla) wheel is wanted at LegDown and above. Two gates, checked where they matter:
///  - extending: the wheel is spawned at LegDown only when the ground leaves room for it (else the gear waits there -
///    a wheel spawned into the ground would kick the ship);
///  - retracting: the wheel goes (progress leaves LegDown) only with no weight on it (else the gear stays down).
/// Pure: the server component steps it and does the spawning (AeroGearComponent).
/// </summary>
public sealed class GearSequence
{
    /// <summary>Timeline marks (fractions of the whole motion).</summary>
    public const float DoorsOpen = 0.2f, LegDown = 0.85f;

    /// <summary>Where it is (0 up .. 1 down) and where it is going.</summary>
    public float Progress { get; private set; }
    public bool Down { get; private set; }
    /// <summary>The whole motion's time (s), up to down.</summary>
    public float Duration = 4f;
    /// <summary>Close the main doors behind the extended leg (the airliner look); false: they stay open while down.</summary>
    public bool CloseDoorsWhenDown = true;
    /// <summary>Why it is not moving when it should (empty: moving or arrived).</summary>
    public string Held { get; private set; } = "";

    public GearSequence(bool down) { Down = down; Progress = down ? 1f : 0f; }

    /// <summary>The real wheel wanted at this progress.</summary>
    public bool WheelWanted => Progress >= LegDown - 1e-6f;
    public bool Arrived => Down ? Progress >= 1f : Progress <= 0f;

    public void Command(bool down) { Down = down; }
    public void Toggle() { Down = !Down; }

    /// <summary>Doors 0 shut .. 1 open, at this progress (closeWhenDown: the main doors shut again behind the leg).</summary>
    public static float DoorsAt(float p, bool closeWhenDown)
    {
        if (p <= DoorsOpen) return Smooth(p / DoorsOpen);
        if (p <= LegDown || !closeWhenDown) return 1f;
        return 1f - Smooth((p - LegDown) / (1f - LegDown));
    }
    public float Doors(float p) => DoorsAt(p, CloseDoorsWhenDown);
    public float Doors() => Doors(Progress);

    /// <summary>Leg 0 stowed .. 1 down, at this progress.</summary>
    public static float Fold(float p) => p <= DoorsOpen ? 0f : p >= LegDown ? 1f : Smooth((p - DoorsOpen) / (LegDown - DoorsOpen));
    public float Fold() => Fold(Progress);

    static float Smooth(float t) { t = Math.Clamp(t, 0f, 1f); return t * t * (3f - 2f * t); }

    /// <summary>
    /// Advance by dt. wheelPresent: the real wheel exists now. roomForWheel: the ground leaves room to spawn it (asked
    /// only when it would be spawned). weightOnWheel: it carries load (asked only when it would be removed). Returns what
    /// the caller must do with the real wheel this step: +1 spawn it, -1 remove it, 0 nothing.
    /// </summary>
    public int Step(float dt, bool wheelPresent, Func<bool> roomForWheel, Func<bool> weightOnWheel)
    {
        Held = "";
        float rate = dt / Math.Max(0.1f, Duration);
        float p = Progress;
        if (Down)
        {
            if (p < 1f)
            {
                float next = Math.Min(1f, p + rate);
                // (crossing into LegDown: the wheel first - if the ground has no room for it, wait at the mark)
                if (!wheelPresent && next >= LegDown - 1e-6f)
                {
                    if (!roomForWheel()) { Progress = Math.Min(p, LegDown - 1e-4f); Held = "no room for the wheel (too near the ground)"; return 0; }
                    Progress = next;
                    return 1;
                }
                Progress = next;
            }
            else if (!wheelPresent) return roomForWheel() ? 1 : 0;   // (down, wheel lost - a reload, a removed wheel: again)
            return 0;
        }
        if (p > 0f)
        {
            float next = Math.Max(0f, p - rate);
            // (crossing below LegDown: the wheel goes first - not while it carries the ship)
            if (wheelPresent && next < LegDown - 1e-6f)
            {
                if (weightOnWheel()) { Progress = Math.Max(next, LegDown); Held = "weight on the wheel"; return 0; }
                Progress = next;
                return -1;
            }
            Progress = next;
        }
        else if (wheelPresent) return weightOnWheel() ? 0 : -1;   // (up, a wheel still there: remove it)
        return 0;
    }

    /// <summary>Restore a saved state (the motion is never saved mid-way: it ends where it was going).</summary>
    public void Restore(bool down) { Down = down; Progress = down ? 1f : 0f; }
}
