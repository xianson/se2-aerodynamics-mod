#pragma warning disable
using System;
using Keen.Game2.Simulation.WorldObjects.CubeBlocks.MechanicalBlocks;
using Keen.VRage.Core;
using Keen.VRage.Core.Game.Components;
namespace AeroMod;

/// <summary>
/// CONTROL SURFACES AS NATIVE SUBGRIDS: vanilla hinges whose head carries a flap, driven by aero like the 1x1
/// control-surface block - fly-by-wire mixing, per surface, of the pilot's wanted rotation:
///   - the flap's effect: a positive hinge angle swings its trailing edge toward n = axis x chord (right-hand rule about
///     the hinge's Forward), which pushes the air that way and the flap the other: force along -n, at the flap's middle
///     (the hinge plus half a chord aft), torque r x (-n) about the centre of mass - its unit direction is what +angle does;
///   - the command: (wanted rotation . that) x gain (lower at high dynamic pressure) - (spin . that) x damping, clamped
///     to +-1, times MaxDeflection, about the NEUTRAL angle (the middle of the hinge's limits: a vanilla hinge turns
///     0..180 degrees, so a flap is mounted to trail at 90 and deflects 70..110), is the angle aimed for; the hinge's
///     velocity = Gain x (aim - angle).
/// The flap's real aerodynamics are its own grid's (aero runs on the subgrid like any grid): this only moves it.
/// Which hinges: Mode 0 none (the default: a player's hinges are theirs), 1 every hinge of a grid that has a cockpit
/// or remote-control input (generated test planes; harness: aeroset HingeSurfaces.Mode 1).
/// Called from the grid's control step (AeroGridComponent.UpdateControlSurfaceInputs) on the server copy.
/// </summary>
public static class HingeSurfaces
{
    public static int Mode;
    /// <summary>Degrees at full command; the hinge's own limits clamp too.</summary>
    public static float MaxDeflection = 20f;
    /// <summary>Command gains (as the 1x1 surfaces): rotation wanted x Kp x speed gain - spin x Kd; the hinge's speed
    /// toward its aim (rad/s per rad).</summary>
    public static float Kp = 1f, Kd = 1.5f, Gain = 6f;
    /// <summary>Chord assumed for the moment arm when the head grid's size is unknown (m).</summary>
    public static float Chord = 2.5f;

    sealed class Surface { public Entity Block; public HingeComponent Hinge; public Vector3 Pos, Axis, Chord0; public float Aim, Neutral; }
    sealed class GridSurfaces { public List<Surface> List = new(); public int Seen = -1; public long At; }
    static readonly System.Runtime.CompilerServices.ConditionalWeakTable<AeroGridComponent, GridSurfaces> _byGrid = new();

    /// <summary>Diagnostics: surfaces driven on the last grid, its commands.</summary>
    public static string Status = "";

    internal static bool Wanted(AeroGridComponent aero) => Mode != 0 && aero.IsServerScene;

    /// <summary>Every hinge on the grid (refreshed every 2 s - blocks come and go): its place and axes (grid frame).</summary>
    static List<Surface> Find(AeroGridComponent aero)
    {
        var gs = _byGrid.GetOrCreateValue(aero);
        long now = System.Diagnostics.Stopwatch.GetTimestamp();
        if (gs.At != 0 && now - gs.At < 2 * System.Diagnostics.Stopwatch.Frequency) return gs.List;
        gs.At = now;
        gs.List.Clear();
        var kids = aero.Entity?.TryGet<HierarchyComponent>()?.Children;
        if (kids == null) return gs.List;
        var gridWt = aero.Entity.Data.GetWorldTransform();
        var inv = Quaternion.Inverse(gridWt.Orientation);
        foreach (var k in kids)
        {
            var h = k?.TryGet<HingeComponent>();
            if (h == null || h.ConnectedEntity == null) continue;   // (no head: nothing to swing)
            var bt = k.Data.GetWorldTransform();
            var q = inv * bt.Orientation;   // (the hinge block's orientation in the grid's frame)
            // (a vanilla hinge turns 0..180 degrees only: the flap's neutral is the middle of its limits - 90 for the
            //  default - and its chord there is the head's +Y turned by that angle about the hinge's Forward)
            float lo = h.EnableLocalLimits ? h.LocalMinLimit : h.MinLimit, hi = h.EnableLocalLimits ? h.LocalMaxLimit : h.MaxLimit;
            float neutral = lo > -1e6f && hi < 1e6f ? 0.5f * (lo + hi) : 0f;
            Vector3 axis = Vector3.Transform(Vector3.Forward, q), up0 = Vector3.Transform(Vector3.Up, q);
            gs.List.Add(new Surface
            {
                Block = k, Hinge = h, Neutral = neutral,
                Pos = Vector3.Transform((Vector3)(bt.Position - gridWt.Position), inv),
                Axis = axis,
                Chord0 = up0 * MathF.Cos(neutral) + Vector3.Cross(axis, up0) * MathF.Sin(neutral),
            });
        }
        return gs.List;
    }

    /// <summary>The grid's control step: every hinge surface toward its share of the wanted rotation.</summary>
    internal static void Drive(AeroGridComponent aero, Vector3 centerOfMass, Vector3 targetAngVel, Vector3 localAngVel, float gainScale)
    {
        if (!Wanted(aero)) return;
        var list = Find(aero);
        if (list.Count == 0) return;
        float max = MaxDeflection * MathF.PI / 180f;
        int n = 0; float sum = 0f;
        foreach (var s in list)
        {
            Vector3 nrm = Vector3.Cross(s.Axis, s.Chord0);
            Vector3 r = s.Pos + s.Chord0 * (0.5f * Chord) - centerOfMass;
            Vector3 eff = Vector3.Cross(r, -nrm);
            float len = eff.Length();
            if (len < 0.05f) continue;
            eff /= len;
            float cmd = Math.Clamp(Vector3.Dot(eff, targetAngVel) * Kp * gainScale - Vector3.Dot(eff, localAngVel) * Kd, -1f, 1f);
            s.Aim = s.Neutral + cmd * max;
            if (!s.Block.Data.TryGet<HingeComponent.HingeData>(out var hd)) continue;
            s.Hinge.SetVelocity(Gain * (s.Aim - hd.CurrentAngle));
            n++; sum += MathF.Abs(cmd);
        }
        Status = $"{n} hinge surfaces, mean |cmd| {(n > 0 ? sum / n : 0f):F2}";
    }
}
