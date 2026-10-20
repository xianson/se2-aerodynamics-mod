using System;
using System.Collections;
using System.Reflection;
using System.Runtime.CompilerServices;

#pragma warning disable CS0649, CS8632, CS1591, CS1573, CS1570, CS1574, CS0419, CS1734
namespace AeroMod;

/// <summary>
/// THE WORLD-SWITCH FIX (2026-10-19, SE2 4.0.0.3151 and 2.4.0.95): the game's script compiler keeps every mod's compiled
/// assemblies in <c>ScriptCodeProvider._results</c> (VRage.Scripting: a
/// <c>Dictionary&lt;VRageProjectID, Dictionary&lt;string, (CompilationResult, Assembly)&gt;&gt;</c>, one provider for every mod)
/// and never takes a mod out when its project unloads. Load a world, leave to the menu, load a world again in the same
/// process: the mod compiles again and <c>value.Add("Simulation", ...)</c> throws "An item with the same key has already been
/// added. Key: Simulation" - Loading Failed. (A stale entry also hands the old assembly's jobs to the next session:
/// <c>LoadedScriptsProviderComponent.ConfigureSession</c> reads every entry.) So each mod takes ITS OWN entry out when the
/// game unloads its project (ScriptResultsCleanup.Engine.cs: the project manager's BeforeProjectsUnloaded) - the entry
/// whose compiled assembly is this one; never another project's.
///
/// This half is engine-free (Tests/ReloadTests runs it on a fake dictionary of the same shape and checks the real field's
/// shape in the installed game). Everything is read as the non-generic <see cref="IDictionary"/> and each value as an
/// <see cref="ITuple"/>, so no engine type is named here. Never throws.
/// SHARED: written once in the Aerodynamics Mod's tools/shared/modsrc/files/ (this path under it), copied into both mods by
/// tools/shared/modsrc/sync.sh; edit it THERE.
/// </summary>
internal static partial class ScriptResultsCleanup
{
    /// <summary>The key of the one outer entry whose inner values hold <paramref name="own"/> (as a tuple item, or the value
    /// itself). Null (and <paramref name="why"/>): not that shape, no entry holds it, or several do (never a guess).</summary>
    internal static object FindOwnKey(object results, Assembly own, out string why)
    {
        why = null;
        if (own == null) { why = "no own assembly"; return null; }
        if (!(results is IDictionary outer)) { why = "the results are not a dictionary (" + (results?.GetType().Name ?? "null") + ")"; return null; }
        object found = null;
        int hits = 0;
        foreach (DictionaryEntry e in outer)
        {
            if (!(e.Value is IDictionary inner)) continue;
            foreach (DictionaryEntry s in inner)
                if (Holds(s.Value, own)) { found = e.Key; hits++; break; }
        }
        if (hits > 1) { why = "the assembly is under " + hits + " projects"; return null; }
        if (found == null) why = "no entry holds this assembly";
        return found;
    }

    static bool Holds(object value, Assembly own)
    {
        if (ReferenceEquals(value, own)) return true;
        if (value is ITuple t)
            for (int i = 0; i < t.Length; i++)
                if (ReferenceEquals(t[i], own)) return true;
        return false;
    }

    /// <summary>Take out the entry under <paramref name="key"/> - only if it still holds <paramref name="own"/> (a later
    /// compile of the same project, or a game that cleans up itself, is left alone). True: removed.</summary>
    internal static bool RemoveOwn(object results, object key, Assembly own, out string why)
    {
        why = null;
        if (key == null) { why = "no key"; return false; }
        if (!(results is IDictionary outer)) { why = "the results are not a dictionary"; return false; }
        if (!outer.Contains(key)) { why = "already gone"; return false; }
        bool ours = false;
        if (outer[key] is IDictionary inner)
            foreach (DictionaryEntry s in inner)
                if (Holds(s.Value, own)) { ours = true; break; }
        if (!ours) { why = "the entry is no longer this assembly's"; return false; }
        outer.Remove(key);
        return true;
    }

    /// <summary>A private instance field declared on <paramref name="type"/> or one of its base types (private fields of a
    /// base class are not returned by a plain GetField on the derived type). Null: none.</summary>
    internal static FieldInfo FindField(Type type, string name)
    {
        for (var t = type; t != null; t = t.BaseType)
        {
            var f = t.GetField(name, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.DeclaredOnly);
            if (f != null) return f;
        }
        return null;
    }
}
