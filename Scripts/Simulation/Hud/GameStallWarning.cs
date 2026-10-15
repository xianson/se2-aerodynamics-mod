#pragma warning disable CS0649, CS8632, CS1591, CS1573, CS1570, CS1574, CS0419, CS1734
using System;
using System.Reflection;
namespace AeroMod;

/// <summary>
/// THE GAME'S STALL WARNING - "THRUST OVERLOAD" - REMOVED (2026-10-13, the user: the big red text when the gravity dampeners are saturated -
/// "remove it"). The cockpit HUD shows it when the grid's thrust cannot hold it against gravity
/// (ThrustStallData.GravityHoldRatio under CockpitHUDConfiguration.StallLiftRatio): it counts thrust only, so an
/// aeroplane held up by its wings is "stalling" all the time. Its thresholds are set below anything the ratio can be
/// (client, in memory: nothing saved, the cockpits' own setting untouched); Hud.GameStallWarning on puts them back.
/// By reflection: CockpitHUDConfiguration is in Game2.Client (not referenceable), its setters private.
/// </summary>
public static class GameStallWarning
{
    /// <summary>The game's warning shown (off: removed).</summary>
    public static bool On = false;
    public static string Status = "-";
    static object _config; static PropertyInfo _lift, _recovery;
    static float _origLift = float.NaN, _origRecovery = float.NaN;
    static bool _applied, _resolved;

    /// <summary>Puts the thresholds as On says (cheap after the first time; called with the settings' poll).</summary>
    public static void Apply()
    {
        bool want = !On;
        if (_resolved && want == _applied) return;
        if (!_resolved && !Resolve()) return;
        try
        {
            if (want) { _lift.SetValue(_config, -1f); _recovery.SetValue(_config, -1f); Status = "removed"; }
            else { _lift.SetValue(_config, _origLift); _recovery.SetValue(_config, _origRecovery); Status = $"shown (lift ratio {_origLift}, recovery {_origRecovery})"; }
            _applied = want;
        }
        catch (Exception e) { Status = "failed: " + e.Message; _applied = want; }
    }

    static bool Resolve()
    {
        _resolved = true;
        try
        {
            Type t = null;
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                try { t = asm.GetType("Keen.Game2.Client.UI.HUD.Cockpit.CockpitHUDConfiguration"); } catch { }
                if (t != null) break;
            }
            if (t == null) { Status = "no cockpit HUD configuration (dedicated server?)"; return false; }
            MethodInfo get = null;
            foreach (var m in typeof(DefinitionManager).GetMethods(BindingFlags.Public | BindingFlags.Instance))
                if (m.Name == "GetConfiguration" && m.IsGenericMethodDefinition && m.GetParameters().Length == 0) { get = m; break; }
            _config = get?.MakeGenericMethod(t).Invoke(DefinitionManager.Instance, null);
            const BindingFlags F = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
            _lift = t.GetProperty("StallLiftRatio", F); _recovery = t.GetProperty("StallRecoveryLiftRatio", F);
            if (_config == null || _lift == null || _recovery == null) { Status = "configuration or its thresholds not found"; return false; }
            _origLift = (float)_lift.GetValue(_config); _origRecovery = (float)_recovery.GetValue(_config);
            _applied = false;
            return true;
        }
        catch (Exception e) { Status = "failed: " + e.Message; return false; }
    }
}
