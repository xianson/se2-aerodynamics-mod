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
/// COUNTER-ROTATING (2026-10-23, the main rotor's terminal switch - AeroRotorSpinComponent, published in the rotor record): drawn
/// as the MIRROR IMAGE of the clockwise rotor (RotorKinematics.MirrorX: every pose the clockwise law's on the mirrored inputs,
/// mirrored back) with the MIRRORED blade set - three more blade subparts at the same dummies whose entity is named
/// CcwBladeName (gen_propulsor_defs.py, mirrored in the blade's chord), shown instead of the clockwise three (the hidden set at
/// opacity 0, as the fade). Old content without that set: the clockwise blades are turned the other way (trailing edge first).
/// PER-BLADE BREAKS (2026-10-23; a helicopter rotor with the blade-break content): every blade has two more models at its dummy -
/// its TIP LOST and its STUB ("..._BladeTipLost_..." / "..._BladeStub_...", and "BladeCCWTipLost" / "BladeCCWStub" for the mirrored
/// set), paired to their blade by place; each blade shows the one model its published state asks (ThrustTorque.BladeState's cuts),
/// the others at opacity 0. Stubs stay drawn at any rpm (a broken hub turning), the blur disc fades with the blades left. When a
/// blade's state steps down the piece it lost is thrown as client debris (BladeDebris: chunk B for a lost tip, A for a stub).
/// Content without those models: a blade that is not whole is hidden.
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
    /// <summary>The blade's tip over its hinge radius when the block's table row has no radius (the main rotors: 7 cells / 0.5 cell -
    /// 10.5 / 0.75 m on the 1.5 m rotor since the longer blades of 2026-10-23, 5 cells before; the Reversible Rotor is 2.5 / 0.5
    /// cell, so the tip comes from PropulsorBlocks' Radius, the tip radius), and the blur disc's cone (deg).</summary>
    public const float TipOverHinge = 10.5f / 0.75f, BlurConeDeg = 2.5f;
    /// <summary>The counter-rotating blade set's subpart entity name (gen_propulsor_defs.py: dummies RotorBladeCCW1..3), and the
    /// part of a child's name that marks it: in game the children are named after their subpart compositions
    /// ("HelicopterRotor_150_BladeCCW_Client_Composite" - 2026-10-23, the first flight: matching CcwBladeName found none, so
    /// no rotor animated: blades still, the blur disc on).</summary>
    public const string CcwBladeName = "RotorBladeCCW", CcwBladeMatch = "BladeCCW";
    /// <summary>The blade-break models' names (the composition names contain them; the most specific is matched first - "BladeCCWTipLost"
    /// contains "BladeCCW").</summary>
    public const string TipLostMatch = "BladeTipLost", StubMatch = "BladeStub", CcwTipLostMatch = "BladeCCWTipLost", CcwStubMatch = "BladeCCWStub";

    /// <summary>A child's blade role by its name: ccw (the mirrored set) and variant (0 the whole blade, 1 tip lost, 2 stub);
    /// false: no blade mark in its name (a whole clockwise blade, the hub or the blur disc - told apart by place).</summary>
    public static bool BladeMark(string name, out bool ccw, out int variant)
    {
        ccw = false; variant = 0;
        if (name == null) return false;
        if (name.IndexOf(CcwTipLostMatch, StringComparison.Ordinal) >= 0) { ccw = true; variant = 1; return true; }
        if (name.IndexOf(CcwStubMatch, StringComparison.Ordinal) >= 0) { ccw = true; variant = 2; return true; }
        if (name.IndexOf(TipLostMatch, StringComparison.Ordinal) >= 0) { variant = 1; return true; }
        if (name.IndexOf(StubMatch, StringComparison.Ordinal) >= 0) { variant = 2; return true; }
        if (name.IndexOf(CcwBladeMatch, StringComparison.Ordinal) >= 0) { ccw = true; return true; }
        return false;
    }

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
        /// <summary>A blade of the counter-rotating (mirrored) set.</summary>
        public bool Ccw;
        /// <summary>Per-blade breaks: the model (0 whole, 1 tip lost, 2 stub) and the blade it belongs to (0..2; -1 not a blade).</summary>
        public int Variant, Index = -1;
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
    /// <summary>Drawn counter-rotating (the record's switch; kept while no record); the mirrored blade set was found.</summary>
    bool _ccw, _hasCcwSet; AeroPropulsorComponent _propc;
    /// <summary>Per-blade breaks: the TipLost / Stub models were found for the clockwise and the mirrored set; the blades' published
    /// states (RotorStrike.Pack) now and as last seen (-1: not seen yet - no debris for a state found on loading).</summary>
    bool _hasVariants, _hasCcwVariants; int _cuts, _seenCuts = -1;
    readonly Vector3[] _bladePos = new Vector3[RotorStrike.Blades]; readonly Quaternion[] _bladeRot = new Quaternion[RotorStrike.Blades];
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
            _ccw = _rec[8] > 0.5f;
            // (this client copy's propulsor: its reaction torque and limiter the server's way round - RotorCyclic in both scenes)
            _propc ??= Entity.TryGet<AeroPropulsorComponent>();
            if (_propc != null) _propc.CounterRotating = _ccw;
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
        bool bladeRec = ThrustTorque.BladeState(grid, _key, ref _bladeMatch, ref _bladeRecheck, now, out float bladeHealth, out int cuts);
        _cuts = bladeRec ? cuts : 0;
        _broken = bladeRec && RotorStrike.IsBroken(bladeHealth);
        // (per-blade breaks: a rotor at its stubs coasts down on the server - its rpm as published, the stubs turning)
        if (_broken && !(_hasVariants && !_prop)) rpm = 0f;
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
        // (the WOBBLE of an unbalanced rotor - 2026-10-23, the user: pieces lost tip the disc once a turn, with the rpm^2)
        var tiltNow = _tilt + Wobble(rpm);

        var disc = RotorKinematics.Tilt(tiltNow);
        var hubPos = _hub.Rest.Position;
        // COUNTER-ROTATING: every pose is the clockwise law's in the mirror (x -> -x), mirrored back - the inputs here
        bool mir = _ccw && !tail;
        var tiltL = mir ? RotorKinematics.MirrorX(tiltNow) : tiltNow;
        var discL = mir ? RotorKinematics.Tilt(tiltL) : disc;
        var hubL = mir ? RotorKinematics.MirrorX(hubPos) : hubPos;
        bool perBlade = _hasVariants;
        float bladeA = _broken && !perBlade ? 0f : RotorKinematics.BladeOpacity(rpm), blurA = _broken ? 0f : RotorKinematics.BlurOpacity(rpm);
        // (the blur disc fades with the blades left: a stub blurs nothing)
        int notStub = 0; for (int i = 0; i < RotorStrike.Blades; i++) if (RotorStrike.Unpack(_cuts, i) != RotorStrike.Cut.Stub) notStub++;
        blurA *= notStub / (float)RotorStrike.Blades;
        bool useCcw = mir && _hasCcwSet;
        bool setVariants = useCcw ? _hasCcwVariants : _hasVariants;
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
                    pos = p.Rest.Position; rot = RotorKinematics.HubRot(discL, _psi, Mir(mir, p.Rest.Orientation)); alpha = 1f; break;
                case Role.Blade:
                {
                    var restRot = Mir(mir, p.Rest.Orientation);
                    var span = RotorKinematics.SpanDir(restRot, _psi);
                    float pitch = RotorKinematics.BladePitch(_coll, tiltL, span) - baked;
                    RotorKinematics.BladePose(hubL, discL, _psi, Mir(mir, p.Rest.Position), restRot, _cone, pitch, out pos, out rot);
                    pos = Mir(mir, pos);
                    // (the set this turn shows: the mirrored set when counter-rotating and it is there; the other one hidden; in it, per
                    //  blade, the model its state asks - without the break models, a blade not whole is hidden)
                    var cut = p.Index >= 0 ? RotorStrike.Unpack(_cuts, p.Index) : RotorStrike.Cut.Whole;
                    bool shown = p.Ccw == useCcw && (setVariants ? p.Variant == (int)cut : p.Variant == 0 && cut == RotorStrike.Cut.Whole);
                    alpha = !shown ? 0f : p.Variant == 2 ? 1f : bladeA;   // (a stub drawn at any rpm)
                    if (p.Index >= 0 && p.Variant == 0 && p.Ccw == useCcw) { _bladePos[p.Index] = pos; _bladeRot[p.Index] = Mir(mir, rot); }
                    var tip = pos + Vector3.Transform(Vector3.UnitZ * (_span > 0f ? _span : 1f), Mir(mir, rot));
                    if (shown && p.Variant == 0) { if (nt == 0) t0 = tip; else if (nt == 1) t1 = tip; else t2 = tip; nt++; }
                    if (log && shown) sb.Append($" b{nt} pitch {(pitch + baked) * 57.2958f:F2}");
                    break;
                }
                default:
                    RotorKinematics.BlurPose(hubL, discL, Mir(mir, p.Rest.Position), Mir(mir, p.Rest.Orientation), _cone, BlurConeDeg * MathF.PI / 180f, _span, out pos, out rot);
                    pos = Mir(mir, pos);
                    alpha = blurA; break;
            }
            rot = Mir(mir, rot);
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
        if (_seenCuts >= 0 && _cuts != _seenCuts) ThrowPieces(grid, rpm, mir);
        _seenCuts = _cuts;
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

    BladeBreakBlocks.Row _bbRow; bool _bbLooked;
    readonly float[] _wobLost = new float[RotorStrike.Blades];

    /// <summary>The disc's tilt (block frame, as _tilt) an unbalanced rotor adds now: RotorStrike.Wobble of the published blades'
    /// lost pieces, turning with the blades. Zero with no break table, no pieces lost or every blade alike.</summary>
    Vector3 Wobble(float rpm)
    {
        if (_cuts == 0 || !(rpm > 0f)) return Vector3.Zero;
        if (!_bbLooked) { _bbLooked = true; _bbRow = BladeBreakBlocks.Of(PropulsorBlocks.RowOf(Entity)?.Name); }
        var row = _bbRow;
        if (row == null) return Vector3.Zero;
        for (int i = 0; i < RotorStrike.Blades; i++)
        {
            RotorStrike.Lost(RotorStrike.Unpack(_cuts, i), row.A.Mass, row.A.ComRadius(row.Hinge), row.B.Mass, row.B.ComRadius(row.Hinge), out _, out float m);
            _wobLost[i] = m;
        }
        float bladeMoment = RotorStrike.BladeMass(_tip) * 0.5f * (row.Hinge + _tip);   // (a whole blade's first moment about the axis)
        var w = RotorStrike.Wobble(_wobLost, bladeMoment, _psi, rpm);
        return new Vector3(w.X, 0f, w.Y);
    }

    static Vector3 Mir(bool m, Vector3 v) => m ? RotorKinematics.MirrorX(v) : v;
    static Quaternion Mir(bool m, Quaternion q) => m ? RotorKinematics.MirrorX(q) : q;

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

    /// <summary>A blade's state stepped down since last seen: the piece(s) it lost thrown as debris (BladeDebris) from where the
    /// blade is now - chunk B for a lost tip, chunk A (and B if it was still on) for a stub.</summary>
    void ThrowPieces(Entity grid, float rpm, bool mir)
    {
        var row = BladeBreakBlocks.Of(PropulsorBlocks.RowOf(Entity)?.Name);
        if (row == null) return;
        var session = Entity.GetSession();
        var bw = Data.GetWorldTransform();
        GridPhysics.TryGetVelocity(grid.Data, out var gridLin, out var gridAng);
        var gw = grid.Data.GetWorldTransform();
        // (the spin: about the block's -Y clockwise, +Y counter-rotating - the mirror)
        float omega = RotorKinematics.TipSpeed / MathF.Max(_tip, 0.5f) * Math.Clamp(rpm, 0f, 1f);
        var spinW = Vector3.Transform(mir ? Vector3.UnitY : -Vector3.UnitY, bw.Orientation) * omega;
        var hubW = bw.Position + (Vector3D)Vector3.Transform(_hub.Rest.Position, bw.Orientation);
        for (int k = 0; k < RotorStrike.Blades; k++)
        {
            var was = _seenCuts < 0 ? RotorStrike.Cut.Whole : RotorStrike.Unpack(_seenCuts, k);
            var now = RotorStrike.Unpack(_cuts, k);
            if (now <= was) continue;
            if (was == RotorStrike.Cut.Whole) Throw(session, row.B, k, bw, gw, gridLin, gridAng, spinW, hubW, "chunk B");
            if (now == RotorStrike.Cut.Stub) Throw(session, row.A, k, bw, gw, gridLin, gridAng, spinW, hubW, "chunk A");
        }
    }

    void Throw(Session session, BladeBreakBlocks.Chunk ch, int k, in WorldTransform bw, in WorldTransform gw, Vector3 gridLin, Vector3 gridAng, Vector3 spinW, Vector3D hubW, string what)
    {
        // (the blade's pose this frame, block-relative, then world; the chunk's centre of mass along it)
        var rot = Quaternion.Normalize(bw.Orientation * _bladeRot[k]);
        var at = bw.Position + (Vector3D)Vector3.Transform(_bladePos[k], bw.Orientation) + (Vector3D)Vector3.Transform(ch.Com, rot);
        var r = (Vector3)(at - hubW);
        var lin = gridLin + Vector3.Cross(gridAng, (Vector3)(at - gw.Position)) + Vector3.Cross(spinW, r);
        BladeDebris.Queue(session, ch.Model, new WorldTransform(at, rot), lin, spinW + gridAng, ch.Mass, what);
    }

    // the pieces are spawned before the physics step (the game's own debris spawner's place), not in the render-time animation job
    [Before(typeof(Keen.Game2.Simulation.Physics))]
    private class OnBladeDebris : JobGroup;

    [OnBladeDebris]
    [MustHave(typeof(AeroRotorAnimComponent))]
    private static void BladeDebrisJob(AeroRotorAnimComponent c) => BladeDebris.Step();

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
        Vector3 c = Vector3.Zero; int marked = 0;
        int[] groups = new int[6];   // (cw whole marked, cw tip lost, cw stub, ccw whole, ccw tip lost, ccw stub)
        foreach (var k in kids)
            if (k != null && k.Data.TryGet<RelativeTransform>(out var rt))
            {
                // (the counter-rotating set and the blade-break models by their names - at the same dummies as the clockwise blades)
                bool mark = BladeMark(k.DebugName, out bool ccw, out int variant);
                _parts.Add(new Part { Entity = k, Rest = rt, Ccw = ccw, Variant = variant });
                if (mark) { marked++; groups[(ccw ? 3 : 0) + variant]++; } else c += rt.Position;
            }
        int ccwN = groups[3];
        // (8 children and none named as the mirrored set: the names did not come through - animate all six blades as one set
        //  (both drawn, clockwise) rather than leave the rotor still with its blur disc on; said once in the log)
        bool unnamed = marked == 0 && _parts.Count == 8;
        if (unnamed) Keen.VRage.Library.Diagnostics.Log.Default?.Warning($"[AERO-ROTORANIM] {_key}: 8 children but none named *{CcwBladeMatch}* - both blade sets drawn as one (counter-rotating blades not told apart)");
        bool groupsOk = true; for (int g = 1; g < 6; g++) groupsOk &= groups[g] == 0 || groups[g] == 3;
        if ((_parts.Count - marked != 5 && !unnamed) || !groupsOk) { Keen.VRage.Library.Diagnostics.Log.Default?.Info($"[AERO-ROTORANIM] {_key}: {_parts.Count} children ({ccwN} counter-rotating blades, break models {groups[1]}/{groups[2]}/{groups[4]}/{groups[5]}), not a split rotor (5, + 3 per extra blade set) - not animated"); _parts.Clear(); return; }
        c /= unnamed ? 8f : 5f;
        Part a = null, b = null; int blades = 0; float hinge = 0f;
        foreach (var p in _parts)
        {
            if (p.Ccw || p.Variant != 0) { p.Role = Role.Blade; continue; }
            var d = p.Rest.Position - c; d.Y = 0f;
            if (d.Length() > 0.1f) { p.Role = Role.Blade; blades++; hinge += d.Length(); }
            else if (a == null) a = p; else b = p;
        }
        if (blades != (unnamed ? 6 : 3) || a == null || b == null) { _parts.Clear(); return; }
        _hasCcwSet = ccwN == 3;
        _hasVariants = groups[1] == 3 && groups[2] == 3; _hasCcwVariants = groups[4] == 3 && groups[5] == 3;
        // (each blade's index: the whole clockwise blades in order; every other blade model to the nearest of them - its hinge)
        var whole = new List<Part>();
        foreach (var p in _parts) if (p.Role == Role.Blade && !p.Ccw && p.Variant == 0) { p.Index = whole.Count % RotorStrike.Blades; whole.Add(p); }
        foreach (var p in _parts)
        {
            if (p.Role != Role.Blade || (!p.Ccw && p.Variant == 0)) continue;
            float best = float.MaxValue;
            foreach (var w in whole) { float d = (w.Rest.Position - p.Rest.Position).LengthSquared(); if (d < best) { best = d; p.Index = w.Index; } }
        }
        _seenCuts = -1;
        if (a.Rest.Position.Y >= b.Rest.Position.Y) { a.Role = Role.Hub; b.Role = Role.Blur; _hub = a; _blur = b; }
        else { b.Role = Role.Hub; a.Role = Role.Blur; _hub = b; _blur = a; }
        _hinge = hinge / blades;
        // (the tip: the block's table radius - PropulsorBlocks, the rig's tip radius; else the main rotors' ratio)
        _tip = row != null && row.Radius > _hinge ? row.Radius : _hinge * TipOverHinge;
        _span = _tip - _hinge;
        if (AeroLog.Detail) Keen.VRage.Library.Diagnostics.Log.Default?.Info($"[AERO-ROTORANIM] {_key} client: hub at {_hub.Rest.Position}, blur at {_blur.Rest.Position}, 3 blades{(_hasCcwSet ? " + 3 mirrored" : "")}{(_hasVariants ? " + break models" : "")}{(_hasCcwVariants ? " (mirrored too)" : "")}, hinge radius {_hinge:F3} m, tip {_tip:F2} m, span {_span:F2} m");
    }
}
