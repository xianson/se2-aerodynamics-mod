#pragma warning disable
using System;
using System.Reflection;
using Keen.Game2.Simulation.WorldObjects.CubeBlocks;
using Keen.Game2.Simulation.WorldObjects.CubeGrids.Damage;
namespace AeroMod;

/// <summary>The world's structure and hull-heat settings (AeroSettings: Structure.*, Heat.*) and diagnostics.</summary>
public static class StructureLoads
{
    /// <summary>Compute the loads and the hull's heat (gauges, API). Damage is separate.</summary>
    public static bool Enabled = true;
    /// <summary>Loaded joints tire, overloaded ones wear, and when its health is out (or past StructureFatigue.SnapUtil) the
    /// outboard block fails - its pieces on the failing side broken, or the whole block (the game splits the rest off;
    /// Structure/StructureFatigue, 2026-10-14) - a whole CUT at once, every member across it (2026-10-15:
    /// StructureEval.DamageBySection). ON by default (2026-10-14, the user: "turn damage on by default - I want to test it from time to time").</summary>
    public static bool Damage = true;
    /// <summary>(harness) dump the next rebuilt player grid's structure input to %TEMP%/AeroMod (DumpStructure).</summary>
    public static bool DumpNext; public static string LastDump = "-";
    /// <summary>The flight controller limits its own steering to what the structure carries (AeroGridComponent.UpdateSteerCap):
    /// the worst utilisation it steers to, the cap's floor, the share of the target under which it recovers, how fast (/s).</summary>
    public static bool SteerRespectsStructure = true;
    public static float SteerCapTarget = 0.9f, SteerCapMin = 0.05f, SteerCapRelax = 0.8f, SteerCapRecover = 0.5f;
    public static string SteerCapLast = "-";
    public static float DumpLoadsAbove = -1f;
    /// <summary>Hull blocks past their burn temperature lose health. ON by default (2026-10-14, the user).</summary>
    public static bool HeatDamage = true;
    /// <summary>Evaluations per second of a player's grid (the only ones evaluated; IdleRate, ActiveSpeed, ActiveSpin,
    /// ActiveSkinK: unused since players' grids only, kept for the settings' sake).</summary>
    public static float Rate = 4f, IdleRate = 1f;
    /// <summary>Seconds a grid stays a player's grid after its pilot's seat last reported it (AeroApi.SetFocusGrid).</summary>
    public static float FocusLinger = 10f;
    /// <summary>Seconds between re-reads of the blocks' health (each a pass over every block on the simulation thread).</summary>
    public static float IntegrityEvery = 2f;
    /// <summary>What counts as active: airspeed (m/s, in air), spin (rad/s), skin temperature (K).</summary>
    public static float ActiveSpeed = 15f, ActiveSpin = 0.1f, ActiveSkinK = 340f;
    /// <summary>Evaluations in the background at once, every grid together (aero's own builds are never queued behind
    /// more than this).</summary>
    public static int MaxInFlight = 1;
    internal static int InFlight;
    /// <summary>Utilisation (1: yield) at which a joint breaks; between 1 and it, it wears (StructureEval's: the offline
    /// failure simulation reads the same).</summary>
    public static float BreakUtil { get => StructureEval.BreakUtil; set => StructureEval.BreakUtil = value; }
    /// <summary>Health lost per second (fraction of full) per unit of utilisation over 1.</summary>
    public static float WearPerSecond { get => StructureEval.WearPerSecond; set => StructureEval.WearPerSecond = value; }
    /// <summary>Most CUTS broken per evaluation, each whole (2026-10-15: StructureEval.DamageBySection; the rest deferred whole
    /// and re-solved on what is left).</summary>
    public static int MaxBreaksPerEval { get => StructureEval.MaxBreaks; set => StructureEval.MaxBreaks = value; }
    /// <summary>NO ZIPPER (2026-10-15): after a cut fails, the structure's damage waits for the structure rebuilt without what
    /// left (the evaluations in between solve the old blocks: the departed piece still on, its load still through the next
    /// cut) - at most this many seconds (s) if no block change comes.</summary>
    public static float BreakHoldMax = 3f;
    /// <summary>Diagnostics: evaluations whose structure damage was held after a break; the last failed cut.</summary>
    public static int BreakHeld; public static string LastCut = "-";
    /// <summary>Seconds between structure rebuilds after blocks change.</summary>
    public static float RebuildCooldown = 1f;

    /// <summary>DEV: log every evaluation of piloted grids ([AERO-STRUCT-T]: utilisation, worst place, hottest skin,
    /// the loads' totals, the solve's time).</summary>
    public static bool Trace;
    // diagnostics (harness aeroget)
    public static int Evals, Builds, Breaks, Wears, Burns;
    /// <summary>COST, over the last second: the simulation thread's time in TickStructure (us per second, every grid), the
    /// background's time evaluating and building (ms per second), evaluations, and grids ticked / evaluated.</summary>
    public static float SimUsPerSec, BgMsPerSec, EvalsPerSec, SimMaxUs;
    public static int GridsTicked, GridsEvaluated;
    static long _simTicks, _bgTicks, _windowAt, _simMax; static int _evalsWin, _ticked, _evaluated;
    static int _idle;
    internal static void CountIdle() => _idle++;   // (racy on purpose: a diagnostic count)
    internal static void AddSim(long ticks)
    {
        System.Threading.Interlocked.Add(ref _simTicks, ticks); System.Threading.Interlocked.Increment(ref _ticked);
        long m; while (ticks > (m = System.Threading.Interlocked.Read(ref _simMax)) && System.Threading.Interlocked.CompareExchange(ref _simMax, ticks, m) != m) { }
    }
    internal static void AddBg(long ticks) { System.Threading.Interlocked.Add(ref _bgTicks, ticks); System.Threading.Interlocked.Increment(ref _evalsWin); }
    internal static void Evaluated() => System.Threading.Interlocked.Increment(ref _evaluated);
    internal static void Publish()
    {
        long now = System.Diagnostics.Stopwatch.GetTimestamp();
        if (_windowAt == 0) { _windowAt = now; return; }
        double sec = (now - _windowAt) / (double)System.Diagnostics.Stopwatch.Frequency;
        if (sec < 1.0) return;
        lock (typeof(StructureLoads))
        {
            if ((now - _windowAt) / (double)System.Diagnostics.Stopwatch.Frequency < 1.0) return;
            double f = System.Diagnostics.Stopwatch.Frequency;
            SimUsPerSec = (float)(System.Threading.Interlocked.Exchange(ref _simTicks, 0) / f * 1e6 / sec);
            SimMaxUs = (float)(System.Threading.Interlocked.Exchange(ref _simMax, 0) / f * 1e6);
            BgMsPerSec = (float)(System.Threading.Interlocked.Exchange(ref _bgTicks, 0) / f * 1e3 / sec);
            EvalsPerSec = (float)(System.Threading.Interlocked.Exchange(ref _evalsWin, 0) / sec);
            GridsTicked = (int)((System.Threading.Interlocked.Exchange(ref _ticked, 0) + System.Threading.Interlocked.Exchange(ref _idle, 0)) / (sec * 60.0) + 0.5);
            GridsEvaluated = System.Threading.Interlocked.Exchange(ref _evaluated, 0);
            _windowAt = now;
        }
    }
    public static float LastEvalMs, LastBuildMs, SeenMaxUtil, SeenMaxSkin;
    public static string LastBuild = "-", LastWorst = "-";
}

/// <summary>
/// STRUCTURE AND HULL HEAT, SERVER (the models: Structure/ShipStructure, Structure/HullHeat - offline-tested in
/// Tests/StructureTests and AeroBench 'struct'). Per grid, StructureLoads.Rate times a second:
///  - on the simulation thread (TickStructure): a snapshot of what loads the ship - the air's applied force and torque,
///    the wings' forces, each thruster's force, the gyros' torque, the angular velocity, the air - and the blocks'
///    health; after a block change (and a cooldown), the blocks themselves (cells, mass, name) for a rebuild;
///  - in the background (AeroWork): the rebuild (slices, surface faces, heat state carried over), then the hull's heat
///    for the time since the last evaluation (its own surface pass when the flow turned), the loads spread onto the
///    blocks (wings over their cells, the rest of the air over the windward faces, thrust at the thrusters, gyro
///    torque over the gyros), every block's temperature into its strength, the solve (each piece free on its loads),
///    and the damage to do;
///  - back on the simulation thread, the next tick: the damage, through the game's damage receiver (game-mode rules
///    apply: creative takes none), and the gauges (StructMaxUtil, StructMaxSkinK).
/// Contact forces (landings, collisions) are not included yet: only the forces this mod knows.
/// </summary>
public partial class AeroGridComponent
{
    sealed class StructSnap
    {
        public readonly StructureEval.Loads L = new();
        public float Density, AirT, Dt;
        public bool Piloted;
        public float SoS, Weight; public Vector3 Up;
        public bool HasTarget; public Quaternion Target, Orient; public Vector3 Reach, SurfPush, SurfPull, SurfNeutral, Airframe;
        public float[] Integrity;
        /// <summary>A railed re-entry (the Orbital mod's entry STATE - OrbitalApi.TryGetEntryState2, OrbitalLink): the true air
        /// velocity (grid-local, m/s: the travel into the air), density (kg/m3) and the band's air temperature (K; 0 none) -
        /// the hull heats from these, its physical speed being the rails' capped one; the braking goes into L.EntryAccel.</summary>
        public float EntryDensity, EntryAirT; public Vector3 EntryAirLocal;
        /// <summary>The structure's damage this evaluation (StructureLoads.Damage, unless held after a break: _stBreakHold).</summary>
        public bool StructDamage;

        /// <summary>Back to a new snapshot's values, every field (2026-10-15: one snapshot per grid, reused - the evaluation
        /// that read the last one is done before the next is taken, TickStructureCore; a new one and its loads' nine lists
        /// were made 4 times a second). A field added here or to StructureEval.Loads is reset here too.</summary>
        public void Reset()
        {
            L.Clear(); L.AeroCom = L.AirVel = Vector3.Zero; L.LiftScale = 1f; L.CellSize = 0.25f; L.CellOffset = 0f;
            Density = AirT = Dt = 0f; Piloted = false; SoS = Weight = 0f; Up = Vector3.Zero;
            HasTarget = false; Target = Orient = default; Reach = SurfPush = SurfPull = SurfNeutral = Airframe = Vector3.Zero;
            Integrity = null; EntryDensity = EntryAirT = 0f; EntryAirLocal = Vector3.Zero; StructDamage = false;
        }
    }

    sealed class StructBuildInput
    {
        public List<int> BoxBlock = new(); public List<Vector3I> Lo = new(), Hi = new();
        public List<float> Mass = new(); public List<int> Mat = new(); public List<string> Names = new();
        /// <summary>The blocks' mount points in grid cells: block, outward direction (grid axes, unit), the cells' box.</summary>
        public List<(int block, Vector3I dir, Vector3I lo, Vector3I hi)> Mounts = new();
        public List<CubeBlockComponent> Blocks = new();
        public float BlockSize;
    }

    ShipStructure _st; HullHeat _hh;
    /// <summary>The scorch spots (HeatScorch): the working list (this thread), and the copy published to the client.</summary>
    internal readonly List<HeatScorch.Spot> ScorchSpots = new();
    internal List<HeatScorch.Spot> ScorchPublished; internal int ScorchVersion;
    float _scorchClock;
    List<CubeBlockComponent> _stBlocks;
    Dictionary<Entity, int> _stIndexOf;
    AeroWork.Item _stJob;
    StructSnap _stSnap, _stSnapBuf;
    StructBuildInput _stBuildIn;
    ShipStructure _stNew; HullHeat _hhNew; List<CubeBlockComponent> _stBlocksNew;
    readonly List<(int block, float fraction, bool destroy)> _stDamage = new();
    float _stClock, _stCooldown;
    long _stEvalAt;
    string _stError;

    /// <summary>The worst joint's utilisation (1: yield), the hottest skin (K), the hottest skin's share of the way
    /// from softening to burning (0 cold, 1 burning) - the last evaluation's.</summary>
    internal float StructMaxUtil, StructMaxSkinK, StructHeatShare;
    internal string StructWorst = "-";

    static PropertyInfo _defProp;
    // (the per-definition caches hold their definitions weakly: an unloaded session's definitions are not kept alive)
    static readonly System.Runtime.CompilerServices.ConditionalWeakTable<object, object> _defMass = new();

    /// <summary>A block's mass (kg): its definition's Mass, by reflection (CubeBlockComponent.Definition's base type is
    /// in VRage.Game: CS0012 for mod code), once per definition; without it, light armour's 1049 kg per 2.5 m cube by
    /// volume.</summary>
    static float BlockMass(CubeBlockComponent b, int cells)
    {
        try
        {
            _defProp ??= typeof(CubeBlockComponent).GetProperty("Definition", BindingFlags.Public | BindingFlags.Instance);
            var d = _defProp?.GetValue(b);
            if (d != null)
            {
                lock (_defMass)
                {
                    float m;
                    if (_defMass.TryGetValue(d, out var boxed)) m = (float)boxed;
                    else
                    {
                        var mp = d.GetType().GetProperty("Mass", BindingFlags.Public | BindingFlags.Instance);
                        m = mp?.GetValue(d) is float f && f > 0f ? f : -1f;
                        _defMass.Add(d, m);
                    }
                    if (m > 0f) return m;   // (-1 cached: no readable mass - by volume below, every time)
                }
            }
        }
        catch { }
        return 1048.6f * cells / 1000f;
    }

    /// <summary>The server's step (AeroSimJob, after the air's forces are applied). forceLocal / torqueLocal: the air's
    /// applied force and torque (grid-local; torque about LastCoM); airVelLocal: the air past the ship (grid-local).</summary>
    internal void TickStructure(float dt, Vector3 forceLocal, Vector3 torqueLocal, Vector3 airVelLocal, float liftScale)
    {
        if (!StructureLoads.Enabled || !IsServerScene || _octree == null) return;
        if (_hh != null && (_scorchClock += dt) >= 0.25f) { HeatScorch.Update(this, _hh, airVelLocal, _scorchClock); _scorchClock = 0f; }   // (re-entry scorch dose: HeatScorch)
        CheckCutSeparated();   // (a failed cut's separation watched: CutDone)
        AccumulateContacts(dt, forceLocal);   // (every step: the physics' contacts are this step's only)
        // (the common case - between evaluations, nothing in the background: two adds and a compare, untimed)
        _stClock += dt; _stCooldown -= dt;
        if (_stJob == null && _stClock < _stNextAt) { StructureLoads.CountIdle(); return; }
        long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
        try { TickStructureCore(forceLocal, torqueLocal, airVelLocal, liftScale); }
        finally { StructureLoads.AddSim(System.Diagnostics.Stopwatch.GetTimestamp() - t0); StructureLoads.Publish(); }
    }

    /// <summary>The clock at which this grid is looked at next (s since its last evaluation).</summary>
    float _stNextAt;
    /// <summary>Until when (Stopwatch ticks) someone is flying this grid (AeroApi.SetFocusGrid).</summary>
    internal long StructFocusUntil;
    float[] _stInteg; float _stIntegAge;

    /// <summary>Bumped by every block change (AeroGridComponent.OnBlocksChangedCore): the structure is rebuilt when it moved.</summary>
    internal int StructBlockVersion;
    int _stVersion = -1;

    void TickStructureCore(Vector3 forceLocal, Vector3 torqueLocal, Vector3 airVelLocal, float liftScale)
    {
        // the last evaluation done: its results in (damage on this thread)
        if (_stJob != null)
        {
            if (!_stJob.IsCompleted) return;
            var job = _stJob; _stJob = null;
            if (job.IsFaulted) { _stError = job.Error?.Message; Log.Default?.Info($"[AERO-STRUCT] grid {Entity?.DebugName}: evaluation failed: {_stError}"); _stCooldown = 10f; }
            if (_stNew != null) { _st = _stNew; _hh = _hhNew; _stBlocks = _stBlocksNew; _stNew = null; _hhNew = null; _stBlocksNew = null; RebuildIndex(); _goneBlocks.Clear(); }
            ApplyStructureDamage();
            ApplyBends();
        }
        float fast = 1f / MathF.Max(StructureLoads.Rate, 0.1f);
        _stNextAt = fast;
        if (_stClock < fast) return;
        // only a grid something is happening to: piloted, moving through air, turning, thrusting, or still hot - a parked
        // or drifting cold grid costs a few compares a tick and nothing in the background
        // (flown: someone seated in it - AeroApi.SetFocusGrid, the Orbital mod's seat - or steering it by its target)
        bool piloted = System.Diagnostics.Stopwatch.GetTimestamp() < StructFocusUntil || Data.Has<Keen.Game2.Simulation.WorldObjects.Movement.TargetControlData>();
        // ONLY PLAYERS' GRIDS (2026-10-13, the user: "only structural failure for player grids"): the grid a player is
        // flying (AeroApi.SetFocusGrid from their seat, kept FocusLinger s after they leave it; or steered by its target) -
        // AI ships, wrecks and parked grids are never evaluated, never damaged
        bool active = piloted;
        // (idle: looked at again in a second; active but not piloted: at the idle rate)
        float slow = 1f / MathF.Max(StructureLoads.IdleRate, 0.05f);
        if (!active) { _stClock = MathF.Min(_stClock, 2f); _stNextAt = _stClock + 1f; return; }
        if (!piloted && _stClock < slow) { _stNextAt = slow; return; }
        if (System.Threading.Volatile.Read(ref StructureLoads.InFlight) >= StructureLoads.MaxInFlight) return;   // (next tick)
        float evalDt = MathF.Min(_stClock, 2f); _stClock = 0f;

        // the blocks: a rebuild when they changed (after the cooldown)
        StructBuildInput build = null;
        if ((StructBlockVersion != _stVersion || _st == null) && _stCooldown <= 0f)
        {
            _stVersion = StructBlockVersion; _stCooldown = StructureLoads.RebuildCooldown;
            build = SnapshotBlocks();
            if (build.Blocks.Count == 0) build = null;
        }
        if (_st == null && build == null) return;

        // NO ZIPPER (2026-10-15): after a cut failed, no structure damage until a rebuild from blocks changed since (what left
        // gone; StructBlockVersion moved past the failure's) - or BreakHoldMax s without one. The old structure still holds the
        // departed piece: its load would run on through the next cut along the span and break it in turn
        bool structDamage = StructureLoads.Damage;
        if (_stBreakHold > 0f)
        {
            if (build != null && _stVersion != _stHoldVersion) _stBreakHold = 0f;   // (this evaluation solves what is left)
            else { _stBreakHold -= evalDt; structDamage = false; StructureLoads.BreakHeld++; }
        }

        // what loads it, now (the grid's one snapshot, reset - the last evaluation is done: _stJob above)
        var snap = _stSnapBuf ??= new StructSnap();
        snap.Reset();
        snap.Dt = evalDt; snap.Density = LastDensity; snap.Piloted = piloted; snap.StructDamage = structDamage;
        snap.AirT = _atmosphereBridge != null && LastDensity > 0f ? (float)_atmosphereBridge.GetState(LastDensity).Temperature : 3f;
        var L = snap.L;
        L.AeroF = forceLocal; L.AeroT = torqueLocal; L.AeroCom = LastCoM; L.AirVel = airVelLocal; L.LiftScale = liftScale; L.AngVel = LastAngVel;
        TakeContacts(L);   // (the window's collisions and ground contact, as forces)
        // (the stall warning's: the speed of sound, which way is up, the weight)
        snap.SoS = HasResult && LastResult.Mach > 0.01f ? (float)(LastSpeed / LastResult.Mach) : 340f;
        // (a railed re-entry: the Orbital mod's state - the travel, density, the band's temperature, the frame's braking.
        //  The temperature is the hull's ambient there: on rails LastDensity is 0 and AirT above was 3 K, a vacuum's)
        if (OrbitalLink.TryGetEntryState2(Entity, out var entryAir, out float entryRho, out float entryT, out var entryBrake))
        {
            var ewt = Data.GetWorldTransform();
            snap.EntryDensity = entryRho; snap.EntryAirLocal = Keen.VRage.Core.WorldTransform.TransformDirectionInv(entryAir, ewt);
            snap.EntryAirT = entryT > 0f ? entryT : _atmosphereBridge != null && entryRho > 0f ? (float)_atmosphereBridge.GetState(entryRho).Temperature : 0f;
            if (StructureEval.EntryLoads) L.EntryAccel = Keen.VRage.Core.WorldTransform.TransformDirectionInv(entryBrake, ewt);
        }
        snap.Up = GroundUpLocal;
        snap.Weight = LastMass * PhysicsHack.GetGravityDirection(Data).Length();
        // (the predicted loss of control's: the commanded attitude, the ship's now, the actuators' reach)
        snap.HasTarget = LoacHasTarget; snap.Target = LoacTarget; snap.Orient = Data.GetWorldTransform().Orientation;
        snap.Reach = LoacReach; snap.SurfPush = SurfacePush; snap.SurfPull = SurfacePull; snap.SurfNeutral = SurfaceNeutral; snap.Airframe = LastAeroTorque;
        // (the gyros: our attitude law's torque, or - the game flying them - the game's own this frame)
        L.GyroT = LastGyroAware ? LastGyroTorque : GyroControl.GameImpulse(Data) / AeroPhysics.Dt;
        (L.CellSize, L.CellOffset) = FxCellGeo;
        var wings = _model?.Wings; var wf = _model?.LastWingForces;
        if (wings != null && wf != null && wf.Count == wings.Count)
            for (int i = 0; i < wings.Count; i++) { if (wings[i].Cells == null) continue; L.WingCells.Add(wings[i].Cells); L.WingF.Add((wf[i].LiftForce + wf[i].InducedDrag) * _model.WingRemaining(i)); }
        var blocks = build?.Blocks ?? _stBlocks;
        var index = build != null ? IndexOf(build.Blocks) : _stIndexOf;
        if (index != null)
        {
            var shares = LastThrustShares; var game = LastThrustGame; int ns = LastThrustCount;
            for (int i = 0; i < _thrusterCache.Count && i < ns && shares != null && i < shares.Length; i++)
            {
                var t = _thrusterCache[i];
                if (t.ThrusterEntity == null || !index.TryGetValue(t.ThrusterEntity, out int bi)) continue;
                L.ThrBlock.Add(bi); L.ThrF.Add(t.ThrustDirection * shares[i]);
                L.ThrF0.Add(t.ThrustDirection * (game != null && i < game.Length ? game[i] : shares[i]));
            }
            foreach (var g in _gyroCache) if (g.GyroEntity != null && index.TryGetValue(g.GyroEntity, out int gi)) L.GyroBlock.Add(gi);
        }
        // the blocks' health: every IntegrityEvery s (and after a rebuild) - a pass over every block, the costliest part
        // of the snapshot; between, the last
        _stIntegAge += evalDt;
        int nb = blocks?.Count ?? 0;
        if (_stInteg == null || _stInteg.Length != nb || build != null || _stIntegAge >= StructureLoads.IntegrityEvery)
        {
            _stIntegAge = 0f;
            // (its array kept while the block count stands: only the evaluation reads it, and that one is done - 17 KB a pass
            //  on a 4400-block ship, every 2 s, 2026-10-15)
            var integ = _stInteg != null && _stInteg.Length == nb ? _stInteg : new float[nb];
            for (int i = 0; i < nb; i++) { var b = blocks[i]; integ[i] = b != null ? Math.Clamp(b.HealthIntegrity, 0f, 1f) : 0f; }
            _stInteg = integ;
        }
        snap.Integrity = _stInteg;
        _stSnap = snap; _stBuildIn = build;
        var st = _st; var hh = _hh;
        System.Threading.Interlocked.Increment(ref StructureLoads.InFlight);
        StructureLoads.Evaluated();
        _stJob = AeroWork.Enqueue(() => 0.05f, () =>
        {
            long tb = System.Diagnostics.Stopwatch.GetTimestamp();
            try { EvaluateStructure(st, hh, build, snap); }
            finally { System.Threading.Interlocked.Decrement(ref StructureLoads.InFlight); StructureLoads.AddBg(System.Diagnostics.Stopwatch.GetTimestamp() - tb); }
        });
    }

    /// <summary>Any thruster pushing this frame (the attitude controller's shares).</summary>
    bool Thrusting()
    {
        var s = LastThrustShares; int n = LastThrustCount;
        if (s == null) return false;
        for (int i = 0; i < n && i < s.Length; i++) if (s[i] > 1f) return true;
        return false;
    }

    Dictionary<Entity, int> IndexOf(List<CubeBlockComponent> blocks)
    {
        var d = new Dictionary<Entity, int>();
        for (int i = 0; i < blocks.Count; i++) { var e = blocks[i]?.Entity; if (e != null) d[e] = i; }
        return d;
    }

    void RebuildIndex() => _stIndexOf = _stBlocks != null ? IndexOf(_stBlocks) : null;

    /// <summary>The blocks, for a rebuild (simulation thread: the octree is the game's): cells, mass, material.</summary>
    StructBuildInput SnapshotBlocks()
    {
        var b = new StructBuildInput { BlockSize = _blockSize > 0 ? _blockSize : AeroConfig.LargeBlockSize };
        foreach (var block in _octree.GetAllCubeBlocks())
        {
            if (block == null) continue;
            int idx = b.Blocks.Count, cells = 0;
            foreach (var g in block.GetTransformedOccupiedCellGroups())
            {
                b.BoxBlock.Add(idx); b.Lo.Add(g.Min); b.Hi.Add(g.Max);
                cells += (g.Max.X - g.Min.X + 1) * (g.Max.Y - g.Min.Y + 1) * (g.Max.Z - g.Min.Z + 1);
            }
            if (cells == 0) continue;
            b.Blocks.Add(block);
            b.Mass.Add(BlockMass(block, cells));
            string name = BlockName(block);
            b.Names.Add(name);
            AddMounts(block, idx, b.Mounts);
            b.Mat.Add(StructMaterial.Classify(name));
        }
        if (StructureLoads.DumpNext) { StructureLoads.DumpNext = false; DumpStructure(b); }
        return b;
    }

    /// <summary>
    /// A block's MOUNT POINTS in grid cells (2026-10-14): the faces it attaches by - generated by the game from its model
    /// (flush flat faces, 0.25 m cells) and what it decides placement and connectivity (grid splits) by. Per definition the
    /// block-local groups are read once (CubeBlockDefinition.MountPointsGroupsPerDirection, by reflection: CS0012) and
    /// cached; per block rotated into the grid as the game does (CubeGridCoords.MakeBlockToGridMatrix of its relative
    /// transform; the direction by its orientation). Rebuilds only.
    /// </summary>
    static void AddMounts(CubeBlockComponent b, int idx, List<(int block, Vector3I dir, Vector3I lo, Vector3I hi)> outMounts)
    {
        try
        {
            _defProp ??= typeof(CubeBlockComponent).GetProperty("Definition", BindingFlags.Public | BindingFlags.Instance);
            var d = _defProp?.GetValue(b);
            if (d == null) return;
            (Vector3I dir, Vector3I lo, Vector3I hi)[] local;
            lock (_defMounts)
            {
                if (!_defMounts.TryGetValue(d, out local))
                {
                    var list = new List<(Vector3I, Vector3I, Vector3I)>();
                    var mp = d.GetType().GetProperty("MountPointsGroupsPerDirection", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                    if (mp?.GetValue(d) is Keen.VRage.Library.Collections.Readers.ListDictionaryReader<Base6Directions.Direction, Keen.Game2.Simulation.WorldObjects.CubeBlocks.MountPointsGroupData> groups)
                        foreach (var kv in groups)
                            foreach (var g in kv.Value)
                                list.Add((kv.Key.GetIntVector(), g.GroupBoundingBox.Min, g.GroupBoundingBox.Max));
                    local = list.ToArray();
                    _defMounts.Add(d, local);
                }
            }
            if (local.Length == 0 || b.Entity == null || !b.Entity.Data.TryGet<Keen.VRage.Core.RelativeTransform>(out var rt)) return;
            var m = Keen.Game2.Simulation.WorldObjects.CubeGrids.CubeGridCoords.MakeBlockToGridMatrix(rt);
            foreach (var (dir, lo, hi) in local)
            {
                var a = Vector3I.Transform(lo, in m); var c = Vector3I.Transform(hi, in m);
                var gd = Vector3I.Round(rt.Orientation * (Vector3)dir);
                outMounts.Add((idx, gd, Vector3I.Min(a, c), Vector3I.Max(a, c)));
            }
        }
        catch (Exception e) { if (!_mountsWarned) { _mountsWarned = true; Log.Default?.Warning($"[AERO-STRUCT] mount points unreadable ({e.GetType().Name}: {e.Message}) - joints by touching cells"); } }
    }
    static readonly System.Runtime.CompilerServices.ConditionalWeakTable<object, (Vector3I dir, Vector3I lo, Vector3I hi)[]> _defMounts = new();
    static bool _mountsWarned;

    /// <summary>A block's kind, for its material class: its DEFINITION's name (Definition.DebugName - the block type), not
    /// its entity's (2026-10-14: on some ships every entity is "CubeBlock_x:y:z" or "Root Entity" - every block a fitting,
    /// no armour, no thrusters: a 2.8 kt ship's structure absurd). The entity's name only when no definition is readable.</summary>
    static string BlockName(CubeBlockComponent b)
    {
        try
        {
            _defProp ??= typeof(CubeBlockComponent).GetProperty("Definition", BindingFlags.Public | BindingFlags.Instance);
            var d = _defProp?.GetValue(b);
            if (d != null)
            {
                lock (_defName)
                {
                    if (!_defName.TryGetValue(d, out var n))
                    {
                        var dn = d.GetType().GetProperty("DebugName", BindingFlags.Public | BindingFlags.Instance)?.GetValue(d) as string;
                        n = string.IsNullOrEmpty(dn) ? "" : System.IO.Path.GetFileNameWithoutExtension(dn.Replace('\\', '/'));
                        _defName.Add(d, n);
                    }
                    if (n.Length > 0) return n;   // ("" cached: no readable name - the entity's below)
                }
            }
        }
        catch { }
        // (no readable definition name: the definition's type, never the entity's own name - "Root Entity" for many blocks)
        try { var d = _defProp?.GetValue(b); if (d != null) return d.GetType().Name; } catch { }
        return "block";
    }
    static readonly System.Runtime.CompilerServices.ConditionalWeakTable<object, string> _defName = new();

    /// <summary>(harness: aeroset StructureLoads.DumpLoadsAbove U) the first evaluation over utilisation U: its loads, for the
    /// offline bench to replay on the .struct dumped with DumpNext (same block indices while the grid is not rebuilt).</summary>
    void DumpLoads(StructureEval.Loads L, float util)
    {
        try
        {
            var ci = System.Globalization.CultureInfo.InvariantCulture;
            string V(Vector3 v) => string.Format(ci, "{0:R} {1:R} {2:R}", v.X, v.Y, v.Z);
            var sb = new System.Text.StringBuilder();
            sb.Append("# loads of '").Append(Entity?.DebugName).Append("' at utilisation ").Append(util.ToString(ci)).Append('\n');
            sb.Append("aeroF ").Append(V(L.AeroF)).Append('\n').Append("aeroT ").Append(V(L.AeroT)).Append('\n').Append("aeroCom ").Append(V(L.AeroCom)).Append('\n');
            sb.Append("airVel ").Append(V(L.AirVel)).Append('\n').Append("gyroT ").Append(V(L.GyroT)).Append('\n').Append("angVel ").Append(V(L.AngVel)).Append('\n');
            sb.Append("scale ").Append(L.LiftScale.ToString(ci)).Append(' ').Append(L.CellSize.ToString(ci)).Append(' ').Append(L.CellOffset.ToString(ci)).Append('\n');
            for (int w = 0; w < L.WingCells.Count; w++)
            {
                sb.Append("wing ").Append(V(L.WingF[w]));
                foreach (var c in L.WingCells[w]) sb.Append(' ').Append(c.X).Append(' ').Append(c.Y).Append(' ').Append(c.Z);
                sb.Append('\n');
            }
            for (int k = 0; k < L.ThrBlock.Count; k++) sb.Append("thr ").Append(L.ThrBlock[k]).Append(' ').Append(V(L.ThrF[k])).Append('\n');
            foreach (int b in L.GyroBlock) sb.Append("gyro ").Append(b).Append('\n');
            string dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "AeroMod");
            System.IO.Directory.CreateDirectory(dir);
            string safe = string.Concat((Entity?.DebugName ?? "grid").Select(ch => char.IsLetterOrDigit(ch) || ch == '_' || ch == '-' ? ch : '_'));
            string path = System.IO.Path.Combine(dir, "struct_" + safe + ".loads");
            System.IO.File.WriteAllText(path, sb.ToString());
            StructureLoads.LastDump = path;
            Log.Default?.Info($"[AERO-STRUCT] dumped the loads at utilisation {util:F2} to {path}");
        }
        catch (Exception e) { StructureLoads.LastDump = "loads failed: " + e.Message; }
    }

    /// <summary>(harness: aeroset StructureLoads.DumpNext true) the next rebuilt player grid's blocks, as the offline bench's
    /// .struct file (Tests/data/struct: boxes in cells, block, mass, kind) in %TEMP%/AeroMod.</summary>
    void DumpStructure(StructBuildInput b)
    {
        try
        {
            var sb = new System.Text.StringBuilder();
            float total = 0f; foreach (var m in b.Mass) total += m;
            sb.Append("# blockSize ").Append(b.BlockSize.ToString(System.Globalization.CultureInfo.InvariantCulture)).Append(" mass ").Append(total.ToString("F0", System.Globalization.CultureInfo.InvariantCulture)).Append(" blocks ").Append(b.Blocks.Count).Append('\n');
            sb.Append("# from game grid '").Append(Entity?.DebugName).Append("'\n");
            for (int i = 0; i < b.BoxBlock.Count; i++)
            {
                int k = b.BoxBlock[i];
                var lo = b.Lo[i]; var hi = b.Hi[i];
                sb.Append(lo.X).Append(' ').Append(lo.Y).Append(' ').Append(lo.Z).Append(' ').Append(hi.X).Append(' ').Append(hi.Y).Append(' ').Append(hi.Z).Append(' ')
                  .Append(k).Append(' ').Append(b.Mass[k].ToString("F1", System.Globalization.CultureInfo.InvariantCulture)).Append(' ')
                  .Append((b.Names[k] ?? "?").Replace(' ', '_')).Append('\n');
            }
            // mount points: "m block dx dy dz loX loY loZ hiX hiY hiZ" (grid cells, the outward direction)
            foreach (var (k, mdir, lo, hi) in b.Mounts)
                sb.Append("m ").Append(k).Append(' ').Append(mdir.X).Append(' ').Append(mdir.Y).Append(' ').Append(mdir.Z).Append(' ')
                  .Append(lo.X).Append(' ').Append(lo.Y).Append(' ').Append(lo.Z).Append(' ').Append(hi.X).Append(' ').Append(hi.Y).Append(' ').Append(hi.Z).Append('\n');
            string dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "AeroMod");
            System.IO.Directory.CreateDirectory(dir);
            string safe = string.Concat((Entity?.DebugName ?? "grid").Select(c => char.IsLetterOrDigit(c) || c == '_' || c == '-' ? c : '_'));
            string path = System.IO.Path.Combine(dir, "struct_" + safe + ".struct");
            System.IO.File.WriteAllText(path, sb.ToString());
            StructureLoads.LastDump = path;
            Log.Default?.Info($"[AERO-STRUCT] dumped {b.Blocks.Count} blocks to {path}");
        }
        catch (Exception e) { StructureLoads.LastDump = "failed: " + e.Message; }
    }

    /// <summary>The background evaluation (one at a time per grid): a rebuild if asked, the heat, the loads, the solve,
    /// the damage to do.</summary>
    void EvaluateStructure(ShipStructure st, HullHeat hh, StructBuildInput build, StructSnap snap)
    {
        long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
        if (build != null)
        {
            var nst = ShipStructure.Build(build.BoxBlock, build.Lo, build.Hi, build.Mass, build.Mat, build.Mounts);
            var nhh = BuildHeat(nst, build, snap);
            if (hh != null && st != null) CarryHeat(st, hh, nst, nhh);
            st = nst; hh = nhh;
            _stNew = nst; _hhNew = nhh; _stBlocksNew = build.Blocks;
            StructureLoads.Builds++;
            StructureLoads.LastBuildMs = (float)System.Diagnostics.Stopwatch.GetElapsedTime(t0).TotalMilliseconds;
            // (the classes the blocks' names gave: armour, fittings, heat shields, trim, hydrogen / atmospheric / ion thrusters, mounts)
            var cls = new int[StructMaterial.Count]; foreach (int m in build.Mat) cls[m]++;
            StructureLoads.LastBuild = $"{Entity?.DebugName}: {nst.Count} blocks (armour {cls[0]}, fittings {cls[1]}, shields {cls[2]}, trim {cls[3]}, thrusters {cls[4] + cls[5] + cls[6]}, mounts {cls[7]}), {nhh.FaceCount} faces, {nst.TotalMass / 1000f:F1} t (physics {LastMass / 1000f:F1} t) in {StructureLoads.LastBuildMs:F0} ms";
            Log.Default?.Info($"[AERO-STRUCT] built {StructureLoads.LastBuild}");
            t0 = System.Diagnostics.Stopwatch.GetTimestamp();
        }
        if (st == null || hh == null) return;
        int n = st.Count;
        // health: the game's, through the fatigue ledger (its loss not yet written - StructureFatigue; SyncHealth)
        SyncHealth(st, build, snap);
        // the heat, for the time since the last evaluation
        var L = snap.L;
        float speed = L.AirVel.Length();
        Vector3 heatAir = L.AirVel; float heatRho = snap.Density, airT = snap.AirT;
        // A RAILED RE-ENTRY (2026-10-14): the Orbital mod flies the entry on rails - the ship's physical speed capped - so
        // the hull never heated in it, and hot structure never weakened (StrengthAt) nor burnt. Its entry STATE (the user:
        // "use an orbital api instead of some glow"): the TRUE air velocity, density and the band's temperature there - the
        // heat steps at those (the ambient too: it was 3 K on rails). Braked with no density (the cap rule alone): no
        // heating, but the faces' pass still along the travel - the braking's load (L.EntryAccel) is spread by it.
        if ((snap.EntryDensity > 0f || L.EntryAccel.LengthSquared() > 0f) && snap.EntryAirLocal.LengthSquared() > 1f)
        {
            float trueSpeed = snap.EntryAirLocal.Length();
            if (trueSpeed > speed) { speed = trueSpeed; heatAir = snap.EntryAirLocal; heatRho = snap.EntryDensity; if (snap.EntryAirT > 0f) airT = snap.EntryAirT; }
        }
        if (speed > 1f && hh.NeedsPass(heatAir)) hh.Pass(heatAir);
        hh.Step(heatRho, airT, speed, heatRho > 1e-6f ? airT : 3f, MathF.Min(snap.Dt, 2f));
        Array.Copy(hh.BlockTempK, st.TempK, n);
        // the loads, the solve (each piece free on them), the damage to do (Structure/StructureEval: offline-simulated)
        StructureEval.Apply(st, hh, L);
        st.SolveFree(L.AngVel);
        UpdateSteerCap(st, hh, L, snap.Dt);
        if (StructureLoads.DumpLoadsAbove > 0f && st.MaxUtil > StructureLoads.DumpLoadsAbove) { StructureLoads.DumpLoadsAbove = -1f; DumpLoads(L, st.MaxUtil); }
        // (BY SECTION, 2026-10-15: whole cuts wear and fail - _stCuts the failed ones, their members' entries together)
        StructureEval.Damage(st, hh, snap.Dt, snap.StructDamage, StructureLoads.HeatDamage, StructureLoads.BreakUtil, StructureLoads.WearPerSecond, StructureLoads.MaxBreaksPerEval, _stDamage, _stWhere, _stCuts);
        // (PERMANENT BENDS, 2026-10-14: a worn cut past its last peak sets the structure outboard of it - Structure/StructureBend;
        //  applied as bone moves on the simulation thread, ApplyBends)
        if (snap.StructDamage && StructureBend.Enabled && _stDamage.Count > 0) StructureBend.Plan(st, _stDamage, _stWhere, _bendPeaks, _stBends);
        else _stBends.Clear();
        var com = st.Com;
        EvaluateStall(snap);
        EvaluateLoac(snap);
        // the gauges
        float maxSkin = hh.MaxSkin(out int hf), share = 0f;
        for (int b = 0; b < n; b++) { int m = st.Mat[b]; float s = (hh.BlockSkinMaxK[b] - StructMaterial.SoftenK[m]) / (StructMaterial.BurnK[m] - StructMaterial.SoftenK[m]); if (s > share) share = s; }
        StructMaxUtil = st.MaxUtil; StructMaxSkinK = maxSkin; StructHeatShare = Math.Clamp(share, 0f, 1f);
        StructWorst = st.MaxContact >= 0 ? $"contact {st.MaxContact} at {st.ContactPoint(st.MaxContact) - com}" : st.MaxBlock >= 0 ? $"block {st.MaxBlock} at {st.Center[st.MaxBlock] - com}" : "-";
        StructureLoads.Evals++;
        StructureLoads.LastEvalMs = (float)System.Diagnostics.Stopwatch.GetElapsedTime(t0).TotalMilliseconds;
        if (st.MaxUtil > StructureLoads.SeenMaxUtil) { StructureLoads.SeenMaxUtil = st.MaxUtil; StructureLoads.LastWorst = $"{Entity?.DebugName}: {st.MaxUtil:F2} {StructWorst}"; }
        if (maxSkin > StructureLoads.SeenMaxSkin) StructureLoads.SeenMaxSkin = maxSkin;
        if (StructureLoads.Trace && snap.Piloted)
        {
            Vector3 thr = Vector3.Zero; foreach (var f in L.ThrF) thr += f;
            float g = st.TotalMass > 0f ? L.AeroF.Length() / (st.TotalMass * 9.81f) : 0f;
            Log.Default?.Info($"[AERO-STRUCT-T] {Entity?.DebugName}: util {st.MaxUtil:F3} at {StructWorst} | skin {maxSkin:F0} K heat {StructHeatShare:F2} | air {L.AeroF.Length() / 1000f:F0} kN ({g:F1} g) wings {L.WingCells.Count} thrust {thr.Length() / 1000f:F0} kN ({L.ThrBlock.Count}) gyro {L.GyroT.Length() / 1000f:F0} kNm ({L.GyroBlock.Count}) w {L.AngVel.Length():F2} v {L.AirVel.Length():F0} rho {snap.Density:F3} | {StructureLoads.LastEvalMs:F1} ms, {st.Count} blocks, damage {_stDamage.Count}");
        }
    }

    /// <summary>The hull's faces for the heat model: its own surface of the blocks' cells (the flight surface is let go
    /// on big grids once their table is in).</summary>
    static HullHeat BuildHeat(ShipStructure st, StructBuildInput build, StructSnap snap)
    {
        var boxes = new List<(Vector3I, Vector3I)>(build.Lo.Count);
        for (int k = 0; k < build.Lo.Count; k++) boxes.Add((build.Lo[k], build.Hi[k]));
        int kk = SnapshotGridAccessor.CellScale(build.BlockSize);
        var (cs, co) = SnapshotGridAccessor.CellGeometry(kk);
        var grid = kk <= 1 ? new SnapshotGridAccessor(boxes) : new SnapshotGridAccessor(boxes).CoarsenMajority(kk);
        var surf = new SmoothSurfaceProvider { CellSize = cs, CellOffset = co };
        surf.BeginBuild(grid, build.BlockSize);
        while (!surf.AddCellBatch(int.MaxValue)) { }
        surf.FinalizeBuild();
        var pos = new List<Vector3>(surf.FaceCount); var nrm = new List<Vector3>(surf.FaceCount); var area = new List<float>(surf.FaceCount); var blk = new List<int>(surf.FaceCount);
        foreach (var f in surf.Faces)
        {
            var raw = f.RawNormal.LengthSquared() > 0.5f ? f.RawNormal : f.Normal;
            pos.Add(f.Position); nrm.Add(f.Normal); area.Add(f.Area); blk.Add(st.BlockAt(f.Position - raw * (cs * 0.25f)));
        }
        return HullHeat.Build(st, pos, nrm, area, blk, cs);
    }

    /// <summary>The heat carried into a rebuilt structure: each face by its place and normal, each block's core by its
    /// first cell.</summary>
    static void CarryHeat(ShipStructure oldS, HullHeat oldH, ShipStructure newS, HullHeat newH)
    {
        static long FaceKey(Vector3 p, Vector3 n) => ((long)MathF.Round(p.X * 8) & 0xFFFFF) << 40 | ((long)MathF.Round(p.Y * 8) & 0xFFFFF) << 20 | ((long)MathF.Round(p.Z * 8) & 0xFFFFF) ^ ((long)MathF.Round(n.X * 2 + 2) * 7 + (long)MathF.Round(n.Y * 2 + 2) * 3 + (long)MathF.Round(n.Z * 2 + 2)) << 61;
        var faces = new Dictionary<long, float>();
        for (int i = 0; i < oldH.FaceCount; i++) faces[FaceKey(oldH.FacePosition(i), oldH.FaceNormal(i))] = oldH.FaceT[i];
        for (int i = 0; i < newH.FaceCount; i++) if (faces.TryGetValue(FaceKey(newH.FacePosition(i), newH.FaceNormal(i)), out float t)) newH.FaceT[i] = t;
        var cores = new Dictionary<long, float>();
        static long BlockKey(Vector3 lo) => ((long)MathF.Round(lo.X * 8) & 0xFFFFF) << 40 | ((long)MathF.Round(lo.Y * 8) & 0xFFFFF) << 20 | ((long)MathF.Round(lo.Z * 8) & 0xFFFFF);
        for (int b = 0; b < oldS.Count; b++) cores[BlockKey(oldS.Lo[b])] = oldH.CoreT[b];
        for (int b = 0; b < newS.Count; b++) if (cores.TryGetValue(BlockKey(newS.Lo[b]), out float t)) newH.CoreT[b] = t;
    }

    readonly List<(Vector3 at, Vector3 dir, float util)> _stWhere = new();
    /// <summary>The last evaluation's failed cuts (StructureEval.CutFail: centre, way out, members' entries in _stDamage).</summary>
    readonly List<StructureEval.CutFail> _stCuts = new();
    /// <summary>NO ZIPPER (2026-10-15): seconds of evaluation the structure's damage still waits after a cut failed, and the
    /// block version it failed at (a rebuild from a later one ends the wait).</summary>
    float _stBreakHold; int _stHoldVersion;
    /// <summary>Overloaded blocks dent as they wear (DentPerUtil of a full dent per unit of utilisation over 1, at most DentMax).</summary>
    public static bool Dents = true;
    /// <summary>A wear step's dent (m) per unit of utilisation over 1 - added each step while it stays overloaded - and the most
    /// a bone is dented in all (m; the game clamps a 0.25 m armour block's bones to 0.25 m besides).</summary>
    public static float DentPerUtil = 0.02f, DentMax = 0.12f;
    /// <summary>Diagnostics: bones dented.</summary>
    public static int DentWrites;
    /// <summary>Each bone's total dent (m, grid axes) and the part under one bone step not yet written (bone units).</summary>
    readonly Dictionary<Vector3I, (Vector3 total, Vector3 frac)> _dentLedger = new();

    /// <summary>A worn armour block's dent: its bones (the 2x2x2 or so round its cells) pushed along the load's direction by
    /// (util - 1) x DentPerUtil, each bone's total capped at DentMax, the part under one bone step carried. Armour only - the
    /// bones move only the armour mesh.</summary>
    void Dent(CubeBlockComponent b, Vector3 dir, float util)
    {
        try
        {
            if (b.Entity.TryGet<Keen.Game2.Simulation.WorldObjects.CubeBlocks.Armors.ArmorBlockComponent>() == null) return;
            var bones = Entity?.TryGet<Keen.Game2.Simulation.WorldObjects.BoneStorage.BoneStorageComponent>();
            if (bones == null) return;
            float step = Math.Clamp((util - 1f) * DentPerUtil, 0f, DentMax);
            if (step <= 0f) return;
            Vector3 d = Vector3.Normalize(dir) * step, unit = new Vector3(127f / MathF.Max(BoneOffsetM, 1e-3f));
            var bb = b.AABB;
            var lo = bones.BlockToBone(bb.Min); var hi = bones.BlockToBone(bb.Max) + Vector3I.One;
            for (int x = lo.X; x <= hi.X; x++) for (int y = lo.Y; y <= hi.Y; y++) for (int z = lo.Z; z <= hi.Z; z++)
            {
                var bone = new Vector3I(x, y, z);
                _dentLedger.TryGetValue(bone, out var led);
                var total = led.total + d;
                float len = total.Length();
                if (len > DentMax) total *= DentMax / len;   // (capped: a dent, not a crater)
                var units = (total - led.total) * unit + led.frac;
                var q = new Vector3((int)units.X, (int)units.Y, (int)units.Z);   // (whole steps; the rest carried)
                _dentLedger[bone] = (total, units - q);
                if (q == Vector3.Zero) continue;
                bones.WriteBoneDelta(bone, new Keen.VRage.Library.Mathematics.Vector3B(q));
                DentWrites++;
            }
        }
        catch (Exception e) { Log.Default?.Info($"[AERO-STRUCT] dent failed: {e.Message}"); }
    }

    /// <summary>A fracture through a block from a point on it (grid-local) along a direction (grid-local), in the block's own
    /// frame - the game's FracturingParameters.LineRelativeToBlock, as its collisions build it. Null: none known.</summary>
    static Keen.Game2.Simulation.WorldObjects.CubeGrids.Damage.FracturingParameters? Fracture(CubeBlockComponent b, Keen.VRage.Core.WorldTransform gwt, Vector3 at, Vector3 dir)
    {
        if (!Fractures || dir.LengthSquared() < 0.5f) return null;
        try
        {
            var bwt = b.Entity.Data.GetWorldTransform();
            Vector3D world = Keen.VRage.Core.WorldTransform.Transform((Vector3D)at, in gwt);
            Vector3 local = (Vector3)Keen.VRage.Core.WorldTransform.TransformInv(world, bwt);
            Vector3 d = Keen.VRage.Core.WorldTransform.TransformDirectionInv(Keen.VRage.Core.WorldTransform.TransformDirection(dir, gwt), bwt);
            return new Keen.Game2.Simulation.WorldObjects.CubeGrids.Damage.FracturingParameters { LineRelativeToBlock = new Line(local, Vector3.Normalize(d), 1f) };
        }
        catch { return null; }
    }
    public static bool Fractures = true;

    /// <summary>THE FATIGUE LEDGER (Structure/StructureFatigue, 2026-10-14): this grid's health not yet written to its blocks
    /// (background: SyncHealth, the evaluation; simulation thread: ApplyStructureDamage - never at once).</summary>
    readonly StructureFatigue.Ledger _stHealth = new();
    readonly List<(int block, float loss)> _stDue = new();
    /// <summary>Each block's last failing joint (structure coordinates) and the way into it, for its health writes' fracture
    /// line (the pieces on the loaded side crack first); for the block list it was taken on.</summary>
    readonly Dictionary<int, (Vector3 at, Vector3 dir)> _stLoaded = new();
    List<CubeBlockComponent> _stLoadedFor;

    /// <summary>(background, the evaluation) The blocks' health into the structure: the game's (snap.Integrity, re-read every
    /// StructureLoads.IntegrityEvery s) less what the ledger holds unwritten - after a rebuild the ledger's blocks carried to
    /// their new places by entity (StructureFatigue.Ledger.Sync, Resize).</summary>
    void SyncHealth(ShipStructure st, StructBuildInput build, StructSnap snap)
    {
        int n = st.Count;
        if (build != null || _stHealth.Count != n)
        {
            int[] oldToNew = null;
            var oldIdx = _stIndexOf;
            if (build != null && oldIdx != null && _stHealth.Count > 0)
            {
                oldToNew = new int[_stHealth.Count]; Array.Fill(oldToNew, -1);
                for (int j = 0; j < build.Blocks.Count && j < n; j++)
                {
                    var e = build.Blocks[j]?.Entity;
                    if (e != null && oldIdx.TryGetValue(e, out int i) && i >= 0 && i < oldToNew.Length) oldToNew[i] = j;
                }
            }
            _stHealth.Resize(n, oldToNew);
        }
        _stHealth.Sync(snap.Integrity, st.Integrity, n, snap.Dt);
    }

    /// <summary>The last evaluation's damage (simulation thread). Rare work: guarded.
    /// PROGRESSIVE (2026-10-14, Structure/StructureFatigue): each entry's health loss goes into the ledger (_stHealth), and
    /// the game's blocks take it in STEPS (the ledger's TakeDue: 5% of a block's health, at most once a second a block, a
    /// settled remainder, a damage stage crossed) - each through the game's damage WITH a fracture line from the block's
    /// loaded joint into it, so its pieces on the loaded side crack first and its damage stages play on the way down; the
    /// game's damage sound once a step, not every evaluation (489 wears in a session were constant clanking). A block that
    /// FAILS (health out, or a snap) takes its pending at once and goes (FailBlock).
    /// BY SECTION (2026-10-15, StructureEval.DamageBySection): a whole CUT fails together - every member in this pass (split
    /// along the cut where it has pieces, destroyed otherwise, each through FailBlock and its Gone guards), the torn-edge smoke
    /// and the tear's joint once at the cut's centre (CutDone), then the structure's damage held until it is rebuilt without
    /// what left (_stBreakHold: no load run on through the next cut on the old blocks).</summary>
    void ApplyStructureDamage()
    {
        if (_stBlocks == null) { _stDamage.Clear(); return; }
        if (_stDamage.Count == 0 && _stHealth.Dirty.Count == 0) return;
        try
        {
            var recv = Entity?.TryGet<GridDamageReceiverComponent>();
            if (recv == null) return;
            var gwt = Entity.Data.GetWorldTransform();
            if (!ReferenceEquals(_stLoadedFor, _stBlocks)) { _stLoaded.Clear(); _stLoadedFor = _stBlocks; }   // (rebuilt: other indices)
            // (TEARING, 2026-10-14: a large piece this breaks off stays tied at the torn joint for StructureTear.TearSeconds -
            //  the split signal watched before any block goes, the failed joints kept for the split that follows)
            List<Vector3> torn = null;
            if (StructureTear.Enabled)
                for (int di = 0; di < _stDamage.Count; di++) if (_stDamage[di].destroy) { StructureTear.Watch(this); torn = new List<Vector3>(); break; }
            // (BY SECTION, 2026-10-15: a failed CUT's members are consecutive entries - all fail here, now; its torn-edge smoke
            //  once, at the cut's centre, out of it, and the tear's joint that centre - not one per member)
            int ci = 0, cutFailed = 0, version0 = StructBlockVersion;   // (the blocks' version before any of this pass's failures)
            for (int di = 0; di < _stDamage.Count; di++)
            {
                var (bi, fraction, destroy) = _stDamage[di];
                (Vector3 at, Vector3 dir, float util) where = di < _stWhere.Count ? _stWhere[di] : (Vector3.Zero, Vector3.Zero, 0f);
                while (ci < _stCuts.Count && di >= _stCuts[ci].First + _stCuts[ci].Count) CutDone(_stCuts[ci++], ref cutFailed, torn, version0);
                bool inCut = ci < _stCuts.Count && di >= _stCuts[ci].First;
                if (bi < 0 || bi >= _stBlocks.Count) continue;
                var b = _stBlocks[bi];
                // (a block gone since the evaluation - broken off by an earlier one in this list, split away: its data is
                //  gone and the receiver threw "Data not found" for it, losing the rest of the list, 2026-10-14)
                if (Gone(b)) { _stHealth.TakeAll(bi); continue; }
                if (where.dir.LengthSquared() > 0.5f) _stLoaded[bi] = (where.at, where.dir);
                _stHealth.Accrue(bi, fraction);
                if (destroy)
                {
                    // (one entry's fault - the game removing it under us - never loses the rest of the list)
                    try { FailBlock(recv, gwt, bi, b, where, torn, !inCut); } catch (Exception fe) { _goneBlocks.Add(b); Log.Default?.Info($"[AERO-STRUCT] fail skipped: {fe.Message}"); }
                    if (inCut) { cutFailed++; if (!Gone(b)) _cutSurvivors.Add(b); }
                    continue;
                }
                // (WORN visibly: a joint past its yield dents its block the way its load pushes, more the further over, every
                //  evaluation it stays over - the game's own deformation bones, written straight: Dent (DealDamage's
                //  deformation is scaled by the damage dealt: a wear step's rounded to nothing). Fatigue under the yield and a
                //  heat burn only lose health)
                if (Dents && where.util > 1f && where.dir.LengthSquared() > 0.5f) Dent(b, where.dir, where.util);
            }
            while (ci < _stCuts.Count) CutDone(_stCuts[ci++], ref cutFailed, torn, version0);
            if (torn != null && torn.Count > 0) StructureTear.Record(Entity, torn);
            // the health writes due (the ledger's steps)
            _stHealth.TakeDue(_stDue);
            foreach (var (bi, loss) in _stDue)
            {
                if (bi < 0 || bi >= _stBlocks.Count || !(loss > 0f)) continue;
                var b = _stBlocks[bi];
                if (Gone(b)) continue;
                float full = b.HealthIntegrity > 1e-3f ? b.AbsoluteHealth / b.HealthIntegrity : 0f;
                float hp = loss * full;
                if (!(hp > 0f)) continue;
                // (never under the game's destruction threshold here: a write that would remove the block is a failure's -
                //  FailBlock - not a wear step's)
                hp = MathF.Min(hp, MathF.Max(b.AbsoluteHealth - (StructureFatigue.BreakBelow + 0.01f) * full, 0f));
                if (!(hp > 0f)) continue;
                try { recv.DealDamage(b, hp, null, _stLoaded.TryGetValue(bi, out var ld) ? Fracture(b, gwt, ld.at, ld.dir) : null); }
                catch (Exception we) { _goneBlocks.Add(b); Log.Default?.Info($"[AERO-STRUCT] wear skipped: {we.Message}"); continue; }
                StructureLoads.Wears++;
            }
        }
        catch (Exception e) { Log.Default?.Info($"[AERO-STRUCT] damage failed: {e.Message}"); }
        _stDamage.Clear(); _stCuts.Clear();
    }

    /// <summary>A failed cut's members all handled (ApplyStructureDamage): if any of them went now, its torn-edge smoke once at
    /// its centre, out of it into what leaves (TearSmoke), the tear's joint there (StructureTear), and the structure's damage
    /// held until the structure is rebuilt without what left (NO ZIPPER: _stBreakHold) - from blocks changed since version0,
    /// the version before the pass (the game may count the removals at once). failed: its members that went (reset).</summary>
    // A FAILED CUT MUST SEPARATE (2026-10-15, the user: "solidly attached but not attached"): a member split rather than
    // destroyed (SplitFace: only the pieces at its failing face) can keep mount points across the cut on its other pieces -
    // the block still bridges it, the game never splits the grid, and the far side hangs on, visibly cut. So a cut whose
    // members survived is watched: no split of this grid (CubeGridComponent.SplitGridSpawnedSignal) within CutVerifyTicks,
    // and its surviving members are destroyed - the bridge gone, the game splits.
    public static int CutVerifyTicks = 30;
    public static int CutsForced;
    readonly List<CubeBlockComponent> _cutSurvivors = new();
    sealed class CutVerify { public List<CubeBlockComponent> Members; public int Stamp, Ticks, Axis; public Vector3 At; public float Plane; public Vector3I Lo, Hi; }
    readonly List<CutVerify> _cutVerify = new();
    int _splitStamp; bool _splitWatched;
    static Keen.Game2.Simulation.WorldObjects.CubeGrids.CubeGridComponent.SplitGridSpawnedSignal.ExternalApi _onSplitStamp;

    void WatchSplits()
    {
        if (_splitWatched || Entity == null) return;
        _splitWatched = true;
        try { _onSplitStamp ??= OnSplitStamp; Keen.Game2.Simulation.WorldObjects.CubeGrids.CubeGridComponent.SplitGridSpawnedSignal.Subscribe(Entity, _onSplitStamp); }
        catch (Exception e) { Log.Default?.Info($"[AERO-STRUCT] split watch failed: {e.Message}"); }
    }
    static void OnSplitStamp(Keen.VRage.DCS.Components.Entity original, Keen.VRage.DCS.Components.Entity piece)
    {
        var a = original?.TryGet<AeroGridComponent>();
        if (a != null) System.Threading.Interlocked.Increment(ref a._splitStamp);
    }

    /// <summary>Every tick (simulation thread), while a failed cut is watched: split since - done; else, its time out, its
    /// surviving members destroyed (the Gone guards).</summary>
    void CheckCutSeparated()
    {
        if (_cutVerify.Count == 0) return;
        int stamp = System.Threading.Volatile.Read(ref _splitStamp);
        GridDamageReceiverComponent recv = null;
        for (int i = _cutVerify.Count - 1; i >= 0; i--)
        {
            var v = _cutVerify[i];
            if (stamp != v.Stamp) { _cutVerify.RemoveAt(i); continue; }   // (it split: the cut separated)
            if (--v.Ticks > 0) continue;
            _cutVerify.RemoveAt(i);
            recv ??= Entity?.TryGet<GridDamageReceiverComponent>();
            if (recv == null || _octree == null) continue;
            // the bridges: the members split rather than destroyed, and every block still TOUCHING ACROSS the cut's plane in
            // its cross-section (cells k | k+1 along its axis - whatever the structure counted as members, these still join
            // the two sides: the second cut of a test failed 29 of 29 members and still did not split, 2026-10-15)
            var bridge = new HashSet<CubeBlockComponent>(ReferenceEqualityComparer.Instance);
            foreach (var b in v.Members) if (!Gone(b)) bridge.Add(b);
            int ax = v.Axis, k0 = (int)MathF.Round(v.Plane / TearMath.Cell - 0.5f);
            int u = (ax + 1) % 3, w = (ax + 2) % 3;
            int uLo = Get(v.Lo, u), uHi = Get(v.Hi, u), wLo = Get(v.Lo, w), wHi = Get(v.Hi, w);
            if ((long)(uHi - uLo + 1) * (wHi - wLo + 1) <= 40000)
                for (int a = uLo; a <= uHi; a++) for (int c = wLo; c <= wHi; c++)
                {
                    var c0 = Cell(ax, k0, u, a, w, c); var c1 = Cell(ax, k0 + 1, u, a, w, c);
                    var b0 = _octree.TryGetCubeBlock(c0); var b1 = _octree.TryGetCubeBlock(c1);
                    if (b0 == null || b1 == null || Gone(b0) || Gone(b1)) continue;
                    if (ReferenceEquals(b0, b1)) { bridge.Add(b0); continue; }   // (one block across the plane)
                    bridge.Add(b0); bridge.Add(b1);   // (two blocks face to face across it)
                }
            int n = 0;
            foreach (var b in bridge)
            {
                if (Gone(b)) continue;
                try { recv.DealDamage(b, float.MaxValue); _goneBlocks.Add(b); n++; }
                catch (Exception e) { _goneBlocks.Add(b); Log.Default?.Info($"[AERO-STRUCT] bridge skipped: {e.Message}"); }
            }
            CutsChecked++;
            if (n > 0) { CutsForced++; Log.Default?.Info($"[AERO-STRUCT] {Entity?.DebugName}: the cut at {v.At} did not separate - {n} block(s) still bridging it, destroyed"); }
        }
    }
    public static int CutsChecked;
    static int Get(Vector3I p, int i) => i == 0 ? p.X : i == 1 ? p.Y : p.Z;
    static Vector3I Cell(int ax, int k, int u, int a, int w, int c)
    {
        var r = new int[3]; r[ax] = k; r[u] = a; r[w] = c;
        return new Vector3I(r[0], r[1], r[2]);
    }

    void CutDone(StructureEval.CutFail cut, ref int failed, List<Vector3> torn, int version0)
    {
        int n = failed; failed = 0;
        if (n == 0) { _cutSurvivors.Clear(); return; }   // (every member already gone: an old cut seen again)
        // (every failed cut watched - it must separate; CheckCutSeparated): its members' cross-section (cells, on the two axes
        //  across the cut, one cell wider all round) and plane, and the members that were split rather than destroyed
        {
            WatchSplits();
            var lo = new Vector3I(int.MaxValue); var hi = new Vector3I(int.MinValue);
            for (int k = cut.First; k < cut.First + cut.Count && k < _stDamage.Count; k++)
            {
                int bi = _stDamage[k].block;
                if (bi < 0 || bi >= _stBlocks.Count || _stBlocks[bi] == null) continue;
                var bb = _stBlocks[bi].AABB; lo = Vector3I.Min(lo, bb.Min); hi = Vector3I.Max(hi, bb.Max);
            }
            if (lo.X <= hi.X)
                _cutVerify.Add(new CutVerify { Members = new List<CubeBlockComponent>(_cutSurvivors), Stamp = System.Threading.Volatile.Read(ref _splitStamp),
                    Ticks = CutVerifyTicks, At = cut.At, Axis = cut.Axis, Plane = cut.Plane, Lo = lo - Vector3I.One, Hi = hi + Vector3I.One });
            _cutSurvivors.Clear();
        }
        if (torn != null) torn.Add(cut.At);
        TearSmoke.Publish(Entity, cut.At, cut.Dir);
        _stBreakHold = StructureLoads.BreakHoldMax; _stHoldVersion = version0;
        StructureLoads.LastCut = $"{Entity?.DebugName}: the cut at {cut.At} (axis {cut.Axis}) failed whole - {n} of {cut.Count} members (worst utilisation {cut.Util:F2})";
        Log.Default?.Info($"[AERO-STRUCT] {StructureLoads.LastCut}");
    }

    /// <summary>SPLIT, NOT DESTROY (Structure.SplitBlocks; 2026-10-14, the user: "instead of just destroying a block, we could
    /// split it?"): a failing block with fracture pieces loses only the pieces at its failing joint's face; it stays, broken,
    /// and the ship parts along that face.</summary>
    public static bool SplitBlocks = true;
    /// <summary>Most fracture pieces one failure kills; how far (m) into the block from the joint, and across its face, the
    /// pieces are looked for; the box (m) each look covers.</summary>
    public static int SplitMaxPieces = 6;
    public static float SplitDepth = 0.2f, SplitSpread = 0.35f, SplitBox = 0.3f;
    /// <summary>Diagnostics: blocks split, destroyed whole, pieces killed; the last.</summary>
    public static int SplitDone, SplitDestroyed, SplitPieces; public static string SplitLast = "-";

    // (BreakableBlockComponent by reflection: its base, BreakableComponent, is in VRage.Game - not a mod reference, CS0012
    //  for any typed use - docs/REFLECTION_AND_WHITELIST.md)
    static FieldInfo _breakableField; static MethodInfo _trySetFracture, _isBroken; static bool _breakableTried;
    static object Breakable(CubeBlockComponent b)
    {
        if (!_breakableTried)
        {
            _breakableTried = true;
            _breakableField = typeof(CubeBlockComponent).GetField("_breakableBlock", BindingFlags.NonPublic | BindingFlags.Instance);
            var t = _breakableField?.FieldType;
            if (t != null)
                foreach (var m in t.GetMethods(BindingFlags.Public | BindingFlags.Instance))
                {
                    var ps = m.GetParameters();
                    if (m.Name == "TrySetFractureIntegrity" && ps.Length == 3 && ps[1].ParameterType == typeof(float) && ps[2].ParameterType == typeof(float)) _trySetFracture = m;
                    else if (m.Name == "IsBroken" && ps.Length == 0 && m.ReturnType == typeof(bool)) _isBroken = m;
                }
            if (_trySetFracture == null) Log.Default?.Info("[AERO-STRUCT] split blocks off: BreakableBlockComponent.TrySetFractureIntegrity not found");
        }
        return _trySetFracture != null ? _breakableField.GetValue(b) : null;
    }

    /// <summary>The failing joint's face of a breakable block broken: the fracture pieces under it, looked for a little way in
    /// from the joint, at its middle and across it (TrySetFractureIntegrity kills the first live piece in a box, through the
    /// game's own fracturing: debris, the fractured model, the collider changed, the block's mount points on dead pieces cut,
    /// the grid's connectivity re-checked and split - server side, replicated). Returns the pieces killed (0: none there -
    /// armour has none).</summary>
    /// <summary>A block the game has removed or is removing: no entity, not alive, its health under its definition's
    /// destruction threshold (the game's own trigger for removing it - CubeBlockComponent ~4441; its flag is private), or one
    /// this grid already saw go. Every damage, dent and fracture call checks it first (a call on a dying block crashed).</summary>
    bool Gone(CubeBlockComponent b)
    {
        if (b == null || b.Entity == null || !PhysicsHack.Alive(b.Entity.Data) || _goneBlocks.Contains(b)) return true;
        // (split away to another grid since the evaluation - alive, but this grid's damage receiver no longer has its data:
        //  "Data not found", 2026-10-15)
        if (!ReferenceEquals(b.Grid?.Entity, Entity)) return true;
        return b.HealthIntegrity <= DestructionThreshold(b) + 1e-4f;
    }
    readonly HashSet<CubeBlockComponent> _goneBlocks = new HashSet<CubeBlockComponent>(ReferenceEqualityComparer.Instance);

    /// <summary>A block's DestructionThreshold (its definition's; per definition, cached; 0 if unreadable).</summary>
    static float DestructionThreshold(CubeBlockComponent b)
    {
        try
        {
            _defProp ??= typeof(CubeBlockComponent).GetProperty("Definition", BindingFlags.Public | BindingFlags.Instance);
            var d = _defProp?.GetValue(b);
            if (d == null) return 0f;
            lock (_defThreshold)
            {
                if (_defThreshold.TryGetValue(d, out var box)) return box.Value;
                float t = d.GetType().GetProperty("DestructionThreshold", BindingFlags.Public | BindingFlags.Instance)?.GetValue(d) is float f ? f : 0f;
                _defThreshold.Add(d, new StrongBox<float>(t));
                return t;
            }
        }
        catch { return 0f; }
    }
    static readonly System.Runtime.CompilerServices.ConditionalWeakTable<object, StrongBox<float>> _defThreshold = new System.Runtime.CompilerServices.ConditionalWeakTable<object, StrongBox<float>>();
    sealed class StrongBox<T> { public T Value; public StrongBox(T v) { Value = v; } }

    int SplitFace(CubeBlockComponent b, Vector3 at, Vector3 dir)
    {
        var bb = Breakable(b);
        if (bb == null || dir.LengthSquared() < 0.5f) return 0;
        var bwt = b.Entity.Data.GetWorldTransform(); var gwt = Entity.Data.GetWorldTransform();
        var box = b.AABB;
        // (the block's box, m: cells centred at i x Cell)
        Vector3 lo = new Vector3(box.Min.X, box.Min.Y, box.Min.Z) * TearMath.Cell - new Vector3(TearMath.Cell * 0.5f - 0.02f);
        Vector3 hi = new Vector3(box.Max.X, box.Max.Y, box.Max.Z) * TearMath.Cell + new Vector3(TearMath.Cell * 0.5f - 0.02f);
        dir = Vector3.Normalize(dir);
        // (across the face: two directions square to the way in)
        var u = Vector3.Normalize(Vector3.Cross(dir, MathF.Abs(dir.Y) < 0.9f ? Vector3.UnitY : Vector3.UnitX));
        var v = Vector3.Cross(dir, u);
        var c = at + dir * SplitDepth;
        var pts = new[] { c, c + u * SplitSpread, c - u * SplitSpread, c + v * SplitSpread, c - v * SplitSpread };
        var args = new object[3]; int killed = 0;
        foreach (var q in pts)
        {
            if (killed >= SplitMaxPieces) break;
            var p = Vector3.Clamp(q, lo, hi);
            Vector3D world = Keen.VRage.Core.WorldTransform.Transform((Vector3D)p, in gwt);
            args[0] = (Vector3)Keen.VRage.Core.WorldTransform.TransformInv(world, bwt); args[1] = SplitBox; args[2] = 0f;
            // (each call kills one piece; once more for the next one in the same box)
            for (int k = 0; k < 2 && killed < SplitMaxPieces; k++)
            {
                if (Gone(b)) return killed;
                if (!(_trySetFracture.Invoke(bb, args) is bool ok) || !ok) break;
                killed++;
                // (the piece's loss took the block under its destruction threshold: the game is removing it now - never
                //  touched again: a second fracture call on it asserted 'Killing dead Entity' and crashed the server, 2026-10-14)
                if (Gone(b)) { _goneBlocks.Add(b); return killed; }
            }
        }
        return killed;
    }

    /// <summary>A block that failed (health out, or a snap). SplitBlocks: its pieces at the failing face broken (SplitFace) -
    /// it stays, broken, its health what its pieces left (the game removes it itself under its destruction threshold; a
    /// critical piece lost stops it working, and a tank may explode - the game's own rules). Otherwise - no pieces there
    /// (armour), or SplitBlocks off - destroyed whole, fractured along the line from the failing joint into it. fx: its own
    /// torn-edge smoke and tear joint (off for a failed cut's members: once for the whole cut, at its centre - CutDone).</summary>
    void FailBlock(GridDamageReceiverComponent recv, Keen.VRage.Core.WorldTransform gwt, int bi, CubeBlockComponent b, (Vector3 at, Vector3 dir, float util) where, List<Vector3> torn, bool fx = true)
    {
        _stHealth.TakeAll(bi);
        if (SplitBlocks)
        {
            int killed = 0;
            try { killed = SplitFace(b, where.at, where.dir); }
            catch (Exception e) { SplitLast = "split failed: " + (e.InnerException ?? e).Message; Log.Default?.Info($"[AERO-STRUCT] {SplitLast}"); }
            if (killed > 0)
            {
                // (TEARING: the failed FACE, not the block's middle - the block stays; TORN-EDGE SMOKE out of the face)
                if (fx) { if (torn != null) torn.Add(where.at); TearSmoke.Publish(Entity, where.at, where.dir); }
                bool alive = !Gone(b);
                _stHealth.Set(bi, alive ? Math.Clamp(b.HealthIntegrity, 0f, 1f) : 0f);
                bool broken = false;
                try { broken = alive && _isBroken != null && _isBroken.Invoke(Breakable(b), null) is true; } catch { }
                SplitDone++; SplitPieces += killed; StructureLoads.Breaks++;
                SplitLast = $"{Entity?.DebugName}: {BlockName(b)} split at its failing face ({killed} pieces, utilisation {where.util:F2}){(alive ? $", health {b.HealthIntegrity:F2}" : ", gone")}{(broken ? ", broken (a critical piece)" : "")}";
                Log.Default?.Info($"[AERO-STRUCT] {SplitLast}");
                return;
            }
        }
        // (a split that faulted or took the block under its threshold: the game is removing it - never destroyed twice)
        if (Gone(b)) { _goneBlocks.Add(b); _stHealth.Set(bi, 0f); return; }
        var bbx = b.AABB; var gone = TearMath.CellCentre(bbx.Min, bbx.Max);   // (read before it goes)
        // (TORN-EDGE SMOKE, 2026-10-14: the ship's side of the break smokes where the block was, out of the gap - the way into
        //  the departing block; the piece's side when the split follows: StructureTear)
        if (fx) { if (torn != null) torn.Add(gone); TearSmoke.Publish(Entity, gone, where.dir); }
        // (FRACTURED where it failed: the game splits the block along a line from the failing joint into it, in the block's own
        //  frame - as its collisions do - not wherever it picks; 2026-10-14)
        recv.DealDamage(b, float.MaxValue, null, Fracture(b, gwt, where.at, where.dir));
        _goneBlocks.Add(b);
        StructureLoads.Breaks++; SplitDestroyed++;
        Log.Default?.Info($"[AERO-STRUCT] grid {Entity?.DebugName}: {BlockName(b)} broke (utilisation {where.util:F2})");   // (the block's kind: its entity's name was 'Root Entity' for some)
    }

    /// <summary>The bends the evaluation planned (StructureBend.Plan, background) - moved here, the simulation thread's.</summary>
    readonly List<StructureBend.Bend> _stBends = new();
    /// <summary>Each joint's peak utilisation it was bent at (StructureBend.Key: by place, kept across rebuilds; background).</summary>
    readonly Dictionary<long, float> _bendPeaks = new();
    /// <summary>Each bone's total bend (m, grid axes) and the part under one bone step not yet written (bone units).</summary>
    readonly Dictionary<Vector3I, (Vector3 total, Vector3 frac)> _bendLedger = new();
    readonly HashSet<Vector3I> _bendBones = new();
    /// <summary>The game's bone step: a bone's full value (127) moves its vertices this far (m; BoneDeformationConfiguration
    /// .MaxBoneOffset, 1 by default - no override in GameData).</summary>
    public static float BoneOffsetM = 1f;
    /// <summary>A far side with more than this share of non-armour blocks is not bent (the bones move only the armour: a thruster
    /// or a wing block would stay put while the armour round it moved).</summary>
    public static float BendNonArmourShare = 0.1f;
    /// <summary>Most bones written per evaluation (every bend together).</summary>
    public static int BendMaxBones = 1500;
    /// <summary>Diagnostics: bends applied, skipped (mostly not armour, no bone storage), bones written; the last.</summary>
    public static int BendsApplied, BendsSkipped, BendBoneWrites; public static string BendLast = "-";

    /// <summary>
    /// PERMANENT BENDS, the game side (2026-10-14; the plan: Structure/StructureBend). Rare (a joint passing its last peak):
    /// guarded. SE2 cannot turn blocks, but its deformation BONES (BoneStorageComponent: one every 4 cells, 1 m; a bone's
    /// Vector3B is its offset in 1/127 of MaxBoneOffset - 1 m - and the armour mesh is skinned to the eight round each
    /// point) can be moved: every bone of the far side's blocks moves by the bend's displacement there - zero at the joint,
    /// growing outward - so the armour sags or twists as one piece. Written straight (WriteBoneDelta(Vector3I, Vector3B)),
    /// not through DealDamage: there the deformation is scaled by the damage dealt (a wear step's sliver of health rounds to
    /// nothing in a bone's 8 mm step) and is a fixed 3x3x3 bones from one corner (a dent, not a bend). The game replicates
    /// the bones to clients (BoneChangedSignal) and clamps each to its chunk's limit (a 0.25 m armour block's chunk: 0.25 m).
    ///  - each bone's total from bends is capped at StructureBend.Max (the ledger); the part under one step is carried, so a
    ///    slow bend is not lost to rounding;
    ///  - ARMOUR ONLY moves: a far side of more than BendNonArmourShare other blocks is not bent;
    ///  - at most BendMaxBones writes an evaluation.
    /// </summary>
    void ApplyBends()
    {
        if (_stBends.Count == 0) return;
        try
        {
            if (_stBlocks == null || !StructureBend.Enabled || !StructureLoads.Damage) return;
            var bones = Entity?.TryGet<Keen.Game2.Simulation.WorldObjects.BoneStorage.BoneStorageComponent>();
            if (bones == null) { BendsSkipped += _stBends.Count; BendLast = "no bone storage"; return; }
            int perBone = Math.Max(1, bones.BlocksPerBone);
            float boneM = perBone * TearMath.Cell, unit = 127f / MathF.Max(BoneOffsetM, 1e-3f);
            int writes = 0;
            foreach (var bend in _stBends)
            {
                // the far side's blocks still here; armour mostly
                _bendBones.Clear();
                int n = 0, other = 0;
                foreach (int bi in bend.Blocks)
                {
                    if (bi < 0 || bi >= _stBlocks.Count) continue;
                    var b = _stBlocks[bi];
                    if (Gone(b)) continue;
                    n++;
                    if (b.Entity.TryGet<Keen.Game2.Simulation.WorldObjects.CubeBlocks.Armors.ArmorBlockComponent>() == null) { other++; continue; }
                    // (its bones: each cell lies between the bones floor(c / 4) and the next)
                    var bb = b.AABB;
                    var lo = bones.BlockToBone(bb.Min); var hi = bones.BlockToBone(bb.Max) + Vector3I.One;
                    for (int x = lo.X; x <= hi.X; x++) for (int y = lo.Y; y <= hi.Y; y++) for (int z = lo.Z; z <= hi.Z; z++) _bendBones.Add(new Vector3I(x, y, z));
                }
                if (n == 0 || other > BendNonArmourShare * n) { BendsSkipped++; BendLast = $"{Entity?.DebugName}: joint at {bend.Joint} not bent - {other} of {n} far blocks not armour"; continue; }
                int wrote = 0; float tip = 0f;
                foreach (var bone in _bendBones)
                {
                    if (writes >= BendMaxBones) break;
                    // (a bone's place: the lower corner of its first cell - bone b at b x 1 m - 0.125, the grid's cells centred at i x 0.25)
                    var p = new Vector3(bone.X, bone.Y, bone.Z) * boneM - new Vector3(TearMath.Cell * 0.5f);
                    var d = StructureBend.Displacement(bend, p);
                    if (d.LengthSquared() < 1e-8f) continue;
                    _bendLedger.TryGetValue(bone, out var led);
                    var total = StructureBend.Capped(led.total, d);
                    var units = (total - led.total) * unit + led.frac;
                    var q = new Vector3((int)units.X, (int)units.Y, (int)units.Z);   // (whole steps; the rest carried)
                    _bendLedger[bone] = (total, units - q);
                    if (q == Vector3.Zero) continue;
                    bones.WriteBoneDelta(bone, new Keen.VRage.Library.Mathematics.Vector3B(q));
                    writes++; wrote++;
                    float l = total.Length(); if (l > tip) tip = l;
                }
                BendsApplied++; BendBoneWrites += wrote;
                BendLast = $"{Entity?.DebugName}: joint at {bend.Joint} (utilisation {bend.Util:F2}) set {bend.Rot.Length() * 57.2958f:F2} deg about {Vector3.Normalize(bend.Rot)}: {n} blocks, {wrote} bones, most {tip * 100f:F0} cm";
                Log.Default?.Info($"[AERO-STRUCT] bend {BendLast}");
            }
        }
        catch (Exception e) { BendsSkipped++; BendLast = "bend failed: " + e.Message; Log.Default?.Info($"[AERO-STRUCT] bend failed: {e.Message}"); }
        finally { _stBends.Clear(); }
    }

    // inputs the attitude controller writes (ThrustTorque.Apply): each thruster's force this frame (N, along its
    // ThrustDirection; _thrusterCache order) and the gyros' torque (grid-local N m)
    internal float[] LastThrustShares, LastThrustGame;
    /// <summary>The flight controller's steering torque from the thrusters x this (ThrustTorque): 1, unless structure damage is
    /// on and the controller's own steering would overload the ship (UpdateSteerCap).</summary>
    internal volatile float StructSteerCap = 1f;

    /// <summary>
    /// THE CONTROLLER RESPECTS THE STRUCTURE (2026-10-14, the user's choice): the flight controller's steering never breaks
    /// the ship it flies - your own hard flying still can. An independent replay of an in-game overload (a 2.8 kt ship at
    /// 4.09) found it was the controller firing every ion thruster as a pure couple (920 MN m) to stop a turn, bending an
    /// engine pod's neck; air, gyros and spin gave 0.07 there. So, while damage is on: past SteerCapTarget the ship is solved
    /// again without the controller's steering thrust (the game's own sharing - your thrust); the steering's part of the
    /// overload is how much of it is the controller's, and the cap brings it back to SteerCapTarget (load is linear in it).
    /// Under SteerCapRelax x the target the cap climbs back (SteerCapRecover per second). An overload from the air alone
    /// leaves the steering be.
    /// </summary>
    void UpdateSteerCap(ShipStructure st, HullHeat hh, StructureEval.Loads L, float dt)
    {
        float cap = StructSteerCap;
        if (!StructureLoads.Damage || !StructureLoads.SteerRespectsStructure) { if (cap != 1f) StructSteerCap = 1f; return; }
        float u = st.MaxUtil, target = StructureLoads.SteerCapTarget;
        if (u > target && L.ThrF0.Count == L.ThrF.Count && L.ThrF.Count > 0)
        {
            // the same loads with the game's own thrust only: what is left without the controller's steering
            var steer = new List<Vector3>(L.ThrF);
            L.ThrF.Clear(); L.ThrF.AddRange(L.ThrF0);
            StructureEval.Apply(st, hh, L);
            st.SolveFree(L.AngVel);
            float u0 = st.MaxUtil;
            L.ThrF.Clear(); L.ThrF.AddRange(steer);
            StructureEval.Apply(st, hh, L);   // (back to the real loads: the damage and the gauges read them)
            st.SolveFree(L.AngVel);
            float part = u - u0;
            if (part > 0.05f * u)
            {
                float k = u0 < target ? (target - u0) / part : 0f;
                StructSteerCap = Math.Clamp(cap * Math.Clamp(k, 0f, 1f), StructureLoads.SteerCapMin, 1f);
                StructureLoads.SteerCapLast = $"{Entity?.DebugName}: worst {u:F2}, without the controller's steering {u0:F2}: steering x{StructSteerCap:F2}";
            }
        }
        else if (u < target * StructureLoads.SteerCapRelax && cap < 1f) StructSteerCap = MathF.Min(1f, cap + StructureLoads.SteerCapRecover * dt);
    }
    internal int LastThrustCount;
    internal Vector3 LastGyroTorque;
    internal bool LastGyroAware;
}
