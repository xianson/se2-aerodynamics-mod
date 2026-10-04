#pragma warning disable
using System;
using System.Text;
using Keen.Game2.Client.GameSystems.PlayerControl;
using Keen.VRage.Library.Localization;
using Keen.VRage.Library.Utils;
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
    public static bool On = true;
    /// <summary>What {Aero:Mach} prints now ("" for nothing).</summary>
    public static volatile string Suffix = "";
    public static string Status = "not started";

    const string Token = "{Aero:Mach}";
    static readonly string[] Keys = { "SpeedometerMetersPerSecond", "SpeedometerKilometersPerHour", "SpeedometerMilesPerHour" };
    static bool _filter;
    static long _nextPatch, _nextValue;

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
        if (now < _nextValue) return;
        _nextValue = now + f / 10;
        if (!On) { Suffix = ""; return; }
        if (now >= _nextPatch) { _nextPatch = now + 3 * f; Install(); }
        // (the Mach of what the local player is in)
        var players = anyGrid?.GetSession()?.SessionComponents.TryGet<ClientPlayersSessionComponent>();
        var top = players?.LocalPlayerController?.GetTopObservableEntity();
        // (seated, that is the cockpit block: its grid is up the hierarchy)
        for (int up = 0; top != null && up < 4 && top.TryGet<AeroGridComponent>() == null; up++)
            top = top.TryGet<Keen.VRage.Core.Game.Components.HierarchyComponent>()?.Parent;
        // (a grid aero simulates - you are seated in it; on foot nothing: a character near a flying grid is not flying)
        var p = top != null && top.TryGet<AeroGridComponent>() != null ? AeroEntryFx.FindFlow(top.Data.GetWorldTransform().Position) : null;
        Suffix = p != null && p.Mach >= 0.05f ? $" · M {p.Mach:0.00}" : "";
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
