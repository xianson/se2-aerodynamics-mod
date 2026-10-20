using System;
using Keen.VRage.Library.Localization;

#pragma warning disable CS0649, CS8632, CS1591, CS1573, CS1570, CS1574, CS0419, CS1734
namespace AeroMod;

/// <summary>
/// THE MOD'S TEXTS IN THE GAME'S TEXT TABLE (2026-10-20). A mod's en-US texts (Assets/Texts/en-US/*.loc-texts, registered by its
/// CultureInfoAssetDefinition Texts/*_en-US.def -> Content/Texts) never reached the game on 4.0: the panel labels logged
/// "LOCALIZATION WARNING: Could not find key ... in the culture set en-US" and enum choices showed their raw keys
/// ("ApBehaviour_Auto"). Why (decompiled 4.0.0.3151): the base culture's texts are read ONCE, at start-up
/// (GameApp.StartPlayerExperienceAsync -> TextManager.InitializeBaseCulture -> CultureInfoDefinitionPostProcessor.InitializeBaseCulture
/// -> LocalizationPackage.LoadBaseCultureAssets), before a world - and its mods' World definition set - is loaded. The mod's culture
/// definition IS indexed when the world loads (CultureInfoDefinitionPostProcessor.ReProcess: several definitions per culture are
/// merged by GetCulture, the file found through the content cache), but nothing reads the texts again (LocalizationManager.ReloadTexts
/// only bumps the version; LoadCulture(en-US) clears the localized table only).
/// The fix: once a session, on the engine's main loop (VRageCore.UpdateQueue - the thread that reads the texts), if this mod's probe
/// key is missing, the base culture is read again with the game's own reader (ITextSource.LoadBaseCultureAssets: every en-US file
/// of every loaded culture definition - the mods' too - into the same table, overwriting with the same texts), then
/// LocalizationManager.ReloadTexts (CultureVersion: the views that poll it refresh). Reflection: TextManager._textSource (internal
/// TextSource). Up to 5 tries 2 s apart; Status says what happened (aeroget / orbitget ModTexts.Status). Another culture than
/// en-US: the mod's keys fall back to en-US (LocalizationPackage.GetText). Several mods doing it: the first one's reload brings
/// every mod's texts, the others find their probe and stop.
/// </summary>
public static class ModTexts
{
    /// <summary>What happened (diagnostics).</summary>
    public static string Status = "not run";
    /// <summary>Base-culture reloads done this process.</summary>
    public static int Reloads;
    const int MaxTries = 5;
    static object _session; static long _nextAt; static int _tries; static volatile bool _queued, _done;

    /// <summary>A client frame (any thread): once a session, the reload queued on the engine's main loop while the probe key (one
    /// of this mod's own en-US keys) is missing - a timestamp compare otherwise.</summary>
    public static void Tick(object session, string probeKey)
    {
        if (session == null) return;
        if (!ReferenceEquals(session, _session)) { _session = session; _tries = 0; _done = false; _nextAt = 0; }
        if (_done || _queued || _tries >= MaxTries) return;
        long now = System.Diagnostics.Stopwatch.GetTimestamp();
        if (now < _nextAt) return;
        _nextAt = now + 2 * System.Diagnostics.Stopwatch.Frequency;
        var queue = Keen.VRage.Core.VRageCore.Instance?.UpdateQueue;
        if (queue == null) { Status = "no engine update queue (VRageCore.UpdateQueue)"; return; }
        _tries++; _queued = true;
        queue.Enqueue(() => { try { Ensure(probeKey); } finally { _queued = false; } });
    }

    /// <summary>On the main loop: the probe there - done; else the base culture read again and the views told. True: present.</summary>
    public static bool Ensure(string probeKey)
    {
        try
        {
            var tm = Singleton<TextManager>.Instance;
            if (tm == null || !tm.Initialized || !tm.BaseCultureInitialized) { Status = "the text manager is not ready yet"; return false; }
            var key = LocKey.FromString(probeKey);
            if (tm.HasText(key)) { _done = true; if (Reloads == 0) Status = $"'{probeKey}' already in the text table"; return true; }
            var src = typeof(TextManager).GetField("_textSource", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)?.GetValue(tm) as ITextSource;
            if (src == null) { Status = "no text source (TextManager._textSource)"; return false; }
            src.LoadBaseCultureAssets();
            Reloads++;
            Singleton<LocalizationManager>.Instance?.ReloadTexts();
            _done = tm.HasText(key);
            Status = _done ? $"en-US texts read again ({Reloads}x): '{probeKey}' found"
                           : $"en-US texts read again ({Reloads}x) but '{probeKey}' still missing - is the mod's culture definition in its Content?";
            Log.Default?.Info("[MODTEXTS] " + Status);
            return _done;
        }
        catch (Exception e)
        {
            Status = "reload failed: " + e.GetType().Name + ": " + e.Message;
            Log.Default?.Warning("[MODTEXTS] " + Status);
            return false;
        }
    }
}
