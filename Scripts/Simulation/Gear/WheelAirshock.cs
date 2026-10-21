#pragma warning disable
using System;

namespace AeroMod;

/// <summary>
/// THE AIRSHOCK (2026-10-15 on the mod's gears, the user: SE1's suspension setting that anticipated ground impacts - "right now it's
/// impossible to land"; 2026-10-20 on EVERY wheel): the wheel down and coming down at it, its suspension goes firm just before it
/// touches - within ArmSeconds of contact at the sink rate now, or ArmHeight above the ground - so the hit is taken over the whole
/// travel; the player's settings come back once it has settled on the wheel (in contact and barely sinking for SettleSeconds), or
/// HoldSeconds after the touch, or when it pulls up before touching. Game-free: per wheel, the server steps it with the wheel's own
/// sink rate (its point's velocity) and its height over the ground (the planet's surface and the ground ray, the nearer -
/// AeroWheelShockComponent); the firmness written is ShockDamping and, airshock only, the stiffness cap (WheelShockSettings).
/// </summary>
public sealed class WheelAirshock
{
    public static float ArmSeconds = 0.5f, ArmHeight = 0.3f, MinSink = 0.5f, SettleSink = 0.3f, SettleSeconds = 0.5f, HoldSeconds = 3f;
    /// <summary>The wheel constraint's damping factor while armed (the solver's damping x this; 1: full).</summary>
    public static float ShockDamping = 1f;

    public bool Armed { get; private set; }
    double _touch = -1, _calm = -1;
    /// <summary>For the readouts: "armed: ...", "settled", "released".</summary>
    public string State = "";

    /// <summary>Arms when the wheel will touch soon; true while it is to be firm.</summary>
    public static bool Coming(bool contact, float sink, float height)
        => !contact && sink > MinSink && (height <= ArmHeight || height / sink <= ArmSeconds);

    /// <summary>
    /// One tick. usable: the wheel is down and this wheel takes part (a gear stowing, the block's own switch off: no); contact: the
    /// wheel touches something; sink: its speed toward the ground (m/s, + down); height: its tyre's bottom over the ground (m;
    /// MaxValue unknown); now: seconds. Returns +1 armed now, -1 released now, 0 no change.
    /// </summary>
    public int Step(bool usable, bool contact, float sink, float height, double now)
    {
        if (!Armed)
        {
            if (!usable || !Coming(contact, sink, height)) return 0;
            Armed = true; _touch = -1; _calm = -1;
            State = $"armed: {height:F2} m up, sinking {sink:F1} m/s";
            return 1;
        }
        if (contact && _touch < 0) _touch = now;
        bool calm = contact && MathF.Abs(sink) < SettleSink;
        if (calm) { if (_calm < 0) _calm = now; } else _calm = -1;
        bool done = !usable || (_calm >= 0 && now - _calm >= SettleSeconds) || (_touch >= 0 && now - _touch >= HoldSeconds)
                    || (!contact && sink < 0f && _touch < 0);   // (pulled up before touching)
        if (!done) return 0;
        Armed = false;
        State = _touch >= 0 ? "settled" : "released";
        return -1;
    }

    /// <summary>Back to unarmed at once (the wheel gone, the mode off).</summary>
    public void Reset() { Armed = false; _touch = -1; _calm = -1; }
}
