#pragma warning disable CS0649, CS8632, CS1591, CS1573, CS1570, CS1574, CS0419, CS1734
using System;
using System.Text;
using Keen.Game2.Client.GameSystems.PlayerControl;
using Keen.VRage.Library.Localization;
namespace AeroMod;

/// <summary>
/// MACH IN THE GAME'S SPEED READOUT: the vanilla speedometer (character, cockpit) prints its localized template -
/// "{Speed} m/s" (SpeedometerMetersPerSecond, and the km/h / mph ones) - through the game's text system, which takes
/// global filters ({Keyword:Value}, TextManager.AddFilter). Aero registers {Aero:Mach} and appends it to the three
/// templates as loaded (LocalizationPackage's string tables, by reflection - re-checked every few seconds: a culture
/// change reloads them), so the readout reads "312 m/s · M 0.92". The value: the Mach of what the local player is in
/// (its top observable entity: the grid when seated; on foot, nothing), from aero's published flow; nothing
/// under Mach 0.05 or with no flow. The readout refreshes when the speed does (its binding), which is when Mach does.
/// Client only; the UI thread reads the suffix (a string reference: atomic).
/// </summary>
public static class MachHud
{
    /// <summary>Off by default (2026-10-12, the user: "take the mach out of the speed indicator, since it's on the right" - the
    /// Orbital mod's flight boxes by ALT show it); Hud.Mach turns it back on.</summary>
    public static bool On = false;
    /// <summary>What {Aero:Mach} prints now ("" for nothing).</summary>
    public static volatile string Suffix = "";
    public static string Status = "not started";

    const string Token = "{Aero:Mach}";
    static readonly string[] Keys = { "SpeedometerMetersPerSecond", "SpeedometerKilometersPerHour", "SpeedometerMilesPerHour" };
    static bool _filter;
    static long _nextPatch, _nextValue;
    static string _lastAp = "", _lastMach = "";

    sealed class Filter : ITextFilter
    {
        public bool FormatText(StringBuilder text, ReadOnlySpan<char> formatString, ITextFilter localFilter)
        {
            if (formatString.SequenceEqual("Mach".AsSpan())) text.Append(Suffix);
            return true;
        }
    }

    /// <summary>Client, every frame (from the flight effects' job; cheap: the work is every 0.1 s and 3 s).</summary>
    internal static void Tick(Keen.VRage.DCS.Components.Entity anyGrid)
    {
        long now = System.Diagnostics.Stopwatch.GetTimestamp(), f = System.Diagnostics.Stopwatch.Frequency;
        // (the effects jobs run in parallel: one of them claims the tick - Install once, never twice at a time)
        long due = System.Threading.Interlocked.Read(ref _nextValue);
        if (now < due || System.Threading.Interlocked.CompareExchange(ref _nextValue, now + f / 10, due) != due) return;
        // (the autopilot's readout too - "AP HOVER POS AGL" - whatever Hud.Mach says: Autopilot.Readout)
        bool ap = Autopilot.Readout && (System.Threading.Volatile.Read(ref AeroGridComponent.ApEngaged) > 0 || FlightAssists.StallProtection);
        if (!On && !ap) { Suffix = ""; return; }
        if (now >= _nextPatch) { _nextPatch = now + 3 * f; Install(); }
        // (the Mach of what the local player is in)
        var players = anyGrid?.GetSession()?.SessionComponents.TryGet<ClientPlayersSessionComponent>();
        var top = players?.LocalPlayerController?.GetTopObservableEntity();
        // (seated, that is the cockpit block: its grid is up the hierarchy)
        for (int up = 0; top != null && up < 4 && top.TryGet<AeroGridComponent>() == null; up++)
            top = top.TryGet<Keen.VRage.Core.Game.Components.HierarchyComponent>()?.Parent;
        // (a grid aero simulates - you are seated in it; on foot nothing: a character near a flying grid is not flying)
        var aero = top?.TryGet<AeroGridComponent>();
        var p = On && aero != null ? AeroEntryFx.FindFlow(top.Data.GetWorldTransform().Position) : null;
        string mach = p != null && p.Mach >= 0.05f ? $" · M {p.Mach:0.00}" : "", apText = ap && aero != null ? aero.ApReadoutClient ?? "" : "";
        if (apText.Length == 0 && ap && aero != null) apText = aero.PilotProtText ?? "";   // (stall protection in manual flight: what clips it)
        // (built only when a part changed: the readout's text is a few fixed strings)
        if (!ReferenceEquals(apText, _lastAp) || mach != _lastMach) { _lastAp = apText; _lastMach = mach; Suffix = apText.Length > 0 ? mach + " · " + apText : mach; }
    }

    /// <summary>The filter (once), and the token on the three templates (when a load replaced them).</summary>
    static void Install()
    {
        try
        {
            var tm = Singleton<TextManager>.Instance;
            if (!_filter)
            {
                try { tm.RemoveFilter(StringId.Get("Aero")); } catch { }   // (a reloaded mod's)
                tm.AddFilter(StringId.Get("Aero"), new Filter());
                _filter = true;
            }
            var src = typeof(TextManager).GetField("_textSource", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)?.GetValue(tm);
            if (src == null) { Status = "no text source"; return; }
            int patched = 0;
            foreach (string table in new[] { "_baseLanguageStrings", "_localizedStrings" })
            {
                if (!(src.GetType().GetField(table, System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)?.GetValue(src) is System.Collections.Generic.Dictionary<StringId, ContentText> d)) continue;
                foreach (string k in Keys)
                {
                    var id = StringId.Get(k);
                    if (!d.TryGetValue(id, out var ct) || ct.Text == null || ct.Text.Contains(Token)) continue;
                    ct.Text += Token;
                    d[id] = ct;
                    patched++;
                }
            }
            Status = $"filter on, {patched} template(s) patched now";
        }
        catch (Exception e) { Status = "install failed: " + e.Message; }
    }
}
