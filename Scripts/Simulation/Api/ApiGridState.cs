#pragma warning disable
using System;
namespace AeroMod;

/// <summary>
/// One grid's published copies for the public API (AeroApi version 7: flight state, control surfaces, thruster forces,
/// structure blocks, heat faces, another mod's block temperatures). Made by the API's first read of the grid
/// (AeroGridComponent.ApiStateFor); filled only while someone reads it (WatchUntil / FacesUntil, Stopwatch ticks): a grid
/// nobody asks about costs one null test a tick.
/// The writers (the simulation thread for the tick record, the structure's background evaluation for the rest) fill the
/// spare ("back") arrays outside the lock and swap them in under it; readers copy out under it. Arrays only grow.
/// </summary>
internal sealed class ApiGridState
{
    public readonly object Lock = new object();
    /// <summary>Publishing on while Stopwatch.GetTimestamp() is under these (the reads push them on: AeroApi.Watch).</summary>
    public long WatchUntil, FacesUntil;

    // ── the tick record (simulation thread: AeroGridComponent.PublishApiTick) ──
    public float[] Flight = new float[AeroApi.FlightStateLength], FlightBack = new float[AeroApi.FlightStateLength];
    public long FlightStamp;   // (Stopwatch ticks of the tick it was taken; 0 none yet)
    public int SurfCount; public Vector3I[] SurfCell = Array.Empty<Vector3I>(); public float[] SurfCmd = Array.Empty<float>(), SurfDeg = Array.Empty<float>(), SurfLift = Array.Empty<float>(), SurfDrag = Array.Empty<float>();
    public Vector3I[] SurfCellB = Array.Empty<Vector3I>(); public float[] SurfCmdB = Array.Empty<float>(), SurfDegB = Array.Empty<float>(), SurfLiftB = Array.Empty<float>(), SurfDragB = Array.Empty<float>();
    public int ThrCount, ThrGen; public float[] ThrForce = Array.Empty<float>(), ThrForceB = Array.Empty<float>();

    // ── the structure (background evaluation: AeroGridComponent.ApiPublishStructure) ──
    /// <summary>The structure the published block order is of (a new one - a rebuild - bumps Layout).</summary>
    public object LayoutOf;
    public int Layout, Evaluation, BlockCount;
    /// <summary>The layout the block state arrays (Util ... LoadM) were last published for (Layout: current).</summary>
    public int StateLayout = -1;
    public Keen.VRage.DCS.Components.Entity[] Blocks = Array.Empty<Keen.VRage.DCS.Components.Entity>();
    public Vector3[] Centres = Array.Empty<Vector3>(); public float[] Mass = Array.Empty<float>();
    public float[] Util = Array.Empty<float>(), Health = Array.Empty<float>(), TempK = Array.Empty<float>(), SkinK = Array.Empty<float>(), HeatW = Array.Empty<float>();
    public float[] UtilB = Array.Empty<float>(), HealthB = Array.Empty<float>(), TempKB = Array.Empty<float>(), SkinKB = Array.Empty<float>(), HeatWB = Array.Empty<float>();
    public Vector3[] LoadF = Array.Empty<Vector3>(), LoadM = Array.Empty<Vector3>(), LoadFB = Array.Empty<Vector3>(), LoadMB = Array.Empty<Vector3>();
    /// <summary>Whether this evaluation's temperatures were another mod's (SetBlockTemperatures).</summary>
    public bool TempsExternal;

    // ── the hull's heat faces (same evaluation, while FacesUntil) ──
    public int FaceLayout = -1, FaceCount;
    public Vector3[] FacePos = Array.Empty<Vector3>(), FaceNrm = Array.Empty<Vector3>(); public float[] FaceArea = Array.Empty<float>(); public int[] FaceBlock = Array.Empty<int>();
    public float[] FaceHeatW = Array.Empty<float>(), FaceSkinK = Array.Empty<float>(), FaceFilm = Array.Empty<float>();
    public float[] FaceHeatWB = Array.Empty<float>(), FaceSkinKB = Array.Empty<float>(), FaceFilmB = Array.Empty<float>();
    public float RecoveryK; public int FaceEvaluation;

    // ── another heat model's block temperatures (AeroApi.SetBlockTemperatures; read by the evaluation: ApiHeatOverride) ──
    public int ExtLayout = -1; public long ExtStamp; public float[] ExtTempK = Array.Empty<float>(), ExtSkinK = Array.Empty<float>();

    /// <summary>a, if it holds n, else a new array of at least n (only grows; never shrinks).</summary>
    public static T[] Fit<T>(T[] a, int n) => a.Length >= n ? a : new T[Math.Max(n, a.Length * 2)];

    public static void Swap<T>(ref T[] a, ref T[] b) { T[] t = a; a = b; b = t; }
}
