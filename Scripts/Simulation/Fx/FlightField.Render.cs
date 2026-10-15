#pragma warning disable CS0649, CS8632, CS1591, CS1573, CS1570, CS1574, CS0419, CS1734
using System;
using System.Reflection;
using Keen.VRage.Core;
using Keen.VRage.Core.Game.Systems;
namespace AeroMod;

/// <summary>The particle and spot-light plumbing: the game's render types late-bound once (docs/REFLECTION_AND_WHITELIST.md),
/// effects spawned, held (FixTime), parked for reuse and disposed; the one spot light aimed and moved with the grid.</summary>
internal sealed partial class FlightField
{
    // ── particles and the spot light (the game's render types are outside what mods compile against: late-bound,
    //    resolved once; see EntryFxBridge and docs/REFLECTION_AND_WHITELIST.md) ──

    private static MethodInfo _spawn, _fixTime, _setParams, _dispose;
    private static Type _tParams, _tSession;
    private object _fxSession;
    private static object _contracts;
    private static MethodInfo _createRoot, _createSpot, _rootUpdate;
    private static bool _renderResolved;

    private static string Inner(Exception e) => (e.InnerException ?? e).Message;

    /// <summary>An effect under the grid's render root at a grid-space transform (it follows the grid), its user
    /// parameters filled by init before the spawn; null and why on failure.</summary>
    private object SpawnEffect(Keen.VRage.DCS.Components.Entity grid, Guid effect, RelativeTransform at, out object prm, out string why, Action<object> init = null)
    {
        prm = null; why = null;
        try
        {
            if (grid == null || _renderParent == null) { why = "no render parent"; return null; }
            if (_spawn == null)
            {
                _tSession = EntryFxBridge.FindType("VRage.Game.Client", "Keen.VRage.Game.Client.Effects.Components.ParticleEffectsSessionComponent");
                _tParams = EntryFxBridge.FindType("VRage.Render", "Keen.VRage.Render.Data.ParticleEffectUserParameters");
                if (_tSession == null || _tParams == null) { why = "particle types not found"; return null; }
                foreach (var m in _tSession.GetMethods(AnyInst))
                    if (m.Name == "TrySpawnEffect") { var ps = m.GetParameters(); if (ps.Length > 3 && ps[0].ParameterType == typeof(RelativeTransform)) { _spawn = m; break; } }
                if (_spawn == null) { why = "TrySpawnEffect not found"; return null; }
            }
            if (_fxSession == null)
            {
                var comps = grid.GetSession()?.SessionComponents;
                if (comps != null) foreach (var c in comps.Components) if (c != null && _tSession.IsInstanceOfType(c)) { _fxSession = c; break; }
                if (_fxSession == null) { why = "no particle session"; return null; }
            }
            // REUSE (the user, 2026-10-09: "spawning new emitters is unacceptable - we should be reusing"): a parked emitter of
            // this effect is moved to the new place, given the new parameters and set emitting again; a new one only when
            // none is parked. Parked: stopped emitting (its particles in flight live out their lives), not disposed.
            RelativeTransform place = _spawnTip ? at : Swung(at, _swingDone);   // (the plasma and the sheets: where the swing has them now)
            if (_carrying && _carried.TryGetValue(effect, out var carried) && carried.Count > 0)
            {
                object hc = carried.Dequeue();
                prm = Activator.CreateInstance(_tParams);
                init?.Invoke(prm);
                SetParamsIfChanged(hc, prm);
                (_updTransform ??= hc.GetType().GetMethod("UpdateTransform", AnyInst, null, new[] { typeof(RelativeTransform) }, null)).Invoke(hc, new object[] { place });
                if (!_spawnTip) _baseOf[hc] = at;
                PoolCarried++;
                return hc;
            }
            if (_pool.TryGetValue(effect, out var parked) && parked.Count > 0)
            {
                object hp = parked.Pop();
                prm = Activator.CreateInstance(_tParams);
                init?.Invoke(prm);
                var ht = hp.GetType();
                (_setParams ??= ht.GetMethod("SetParameters", AnyInst)).Invoke(hp, new[] { prm }); _prmOf[hp] = prm;
                (_updTransform ??= ht.GetMethod("UpdateTransform", AnyInst, null, new[] { typeof(RelativeTransform) }, null)).Invoke(hp, new object[] { place });
                if (!_spawnTip) _baseOf[hp] = at;
                (_resume ??= ht.GetMethod("ResumeEmitting", AnyInst, null, Type.EmptyTypes, null)).Invoke(hp, null);
                PoolReused++;
                return hp;
            }
            if (!DefinitionManager.Instance.TryGetDefinition(effect, out Definition def) || def == null) { why = "effect not loaded: " + effect; return null; }
            prm = Activator.CreateInstance(_tParams);
            init?.Invoke(prm);   // (a boxed struct: the sets stick)
            var p = _spawn.GetParameters();
            var args = new object[p.Length];
            for (int i = 0; i < args.Length; i++) args[i] = p[i].HasDefaultValue ? p[i].DefaultValue : null;
            args[0] = place; args[1] = def; args[2] = prm; args[3] = _renderParent;
            for (int i = 4; i < args.Length; i++) if (p[i].ParameterType == typeof(string)) args[i] = "AeroFlight";
            object h = _spawn.Invoke(_fxSession, args);
            if (h == null) why = "spawn refused";
            else { _effectOf[h] = effect; _prmOf[h] = prm; PoolSpawned++; if (!_spawnTip) _baseOf[h] = at; }
            return h;
        }
        catch (Exception e) { why = "spawn: " + Inner(e); return null; }
    }

    private static void FixTime(object h, float seconds) => FixTime(h, seconds, FixTimeStep);
    /// <summary>Plasma and vapour sheets: written on a 0.01 change (fine - no 5 % steps, no residual hanging on); the tips
    /// keep FixTimeStep (their clumping A/B). The floor is always written.</summary>
    public static float FixTimeFine = 0.01f;
    internal static void NoteFixed(object h, float seconds) { lock (_fixedAt) _fixedAt[h] = seconds; }
    private static void FixTime(object h, float seconds, float step)
    {
        // (written only when it moved: rewritten ten times a second whether or not it changed, the trails came out in
        //  clumps - beads every ~15 m at 150 m/s; FixTimeEvery > 0 forces the old cadence, for the A/B)
        lock (_fixedAt)
        {
            if (FixTimeEvery == 0 && _fixedAt.TryGetValue(h, out float was) && MathF.Abs(was - seconds) < step && !(seconds <= 0.0011f && was > seconds)) return;
            _fixedAt[h] = seconds;
        }
        _fixTime ??= h.GetType().GetMethod("FixEffectTime", AnyInst);
        _fixTime.Invoke(h, new object[] { true, TimeSpan.FromSeconds(seconds) });
        FixTimeWrites++;
    }
    private static readonly Dictionary<object, float> _fixedAt = new Dictionary<object, float>(ReferenceEqualityComparer.Instance);
    /// <summary>An effect's time is written again only when it moved by this; FixTimeEvery 1: every call (the old way).</summary>
    public static float FixTimeStep = 0.05f;
    public static int FixTimeEvery, FixTimeWrites;

    private static MethodInfo _stopEmitting, _disposeAfter, _updTransform, _resume;
    /// <summary>Parked emitters by effect, and each emitter's effect: what Park keeps and SpawnEffect reuses.</summary>
    private readonly Dictionary<Guid, Stack<object>> _pool = new Dictionary<Guid, Stack<object>>();
    private readonly Dictionary<object, Guid> _effectOf = new Dictionary<object, Guid>(ReferenceEqualityComparer.Instance);
    /// <summary>Since the last reset: emitters really spawned, and reused from the pool.</summary>
    public static int PoolSpawned, PoolReused;

    /// <summary>The most emitters of one effect kept parked per field (the rest disposed).</summary>
    public static int MaxParked = 48;
    /// <summary>An emitter no longer needed here: PARKED for reuse - its emission stopped (the particles in flight finish
    /// their life) and its effect time at the floor - not disposed. Only an emitter this field did not spawn is disposed.</summary>
    private void Park(object h)
    {
        if (h == null) return;
        Unswing(h);
        if (!_effectOf.TryGetValue(h, out Guid eff)) { DisposeEffect(h); return; }
        try
        {
            var t = h.GetType();
            _stopEmitting ??= t.GetMethod("StopEmitting", AnyInst, null, new[] { typeof(bool) }, null);
            _stopEmitting?.Invoke(h, new object[] { false });
            FixTime(h, 0.00001f, 0f);
            if (!_pool.TryGetValue(eff, out var st)) _pool[eff] = st = new Stack<object>();
            // (capped: the pool only grew - 433 parked vapour emitters on one grid (the Orbital Mod's particle census,
            //  2026-10-12); past MaxParked per effect an emitter goes for good, its particles finishing their life)
            if (st.Count >= MaxParked) { _effectOf.Remove(h); _prmOf.Remove(h); _baseOf.Remove(h); DisposeEffect(h); return; }
            st.Push(h);
        }
        catch (Exception) { _effectOf.Remove(h); DisposeEffect(h); }
    }

    /// <summary>The field is over (out of range, the grid gone, the effects off): everything really disposed, the pool too.</summary>
    public void Destroy()
    {
        DropAll();
        foreach (var st in _pool.Values) foreach (var h in st) DisposeEffect(h);
        foreach (var q in _carried.Values) foreach (var h in q) DisposeEffect(h);
        _carried.Clear(); _carrying = false;
        _pool.Clear(); _effectOf.Clear(); _prmOf.Clear(); _baseOf.Clear();
    }

    /// <summary>An effect goes GRACEFULLY: it stops emitting and disposes itself once its particles have died (a
    /// dropped group - tips re-placed for a new length, vapour leaving the band, plasma cooling - no longer pops: its
    /// sparks and puffs in flight finish their life). Falls back to an immediate dispose.</summary>
    private static void DisposeEffect(object h)
    {
        lock (_fixedAt) _fixedAt.Remove(h);   // (its last written time forgotten with it)
        if (h == null) return;
        try
        {
            var t = h.GetType();
            _stopEmitting ??= t.GetMethod("StopEmitting", AnyInst, null, new[] { typeof(bool) }, null);
            _disposeAfter ??= t.GetMethod("DisposeAfterParticlesDie", AnyInst, null, Type.EmptyTypes, null);
            if (_stopEmitting != null && _disposeAfter != null)
            {
                _stopEmitting.Invoke(h, new object[] { false });
                _disposeAfter.Invoke(h, null);
                return;
            }
            _dispose ??= t.GetMethod("Dispose", AnyInst, null, new[] { typeof(bool) }, null);
            _dispose?.Invoke(h, new object[] { true });
        }
        catch (Exception) { }
    }

    private static bool ResolveRender()
    {
        if (_renderResolved) return _contracts != null && _createRoot != null && _createSpot != null;
        _renderResolved = true;
        try
        {
            var engine = EntryFxBridge.FindType("VRage.Render", "Keen.VRage.Render.EngineComponents.RenderEngineComponent");
            object instance = engine?.GetField("Instance", BindingFlags.Public | BindingFlags.Static)?.GetValue(null);
            _contracts = instance?.GetType().GetProperty("RenderContracts")?.GetValue(instance);
            if (_contracts == null) return false;
            foreach (var m in _contracts.GetType().GetMethods(BindingFlags.Public | BindingFlags.Instance))
            {
                if (m.Name == "CreateRootEntity" && m.GetParameters().Length == 3) _createRoot = m;
                if (m.Name == "CreateSpotLightEntity") _createSpot = m;
            }
        }
        catch (Exception) { _contracts = null; }
        return _contracts != null && _createRoot != null && _createSpot != null;
    }

    private static void UpdateRootTransform(object root, WorldTransform at)
    {
        if (root == null) return;
        _rootUpdate ??= root.GetType().GetMethod("UpdateTransform");
        _rootUpdate?.Invoke(root, new object[] { at });
    }

    private static void DisposeRender(object o)
    {
        if (o == null) return;
        try { o.GetType().GetMethod("Dispose", Type.EmptyTypes)?.Invoke(o, null); } catch (Exception) { }
    }

    /// <summary>The plasma's light: ONE spot light SpotDist x R upwind of the front, aimed down the flow, its cone just
    /// covering SpotCover x R - the windward surface lit, nothing behind; its own render root, moved with the grid.</summary>
    private void SpawnSpot(Vector3 f, float R, int N)
    {
        try
        {
            if (!ResolveRender()) { _spotWhy = "render contracts not available"; return; }
            if (!SpotAim(f, out var at, out var q2)) { _spotWhy = "no hull"; return; }
            float cone = 2f * MathF.Atan(SpotCover / Math.Max(0.1f, SpotDist));
            // (the light sits at its root's origin; the ROOT carries the aim - moved every frame, so the light is re-aimed in
            //  place: a light's own transform is fixed when it is made)
            _spotPos = at; _spotQ = q2; _spotEase = 0; _spotMovedAt = at; _spotMovedQ = q2;
            _spotRoot = _createRoot.Invoke(_contracts, new object[] { "AeroPlasmaSpotRoot", _client.Data.GetWorldTransform(), true });
            if (_spotRoot == null) { _spotWhy = "no root"; return; }
            var ps = _createSpot.GetParameters();
            var args = new object[ps.Length];
            for (int i = 0; i < ps.Length; i++) args[i] = ps[i].HasDefaultValue ? ps[i].DefaultValue : null;
            var pc = SparkBirth;   // (the sparks' birth colour, sRGB, in linear)
            float lr = MathF.Pow(pc[0] * LightTint[0], 2.2f), lg = MathF.Pow(pc[1] * LightTint[1], 2.2f), lb = MathF.Pow(pc[2] * LightTint[2], 2.2f);
            float s = Spot * _spotMul;
            for (int i = 0; i < ps.Length; i++)
                switch (ps[i].Name)
                {
                    case "debugName": args[i] = "AeroPlasmaSpot"; break;
                    case "lightIntensityRGB": args[i] = new ColorLinear(lr * s, lg * s, lb * s, 1f); break;
                    case "outerConeAngle": args[i] = cone; break;
                    case "localLightTransform": args[i] = new RelativeTransform(at, q2); break;
                    case "rootEntity": args[i] = _spotRoot; break;
                    case "tryForceShadowMapAlwaysAllocated": args[i] = SpotShadow; break;
                    case "falloffMultiplier": args[i] = SpotFalloff; break;
                }
            _spot = _createSpot.Invoke(_contracts, args);
            RecSpotMade++;
            _spotWhy = null;
            _spotSetS = _spotMul;
        }
        catch (Exception e) { _spotWhy = "spot: " + Inner(e); StopSpot(); }
    }

    // ── the spot's aim: SpotDist x R upwind of the hull's front along the flow f, pointing down it (grid-local) ──
    private Vector3 _spotPos; private Quaternion _spotQ = Quaternion.Identity; private long _spotEase;
    /// <summary>How fast the light follows a new aim (s).</summary>
    public static float SpotAimTau = 0f;
    /// <summary>How far upwind of the front the light sits: SpotDist x R at full strength, further as the entry weakens -
    /// the light reaching the hull goes as 1 / distance^2, so it is the strength (made at full brightness; never dimmed
    /// by SetParameters).</summary>
    private float SpotDistance() => SpotDist * _shapeR / MathF.Sqrt(Math.Clamp(_entryS * _spotFlick, 0.01f, 1.5f));
    /// <summary>The flicker: the light's strength x (1 + SpotFlicker x a fire-like wobble - three unrelated frequencies,
    /// SpotFlickerHz the fastest), through its distance like the strength (the user, 2026-10-09: "slightly flickery").</summary>
    public static float SpotFlicker = 0.2f, SpotFlickerHz = 17f;
    private float _spotFlick = 1f;
    private static float Flick(double t)
    {
        double w = 2 * Math.PI * SpotFlickerHz;
        double n = 0.5 * Math.Sin(w * t) + 0.3 * Math.Sin(w * 0.537 * t + 1.3) + 0.2 * Math.Sin(w * 0.291 * t + 4.1);
        return 1f + SpotFlicker * (float)n;
    }
    private bool SpotAim(Vector3 f, out Vector3 at, out Quaternion q)
    {
        at = default; q = Quaternion.Identity;
        if (BakedOn && _bakedSpotOk)
        {
            at = _bakedSpotMid + f * (_bakedSpotFront + SpotDistance());
            q = Quaternion.CreateFromTwoVectors(Vector3.UnitZ, SpotFlip ? -f : f);
            return true;
        }
        if (_hullHas == null || _hullSurf == null || f == default) return false;
        int N = _hullHas.GetLength(0) - 1;
        float zFront = float.MinValue; Vector3 mid = Vector3.Zero; int cnt = 0;
        for (int i = 0; i <= N; i++) for (int j = 0; j <= N; j++)
            if (_hullHas[i, j]) { var p = _hullSurf[i, j]; zFront = Math.Max(zFront, Vector3.Dot(p, f)); mid += p - f * Vector3.Dot(p, f); cnt++; }
        if (cnt == 0) return false;
        mid /= cnt;
        at = mid + f * (zFront + SpotDistance());
        q = Quaternion.CreateFromTwoVectors(Vector3.UnitZ, SpotFlip ? -f : f);   // (the engine's forward is -Z)
        return true;
    }
    private static MethodInfo _spotMove;
    private Vector3 _spotMovedAt; private Quaternion _spotMovedQ;
    /// <summary>Every frame: the root with the grid; the light itself (SpotLightEntity.UpdateTransform) on the live aim -
    /// SpotAimTau 0: no easing (the user, 2026-10-09: "no lag").</summary>
    private void SpotFollow(WorldTransform wt)
    {
        if (_spotRoot == null) return;
        UpdateRootTransform(_spotRoot, wt);
        _spotFlick = SpotFlicker > 0f ? Flick(System.Diagnostics.Stopwatch.GetTimestamp() / (double)System.Diagnostics.Stopwatch.Frequency) : 1f;
        if (_spot == null || _axisSm == default || !SpotAim(_axisSm, out var goal, out var goalQ)) return;
        long now = System.Diagnostics.Stopwatch.GetTimestamp();
        float dt = _spotEase == 0 ? 0f : (float)Math.Min(0.25, (now - _spotEase) / (double)System.Diagnostics.Stopwatch.Frequency);
        _spotEase = now;
        float k = SpotAimTau <= 0f || dt <= 0f ? 1f : 1f - MathF.Exp(-dt / SpotAimTau);
        _spotPos = Vector3.Lerp(_spotPos, goal, k);
        _spotQ = Quaternion.Normalize(Quaternion.Slerp(_spotQ, goalQ, k));
        if ((_spotPos - _spotMovedAt).LengthSquared() < 0.0004f && MathF.Abs(Quaternion.Dot(_spotQ, _spotMovedQ)) > 0.9999996f) return;
        try
        {
            (_spotMove ??= _spot.GetType().GetMethod("UpdateTransform", AnyInst, null, new[] { typeof(RelativeTransform) }, null))
                ?.Invoke(_spot, new object[] { new RelativeTransform(_spotPos, _spotQ) });
            _spotMovedAt = _spotPos; _spotMovedQ = _spotQ;
        }
        catch (Exception e) { _spotWhy = "spot move: " + Inner(e); }
    }

    // (the strength the spot was made at: the status shows it)
    private float _spotSetS = -1f;
    private void StopSpot()
    {
        _spotSetS = -1f;
        try { (_spot as IDisposable)?.Dispose(); } catch (Exception) { }
        DisposeRender(_spotRoot);
        _spot = null; _spotRoot = null;
    }
}
