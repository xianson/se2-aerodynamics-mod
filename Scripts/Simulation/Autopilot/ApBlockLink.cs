#pragma warning disable CS0649, CS8632, CS1591, CS1573, CS1570, CS1574, CS0419, CS1734
using System;
namespace AeroMod;

/// <summary>The Autopilot block's modes as the flight director's goals.</summary>
public static class ApModes
{
    /// <summary>The goals a mode holds: hover hold - the place and the height; height hold - the height (the pilot flies the rest).</summary>
    public static ApGoal GoalsOf(ApMode m) => m switch
    {
        ApMode.HoverHold => ApGoal.Hover | ApGoal.Height,
        ApMode.HeightHold => ApGoal.Height,
        _ => ApGoal.None,
    };

    /// <summary>The mode the goals held are (after a let-go: steering drops the hover, the height stays - height hold).</summary>
    public static ApMode ModeOf(ApGoal g) => (g & ApGoal.Hover) != 0 ? ApMode.HoverHold : (g & ApGoal.Height) != 0 ? ApMode.HeightHold : ApMode.Off;

    public static string ModeName(ApMode m) => m switch
    {
        ApMode.HoverHold => "Hover", ApMode.HeightHold => "Hold height", ApMode.FlightHold => "Cruise", ApMode.Navigate => "Follow route",
        ApMode.Engage => "Attack", _ => "Off",
    };

    /// <summary>(2026-10-20) The panel's mode choice by name (the Mode dropdown's words).</summary>
    public static string ChoiceName(ApModeChoice c) => c switch
    {
        ApModeChoice.Hover => "Hover", ApModeChoice.HoldHeight => "Hold height", ApModeChoice.Cruise => "Cruise", ApModeChoice.FollowRoute => "Follow route",
        ApModeChoice.ReturnHome => "Return home", ApModeChoice.FollowShip => "Follow ship", ApModeChoice.Attack => "Attack", _ => "Off",
    };

}

/// <summary>
/// A MODE THAT NEVER ENGAGES, timed out (engine-free; the block's own server step runs it). The block's panel says a mode; the
/// grid's autopilot step reports what it flies. If that step never reports the mode flying - a wreck with no thruster or gyro
/// left: the grid's attitude job (and the autopilot's step in it) does not run for it - the panel went on saying "Hover hold -
/// engaging" forever (in game 2026-10-19, two wrecked grids). After TimeoutTicks of the same mode not flown: true once (the
/// block puts its panel back to Off, the reason said: ApReason.NoResponse). A standby block (another one flies) never times out.
/// </summary>
public sealed class ApEngageWatch
{
    /// <summary>10 s of the block's steps (60 a second) - past the link's load grace (ApBlockLink.LoadGraceSeconds) twice over.</summary>
    public const int TimeoutTicks = 600;
    ApMode _mode; int _ticks;
    /// <summary>One block step: the panel's mode, whether the grid's last report flies that mode, whether the block stands by.</summary>
    public bool Step(ApMode panel, bool flyingIt, bool standby)
    {
        if (panel != _mode) { _mode = panel; _ticks = 0; }
        if (panel == ApMode.Off || flyingIt || standby) { _ticks = 0; return false; }
        if (++_ticks < TimeoutTicks) return false;
        _ticks = 0;
        return true;
    }
}

/// <summary>Which of a grid's Autopilot blocks flies it: the first WORKING one that is switched on, in a fixed order (the lowest
/// block position: x, then y, then z - the same after a save and load, whatever order the blocks came in); the others stand by.</summary>
public static class ApArbiter
{
    /// <summary>The index of the block that flies (-1: none can). ready: built, powered, switched on; pos: its block position.</summary>
    public static int Pick(ReadOnlySpan<bool> ready, ReadOnlySpan<int> x, ReadOnlySpan<int> y, ReadOnlySpan<int> z)
    {
        int best = -1;
        for (int i = 0; i < ready.Length; i++)
        {
            if (!ready[i]) continue;
            if (best < 0 || Less(x[i], y[i], z[i], x[best], y[best], z[best])) best = i;
        }
        return best;
    }
    static bool Less(int ax, int ay, int az, int bx, int by, int bz) => ax != bx ? ax < bx : ay != by ? ay < by : az < bz;
}

/// <summary>
/// THE BLOCK AND THE FLIGHT DIRECTOR, kept in step (engine-free; the grid's server step runs it for the block that flies). Each tick,
/// before the director's step: the block gone dark lets go (BlockOff); a mode the player chose on the panel engages (refused: the
/// panel goes back to Off, the reason shown); a height the player typed is flown to; a new reference is caught without a jump.
/// After the step: a let-go (steering, the dampeners, an upset) is written back to the panel's mode, and the height held (caught at
/// the engage, moved by the up / down keys) to its height. Writes go to Out (the caller writes the block's synced values).
/// </summary>
public sealed class ApBlockLink
{
    /// <summary>What the panel should now show (the caller writes the changed ones to the block).</summary>
    public struct Out
    {
        public bool ModeChanged, HeightChanged; public ApMode Mode; public float HeightM; public ApReason Reason;
        /// <summary>(the flight pilot) The holds it caught at an engage (heading, altitude, speed), the return point it set.</summary>
        public bool HoldsChanged, HomeChanged; public float HeadingDeg, AltM, SpeedMs; public Vector3D Home;
        /// <summary>(2026-10-19) A combat slot's saved state changed (ApDogfightState / ApRocketState.Save): the panel's DfSaved / RgSaved (null: unchanged).</summary>
        public bool SlotSavedChanged; public string DfSaved, RgSaved;
    }

    /// <summary>The flight pilot this link flies the pilot modes with (FlightHold, Navigate, Engage; ApPilot) - its target source
    /// and its route are the caller's to set.</summary>
    public readonly ApPilot Pilot = new ApPilot();
    ApStyle _style, _cfgStyle;
    /// <summary>The style the pilot mode flies (resolved at its engage).</summary>
    public ApStyle Style => _style;


    /// <summary>A panel's mode refused this soon after the link was made (a world loading: the thrusters' maxima, the stall sweep,
    /// the gravity not in yet) is tried again rather than reset - an unattended hover saved with the world survives the load.</summary>
    public const float LoadGraceSeconds = 5f;
    /// <summary>The held height written back past this change (m) - the panel's number follows the hold, not its noise.</summary>
    public const float HeightWriteStep = 0.5f;

    ApMode _mode; float _height = float.NaN; ApHeightRef _ref; bool _first = true; float _age;

    /// <summary>Before the director's step: the panel's changes into it. ready: the block works and is switched on.</summary>
    public void Before(FlightDirector fd, in ApPanel p, bool ready, in ApSense s, ref Out o)
    {
        o = default; o.Mode = p.Mode; o.HeightM = p.HeightM;
        _age += s.Dt > 0f ? s.Dt : 1f / 60f;
        if (_first) { _first = false; _mode = ApMode.Off; _height = p.HeightM; _ref = p.HeightRef; }
        if (!ready)
        {
            if (fd.Engaged) fd.Disengage(ApReason.BlockOff);
            if (p.Mode != ApMode.Off) { o.Mode = ApMode.Off; o.ModeChanged = true; o.Reason = ApReason.BlockOff; }
            _mode = ApMode.Off;
            return;
        }
        if (p.HeightRef != _ref) { fd.SetHeightRef(p.HeightRef, in s); _ref = p.HeightRef; _height = float.NaN; }
        Pilot.Fd = fd;
        if (ApPanel.IsPilotMode(_mode) && p.Mode == _mode)
        {
            Pilot.SyncCfg(in p, in s);
            // (the style set on the panel changed while flying: the mode begun again in the new style)
            if (p.Style != _cfgStyle) _mode = ApMode.Off;
        }
        if (p.Mode != _mode && ApPanel.IsPilotMode(p.Mode))
        {
            // a pilot mode: the pilot begins it (its own refusals), then the flight director takes its goals with the pilot as the source
            if (fd.Engaged) fd.Disengage(ApReason.TurnedOff);
            Pilot.SyncCfg(in p, in s);
            bool ok = Pilot.Begin(p.Mode, in s, out var why);
            if (ok) ok = fd.Engage(ApPilot.GoalsFor(Pilot.Style), in s, out why, Pilot);
            if (ok) { _mode = p.Mode; _style = Pilot.Style; _cfgStyle = p.Style; _height = float.NaN; }
            else if (_age < LoadGraceSeconds && why != ApReason.NotAllowed) { /* (loading: tried again next tick) */ }
            else { _mode = ApMode.Off; o.Mode = ApMode.Off; o.ModeChanged = true; o.Reason = why; }
            return;
        }
        if (p.Mode != _mode)
        {
            // the player chose a mode (or a save brought one): exactly its goals
            ApGoal want = ApModes.GoalsOf(p.Mode);
            if (p.Mode == ApMode.Off) { if (fd.Engaged) fd.Disengage(ApReason.TurnedOff); _mode = ApMode.Off; o.Reason = ApReason.TurnedOff; }
            else
            {
                if (fd.Engaged && (fd.Goals & ~want) != 0) fd.Disengage(ApReason.TurnedOff, fd.Goals & ~want);
                if (fd.Engage(want & ~fd.Goals, in s, out var why) || (fd.Goals & want) == want) { _mode = p.Mode; _height = float.NaN; }
                else if (_age < LoadGraceSeconds && why != ApReason.NotAllowed) { /* (loading: tried again next tick) */ }
                else { _mode = ApMode.Off; o.Mode = ApMode.Off; o.ModeChanged = true; o.Reason = why; }
            }
            return;
        }
        // a height typed on the panel: flown to
        if (fd.Engaged && float.IsFinite(_height) && MathF.Abs(p.HeightM - _height) > 0.05f) { fd.SetHeight(p.HeightM, in s); _height = p.HeightM; }
    }

    /// <summary>The block may fly its grid: built (functional), switched on and supplied with power (the game's PowerableBlockComponent;
    /// a block without one - none in the content - always may). ignorePower: the harness's override.</summary>
    public static bool BlockReady(bool hasPower, bool functional, bool enabled, bool supplied, bool ignorePower)
        => !hasPower || functional && enabled && (supplied || ignorePower);

    /// <summary>A level-off asked: the pilot mode begun again next tick (its holds caught again from the flight now).</summary>
    public void Restart() { if (ApPanel.IsPilotMode(_mode)) _mode = ApMode.Off; }

    /// <summary>After the director's step: its let-go and its held height back to the panel.</summary>

    public void After(FlightDirector fd, in ApPanel p, ref Out o)
    {
        // (a pilot mode holds while its external goal does; what it caught at the engage goes to the panel)
        ApMode now = ApPanel.IsPilotMode(_mode) && (fd.Goals & ApGoal.External) != 0 ? _mode : ApModes.ModeOf(fd.Goals);
        if (Pilot.CapturedHolds) { Pilot.CapturedHolds = false; o.HoldsChanged = true; o.HeadingDeg = Pilot.CapHeading; o.AltM = Pilot.CapAlt; o.SpeedMs = Pilot.CapSpeed; }
        if (Pilot.CapturedHome) { Pilot.CapturedHome = false; o.HomeChanged = true; o.Home = Pilot.Home; }
        if (Pilot.SlotSavedChanged) { Pilot.SlotSavedChanged = false; o.SlotSavedChanged = true; o.DfSaved = Pilot.DfSavedOut; o.RgSaved = Pilot.RgSavedOut; Pilot.DfSavedOut = Pilot.RgSavedOut = null; }
        if (_mode != ApMode.Off && now != _mode)
        {
            _mode = now; o.Mode = now; o.ModeChanged = true; o.Reason = fd.Reason;
        }
        // (the height held - caught at the engage, moved by the up / down keys - shown; a number the player typed is held exactly,
        //  so it is never written over)
        if (!ApPanel.IsPilotMode(_mode) && fd.HeldHeight(out float h))

        {
            if (MathF.Abs(h - p.HeightM) > HeightWriteStep) { o.HeightM = MathF.Round(h, 1); o.HeightChanged = true; _height = o.HeightM; }
            else if (!float.IsFinite(_height)) _height = p.HeightM;
        }
    }

    /// <summary>The block's status line: "Hover - holding 25.0 m over the ground", "Off - you steered", "Standby - ..." (heightM
    /// NaN: no height held yet - climbing on the keys). noGround: the flight director's NoGround - the height held along the
    /// gravity (over the ground) or read from where it engaged (level), said.</summary>
    public static string Status(ApMode mode, ApReason last, bool flying, bool ready, bool standby, float heightM, ApHeightRef href, ApProt prot, bool noGround = false, string name = null)
    {
        if (standby) return "Standby - another autopilot block flies this ship";
        if (!ready) return "Off - " + (last == ApReason.BlockOff || last == ApReason.None ? "the block is off, unpowered or damaged" : ApText.Say(last));
        if (mode == ApMode.Off) return last == ApReason.None || last == ApReason.TurnedOff ? "Off" : "Off - " + ApText.Say(last);
        string where = href == ApHeightRef.Ground ? " over the ground" : " altitude";
        string s = (name ?? ApModes.ModeName(mode)) + (!flying ? " - engaging" : float.IsFinite(heightM) ? $" - holding {heightM:F1} m{where}" : noGround ? " - holding its height" : " - on");
        if (flying && noGround) s += href == ApHeightRef.Ground ? " · no ground in sight: held along the gravity" : " · no ground found: measured from where it engaged";
        if (prot != ApProt.None) s += " · " + ApText.Prot(prot);
        return s;
    }
}
