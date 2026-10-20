#pragma warning disable CS0649, CS8632, CS1591, CS1573, CS1570, CS1574, CS0419, CS1734
using System;
namespace AeroMod;

/// <summary>
/// What the flight pilot shows (engine-free): a snapshot of values taken on the simulation thread (ApPilot.View - no allocation;
/// the target's and the waypoint's names are references to strings it already holds), the status line built from it only when the
/// terminal reads it (ApPilotView.Text).
/// </summary>
public struct ApPilotView
{
    public ApMode Mode; public ApStyle Style; public ApBehaviour Behaviour; public int Phase; public bool Loitering, Recovering, Returning;
    public bool HasTarget; public string TargetName; public float Range, BearingDeg;
    public bool AimOn, InCone, InRange; public float AimErrDeg;
    public int Wp, WpCount; public string WpName; public float WpDist, Eta;
    public float HeadingNow, HeightNow, SpeedNow, HeadingWant, HeightWant, SpeedWant; public bool HeadingOn, AltOn, SpeedOn;
    public ApProt Prot;
    /// <summary>A target the filter refused (its words, held by the target source; null none).</summary>
    public string Refused;
    /// <summary>(2026-10-19) A combat slot's own status words (ApSlotOut.Status; null none) and the formation's slot error (m), its
    /// part along the leader's path (m, + behind the slot), overshooting, the leader's speed (m/s).</summary>
    public string SlotText; public float FormErr, FormAlong, LeaderSpeed; public bool FormOver;

    public bool Same(in ApPilotView o) => o.Mode == Mode && o.Style == Style && o.Behaviour == Behaviour && o.Phase == Phase && o.Loitering == Loitering
        && o.Recovering == Recovering && o.Returning == Returning && o.HasTarget == HasTarget && ReferenceEquals(o.TargetName, TargetName)
        && MathF.Abs(o.Range - Range) < 5f && MathF.Abs(o.BearingDeg - BearingDeg) < 1f && o.AimOn == AimOn && o.InCone == InCone && o.InRange == InRange
        && (MathF.Abs(o.AimErrDeg - AimErrDeg) < 0.1f || (float.IsNaN(o.AimErrDeg) && float.IsNaN(AimErrDeg))) && o.Wp == Wp && o.WpCount == WpCount
        && ReferenceEquals(o.WpName, WpName) && MathF.Abs(o.WpDist - WpDist) < 10f && MathF.Abs(o.HeadingNow - HeadingNow) < 1f
        && MathF.Abs(o.HeightNow - HeightNow) < 1f && MathF.Abs(o.SpeedNow - SpeedNow) < 0.5f && o.Prot == Prot
        && o.HeadingWant == HeadingWant && o.HeightWant == HeightWant && o.SpeedWant == SpeedWant && o.HeadingOn == HeadingOn && o.AltOn == AltOn && o.SpeedOn == SpeedOn
        && ReferenceEquals(o.Refused, Refused) && ReferenceEquals(o.SlotText, SlotText) && MathF.Abs(o.FormErr - FormErr) < 0.5f && MathF.Abs(o.FormAlong - FormAlong) < 0.5f
        && o.FormOver == FormOver && MathF.Abs(o.LeaderSpeed - LeaderSpeed) < 0.5f;

    public static string StyleName(ApStyle s) => s switch { ApStyle.Airplane => "airplane", ApStyle.Helicopter => "helicopter", ApStyle.SixAxis => "six-axis", _ => "auto" };

    public static string BehaviourName(ApBehaviour b) => b switch
    {
        ApBehaviour.AttackRun => "Attack run", ApBehaviour.AttackRunUTurn => "Attack run (U-turn)", ApBehaviour.Broadside => "Broadside",
        ApBehaviour.Circle => "Circle", ApBehaviour.HoverAbove => "Hover above", ApBehaviour.HoverBelow => "Hover below", ApBehaviour.PointAt => "Point at",
        ApBehaviour.Standoff => "Standoff", ApBehaviour.PursuitLead => "Pursuit (lead)", ApBehaviour.PursuitPure => "Pursuit (pure)",
        ApBehaviour.PursuitLag => "Pursuit (lag)", ApBehaviour.Extend => "Extend (energy)", ApBehaviour.Dogfight => "Dogfight",
        ApBehaviour.RocketGuidance => "Rocket guidance", ApBehaviour.Formation => "Formation", _ => "Auto",
    };

    static string Dist(float m) => m >= 1000f ? $"{m / 1000f:F1} km" : $"{m:F0} m";
    static string Time(float s) => !float.IsFinite(s) || s > 35999f ? "-" : s >= 60f ? $"{(int)(s / 60f)}:{(int)(s % 60f):00}" : $"{s:F0} s";

    /// <summary>The status line ("Engage (airplane) - Pursuit (lead) on Raider 1.2 km, 35° right · aim 1.4° IN CONE · G LIMIT").</summary>
    public string Text()
    {
        string head = (Mode == ApMode.FlightHold ? "Flight hold" : Mode == ApMode.Navigate ? "Navigate" : "Engage") + " (" + StyleName(Style) + ")";
        string body;
        if (Recovering) body = "levelling - gaining airspeed";
        else if (Mode == ApMode.FlightHold)
            body = (HeadingOn ? $"HDG {HeadingWant:000}" : "wings level") + (AltOn ? $" · ALT {HeightWant:F0} m" : "") + (SpeedOn ? $" · {SpeedWant:F0} m/s" : "")
                 + $" (now {HeadingNow:000}, {HeightNow:F0} m, {SpeedNow:F0} m/s)";
        else if (Mode == ApMode.Engage && HasTarget)
        {
            string side = MathF.Abs(BearingDeg) < 2f ? "ahead" : BearingDeg > 0f ? $"{BearingDeg:F0}° right" : $"{-BearingDeg:F0}° left";
            string who = string.IsNullOrEmpty(TargetName) ? "target" : TargetName;
            if (Behaviour == ApBehaviour.Formation)
                body = $"Formation on {who} - {(FormErr < 1f ? "in the slot" : $"{FormErr:F0} m from the slot ({(FormAlong >= 0f ? $"{FormAlong:F0} m behind" : $"{-FormAlong:F0} m ahead")})")}{(FormOver ? " - overshoot, dropping back" : "")} · leader {LeaderSpeed:F0} m/s";
            else body = $"{BehaviourName(Behaviour)}{(string.IsNullOrEmpty(SlotText) ? "" : " - " + SlotText)} on {who} {Dist(Range)}, {side}";
            if (AimOn) body += float.IsFinite(AimErrDeg) ? $" · aim {AimErrDeg:F1}°{(InCone ? " IN CONE" : !InRange ? " (out of range)" : "")}" : "";
        }
        else if (Mode == ApMode.Engage && !Returning && !string.IsNullOrEmpty(Refused)) body = "no target - " + Refused + (Loitering ? " - loitering" : "");
        else if (Loitering) body = Returning ? "back at the return point - loitering" : Mode == ApMode.Engage && WpCount == 0 ? "no target - loitering" : "loitering at the route's end";
        else if (Returning) body = $"returning - {Dist(WpDist)}, ETA {Time(Eta)}";
        else body = (Mode == ApMode.Engage ? "no target - " : "") + $"WP {Wp + 1}/{WpCount} {WpName} {Dist(WpDist)}, ETA {Time(Eta)}";
        string s = head + " - " + body;
        string p = ApText.Prot(Prot);
        return p.Length > 0 ? s + " · " + p : s;
    }
}
