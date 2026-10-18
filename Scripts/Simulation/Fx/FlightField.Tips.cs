#pragma warning disable CS0649, CS8632, CS1591, CS1573, CS1570, CS1574, CS0419, CS1734
using System;
using System.Reflection;
using Keen.VRage.Core;
using Keen.VRage.Core.Game.Systems;
namespace AeroMod;

/// <summary>The wing-tip trails: the tips found on the ship's hull (span ends, re-found across the live wind), each emitter's
/// spawn line and lead kept live every frame, and the trail tracker / overlay that checks them.</summary>
internal sealed partial class FlightField
{
    // ── the tips' spawn line: a particle is born only where its emitter is, once a frame - at 150 m/s and 60 fps the tip
    //    moves 2.5 m between frames, so each frame's particles came out as one clump with a gap behind it (the user,
    //    2026-10-08). The tip emitter is a thin line along the trail (Vapour_Tip EmitterSize 0.01 x 0.01 x 1 m, local +Z =
    //    backwards), its length (EmitterSizeMultiplier) one frame's travel: each frame's particles fill the stretch the tip
    //    covered. Kept live at 10 Hz as the speed changes. ──
    private readonly Dictionary<object, (object prm, float len)> _tipPrm = new Dictionary<object, (object, float)>(ReferenceEqualityComparer.Instance);
    private float _frameDt = 1f / 60f;
    /// <summary>Each tip's last airspeed send (Stopwatch ticks): one shared time let one tip's send hold every other tip's
    /// for 3 s - after a speed change one trail updated and the other waited, 2026-10-15.</summary>
    private readonly Dictionary<object, long> _tipLineAt = new Dictionary<object, long>(ReferenceEqualityComparer.Instance);
    /// <summary>This field's own tip report (TipWhy is shared - the last field to place wrote it); NearTipWhy: the one
    /// nearest the camera (your ship).</summary>
    public string TipWhyHere = "not placed";
    public static string NearTipWhy = "";
    public static float TipLineMin = 0.05f, TipLineMax = 40f;
    /// <summary>The tip particles' backward speed multiplier (x the effect's 4 m/s, +-25 %): each frame's clump smears along
    /// the trail and fills the gap behind it. 0.01: none (laid still in the air).</summary>
    public static float TipSpread = 0.01f;   // (no push: with 90 % inheritance alone the trail is continuous from the tip - the 2026-10-08 sweep; a push opened a gap at the tip and broke it up)
    // WIND (the user, 2026-10-09: "they're not following the windspeed"): the tips inherit ALL of the ship's motion
    // (Vapour_Tip EmitterMotionInheritance 1) and are emitted backwards at the AIRSPEED - so each particle moves with the air
    // itself: still in the world in real flight, streaming back at the airflow past a held (puppeted) ship. At 90 % and no
    // push they drifted back at a tenth of the airspeed - and not at all past a held ship.
    private float TipDrift => _speed;
    /// <summary>The tips' VelocityMultiplier for the airspeed: 4 m/s x TipScale (the effect's speed, scaled by its
    /// EmitterScaleMultiplier) x it = the airspeed.</summary>
    private float TipWindVm => MathF.Max(0.01f, _speed / (4f * MathF.Max(0.1f, TipScale)));
    private float TipLineLen() => Math.Clamp(TipDrift * Math.Max(1f / 240f, _frameDt), TipLineMin, TipLineMax);
    /// <summary>
    /// The tip emitters' LEAD (s x airspeed upwind of the tip): a trail moving with the air showed nothing for its first
    /// ~0.07 s - a gap of ~30 m at 440 m/s, ~10 m at 150 (it scales with the airspeed: a time, not a place) - so each emitter
    /// sits that far upwind of its tip, kept live, and the visible trail begins at the tip.
    /// </summary>
    public static float TipLead = 0f;   // (0: the emitter AT the tip. The 'invisible first 0.1 s' was the spawn line - 6x too long through the
                                        //  effect scale, its particles born at its far end - not a delay; with no line the trail starts at the tip)
    private readonly Dictionary<object, (Vector3 at, Quaternion q, float lead)> _tipAt = new Dictionary<object, (Vector3, Quaternion, float)>(ReferenceEqualityComparer.Instance);
    // ── the tips re-found across the LIVE wind (the user, 2026-10-09): twice a second the hull's extremes along the span axis
    //    taken across the actual airflow, each at its downwind (trailing) edge; an emitter moved (UpdateTransform) when its
    //    tip moved more than 0.3 m - never re-spawned. Placement still decides WHETHER there are tips (thin and sharp). ──
    private Vector3 _tipSpan, _tipC; private List<Vector3> _hullTips; private long _windTipsAt;
    private readonly List<Vector3> _tipPtsA = new List<Vector3>(), _tipPtsB = new List<Vector3>();
    /// <summary>Guards the hull-point buffers (_tipPtsA / _tipPtsB, _hullTips): ComputeTips refills one while WindTips reads the
    /// other, and two ComputeTips on one field picked the same buffer - "Collection was modified" in the tip search after a grid
    /// split (2026-10-18, a client crash). Taken a few times a second at most: placement, the twice-a-second truth check, 10 Hz.</summary>
    private readonly object _tipLock = new object();
    private readonly List<object> _tipOrder = new List<object>();
    private readonly Vector3[] _wtFound = new Vector3[2]; private readonly float[] _wtBest = new float[2];
    private void WindTips(Vector3 flow)
    {
        long now = System.Diagnostics.Stopwatch.GetTimestamp();
        if (_hullTips == null || _tipOrder.Count == 0 || now - _windTipsAt < System.Diagnostics.Stopwatch.Frequency / 10) return;
        _windTipsAt = now;
        lock (_tipLock) WindTipsLocked(flow);
    }
    private void WindTipsLocked(Vector3 flow)
    {
        Vector3 u = _tipSpan - flow * Vector3.Dot(_tipSpan, flow);
        if (u.LengthSquared() < 1e-4f) return;
        u = Vector3.Normalize(u);
        float hi = float.MinValue, lo = float.MaxValue;
        foreach (var p in _hullTips) { float d = Vector3.Dot(p - _tipC, u); hi = MathF.Max(hi, d); lo = MathF.Min(lo, d); }
        var found = _wtFound; var best = _wtBest; found[0] = found[1] = default; best[0] = best[1] = float.MinValue;   // (kept: ten times a second)
        foreach (var p in _hullTips)
        {
            float d = Vector3.Dot(p - _tipC, u), down = -Vector3.Dot(p, flow);
            if (MathF.Abs(d - hi) <= 0.5f && down > best[0]) { best[0] = down; found[0] = p; }
            if (MathF.Abs(d - lo) <= 0.5f && down > best[1]) { best[1] = down; found[1] = p; }
        }
        for (int k = 0; k < _tipOrder.Count && k < 2; k++)
        {
            var h = _tipOrder[k];
            if (best[k] == float.MinValue || !_tipAt.TryGetValue(h, out var cur)) continue;
            if ((cur.at - found[k]).LengthSquared() < 0.0025f) continue;
            _tipAt[h] = (found[k], cur.q, -1f);   // (moved by TipLead_ below, this tick)
            if (k < _tipLocal.Count) _tipLocal[k] = found[k];
        }
    }

    /// <summary>How fast a tip emitter follows its target place and the wind direction (s).</summary>
    public static float TipEaseTau = 0f;   // (0: on the live target every frame - the user, 2026-10-09: "no lag")
    private readonly Dictionary<object, (Vector3 at, Quaternion q)> _tipShown = new();
    private readonly List<object> _tipKeys = new List<object>(), _tipPrmKeys = new List<object>();
    /// <summary>EVERY FRAME (it was 10 Hz with a 1 degree / 0.5 m step, and the tip points 2 Hz with 0.3 m: turning fast the
    /// trails' origins and directions stepped - the user, 2026-10-09: "make sure everything smoothly flows"): each tip
    /// emitter eased onto its tip point (WindTips, 10 Hz) and the live wind direction, moved when it moved 2 cm or 0.2 deg.</summary>
    private void TipLead_(float fdt)
    {
        float lead = TipLead * _speed;
        if (_axisSm != default) WindTips(_axisSm);
        // DOWNWIND (the user, 2026-10-09: "not reacting to the actual wind direction"): each tip emitter sprays along the LIVE
        // airflow (the eased flow axis _axisSm, the grid's frame)
        Vector3 flow = _axisSm != default ? _axisSm : _tipF;
        if (flow == default || _tipAt.Count == 0) return;
        var qw = Quaternion.CreateFromTwoVectors(Vector3.UnitZ, -flow);
        Vector3 back = -flow;
        float k = fdt <= 0f ? 1f : 1f - MathF.Exp(-fdt / Math.Max(0.01f, TipEaseTau));
        var keys = _tipKeys; keys.Clear(); keys.AddRange(_tipAt.Keys);   // (a copy - an emitter that fails is removed - kept: it was a new list every frame)
        foreach (var h in keys)
        {
            var (at, q, was) = _tipAt[h];
            Vector3 goal = at - back * lead;
            Vector3 pos; Quaternion rot;
            if (_tipShown.TryGetValue(h, out var shown) && was >= 0f) { pos = Vector3.Lerp(shown.at, goal, k); rot = Quaternion.Normalize(Quaternion.Slerp(shown.q, qw, k)); }
            else { pos = goal; rot = qw; }
            if (was >= 0f && (pos - shown.at).LengthSquared() < 0.0004f && MathF.Abs(Quaternion.Dot(rot, shown.q)) > 0.9999985f) continue;
            try
            {
                (_updTransform ??= h.GetType().GetMethod("UpdateTransform", AnyInst, null, new[] { typeof(RelativeTransform) }, null))
                    .Invoke(h, new object[] { new RelativeTransform(pos, rot) });
                _tipShown[h] = (pos, rot);
                _tipAt[h] = (at, rot, lead);
            }
            catch (Exception e) { _puffWhy = "tip lead: " + Inner(e); _tipAt.Remove(h); _tipShown.Remove(h); }
        }
    }

    private void TipLine()
    {
        if (_tipPrm.Count == 0) return;
        float want = TipLineLen();
        var keys = _tipPrmKeys; keys.Clear(); keys.AddRange(_tipPrm.Keys);
        foreach (var h in keys)
        {
            var (prm, len) = _tipPrm[h];
            // (EVERY SetParameters rebuilds the GPU emitter and deletes its live particles - the trail vanished for ~0.3 s at each:
            //  the airspeed is sent only on a 15 % change and at most every 3 s; there is no spawn line any more to keep up)
            want = TipWindVm;
            _tipLineAt.TryGetValue(h, out long lastAt);
            if (len > 0f && (MathF.Abs(want - len) < 0.15f * len || System.Diagnostics.Stopwatch.GetTimestamp() - lastAt < 3 * System.Diagnostics.Stopwatch.Frequency)) continue;
            _tipLineAt[h] = System.Diagnostics.Stopwatch.GetTimestamp();
            try
            {
                prm.GetType().GetField("VelocityMultiplier").SetValue(prm, want);   // (the airspeed)
                (_setParams ??= h.GetType().GetMethod("SetParameters", AnyInst)).Invoke(h, new[] { prm }); _prmOf[h] = prm;
                _tipPrm[h] = (prm, want);
            }
            catch (Exception e) { _puffWhy = "tip line: " + Inner(e); _tipPrm.Remove(h); _tipLineAt.Remove(h); }
        }
    }

    /// <summary>
    /// The ship's two wing tips, once per flight (the trail audit, 2026-10-08): from the hull of EVERY grid of the ship (pods
    /// and nacelles included, in this grid's frame), measured from the hull's centre - not the grid origin - along the SPAN
    /// axis only (the across-flow grid axis the hull is widest on: no fin or belly trails), each at the rearmost hull point
    /// of its tip. Not tied to the angle of attack: a pull-up no longer drops and re-places them.
    /// </summary>
    private void ShipTips(Vector3 f)
    {
        // (the forward axis is chosen once per flight - kept while the tips are up - so the span never swaps to the
        //  nose-tail axis when the ship pitches or falls belly-first)
        if (_tipF == default) _tipF = f;
        var tips = ComputeTips(_tipF, out var a, out float wid, out int count);
        _tipLocal.Clear(); _tipHist.Clear();
        if (tips == null) { TipWhy = TipWhyHere = "no hull"; return; }
        var q = Quaternion.CreateFromTwoVectors(Vector3.UnitZ, -a);
        foreach (var t in tips)
        {
            VapourEffect(VapourTip, t, q, TipScale, 1f, false, TipWindVm, true);   // (a point at the tip: no spawn line - see TipLine)
            _tipLocal.Add(t); _tipHist.Add(new List<(double t, Vector3D p)>());
        }
        TrkPlaced += tips.Count;
        TipWhy = TipWhyHere = $"{tips.Count} tips at the span's ends ({wid:F1} m wide, {count} hull points of {Math.Max(1, _server?.FxGroup.Length ?? 1)} grid(s)){TipShape}";
    }

    /// <summary>The ship's two span-end tips (this grid's frame), from the hull of every grid of the ship: measured from the
    /// hull's centre along the span axis only, each at the rearmost hull point of its tip. Null: no hull.</summary>
    private List<Vector3> ComputeTips(Vector3 f, out Vector3 a, out float wid, out int count)
    {
        lock (_tipLock) return ComputeTipsLocked(f, out a, out wid, out count);
    }
    private List<Vector3> ComputeTipsLocked(Vector3 f, out Vector3 a, out float wid, out int count)
    {
        a = default; wid = 0f; count = 0;
        // (two lists in turn: the one WindTips is not reading (_hullTips) filled - it was a fresh list of every hull point
        //  of the ship twice a second; kept only on success, as before)
        var pts = ReferenceEquals(_hullTips, _tipPtsA) ? _tipPtsB : _tipPtsA; pts.Clear();
        var e = _server?.Entity;
        if (_server != null && e != null && GridPhysics.Alive(e.Data))
        {
            var wtO = e.Data.GetWorldTransform();
            foreach (var m in _server.FxGroup.Length > 0 ? _server.FxGroup : new[] { _server })
            {
                var ch = m?.FxChunks; var me = m?.Entity;
                // (a grid of the group gone since the last group search - split off, closed: its transform threw)
                if (ch == null || me == null || !GridPhysics.Alive(me.Data)) continue;
                var wtM = me.Data.GetWorldTransform();
                bool self = ReferenceEquals(m, _server);
                lock (ch.OccLock)   // (that grid's lists are its simulation thread's, edited in place: ChunkedTable.OccLock)
                foreach (var list in ch.Occluders)
                    foreach (var p16 in list)
                    {
                        Vector3 v = p16.V;
                        if (!self)
                        {
                            Vector3D w = WorldTransform.Transform(new Vector3D(v.X, v.Y, v.Z), wtM);
                            Vector3D l = WorldTransform.TransformInv(w, wtO);
                            v = new Vector3((float)l.X, (float)l.Y, (float)l.Z);
                        }
                        pts.Add(v);
                    }
            }
        }
        count = pts.Count;
        if (pts.Count < 2) return null;
        Vector3 c = Vector3.Zero; foreach (var p in pts) c += p; c /= pts.Count;
        // forward: the grid axis most along the flow (into the air); the span: of the other two, the one the hull is widest on
        var axes = new[] { Vector3.UnitX, Vector3.UnitY, Vector3.UnitZ };
        int fi = 0; for (int k = 1; k < 3; k++) if (MathF.Abs(Vector3.Dot(axes[k], f)) > MathF.Abs(Vector3.Dot(axes[fi], f))) fi = k;
        a = axes[fi] * MathF.Sign(Vector3.Dot(axes[fi], f));
        Vector3 u = default; wid = -1f;
        for (int k = 0; k < 3; k++)
        {
            if (k == fi) continue;
            float lo = float.MaxValue, hi = float.MinValue;
            foreach (var p in pts) { float d = Vector3.Dot(p - c, axes[k]); lo = MathF.Min(lo, d); hi = MathF.Max(hi, d); }
            if (hi - lo > wid) { wid = hi - lo; u = axes[k]; }
        }
        float hiS = float.MinValue, loS = float.MaxValue;
        foreach (var p in pts) { float d = Vector3.Dot(p - c, u); hiS = MathF.Max(hiS, d); loS = MathF.Min(loS, d); }
        _tipSpan = u; _hullTips = pts; _tipC = c;   // (kept: the tips re-found across the live wind - WindTips)
        // ONLY A REAL WING TIP (the user, 2026-10-08: "only if the outline is truly sharp and thin"; a brick has none): the
        // outer TipZone of the span on that side must be thin - under TipThinFrac of the ship's thickest - and sharp - its
        // chord under TipSharpFrac of the ship's longest
        Vector3 t3 = Vector3.Normalize(Vector3.Cross(a, u));
        float Ext(Func<Vector3, bool> sel, Vector3 ax)
        {
            float lo = float.MaxValue, hi = float.MinValue;
            foreach (var p in pts) if (sel(p)) { float d = Vector3.Dot(p, ax); lo = MathF.Min(lo, d); hi = MathF.Max(hi, d); }
            return hi > lo ? hi - lo : 0f;
        }
        float thickAll = Ext(_ => true, t3), chordAll = Ext(_ => true, a);
        var tips = new List<Vector3>();
        var why = new System.Text.StringBuilder();
        bool pairFail = false;
        foreach (float side in new[] { hiS, loS })
        {
            float zone = TipZone * wid;
            Func<Vector3, bool> outer = p => MathF.Abs(Vector3.Dot(p - c, u) - side) <= zone;
            float thick = Ext(outer, t3), chord = Ext(outer, a);
            bool thin = thick <= TipThinFrac * thickAll, sharp = chord <= TipSharpFrac * chordAll;
            why.Append($" [{(side > 0 ? "+" : "-")} thick {thick:F1}/{thickAll:F1} chord {chord:F1}/{chordAll:F1}{(thin && sharp ? "" : " - not a tip")}]");
            if (!thin || !sharp) { tips.Clear(); pairFail = true; continue; }   // (a pair or none: one trail on a symmetric ship looked broken)
            if (pairFail) continue;
            Vector3 best = default; float bestBack = float.MaxValue; bool any = false;
            foreach (var p in pts)
            {
                if (MathF.Abs(Vector3.Dot(p - c, u) - side) > 0.5f) continue;
                float back = Vector3.Dot(p, a);
                if (back < bestBack) { bestBack = back; best = p; any = true; }
            }
            if (any) tips.Add(best);
        }
        TipShape = why.ToString();
        if (pairFail) tips.Clear();
        return tips;
    }
    /// <summary>A tip: the outer TipZone of the span, thinner than TipThinFrac of the ship's thickness and with a chord under
    /// TipSharpFrac of its longest.</summary>
    public static float TipZone = 0.1f, TipThinFrac = 0.6f, TipSharpFrac = 0.6f;
    public static string TipShape = "";

    // ── the trail tracker: where each tip emitter is every frame, and the trail its particles must leave ──
    // (the particles live on the GPU - only counts come back - so we track what we own: the emitters. With no motion
    //  inheritance and no emission speed, a tip particle stays where it was born: the emitter's path over the last TrailLife
    //  seconds IS the trail.)
    private readonly List<Vector3> _tipLocal = new List<Vector3>();
    private readonly List<List<(double t, Vector3D p)>> _tipHist = new List<List<(double, Vector3D)>>();
    private double _tipCheckAt; private int _tipGenSeen = -1;
    private Vector3 _tipF;   // (the flow the tips were chosen with: kept while the air comes along the same grid axis)
    private long _axisFlipAt;
    /// <summary>The grid axis (0 X, 1 Y, 2 Z) most along a direction.</summary>
    static int Major(Vector3 v) { float x = MathF.Abs(v.X), y = MathF.Abs(v.Y), z = MathF.Abs(v.Z); return x >= y && x >= z ? 0 : y >= z ? 1 : 2; }
    static float Comp(Vector3 v, int i) => i == 0 ? v.X : i == 1 ? v.Y : v.Z;
    private long _axisPastAt;
    /// <summary>Draw each tip emitter's predicted trail (yellow), the emitter (red) and the hull's true tip now (green).</summary>
    public static bool TrailDebug;
    public static float TrailLife = 2f;
    /// <summary>Since the last reset: frames tracked, emitters placed, re-placements while flying (a tip moved to a new
    /// point), the largest gap between an emitter and the hull's true tip (m, checked twice a second), the largest one-frame
    /// emitter jump beyond the ship's own motion (m), the longest predicted trail (m), tips seen on a frame.</summary>
    public static int TrkFrames, TrkPlaced, TrkReplaced, TrkMaxTips;
    public static float TrkMaxGap, TrkMaxJump, TrkMaxLen;
    public static void TrkReset() { TrkFrames = TrkPlaced = TrkReplaced = TrkMaxTips = 0; TrkMaxGap = TrkMaxJump = TrkMaxLen = 0f; }
    public static string TrkReport() =>
        $"trail frames {TrkFrames} placed {TrkPlaced} replaced {TrkReplaced} tips {TrkMaxTips} max-gap {TrkMaxGap:F2} max-jump {TrkMaxJump:F2} max-len {TrkMaxLen:F0}";

    /// <summary>Client, every frame after Update: the tip emitters' world positions recorded, judged, and drawn on request.</summary>
    public void TrackTips(in WorldTransform wt)
    {
        if (!_tipsUp || _tipLocal.Count == 0) { _tipHist.Clear(); return; }
        double now = System.Diagnostics.Stopwatch.GetTimestamp() / (double)System.Diagnostics.Stopwatch.Frequency;
        if (_tipGenSeen >= 0 && _tipGenSeen != _genTips) TrkReplaced++;
        _tipGenSeen = _genTips;
        TrkFrames++; TrkMaxTips = Math.Max(TrkMaxTips, _tipLocal.Count);
        // the hull's true tips now, twice a second: how far the emitters sit from them
        List<Vector3> truth = null;
        if (now - _tipCheckAt > 0.5 && _tipF != default)
        {
            _tipCheckAt = now;
            truth = ComputeTips(_tipF, out _, out _, out _);
            if (truth != null && truth.Count > 0)
                foreach (var t in _tipLocal)
                {
                    float g = float.MaxValue; foreach (var h in truth) g = MathF.Min(g, (t - h).Length());
                    TrkMaxGap = MathF.Max(TrkMaxGap, g);
                }
        }
        if (truth != null) { _truth = truth; }
        for (int k = 0; k < _tipLocal.Count && k < _tipHist.Count; k++)
        {
            Vector3D w = WorldTransform.Transform(new Vector3D(_tipLocal[k].X, _tipLocal[k].Y, _tipLocal[k].Z), wt);
            var h = _tipHist[k];
            h.Add((now, w));
            while (h.Count > 0 && now - h[0].t > TrailLife) h.RemoveAt(0);
            float len = 0f;
            for (int i = 1; i < h.Count; i++) len += (float)(h[i].p - h[i - 1].p).Length();
            TrkMaxLen = MathF.Max(TrkMaxLen, len);
        }
    }
    private List<Vector3> _truth;

    /// <summary>The overlay: each emitter's predicted trail (yellow), the emitter (red), the hull's true tip (green).</summary>
    public void DrawTrails(in WorldTransform wt, Keen.VRage.Core.Game.RuntimeSystems.DebugDraw.IDebugDrawProvider ddp)
    {
        var dd = ddp?.GlobalBuilder; if (dd == null || !_tipsUp) return;
        for (int k = 0; k < _tipLocal.Count && k < _tipHist.Count; k++)
        {
            var h = _tipHist[k];
            for (int i = 1; i < h.Count; i++) dd.AddLine(h[i - 1].p, h[i].p, new ColorSRGB(1f, 0.85f, 0.2f));
            Vector3D w = WorldTransform.Transform(new Vector3D(_tipLocal[k].X, _tipLocal[k].Y, _tipLocal[k].Z), wt);
            dd.AddLine(w - new Vector3D(0.5, 0, 0), w + new Vector3D(0.5, 0, 0), new ColorSRGB(1f, 0.2f, 0.2f));
            dd.AddLine(w - new Vector3D(0, 0.5, 0), w + new Vector3D(0, 0.5, 0), new ColorSRGB(1f, 0.2f, 0.2f));
        }
        if (_truth != null)
            foreach (var t in _truth)
            {
                Vector3D w = WorldTransform.Transform(new Vector3D(t.X, t.Y, t.Z), wt);
                dd.AddLine(w - new Vector3D(0, 0, 0.7), w + new Vector3D(0, 0, 0.7), new ColorSRGB(0.2f, 1f, 0.3f));
            }
    }
}
