using System;
using Keen.Game2.Simulation.WorldObjects.CubeGrids;
using Keen.Game2.Simulation.WorldObjects.CubeGrids.BlockOctrees;
using Keen.Game2.Simulation.WorldObjects.Movement;
using Keen.VRage.Core;
using Keen.VRage.Core.Game.GameSystems.OWT;

#pragma warning disable CS0649, CS8632, CS1591, CS1573, CS1570, CS1574, CS0419, CS1734
namespace AeroMod;

/// <summary>
/// Per-grid aerodynamics component: builds the grid's surface, wings and force table (Build), follows damage (Damage),
/// computes the air's forces each frame (Compute; applied by the sim job, AeroSimJob.cs) and drives the control
/// surfaces (Surfaces) and the attitude hold (Attitude). Its fields live here; each concern in its own partial file.
/// </summary>
[WhenSimulated]
public partial class AeroGridComponent : Component, IInSceneListener
{
    [Keen.VRage.DCS.Annotations.Component]
    private readonly CubeGridComponent _grid;

    [Keen.VRage.DCS.Annotations.Component]
    internal readonly BlockOctreeComponent _octree;

    // ── Aero pipeline state ──

    private Se2GridAccessor _gridAccessor;
    private SmoothSurfaceProvider _surface;       // active surface (used for force computation)
    private SmoothSurfaceProvider _buildSurface;  // back buffer (used during staggered rebuild)
    private LiftingSurfaceModel _model;
    private ManifoldClassifier _manifold;
    // A rebuild in the background (StartBackgroundBuild): the spare classifier it classifies into, the
    // wings it detects, and the task. The active surface, classifier and wings serve the simulation until the swap.
    private ManifoldClassifier _buildManifold;
    private AeroWork.Item _finalizeTask;
    /// <summary>Bumped by every background build and by a re-add: a build whose number is no longer this one is stale -
    /// it neither starts nor hands over its results (2026-10-15: a grid removed and re-added mid-build - a teleport - got
    /// the old worker's table, chunks and wings for the old shape, or its quick table installed on the new model).</summary>
    private volatile int _buildGen;
    private List<LiftingSurface> _builtWings;
    private PrecomputedShadowMap _builtShadow, _spareShadow;
    private ForceTable _builtTable;
    // -- Damage, locally: the chunked table (ChunkedTable) recomputes the chunks damage touched, in the background,
    //    ~0.1 s instead of a whole rebuild; the whole rebuild follows once damage has been quiet a while. --
    private ChunkedTable _chunks, _builtChunks;
    private readonly HashSet<long> _localDirty = new(LongKey.Comparer);
    /// <summary>Per dirty chunk: how many block changes reached it (the most-changed chunks go first).</summary>
    private readonly Dictionary<long, int> _localWeight = new(LongKey.Comparer);
    /// <summary>Chunks per local update: bounded work (~30-50 ms on Red Ship) - the most-changed first, the rest after.</summary>
    public static int ChunksPerUpdate = 8;
    private AeroWork.Item _localTask;

    private int _chunkGen, _localGen;
    private long _lastDamage;
    private bool _fullAfterQuiet;
    /// <summary>Damage quiet this long (s): the whole rebuild (downstream shadowing, wings) runs.</summary>
    public static double QuietBeforeFullRebuild = 5.0;
    public static bool LocalUpdates = true;
    /// <summary>Below this speed (m/s) a damaged grid's table waits (q ~ 50 Pa at sea level: its drag is nothing).</summary>
    public static float MinSpeedForUpdates = 10f;
    /// <summary>Chunk size (m): 8 measured best (Red Ship, a block: 95 ms at 16 m -> 27 ms; 4 m no faster).</summary>
    public static float ChunkSize = 8f;
    internal double LastLocalMs, QuickMs;
    private volatile ForceTable _quickTable;
    internal int ChunksDropped;
    private readonly HashSet<long> _dropKeys = new(LongKey.Comparer);
    private readonly List<(long key, float[] share)> _orphanBuf = new();
    private readonly HashSet<long> _tmpDirty = new(LongKey.Comparer);
    private int _seedTries;
    private readonly HashSet<Keen.VRage.DCS.Components.Entity> _thrusterSet = new();
    private int _thrusterSetCount = -1;
    internal void ThrustersRebuilt() { _thrusterSetCount = -1; _thrusterGen++; ThrusterSnap = _thrusterCache.ToArray(); }
    /// <summary>The thruster list as of its last rebuild, for other threads (AeroApi.GetThrusters): a copy, never changed
    /// once published (the live _thrusterCache is cleared and refilled in place, 2026-10-15).</summary>
    internal volatile ThrusterInfo[] ThrusterSnap = Array.Empty<ThrusterInfo>();
    /// <summary>Counts thruster cache rebuilds (ThrustTorque re-keys its published flame shares on a change).</summary>
    internal int _thrusterGen;
    /// <summary>This grid's published flame shares (ThrustTorque.Publish; server copy only).</summary>
    internal ThrustTorque.Published _flamePub;
    private ChunkedTable.LocalResult _localRes;
    internal int LocalUpdatesDone;
    /// <summary>Grid cells per surface cell (large-block grids: 2). Such a surface is never updated cell by cell.</summary>
    private int _cellScale = 1;
    // ThrustTorque's cached thruster geometry (see Apply)
    internal float[] _rcsCapDir = new float[6];
    internal int _rcsGeomCount = -1;
    internal Vector3 _rcsGeomCom;
    internal float _rcsArmSq;
    // ... and each thruster's efficiency in the air it is in (its thrust class: the game's GetThrustEfficiency)
    internal float[] _rcsNom = Array.Empty<float>();
    internal Keen.Game2.Simulation.GameSystems.Movement.ThrustClassData[] _rcsClass = Array.Empty<Keen.Game2.Simulation.GameSystems.Movement.ThrustClassData>();
    internal bool[] _rcsHasClass = Array.Empty<bool>();
    internal float _rcsGeomAir = -1f;
    // force tables are built on a face model of their own (the live one may be computing): one kept for reuse
    private static readonly Stack<DampedShadowedDragModel> _tableBuilders = new();
    /// <summary>The face physics tables are built with (the table builders' defaults): part of the cache key. Read when
    /// asked: the static switches (SubsonicSquareLaw, ReynoldsSkin) as they are now, not as at type init (2026-10-15).</summary>
    private static FacePhysics TablePhysics => new FacePhysics(_tablePhysicsModel);
    private static readonly DampedShadowedDragModel _tablePhysicsModel = new DampedShadowedDragModel();
    /// <summary>Builds shorter than this are not cached (a small grid's: a file costs more than it saves).</summary>
    private const double CacheAboveMs = 40;
    /// <summary>Whether the last build came from the table cache.</summary>
    public bool FromCache { get; private set; }
    private static DampedShadowedDragModel RentTableBuilder() { lock (_tableBuilders) return _tableBuilders.Count > 0 ? _tableBuilders.Pop() : new DampedShadowedDragModel(); }
    private static void ReturnTableBuilder(DampedShadowedDragModel b) { lock (_tableBuilders) if (_tableBuilders.Count < 1) _tableBuilders.Push(b); }
    private volatile string _rebuildNote = "";
    private AeroComponentRegistry _components;
    private BlockComponentFactory _factory;
    private float _blockSize;
    private float _cachedBlockSize = -1;
    private bool _dirty;
    private int _surfaceWaited;
    /// <summary>The longest (frames) block changes wait for the surface, however steady the damage.</summary>
    private const int MaxSurfaceWait = 60;
    /// <summary>Above this many faces, every surface change is a background rebuild.</summary>
    private const int BigSurfaceFaces = 20000;
    private const double BigRebuildGapSeconds = 2.0;
    private long _lastRebuildStart;
    internal int _switchGen;

    /// <summary>Everything rebuilt from scratch (after aero was switched off and on).</summary>
    internal void ForceFullRebuild()
    {
        _dirty = true; _fullRebuildNeeded = true; _thrusterCacheDirty = true; _gyroCacheDirty = true; _cachedBlockSize = -1;
        _pendingAddedCells.Clear(); _pendingRemovedCells.Clear(); _components.Clear();
    }
    private bool _fullRebuildNeeded;
    private bool _initialized;

    // ── Staggered rebuild (managed by AeroScheduler) ──
    private bool _staggeredBuildActive;
    private bool _rebuildRestartNeeded; // topology changed mid-build

    // ── Deferred full wing re-detection ──
    private bool _wingsDirty;
    private int _wingCooldownTicks;
    private const int WingDetectCooldown = 60; // ~1s at 60Hz — full detection with ray-march

    // ── Surface update cooldown (suppress rapid incremental updates during impacts) ──
    private int _surfaceCooldownTicks;
    private const int SurfaceCooldown = 60; // ~1s at 60Hz

    // ── Face override index (for Cp heatmap) ──
    private bool _faceOverridesDirty = true;

    // ── Accumulated changes for incremental wing update ──
    private List<Vector3I> _wingAddedCells = new();
    private List<Vector3I> _wingRemovedCells = new();

    // ── Batched block change accumulation ──
    private List<Vector3I> _pendingAddedCells = new();
    private List<Vector3I> _pendingRemovedCells = new();

    // ── Offset thrust (RCS) ──
    internal List<ThrusterInfo> _thrusterCache = new();
    private bool _thrusterCacheDirty = true;

    // ── Gyroscope control ──
    internal List<GyroInfo> _gyroCache = new();
    private bool _gyroCacheDirty = true;

    // ── Ground effect ──
    /// <summary>Height above ground in meters. -1 = unknown.</summary>
    internal float GroundHeight = -1f;
    /// <summary>Away from the ground (against gravity), grid frame, unit; zero out of gravity (AeroSimJob).</summary>
    internal Vector3 GroundUpLocal;
    internal readonly PhysicsHack.GroundProbe Ground = new PhysicsHack.GroundProbe();

    // ── Cached physics state (written by sim job @ 60Hz, read by draw job) ──
    internal Vector3 LastLinVel;
    internal Vector3 LastAngVel;
    internal Vector3 LastCoM;
    internal float LastMass;
    internal float LastDensity;
    /// <summary>LastDensity / LastAirVel / LastMach hold this grid's own air: set by its first full step (AeroSimJob), cleared
    /// when it leaves the scene. Before that they are zeros - not a vacuum. A reader of the air (the propulsors, the solid
    /// nozzles) takes the game's air or its rating until it is set, then this grid's air WHATEVER the force model did that
    /// step (2026-10-17: the solid nozzle switched sources on HasResult - set only above AeroConfig.MinSpeed - so its
    /// pressure ratio jumped between two scales as a held grid started and stopped moving).</summary>
    internal bool AirKnown;
    internal float LastSpeed;
    /// <summary>The grid's Mach number every step (AeroSimJob): its airspeed (the wind out) over the local speed of sound - the
    /// aero model's own (AtmosphereBridge at the air's density, as AeroResult.Mach), VacuumSoundSpeed where it has none (vacuum). Unlike
    /// LastResult.Mach it is kept on the steps the force model skips or returns early (slow, thin air). The vanilla atmospheric
    /// thrusters' Mach curve reads it (AeroThrustSettingsComponent.Jet, JetCurve).</summary>
    internal float LastMach;
    /// <summary>The grid's AIRSPEED vector every step (world, m/s: its velocity less the wind's - AeroWind), and the local
    /// speed of sound it was taken over (m/s; the force model's, VacuumSoundSpeed where it has none). With LastMach the ONE
    /// source of the air a grid meets (2026-10-17): the propulsors' curves and axial speed (AeroPropulsorComponent), the
    /// entry heat, the flow record, the structure's stall warning - never the ground speed, never a fixed speed of sound.</summary>
    internal Vector3 LastAirVel;
    internal float LastSoundSpeed = VacuumSoundSpeed;
    /// <summary>The speed of sound taken where the model has none (vacuum, no atmosphere model yet: m/s) - the sea-level one.</summary>
    internal const float VacuumSoundSpeed = ThrustProfiles.SeaLevelSoundSpeed;
    /// <summary>The local speed of sound (m/s) in air of this density - the force model's own (AtmosphereBridge).</summary>
    internal static float SoundSpeedOf(float density)
    {
        double a = _atmosphereBridge != null ? _atmosphereBridge.GetState(density).SpeedOfSound : 0.0;
        return a > 1.0 && double.IsFinite(a) ? (float)a : VacuumSoundSpeed;
    }
    /// <summary>One step's air (AeroSimJob, every step, the quiet path too): LastAirVel, LastSoundSpeed and LastMach together.</summary>
    internal void SetAir(Vector3 airVel, float density)
    {
        LastAirVel = airVel;
        LastSoundSpeed = SoundSpeedOf(density);
        float m = airVel.Length() / LastSoundSpeed;
        LastMach = float.IsFinite(m) ? m : 0f;
    }
    /// <summary>Entry heat (AeroEntryFx.Server: Mach-based, lagged).</summary>
    internal float EntryHeat;
    internal bool FlowPublished;   // (AeroEntryFx.Flow: published for other effects)
    internal bool HasTable => _model?.Table != null;
    /// <summary>The detected wings and the cell geometry they are in (size, offset) - for the effects (AeroEntryFx.WingTips).</summary>
    internal List<LiftingSurface> FxWings => _model?.Wings;
    /// <summary>The force table's chunks (hull points, per-chunk drag) - for the flight effects (FlightField).</summary>
    internal ChunkedTable FxChunks => _chunks;
    /// <summary>The model and the surface it runs on - for the public API (AeroApi: wings, surfaces, prediction).</summary>
    internal LiftingSurfaceModel FxModel => _model;
    internal SmoothSurfaceProvider FxSurface => _surface;
    internal float FxBlockSize => _blockSize;
    /// <summary>Each block's occupied-cell boxes, now (simulation thread) - as a rebuild captures them.</summary>
    internal List<(Vector3I, Vector3I)> CaptureBoxes()
    {
        var boxes = new List<(Vector3I, Vector3I)>();
        if (_octree == null) return boxes;
        foreach (var block in _octree.GetAllCubeBlocks())
        {
            if (block == null || BlockComponentFactory.IsSelfModelled(block)) continue;
            foreach (var g in block.GetTransformedOccupiedCellGroups()) boxes.Add((g.Min, g.Max));
        }
        return boxes;
    }
    internal (float size, float offset) FxCellGeo => _surface != null ? (_surface.CellSize, _surface.CellOffset) : (0.25f, 0f);
    /// <summary>This grid's table is wanted though it sees no air (an entry source flies it through some).</summary>
    internal bool EntryWantsTable;
    internal Vector3 LastInvInertia;      // (1/Ixx, 1/Iyy, 1/Izz) principal axes
    internal Quaternion LastInertiaMajorAxisRot = Quaternion.Identity; // principal → body rotation

    // Shared across all grids
    private static AtmosphereBridge _atmosphereBridge;

    /// <summary>Last computed result.</summary>
    internal AeroResult LastResult;
    internal float[] _thrustUse = Array.Empty<float>();
    internal Vector3[] _rcsArm = Array.Empty<Vector3>(), _rcsDir = Array.Empty<Vector3>();
    /// <summary>ThrustTorque: which thrusters are reversible (ThrusterInfo.Rev, for thruster list _rcsRevGen), any, and each
    /// one's game side now (+1 / -1: where the game books its maximum).</summary>
    internal bool[] _rcsRev = Array.Empty<bool>(); internal float[] _rcsSide = Array.Empty<float>(); internal int _rcsRevGen = -1; internal bool _rcsAnyRev;
    internal float[] _rcsCap = Array.Empty<float>(), _rcsF = Array.Empty<float>(), _rcsPrev = Array.Empty<float>(), _rcsY = Array.Empty<float>();
    internal bool _rcsWarm;
    /// <summary>ThrustTorque: this grid's lit solid rocket nozzles in its attitude solve (AeroSolidNozzleComponent.SteerSlots) and
    /// the solve's arrays with them appended after the thrusters (grown only, never shrunk: no per-frame allocation).</summary>
    internal readonly System.Collections.Generic.List<AeroSolidNozzleComponent> _solidSteer = new();
    internal Vector3[] _sArm = Array.Empty<Vector3>(), _sDir = Array.Empty<Vector3>();
    internal float[] _sCap = Array.Empty<float>(), _sLo = Array.Empty<float>(), _sF0 = Array.Empty<float>(), _sF = Array.Empty<float>(), _sPrev = Array.Empty<float>(), _sY = Array.Empty<float>();
    internal bool[] _sRev = Array.Empty<bool>();
    internal bool IsServerScene = true;
    static readonly System.Collections.Generic.HashSet<string> _scenesSeen = new();
    internal int _rcsWarmGen = -1;

    internal ThrustTorque.Report LastThrust;
    /// <summary>DEV (harness: aeroset AeroGridComponent.TestTurnPitch 10, then TestTurnGen n - a new n re-bases): a seated
    /// grid's target, as the reticle sets it, turned from its orientation at that moment. 0: the pilot's own.</summary>
    public static int TestTurnGen; public static float TestTurnPitch, TestTurnYaw, TestTurnRoll;
    /// <summary>The sessions seen (weakly): a new one - a world loaded - sets the dev knobs above back to off. They survived
    /// a reload and, non-zero, overwrote every seated grid's target each tick, 2026-10-15.</summary>
    static readonly System.Runtime.CompilerServices.ConditionalWeakTable<object, object> _devSessions = new();
    static void DevResetOnNewSession(object session)
    {
        if (session == null) return;
        lock (_devSessions)
        {
            if (_devSessions.TryGetValue(session, out _)) return;
            _devSessions.Add(session, _devSessions);
        }
        TestTurnGen = 0; TestTurnPitch = TestTurnYaw = TestTurnRoll = 0f;
        AeroApi.ResetStructureEvents();   // (a new world: the last one's structure events let go - a world with no planet has no carrier to say it went, 2026-10-15 review)
    }
    private int _testTurnGen; private Quaternion _testTurnBase = Quaternion.Identity;
    /// <summary>The allocation's last step: the surfaces it gave inputs to, and how many at their limit.</summary>
    internal int _allocN, _allocSat;
    /// <summary>The air-breathing engines of the last build (their block boxes, 0.25 m cells, and axes): flow-through.</summary>
    internal List<(Vector3I lo, Vector3I hi, Vector3 axis)> _engines;
    /// <summary>The atmospheric thrusters (an air-breathing profile) as boxes and axes, from the thruster list.</summary>
    internal List<(Vector3I lo, Vector3I hi, Vector3 axis)> EngineBoxes()
    {
        var l = new List<(Vector3I, Vector3I, Vector3)>();
        foreach (var t in _thrusterCache) if (t.AirBreathing && t.CellMax.X >= t.CellMin.X) l.Add((t.CellMin, t.CellMax, t.ThrustDirection));
        return l;
    }
    /// <summary>The air's torque applied this frame (local, N m, the world's multipliers in; zero: none) - the attitude
    /// control's feedforward (ThrustTorque, aero-aware).</summary>
    internal Vector3 LastAeroTorque;
    /// <summary>The control surfaces' torque applied this frame (local, N m, the world's multipliers in): the block
    /// components' share of the air's torque. The aero-aware law counts it as delivered by an actuator.</summary>
    internal Vector3 LastSurfaceTorque;
    /// <summary>The attitude controller's whole torque demand (local, N m): I x the law's acceleration - the airframe's own
    /// torque (surfaces excluded), for every actuator together - surfaces, thrusters, gyros. Written by ThrustTorque.Apply
    /// (the late attitude job: the surfaces, computed earlier in the tick, read the previous tick's). Zero, with
    /// LastControlTorqueValid false, when the aero-aware law is not flying (rate input, free, vacuum, harness).</summary>
    internal Vector3 LastControlTorque;
    internal bool LastControlTorqueValid;
    /// <summary>The wind at the grid (world m/s; AeroWind), whether aero applied its forces last tick (the game's own wind
    /// push is then taken back), and the impulse taken back (world N s).</summary>
    internal Vector3 LastWind, LastWindUndone; internal bool AeroForcesApplied;
    private int _subPartCheck; private bool _isSubPart;
    /// <summary>For AeroCost's 'top grid' line.</summary>
    internal int FacesNow => _surface?.FaceCount > 0 ? _surface.FaceCount : _lastFaces;
    private int _lastFaces;
    internal bool Rebuilding => _staggeredBuildActive;
    internal bool UsesTable => _model?.UsedTable ?? false;
    internal string LocalNote => _chunks == null ? "" : $"chunks={_chunks.ChunkCount} local={LocalUpdatesDone} last {LastLocalMs:F0} ms dropped={ChunksDropped} waiting={_localDirty.Count}";

    /// <summary>In a planet's gravity (set by the job): a grid there may meet air soon, so it is built beforehand.</summary>
    internal bool InGravity;
    /// <summary>The gravity here (m/s2), last simulation frame: the debug draw's lift-to-weight.</summary>
    internal float LastGravity;

    /// <summary>Static grids (stations) never move: no aero.</summary>
    internal bool IsStatic => !Data.TryGet<Keen.VRage.Physics.Data.RigidBodyMassProperties>(out var mp) || mp.InvMass <= 0f;

    /// <summary>Rebuild order (AeroScheduler): grids without forces yet first, then piloted, then by speed in air.</summary>
    internal float BuildPriority =>
        (_model?.Table == null ? 1000f : 0f) + (Data.Has<TargetControlData>() ? 500f : 0f) + (LastDensity > 0f ? MathF.Min(LastSpeed, 300f) : 0f);

    /// <summary>BuildPriority as of this grid's last frame (the work queue reads it from its own threads).</summary>
    internal volatile float CachedPriority;
    /// <summary>The scheduler's grants (AeroScheduler): a build slot to begin, and this frame's cell budget.</summary>
    internal volatile bool SchedBegin;
    internal int SchedGrant;
    /// <summary>Frames in a row this grid's recompute waited for the frame budget (AeroFrameBudget).</summary>
    internal int BudgetSkips;

    /// <summary>Recompute the forces every how many frames (the last ones are applied between): piloted or fast
    /// grids every frame, slower ones every 2nd or 4th - many grids cost proportionally less.</summary>
    internal int ComputeInterval => Data.Has<TargetControlData>() || LastSpeed >= 80f ? 1 : LastSpeed >= 20f ? 2 : 4;
    internal int LodPhase = System.Threading.Interlocked.Increment(ref _lodSeed) & 3;
    private static int _lodSeed;

    /// <summary>Between recomputes: the scheduler still ticks and the attitude hold still runs.</summary>
    internal void SkipCompute(WorldTransform wt, Vector3 angularVelocity)
    {
        UpdateAttitudeHold(wt, angularVelocity);
        if (_chunks != null && LocalUpdates) TickLocal();   // (a finished local update goes in at once, not on the next recompute)
        if (_quickTable != null) { var qt = _quickTable; _quickTable = null; if (_model.Table == null) _model.InstallForceTable(qt); }
        AeroScheduler.EnsureTicked();
        AeroScheduler.RunMine(this);
    }
    internal string ShadowNote => _model?.InnerModel is DampedShadowedDragModel d ? $"shadow visible={d.ShadowMap.VisibleCount} shadowed={d.ShadowMap.ShadowedCount}" : "";
    internal bool HasResult;

    /// <summary>Component (CS/wing) torque from last frame, separate from body torque.</summary>
    internal Vector3 LastComponentTorque;
    /// <summary>DEV (harness: aeroset AeroGridComponent.SurfaceTorqueLog true): twice a second per grid with block components,
    /// their torque (grid frame, N m), the rotation wanted and the rotation now - [AERO-CS-TORQUE] lines.</summary>
    public static bool SurfaceTorqueLog;
    private long _surfaceLogAt;
    /// <summary>Component (CS/wing) force from last frame, separate from body force.</summary>
    internal Vector3 LastComponentForce;

    /// <summary>Block-level aero component registry.</summary>
    public AeroComponentRegistry Components => _components;

    /// <summary>Face hull/cavity classifier for internal surface culling.</summary>
    public ManifoldClassifier Manifold => _manifold;

    // ── Lifecycle ──

    /// <summary>Every aero grid in a scene (server and client copies): DesignProbe pairs a client grid with its server copy.</summary>
    internal static readonly HashSet<AeroGridComponent> Live = new();
    void IInSceneListener.OnAddedToScene()
    {
        lock (Live) Live.Add(this);
        DevResetOnNewSession(Entity?.GetSession());
        if (AeroLog.Detail) Log.Default?.Info("[AERO] AeroGridComponent.OnAddedToScene()");   // (every grid, both scenes, each re-add: 750 lines a session - detail only)
        // (re-added after a removal: a rebuild left half-done would block every later one - _staggeredBuildActive stayed set)
        _staggeredBuildActive = false; _finalizeTask = null; SchedBegin = false; SchedGrant = 0;
        _buildGen++;   // (a build still running for the last time in the scene: stale)

        _gridAccessor = new Se2GridAccessor(_octree);
        _surface = new SmoothSurfaceProvider();
        _buildSurface = new SmoothSurfaceProvider();

        var innerDrag = new DampedShadowedDragModel();
        _model = new LiftingSurfaceModel(innerDrag, liftModel: new CompressibleWingModel());
        _manifold = new ManifoldClassifier();
        _buildManifold = new ManifoldClassifier();
        _components = new AeroComponentRegistry();

        // Wire manifold into wing detector for cavity face filtering
        if (_model.Detector is ConnectedComponentWingDetector ccwd)
        {
            ccwd.Manifold = _manifold;
            ccwd.ManifoldSurface = _surface;
        }
        _factory = new BlockComponentFactory();
        DefaultMappings.Register(_factory);

        _atmosphereBridge ??= new AtmosphereBridge();

        // Attach ObservedWorldTransform so the draw job triggers on transform changes
        ObservedWorldTransform.AttachTo(Data, Data);

        // Enable debug draw globally (off by default)
        Keen.VRage.Core.GlobalDebugSettings.Default.EnabledDebugDraw = true;

        // The client copy shows thruster flames for the controller's sharing (ThrustTorque.Publish, AeroFlameComponent). The game
        // names its scenes "Server" and "Client" (WorldSessionComponent); in single player both sessions have the
        // game server service, so that cannot tell them apart.
        try { IsServerScene = !string.Equals(Data.Scene?.DebugName, "Client", StringComparison.Ordinal); } catch { IsServerScene = true; }
        lock (_scenesSeen) if (_scenesSeen.Add(Data.Scene?.DebugName ?? "null")) Log.Default?.Info($"[AERO] grid scene '{Data.Scene?.DebugName}' (server={IsServerScene}, session {Entity.GetSession()?.GetHashCode()})");

        // Defer heavy work (surface build, wing detection) to first compute call
        _dirty = true;
        _fullRebuildNeeded = true;
        _initialized = true;
    }

    void IInSceneListener.OnBeforeRemovedFromScene()
    {
        lock (Live) Live.Remove(this);
        DesignProbe.Forget(this);
        AeroEntryFx.Forget(this);
        ThrustTorque.Forget(this);   // (its published flame shares held this component - and its tables - alive forever)
        Ground.Release();            // (the ground ray's native hit/ignore lists)
        _initialized = false;
        AirKnown = false;            // (re-added: its air is read again before anyone takes it)
        AeroScheduler.Remove(this);
        // (2026-10-16: a crashed rotorcraft's pieces being removed killed the game here - NullReferenceException in the engine's
        //  ObservedWorldTransformSystem.DetachOWTFrom: its observed transform was already gone, and it dereferences the missing
        //  data. Only detach what is still attached; removal is rare, so the guard costs nothing)
        try { if (Data.Has<ObservedWorldTransform>()) ObservedWorldTransform.DetachFrom(Data, Data); }
        catch (Exception e) { Log.Default?.Info($"[AERO] grid removal: observed transform detach failed ({e.GetType().Name}) - skipped"); }
    }

}
