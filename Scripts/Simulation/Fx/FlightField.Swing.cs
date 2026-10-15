#pragma warning disable CS0649, CS8632, CS1591, CS1573, CS1570, CS1574, CS0419, CS1734
using System;
using Keen.VRage.Core;
namespace AeroMod;

/// <summary>
/// THE SWING: the plasma's and the vapour sheets' emitters turned every frame from the flow they were laid out for
/// (_axis) to the eased live flow (_axisSm), about the hull's centre - so between re-layouts (past AxisTol) the whole
/// field turns smoothly with the air instead of holding still and jumping (the user, 2026-10-09: "make sure everything
/// smoothly flows"). Each emitter's laid-out transform is kept (_baseOf); while the swing changes (over SwingStepDeg) each
/// is moved (UpdateTransform: a move, its particles kept). At a re-layout _axis becomes _axisSm: the swing is back to
/// none in the same frame the emitters are carried onto the new layout - where the swing had them, within AxisTol.
/// (A render root of the field's own, turned in one call, cannot be: a particle parent is an IRenderParent, in an
/// assembly mods cannot reference.) The tips are not swung: world-space trails, aimed by TipLead_.
/// </summary>
internal sealed partial class FlightField
{
    public static bool SwingOn = true;
    /// <summary>The swing is applied again once it moved this far (degrees).</summary>
    public static float SwingStepDeg = 0.1f;

    private readonly Dictionary<object, RelativeTransform> _baseOf = new();
    private Vector3 _pivot;
    private Quaternion _swingDone = Quaternion.Identity;
    public static int RecSwingMoves;

    private RelativeTransform Swung(RelativeTransform at, Quaternion q) =>
        q == Quaternion.Identity ? at : new RelativeTransform(_pivot + q * (at.Position - _pivot), q * at.Orientation);

    /// <summary>Every frame: the swing from the laid-out flow to the live one; the emitters moved when it changed.</summary>
    private void SwingFollow()
    {
        Quaternion q = Quaternion.Identity;
        if (SwingOn && _axis != default && _axisSm != default && Vector3.Dot(_axis, _axisSm) < 0.9999999f)
            q = Quaternion.CreateFromTwoVectors(_axis, _axisSm);
        float d = MathF.Abs(Quaternion.Dot(q, _swingDone));
        if (d > MathF.Cos(SwingStepDeg * MathF.PI / 360f)) return;   // (under SwingStepDeg from what was applied)
        _swingDone = q;
        if (_baseOf.Count == 0) return;
        try
        {
            foreach (var kv in _baseOf)
            {
                var h = kv.Key;
                (_updTransform ??= h.GetType().GetMethod("UpdateTransform", AnyInst, null, new[] { typeof(RelativeTransform) }, null))
                    .Invoke(h, new object[] { Swung(kv.Value, q) });
                RecSwingMoves++;
            }
        }
        catch (Exception e) { _puffWhy = "swing: " + Inner(e); }
    }

    /// <summary>An emitter no longer the field's: not swung.</summary>
    private void Unswing(object h) => _baseOf.Remove(h);
}
