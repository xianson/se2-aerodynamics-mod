#pragma warning disable
using System;
using System.Reflection;
using Keen.VRage.Core;
using Keen.VRage.Core.Game.Systems;
namespace AeroMod;

/// <summary>The plasma's spawn pass: every emitter queued (SpawnPerTick a frame), each one's held time from its local
/// weight, its user parameters filled before the spawn; StopPuffs parks the lot.</summary>
internal sealed partial class FlightField
{
    private void StopPuffs()
    {
        if (_noStop) return;   // (flight mode: the plasma and the vapour spawned as one field)
        _plasma.Clear(); _spotMul = 1f; _spotUsed = -1f;
        _vapour.Clear();
        _rng = new Random(1);
        _spawnQueue.Clear();
        foreach (var h in _puffs) Park(h);   // (parked for reuse, not disposed)
        _puffs.Clear(); _puffWhy = null;
        foreach (var h in _gridLights) DisposeEffect(h);
        _gridLights.Clear();
        StopSpot(); _spotWhy = null;
    }

    private void SpawnPuffs(Vector3[,] P3, float[,] past, Vector3[] ring, Vector3 f, float R)
    {
        StopPuffs();
        int N = P3.GetLength(0) - 1, step = Math.Max(1, PuffEvery);
        var turn = Quaternion.CreateFromTwoVectors(Vector3.UnitZ, -f);
        if (PuffTest > 0)
        {
            Puff(ShockSparks, _nose, PuffTest == 3 ? Quaternion.Identity : turn, PuffTest == 1 ? 9f : 1f);
            return;
        }
        float cell = (P3[N, 0] - P3[0, 0]).Length() / N;
        if (Weighted)
        {
            ChunkDrag(f);
            _weightMax = 1e-6f;
            for (int i = 0; i <= N; i++) for (int j = 0; j <= N; j++) if (_hullHas[i, j]) _weightMax = Math.Max(_weightMax, RawWeight(_hullSurf[i, j], f));
        }
        _flow = f;
        {
            _stagMid = Vector3.Zero; int sc0 = 0;
            for (int i = 0; i <= N; i++) for (int j = 0; j <= N; j++) if (_hullHas[i, j]) { _stagMid += _hullSurf[i, j]; sc0++; }
            if (sc0 > 0) _stagMid /= sc0;
        }
        if ((PuffSource & 4) != 0) { SpawnLines(P3, f, R, N, turn); if (_puffWhy != null) return; }
        if ((PuffSource & 3) != 0)
        {
            var H = _hullHas; var S = _hullSurf; Vector3 up = f * (SurfaceLift * R);
            _stagMid = Vector3.Zero; int sc = 0;
            for (int i = 0; i <= N; i++) for (int j = 0; j <= N; j++) if (H[i, j]) { _stagMid += S[i, j]; sc++; }
            if (sc > 0) _stagMid /= sc;
            float hz = float.MinValue;
            for (int i = 0; i <= N; i++) for (int j = 0; j <= N; j++) if (H[i, j]) hz = Math.Max(hz, Vector3.Dot(S[i, j], f));
            int fs2 = FrontEvery > 0 ? Math.Min(FrontEvery, step) : step, edgeN = 0;
            for (int i = 0; i <= N; i++)
                for (int j = 0; j <= N; j++)
                {
                    if (!H[i, j]) continue;
                    bool edge = i == 0 || j == 0 || i == N || j == N || !H[i - 1, j] || !H[i + 1, j] || !H[i, j - 1] || !H[i, j + 1];
                    if ((PuffSource & 2) != 0 && edge && edgeN++ % Math.Max(1, EdgeEvery) == 0)
                        Puff(TailSparks, S[i, j] + up, turn, cell * EdgeEvery * StreamScale);
                    else if ((PuffSource & 1) != 0)
                    {
                        bool front = FrontEvery > 0 && hz - Vector3.Dot(S[i, j], f) < FrontDepth * R && i % fs2 == 0 && j % fs2 == 0;
                        if (!(i % step == 0 && j % step == 0) && !front) continue;
                        var nrm = (NormalSource == 1 ? HullNormal(S[i, j], f) : null) ?? SurfaceNormal(i, j, f);
                        Vector3 dir = SprayDir(S[i, j], f, nrm);
                        var turnHere = SurfaceSpray ? Quaternion.CreateFromTwoVectors(Vector3.UnitZ, dir) : turn;
                        Vector3 at = SurfaceSpray ? S[i, j] + nrm * (SurfaceOff * R) : S[i, j] + up;
                        Puff(ShockSparks, at, turnHere, cell * (front ? fs2 : step) * PuffScale);
                    }
                    if (_puffWhy != null) return;
                }
            return;
        }
        if (PuffSource != 0) return;
        float zFront = float.MinValue;
        foreach (var q in P3) zFront = Math.Max(zFront, Vector3.Dot(q, f));
        int fs = FrontEvery > 0 ? Math.Min(FrontEvery, step) : step;
        for (int i = 0; i <= N; i++)
            for (int j = 0; j <= N; j++)
            {
                if (past[i, j] > TrimPast) continue;
                bool onGrid = i % step == 0 && j % step == 0;
                bool front = FrontEvery > 0 && zFront - Vector3.Dot(P3[i, j], f) < FrontDepth * R && i % fs == 0 && j % fs == 0;
                if (!onGrid && !front) continue;
                float sc = cell * (front ? fs : step) * PuffScale;
                if (Sparks != 1) Puff(ShockPuff, P3[i, j], turn, sc);
                if (Sparks >= 1) Puff(ShockSparks, P3[i, j], turn, sc);
                if (_puffWhy != null) return;
            }
        int around = ring.Length - 1, rs = Math.Max(1, StreamEvery);
        float arc = 0f;
        for (int k = 0; k < around; k++) arc += (ring[k + 1] - ring[k]).Length();
        for (int k = 0; k < around; k += rs)
        {
            if (Sparks != 1) Puff(StreamPuff, ring[k], turn, arc / around * rs * StreamScale);
            if (Sparks >= 1) Puff(TailSparks, ring[k], turn, arc / around * rs * StreamScale);
            if (_puffWhy != null) return;
        }
    }

    private readonly Queue<Action> _spawnQueue = new Queue<Action>();
    public static int SpawnPerTick = 48;   // (was 12: a rebuild filled in over 20-40 frames - the gap showed)

    /// <summary>The emitter's held time: heat x its local weight (normalised, ^WeightGamma) / density; -1 unweighted.</summary>
    private float WeightTime(Vector3 at, float density)
    {
        if (!Weighted) return -1f;
        float w = MathF.Pow(Math.Clamp(RawWeight(at, _flow) / _weightMax, 0f, 1f), WeightGamma);
        if (w < WeightMin) return 0f;
        return Math.Clamp(Heat * w / Math.Max(1f, density), 0.001f, 0.999f);
    }

    private void Puff(Guid effect, Vector3 at, Quaternion turn, float scale)
    {
        if (!_draining && !_relayout) { _spawnQueue.Enqueue(() => Puff(effect, at, turn, scale)); return; }
        bool spark0 = effect != ShockPuff && effect != StreamPuff;
        float held = spark0 ? WeightTime(at, 1f) : -1f;
        if (held == 0f) return;
        if (held > 0f) effect = Recipe ? RecipeGuid(effect == TailSparks) : RateGuid(TexKind(effect == TailSparks), StreakField);
        bool spark = effect != ShockPuff && effect != StreamPuff;
        void Fill(object prm)
        {
            var t = prm.GetType();
            t.GetField("EmitterScaleMultiplier").SetValue(prm, Math.Max(0.1f, scale));
            if (spark) t.GetField("ColorMultiplier").SetValue(prm, new ColorSRGB(SparkBright, SparkBright, SparkBright, 1f));
            // (measured: a local-space effect's particle speed is already x its EmitterScaleMultiplier - x the spacing
            //  again sent the sparks 9x too fast, starting a plane-length behind their point; puffs kept as tuned)
            t.GetField("VelocityMultiplier").SetValue(prm, spark ? Math.Max(0.01f, SparkSpeed) : Math.Max(0.1f, scale));
            if (spark)
            {
                t.GetField("EmitterSizeMultiplier").SetValue(prm, SpawnSize);
                t.GetField("ParticleAspectRatio").SetValue(prm, SparkAspect);
                if (Strict) t.GetField("VelocityDirection").SetValue(prm, Vector3.UnitZ);
            }
        }
        object h = SpawnEffect(_client, effect, new RelativeTransform(at, turn), out object prm, out _puffWhy, Fill);
        if (h == null) return;
        _puffs.Add(h);
        try
        {
            SetParamsIfChanged(h, prm);
            { float w7 = held > 0f ? held / Math.Max(1e-3f, Heat) : 1f; _plasma.Add((h, w7)); held = Math.Clamp(_entryS * w7, 0.00001f, 0.999f); }
            if (held > 0f) { (_fixTime ??= h.GetType().GetMethod("FixEffectTime", AnyInst)).Invoke(h, new object[] { true, TimeSpan.FromSeconds(held) }); NoteFixed(h, held); }
        }
        catch (Exception e) { _puffWhy = "set: " + Inner(e); }
    }
}
