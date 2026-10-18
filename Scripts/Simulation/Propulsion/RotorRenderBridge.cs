#pragma warning disable
using System;
using System.Reflection;
using Keen.VRage.Core;

namespace AeroMod;

/// <summary>
/// A spawned subpart's FADE (the rotor's blades and blur disc, AeroRotorAnimComponent; client): the engine's per-entity
/// opacity (ModelEntity.SetOpacity - the scene shaders DITHER any material by it: Engine/Shaders Scene/DrawCommandsGeneration
/// "ditherNeeded = lodOpacity != 1 || entity.GetOpacity() != 1", Common/Dithering.hlsli; with the game's temporal AA it reads
/// as translucency, but a still frame shows the stipple - so the blur disc's steady translucency is its own PBRTransparent
/// material's alpha at opacity 1, and only the fades dither) and its Visible render flag (off at opacity 0). The subpart's ModelRenderComponent (Game2.Client) derives
/// from VRage.Game.Client's ModelRenderBaseComponent, and ModelEntity / RenderFlags are VRage.Render: neither assembly is a
/// mod reference, so the type, its public RenderModelEntity and the entity's public SetOpacity / SetRenderFlagsState /
/// IsValid are found once by name (docs/REFLECTION_AND_WHITELIST.md). Called only when a value changes (a fade), never per
/// frame at rest. Not found: Problem says why, nothing fades (the parts stay as spawned: blades shown, the disc too).
/// </summary>
public static class RotorRenderBridge
{
    static bool _resolved, _ok;
    static Type _compType;
    static PropertyInfo _entityProp, _isValid;
    static MethodInfo _setOpacity, _setFlags;
    static object _visibleFlag;
    public static string Problem = "";

    static bool Resolve()
    {
        if (_resolved) return _ok;
        _resolved = true;
        _compType = EntryFxBridge.FindType("VRage.Game.Client", "Keen.VRage.Game.Client.Render.ModelRenderBaseComponent");
        _entityProp = _compType?.GetProperty("RenderModelEntity", BindingFlags.Public | BindingFlags.Instance);
        var me = _entityProp?.PropertyType;
        _isValid = me?.GetProperty("IsValid", BindingFlags.Public | BindingFlags.Instance);
        _setOpacity = me?.GetMethod("SetOpacity", BindingFlags.Public | BindingFlags.Instance, null, new[] { typeof(float) }, null);
        var flags = EntryFxBridge.FindType("VRage.Render", "Keen.VRage.Render.Data.RenderFlags");
        if (flags != null)
        {
            _visibleFlag = Enum.ToObject(flags, 1);   // (RenderFlags.Visible)
            _setFlags = me?.GetMethod("SetRenderFlagsState", BindingFlags.Public | BindingFlags.Instance, null, new[] { flags, typeof(bool) }, null);
        }
        _ok = _compType != null && _entityProp != null && _isValid != null && _setOpacity != null && _setFlags != null;
        if (!_ok)
        {
            Problem = _compType == null ? "ModelRenderBaseComponent not found" : _entityProp == null ? "RenderModelEntity not found" : "ModelEntity SetOpacity / SetRenderFlagsState / IsValid not found";
            Log.Default?.Info("[AERO] rotor blade / blur fades off: " + Problem);
        }
        return _ok;
    }

    /// <summary>The entity's model render component (an object: its type is not a mod reference), or null.</summary>
    internal static object RenderOf(Entity e)
    {
        if (!Resolve() || e == null) return null;
        foreach (var c in e.Components) if (c != null && _compType.IsInstanceOfType(c)) return c;
        return null;
    }

    /// <summary>Show the part at this opacity (0: hidden). False: not applied (no render entity yet, or not resolved).</summary>
    internal static bool Set(object render, float opacity, object[] args1, object[] args2)
    {
        if (render == null || !_ok) return false;
        try
        {
            object me = _entityProp.GetValue(render);   // (a boxed struct: the call is only made when the fade changes)
            if (me == null || !(bool)_isValid.GetValue(me)) return false;
            bool visible = opacity > 0.004f;
            args1[0] = Math.Clamp(opacity, 0f, 1f);
            _setOpacity.Invoke(me, args1);
            args2[0] = _visibleFlag; args2[1] = visible;
            _setFlags.Invoke(me, args2);
            return true;
        }
        catch (Exception ex)
        {
            // (once: the fades are switched off - not a per-frame cost)
            _ok = false; Problem = "fade failed: " + ex.GetType().Name + " " + ex.Message;
            Log.Default?.Info("[AERO] rotor blade / blur fades off: " + Problem);
            return false;
        }
    }
}
