#pragma warning disable
using System;
using System.Collections.Generic;
using Keen.Game2.Simulation.WorldObjects.CubeBlocks;
using Keen.VRage.Core;
using Keen.VRage.Core.Game.Components;
using Keen.VRage.Core.Render;
using Keen.VRage.Core.Systems;
using Keen.VRage.DCS.Annotations;

namespace AeroMod;

/// <summary>
/// A helicopter rotor's moving parts on a CLIENT (injected into the rotor blocks' client prefabs): the hub, three blades and
/// the blur disc, spawned as subparts at their dummies (ConditionalSubParts: tools/meshgen/rotor_split.py,
/// gen_propulsor_defs.py) - visual only. Which child is which is told by where it spawned: the blades off the axis (each
/// turned 120 deg), on the axis the hub above the blur disc. Every frame, from the server's rotor record
/// (RotorCyclic.Publish -> ThrustTorque.RotorState: the governed rpm, the thrust, its maximum, the cyclic's disc tilt),
/// RotorKinematics sets them: the spin at the governed rpm (tip speed TipSpeed), the collective and the cyclic's once-a-turn
/// pitch on each blade, the coning, the disc tilted with the cyclic, and at speed the blades fade out as the blur disc fades
/// in (RotorRenderBridge: the engine's opacity - dithered while below 1; the disc's own translucency is its AeroRotorBlur
/// material's alpha, so at speed it blends smoothly). No record (the grid static, the aero mod's thrust off): the rpm is
/// estimated here from the block's own state, no thrust, no tilt.
/// The REVERSIBLE ROTOR (PropulsorKinds.TailRotor, 2026-10-17) is drawn the same but with no collective, no cyclic and no
/// coning: every blade at +/- TailRotor.PitchDeg by the side it pushes (its ReversibleThrustComponent.PushSide), eased
/// through flat as it turns round.
/// A TURBOPROP (2026-10-17, the user: "the same 0.75 blade speed / blur treatment") split into a spinner, a blades subpart and
/// a blur disc (their subpart prefabs' entity names TurbopropSpinner / TurbopropBlades / TurbopropBlur - gen_propulsor_defs.py)
/// turns the spinner and blades about its axis at the governed rpm (idle 75 %, up with the thrust asked) and fades the blades
/// out as the blur disc fades in (the rotors' fades). Not split (the one vanilla-spun prop subpart) or no blur disc: left as
/// it is / the blades never fade.
/// BROKEN BLADES (rotor strikes, 2026-10-17: the server publishes them - ThrustTorque.BladeState): the rotor or prop stands still, its
/// blades and blur disc hidden, the hub / spinner left as the stub; welded back up, they return.
/// </summary>
public partial class AeroRotorAnimComponent : Component, IInSceneListener
{
    public static bool Enabled = true;
    /// <summary>Harness: [AERO-ROTORANIM] lines (each rotor, ~5 a second) and the in-game checks (Summary).</summary>
    public static bool Log;
    /// <summary>Harness: the visual spin x this (1: the governed rpm; small values slow it down to see the blades' pitch).</summary>
    public static float DevSpinScale = 1f;
    /// <summary>The smoothing of the published collective, coning and tilt (s): the server's steps are a frame apart.</summary>
    public const float SmoothSeconds = 0.12f;
    /// <summary>The blade's tip over its hinge radius when the block's table row has no radius (the main rotors, rotor_rig_v2.py:
    /// 5 cells / 0.5 cell - 7.5 / 0.75 m on the 1.5 m rotor; the Reversible Rotor is 2.5 / 0.5 cell, so the tip comes from
    /// PropulsorBlocks' Radius, the tip radius), and the blur disc's cone (deg).</summary>
    public const float TipOverHinge = 7.5f / 0.75f, BlurConeDeg = 2.5f;

    enum Role { Hub, Blade, Blur }
    /// <summary>The turboprop's parts by their subpart prefab's entity name (gen_propulsor_defs.py writes them).</summary>
    public const string PropSpinnerName = "TurbopropSpinner", PropBladesName = "TurbopropBlades", PropBlurName = "TurbopropBlur";
    /// <summary>A turboprop's drawn spin at 100 % rpm (rev/s: a picture, not its shaft speed - a 0.6 m prop's real ~50 rev/s
    /// strobes at 60 fps) and its axis in the block (the spec's rotor_rotation_axis: Forward, a positive turn about it).</summary>
    public static float PropSpinRevPerSec = 4f;
    public static readonly Vector3 PropSpinAxis = new Vector3(0f, 0f, -1f);
    AeroPropulsorComponent.Kinds _kind = AeroPropulsorComponent.Kinds.Rotor; bool _prop; ReversibleThrustComponent _rev;
    sealed class Part
    {
        public Entity Entity; public RelativeTransform Rest; public Role Role; public object Render;
        public float Shown = -1f; public readonly object[] A1 = new object[1], A2 = new object[2];
        public Vector3 LastPos; public Quaternion LastRot; public bool HasLast;
    }
    readonly List<Part> _parts = new();
    Part _hub, _blur;
    int _childCount = -1;
    Vector3I _key; bool _hasKey;
    ThrustTorque.Published _match; long _recheckAt;
    /// <summary>Rotor strikes: the server's blade record (ThrustTorque.BladeState) and whether the blades are broken now.</summary>
    ThrustTorque.Published _bladeMatch; long _bladeRecheck; bool _broken;
    readonly float[] _rec = new float[ThrustTorque.RotorStride];
    Quaternion _blockInGrid = Quaternion.Identity; bool _hasBlockQ;
    float _rpm = -1f, _psi, _coll, _cone, _hinge, _span, _tip; Vector3 _tilt; long _last; bool _matched;
    long _logAt;

    // ── in-game checks (harness: aeroget AeroRotorAnimComponent.Summary) ──
    static int _frames, _poseErrors, _matchedFrames; static float _worstTipPlaneDeg, _worstPose;
    public static string Summary => $"frames {_frames} matched {_matchedFrames} pose errors {_poseErrors} (worst {_worstPose:F4} m) tip-plane vs command worst {_worstTipPlaneDeg:F3} deg | fades: {(RotorRenderBridge.Problem.Length == 0 ? "ok" : RotorRenderBridge.Problem)}";
    public static bool ResetChecks { set { _frames = _poseErrors = _matchedFrames = 0; _worstTipPlaneDeg = _worstPose = 0f; } }

    void IInSceneListener.OnAddedToScene() { }
    void IInSceneListener.OnBeforeRemovedFromScene() { _parts.Clear(); _childCount = -1; _hub = _blur = null; }

    [After(typeof(RenderSubmissionBegin))]
    private class OnRotorAnim : JobGroup;

    [OnRotorAnim]
    [MustHave(typeof(AeroRotorAnimComponent))]
    private static void RotorAnimJob(AeroRotorAnimComponent c)
    {
        if (!Enabled || !GridPhysics.Alive(c.Data)) return;
        c.Step();
    }

    void Step()
    {
        if (!_hasKey)
        {
            var block = Entity?.TryGet<CubeBlockComponent>();
            if (block == null) return;
            var bb = block.AABB; _key = bb.Min + bb.Max; _hasKey = true;
        }
        FindParts();
        if (_hub == null) return;
        var grid = Entity.GetTopLevelParent();
        if (grid == null) return;
        if (!_hasBlockQ)
        {
            // (the block's turn in its grid - fixed: grid-local tilts into the block's frame)
            _blockInGrid = Quaternion.Normalize(Quaternion.Inverse(grid.Data.GetWorldTransform().Orientation) * Data.GetWorldTransform().Orientation);
            _hasBlockQ = true;
        }
        long now = System.Diagnostics.Stopwatch.GetTimestamp();
        float dt = _last == 0 ? 0f : Math.Clamp((float)((now - _last) / (double)System.Diagnostics.Stopwatch.Frequency), 0f, 0.1f);
        _last = now;

        _matched = ThrustTorque.RotorState(grid, _key, ref _match, ref _recheckAt, now, _rec);
        float rpm, thrust, maxNow, rated; Vector3 tiltGrid;
        if (_matched)
        {
            rpm = _rec[0]; thrust = _rec[1]; maxNow = _rec[2]; rated = _rec[3]; tiltGrid = new Vector3(_rec[4], _rec[5], _rec[6]);
        }
        else
        {
            // (no record: the rpm from the block's own state, nothing else)
            bool on = !Data.Has<DisabledTag>();
            float idle = AeroPropulsorComponent.IdleRpm(_kind);
            rpm = RotorKinematics.StepRpm(_rpm < 0f ? (on ? idle : 0f) : _rpm, on, dt, idle);
            thrust = maxNow = rated = 0f; tiltGrid = Vector3.Zero;
        }
        // BROKEN BLADES (rotor strikes: the server's AeroRotorStrikeComponent publishes blades that are not whole): stopped, the
        // blades and the blur disc hidden - the hub / spinner left as the stub
        _broken = ThrustTorque.BladeState(grid, _key, ref _bladeMatch, ref _bladeRecheck, now, out float bladeHealth) && RotorStrike.IsBroken(bladeHealth);
        if (_broken) rpm = 0f;
        _rpm = rpm;
        if (_prop) { StepProp(rpm, dt); return; }
        bool tail = _kind == AeroPropulsorComponent.Kinds.TailRotor;
        if (tail) { thrust = 0f; tiltGrid = Vector3.Zero; }   // (no collective, no coning, no cyclic: the pitch is its side's)
        float omega = RotorKinematics.TipSpeed / MathF.Max(_tip, 0.5f);
        _psi += omega * rpm * DevSpinScale * dt;
        if (_psi > MathF.PI * 2f) _psi -= MathF.PI * 2f * MathF.Floor(_psi / (MathF.PI * 2f));
        float k = dt > 0f ? 1f - MathF.Exp(-dt / SmoothSeconds) : 1f;
        // (the Reversible Rotor: its blades by its push's side - its ReversibleThrustComponent's, the server's)
        _rev ??= tail ? Entity.TryGet<ReversibleThrustComponent>() : null;
        float coll = tail ? TailRotor.Pitch(_rev != null ? _rev.PushSide : 1) : RotorKinematics.Collective(thrust, maxNow);
        _coll += (coll - _coll) * k;
        _cone += ((tail ? 0f : RotorKinematics.Coning(thrust, rated, rpm)) - _cone) * k;
        float baked = tail ? TailRotor.BakedPitch75Deg * MathF.PI / 180f : BakedPitch75;
        var tiltBlock = Vector3.Transform(tiltGrid, Quaternion.Inverse(_blockInGrid));
        _tilt += (tiltBlock - _tilt) * k;

        var disc = RotorKinematics.Tilt(_tilt);
        var hubPos = _hub.Rest.Position;
        float bladeA = _broken ? 0f : RotorKinematics.BladeOpacity(rpm), blurA = _broken ? 0f : RotorKinematics.BlurOpacity(rpm);
        bool log = Log && now - _logAt > System.Diagnostics.Stopwatch.Frequency / 5;
        if (log) _logAt = now;
        System.Text.StringBuilder sb = log ? new System.Text.StringBuilder() : null;
        Vector3 t0 = default, t1 = default, t2 = default; int nt = 0;
        foreach (var p in _parts)
        {
            if (p.Entity == null || !GridPhysics.Alive(p.Entity.Data)) { _childCount = -1; continue; }
            Vector3 pos; Quaternion rot; float alpha;
            switch (p.Role)
            {
                case Role.Hub:
                    pos = p.Rest.Position; rot = RotorKinematics.HubRot(disc, _psi, p.Rest.Orientation); alpha = 1f; break;
                case Role.Blade:
                {
                    var span = RotorKinematics.SpanDir(p.Rest.Orientation, _psi);
                    float pitch = RotorKinematics.BladePitch(_coll, _tilt, span) - baked;
                    RotorKinematics.BladePose(hubPos, disc, _psi, p.Rest.Position, p.Rest.Orientation, _cone, pitch, out pos, out rot);
                    alpha = bladeA;
                    var tip = pos + Vector3.Transform(Vector3.UnitZ * (_span > 0f ? _span : 1f), rot);
                    if (nt == 0) t0 = tip; else if (nt == 1) t1 = tip; else t2 = tip; nt++;
                    if (log) sb.Append($" b{nt} pitch {(pitch + baked) * 57.2958f:F2}");
                    break;
                }
                default:
                    RotorKinematics.BlurPose(hubPos, disc, p.Rest.Position, p.Rest.Orientation, _cone, BlurConeDeg * MathF.PI / 180f, _span, out pos, out rot);
                    alpha = blurA; break;
            }
            // (the check: last frame's pose is where the engine has the part now - block world x relative)
            if (Log && p.HasLast) CheckPose(p);
            p.Entity.Data.Set(new RelativeTransform(pos, rot));
            p.LastPos = pos; p.LastRot = rot; p.HasLast = true;
            if (p.Role != Role.Hub && (MathF.Abs(alpha - p.Shown) > 0.01f || (alpha <= 0f && p.Shown > 0f) || (alpha >= 1f && p.Shown < 1f)))
            {
                p.Render ??= RotorRenderBridge.RenderOf(p.Entity);
                if (RotorRenderBridge.Set(p.Render, alpha, p.A1, p.A2)) p.Shown = alpha;
            }
        }
        if (Log)
        {
            _frames++; if (_matched) _matchedFrames++;
            if (nt == 3)
            {
                // the tips' plane leans as commanded: the coning raises every tip alike, the tilt leans the plane
                var n = Vector3.Normalize(Vector3.Cross(t1 - t0, t2 - t0)); if (n.Y < 0f) n = -n;
                var want = Vector3.Transform(Vector3.UnitY, disc);
                float err = MathF.Acos(Math.Clamp(Vector3.Dot(n, want), -1f, 1f)) * 57.2958f;
                if (err > _worstTipPlaneDeg) _worstTipPlaneDeg = err;
            }
        }
        if (log)
            Keen.VRage.Library.Diagnostics.Log.Default?.Info($"[AERO-ROTORANIM] {_key} {(_matched ? "rec" : "local")} rpm {rpm * 100f:F1}% psi {_psi * 57.2958f:F0} T {thrust:F0}/{maxNow:F0} N (rated {rated:F0}) coll {_coll * 57.2958f:F2} cone {_cone * 57.2958f:F2} tilt ({_tilt.X * 57.2958f:F2}, {_tilt.Z * 57.2958f:F2}) deg (grid {tiltGrid.X * 57.2958f:F2}, {tiltGrid.Y * 57.2958f:F2}, {tiltGrid.Z * 57.2958f:F2}) blades {bladeA:F2} blur {blurA:F2}{sb}");
    }

    /// <summary>The blade model's own pitch at 75 % radius (rad, rotor_rig_v2.py "baked_pitch75_deg": the main rotors' blade, a
    /// linear twist 8.0 deg at 30 % to 2.6 deg at 95 %; the Reversible Rotor's blade is 4.78 - drawn 0.5 deg low): the collective is
    /// drawn as the pitch less this.</summary>
    public const float BakedPitch75 = 4.25f * MathF.PI / 180f;

    Part _blades;

    /// <summary>A turboprop split into spinner, blades and blur disc (by their entity names; the intake fan and anything else
    /// left alone - the fan keeps its vanilla spin). Without spinner and blades: not animated (the old one-piece prop, spun
    /// by the vanilla animation). Without the blur disc: the blades never fade.</summary>
    void FindPropParts(IEnumerable<Entity> kids, PropulsorBlocks.Row row)
    {
        Part spinner = null, blades = null, blur = null;
        var all = new List<Part>();   // (once per change of the children: not a per-frame allocation)
        var names = new System.Text.StringBuilder();
        foreach (var k in kids)
        {
            if (k == null || !k.Data.TryGet<RelativeTransform>(out var rt)) continue;
            string name = k.DebugName;
            names.Append(name).Append(" @").Append(rt.Position).Append("; ");
            var q = new Part { Entity = k, Rest = rt };
            all.Add(q);
            // (by name: the prefab's entity name, or its subpart dummy's - whichever the game gives the spawned child)
            // (in game the children are named after their subpart compositions - "Turboprop_150_Rotor_Client_Composite",
            //  "_Blade_", "_Blur_", "_Fan_" (2026-10-17): matched by those parts first)
            if (NameIs(name, "_Rotor_", PropSpinnerName) || NameIs(name, "PropRotor", "PropRotor")) { q.Role = Role.Hub; spinner = q; }
            else if (NameIs(name, "_Blade_", PropBladesName) || NameIs(name, "PropBlade", "PropBlade")) { q.Role = Role.Blade; blades = q; }
            else if (NameIs(name, "_Blur_", PropBlurName) || NameIs(name, "PropBlur", "PropBlur")) { q.Role = Role.Blur; blur = q; }
        }
        if (spinner == null || blades == null || blur == null) FindPropPartsByPlace(all, ref spinner, ref blades, ref blur);
        // (2026-10-17, in game: the names did not come through - "turboprop without a split spinner" on every one, the blur
        //  disc left showing over still blades. The children's names and places, so the next run shows what the game gives)
        if (AeroLog.Detail) Keen.VRage.Library.Diagnostics.Log.Default?.Info($"[AERO-ROTORANIM] {_key} client: turboprop children {names}");
        if (blur != null) _parts.Add(blur);
        if (spinner == null || blades == null)
        {
            // (not told apart: whatever was found by place keeps turning together - the spinner and blades as one, no fade; the
            //  blur disc stays hidden, so a parked prop never shows a disc over its blades)
            _parts.Clear();
            foreach (var q in all) if (q != blur && q.Role == Role.Hub) _parts.Add(q);
            if (blur != null) { blur.Role = Role.Blur; _parts.Add(blur); }
            if (_parts.Count == 0 || _parts[0].Role != Role.Hub)
            {
                if (blur != null) { var r = RotorRenderBridge.RenderOf(blur.Entity); RotorRenderBridge.Set(r, 0f, blur.A1, blur.A2); }
                _parts.Clear();
                Keen.VRage.Library.Diagnostics.Log.Default?.Info($"[AERO-ROTORANIM] {_key}: turboprop without a split spinner / blades - not animated, blur disc hidden");
                return;
            }
            _hub = _parts[0]; _blades = null; _blur = blur; _prop = true; _propNoFade = true;
            _tip = row != null ? row.Radius : 1f;
            Keen.VRage.Library.Diagnostics.Log.Default?.Info($"[AERO-ROTORANIM] {_key} client: turboprop spinner and blades not told apart - turned together, no fade, blur disc hidden");
            return;
        }
        _parts.Add(spinner); _parts.Add(blades);
        _propNoFade = false;
        _hub = spinner; _blades = blades; _blur = blur; _prop = true;
        _tip = row != null ? row.Radius : 1f;
        if (AeroLog.Detail) Keen.VRage.Library.Diagnostics.Log.Default?.Info($"[AERO-ROTORANIM] {_key} client: turboprop spinner at {spinner.Rest.Position}, blades at {blades.Rest.Position}, {(blur != null ? $"blur disc at {blur.Rest.Position}" : "no blur disc (the blades never fade)")}");
    }

    /// <summary>A turboprop's frame: the spinner and blades turned about its axis through the spinner by the governed rpm; the
    /// blades fade out as the blur disc fades in (none: the blades stay).</summary>
    void StepProp(float rpm, float dt)
    {
        _psi += PropSpinRevPerSec * 2f * MathF.PI * rpm * DevSpinScale * dt;
        if (_psi > MathF.PI * 2f) _psi -= MathF.PI * 2f * MathF.Floor(_psi / (MathF.PI * 2f));
        var spin = Quaternion.CreateFromAxisAngle(PropSpinAxis, _psi);
        var pivot = _hub.Rest.Position;
        float bladeA = _broken && !_propNoFade ? 0f : _blur != null && !_propNoFade ? RotorKinematics.BladeOpacity(rpm) : 1f;
        float blurA = _propNoFade || _broken ? 0f : RotorKinematics.BlurOpacity(rpm);   // (0 at rest: a parked prop shows no disc; none broken)
        foreach (var p in _parts)
        {
            if (p.Entity == null || !GridPhysics.Alive(p.Entity.Data)) { _childCount = -1; continue; }
            float alpha = 1f;
            if (p.Role != Role.Blur)
            {
                var pos = pivot + Vector3.Transform(p.Rest.Position - pivot, spin);
                p.Entity.Data.Set(new RelativeTransform(pos, Quaternion.Normalize(spin * p.Rest.Orientation)));
                if (p.Role == Role.Blade) alpha = bladeA;
            }
            else alpha = blurA;
            if (p.Role != Role.Hub && (MathF.Abs(alpha - p.Shown) > 0.01f || (alpha <= 0f && p.Shown > 0f) || (alpha >= 1f && p.Shown < 1f)))
            {
                p.Render ??= RotorRenderBridge.RenderOf(p.Entity);
                if (RotorRenderBridge.Set(p.Render, alpha, p.A1, p.A2)) p.Shown = alpha;
            }
        }
        if (Log) { _frames++; if (_matched) _matchedFrames++; }
    }

    void CheckPose(Part p)
    {
        var bw = Data.GetWorldTransform(); var pw = p.Entity.Data.GetWorldTransform();
        var expect = bw.Position + (Vector3D)Vector3.Transform(p.LastPos, bw.Orientation);
        float d = (float)(pw.Position - expect).Length();
        // (read a frame later: 5 cm allowed for the block's own motion in a frame)
        if (d > _worstPose) _worstPose = d;
        if (d > 0.05f) _poseErrors++;
    }

    bool _propNoFade;

    static bool NameIs(string name, string a, string b) =>
        name != null && (name.IndexOf(a, StringComparison.Ordinal) >= 0 || name.IndexOf(b, StringComparison.Ordinal) >= 0);

    /// <summary>The turboprop's parts by their places (the names not given): the spinner and the blades share one dummy point,
    /// the blur disc sits a few mm aft of it (gen_propulsor_defs.py), the intake fan elsewhere. The two at the shared point
    /// cannot be told apart by place: both are marked Hub (turned together); the blur disc is found.</summary>
    static void FindPropPartsByPlace(List<Part> all, ref Part spinner, ref Part blades, ref Part blur)
    {
        for (int i = 0; i < all.Count; i++)
            for (int j = i + 1; j < all.Count; j++)
            {
                if ((all[i].Rest.Position - all[j].Rest.Position).Length() > 1e-4f) continue;
                // (the shared point: the spinner and the blades)
                if (spinner == null && blades == null) { all[i].Role = Role.Hub; all[j].Role = Role.Hub; }
                var p0 = all[i].Rest.Position;
                for (int k = 0; k < all.Count; k++)
                {
                    if (k == i || k == j || blur != null) continue;
                    float d = (all[k].Rest.Position - p0).Length();
                    if (d > 5e-4f && d < 0.03f) { all[k].Role = Role.Blur; blur = all[k]; }
                }
                return;
            }
    }

    /// <summary>The spawned children (on a change of their count): the blades off the axis (their centroid is the axis),
    /// on it the hub (higher) and the blur disc.</summary>
    void FindParts()
    {
        var kids = Entity?.TryGet<HierarchyComponent>()?.Children;
        if (kids == null) { _parts.Clear(); _hub = _blur = null; return; }
        int n = 0; foreach (var k in kids) n++;
        if (n == _childCount) return;
        _childCount = n; _parts.Clear(); _hub = _blur = null; _blades = null; _prop = false;
        var row = PropulsorBlocks.RowOf(Entity);
        if (row != null) _kind = row.Kind;
        if (_kind == AeroPropulsorComponent.Kinds.Turboprop) { FindPropParts(kids, row); return; }
        Vector3 c = Vector3.Zero;
        foreach (var k in kids)
            if (k != null && k.Data.TryGet<RelativeTransform>(out var rt)) { _parts.Add(new Part { Entity = k, Rest = rt }); c += rt.Position; }
        if (_parts.Count != 5) { Keen.VRage.Library.Diagnostics.Log.Default?.Info($"[AERO-ROTORANIM] {_key}: {_parts.Count} children, not a split rotor (5) - not animated"); _parts.Clear(); return; }
        c /= 5f;
        Part a = null, b = null; int blades = 0; float hinge = 0f;
        foreach (var p in _parts)
        {
            var d = p.Rest.Position - c; d.Y = 0f;
            if (d.Length() > 0.1f) { p.Role = Role.Blade; blades++; hinge += d.Length(); }
            else if (a == null) a = p; else b = p;
        }
        if (blades != 3 || a == null || b == null) { _parts.Clear(); return; }
        if (a.Rest.Position.Y >= b.Rest.Position.Y) { a.Role = Role.Hub; b.Role = Role.Blur; _hub = a; _blur = b; }
        else { b.Role = Role.Hub; a.Role = Role.Blur; _hub = b; _blur = a; }
        _hinge = hinge / 3f;
        // (the tip: the block's table radius - PropulsorBlocks, the rig's tip radius; else the main rotors' ratio)
        _tip = row != null && row.Radius > _hinge ? row.Radius : _hinge * TipOverHinge;
        _span = _tip - _hinge;
        if (AeroLog.Detail) Keen.VRage.Library.Diagnostics.Log.Default?.Info($"[AERO-ROTORANIM] {_key} client: hub at {_hub.Rest.Position}, blur at {_blur.Rest.Position}, 3 blades, hinge radius {_hinge:F3} m, tip {_tip:F2} m, span {_span:F2} m");
    }
}
