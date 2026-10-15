#pragma warning disable
using System;
using Keen.Game2.Simulation.WorldObjects.CubeBlocks;
namespace AeroMod;

/// <summary>
/// STRUCTURE AND HULL HEAT, SERVER (the models: Structure/Model/ShipStructure, HullHeat - offline-tested in
/// Tests/StructureTests and AeroBench 'struct'). Per grid, StructureLoads.Rate times a second:
///  - on the simulation thread (TickStructure): a snapshot of what loads the ship - the air's applied force and torque,
///    the wings' forces, each thruster's force, the gyros' torque, the angular velocity, the air, the contacts
///    (StructureContacts) - and the blocks' health; after a block change (and a cooldown), the blocks themselves (cells,
///    mass, name, mount points: StructureServer.Rebuild) for a rebuild;
///  - in the background (AeroWork): the rebuild (slices, surface faces, heat state carried over), then the hull's heat
///    for the time since the last evaluation (its own surface pass when the flow turned), the loads spread onto the
///    blocks (wings over their cells, the rest of the air over the windward faces, thrust at the thrusters, gyro
///    torque over the gyros), every block's temperature into its strength, the solve (each piece free on its loads),
///    and the damage to do;
///  - back on the simulation thread, the next tick: the damage, through the game's damage receiver (game-mode rules
///    apply: creative takes none: StructureServer.Damage, .Failure, .Deform), and the gauges (StructMaxUtil, StructMaxSkinK).
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
        /// <summary>Each block's definition's DestructionThreshold (ShipStructure.BreakAt).</summary>
        public List<float> BreakAt = new();
        /// <summary>The blocks' mount points in grid cells: block, outward direction (grid axes, unit), the cells' box.</summary>
        public List<(int block, Vector3I dir, Vector3I lo, Vector3I hi)> Mounts = new();
        public List<CubeBlockComponent> Blocks = new();
        public float BlockSize;
    }

    ShipStructure _st; HullHeat _hh;
    /// <summary>The scorch spots (HeatScorch): the working list (this thread), and the copy published to the client.</summary>
    internal readonly List<HeatScorch.Spot> ScorchSpots = new();
    /// <summary>The structure _hh was built with (HeatScorch: its faces' blocks' classes; simulation thread).</summary>
    internal ShipStructure StructureForScorch => _st;
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
    float[] _stInteg, _stIntegOther; float _stIntegAge;

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
        // only a grid someone flies: any other costs a few compares a tick and nothing in the background
        // (flown: someone seated in it - AeroApi.SetFocusGrid, the Orbital mod's seat - or steering it by its target)
        bool piloted = System.Diagnostics.Stopwatch.GetTimestamp() < StructFocusUntil || Data.Has<Keen.Game2.Simulation.WorldObjects.Movement.TargetControlData>();
        // ONLY PLAYERS' GRIDS (2026-10-13, the user: "only structural failure for player grids"): the grid a player is
        // flying (AeroApi.SetFocusGrid from their seat, kept FocusLinger s after they leave it; or steered by its target) -
        // AI ships, wrecks and parked grids are never evaluated, never damaged
        bool active = piloted;
        // (idle: looked at again in a second)
        if (!active) { _stClock = MathF.Min(_stClock, 2f); _stNextAt = _stClock + 1f; return; }
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
            // (only shares of THIS thruster list: right after a rebuild the shares are the old list's - paired by index they put
            //  thrust on the wrong blocks for an evaluation, 2026-10-15)
            var shares = LastThrustShares; var game = LastThrustGame; int ns = LastThrustGen == _thrusterGen ? LastThrustCount : 0;
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
            // (two arrays kept while the block count stands, taken in turn: only the evaluation reads it, and that one is done -
            //  17 KB a pass on a 4400-block ship, every 2 s, 2026-10-15. In turn, not one: the ledger knows a fresh reading by a
            //  new array (StructureFatigue.Ledger.Sync) - one array refilled was never fresh again, so a weld (Repairs) or
            //  someone else's damage never reached it and its pending wear was written over the repair, 2026-10-15)
            var integ = _stIntegOther != null && _stIntegOther.Length == nb ? _stIntegOther : new float[nb];
            for (int i = 0; i < nb; i++) { var b = blocks[i]; integ[i] = b != null ? Math.Clamp(b.HealthIntegrity, 0f, 1f) : 0f; }
            _stIntegOther = _stInteg; _stInteg = integ;
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

    Dictionary<Entity, int> IndexOf(List<CubeBlockComponent> blocks)
    {
        var d = new Dictionary<Entity, int>();
        for (int i = 0; i < blocks.Count; i++) { var e = blocks[i]?.Entity; if (e != null) d[e] = i; }
        return d;
    }

    void RebuildIndex() => _stIndexOf = _stBlocks != null ? IndexOf(_stBlocks) : null;

    /// <summary>The background evaluation (one at a time per grid): a rebuild if asked, the heat, the loads, the solve,
    /// the damage to do.</summary>
    void EvaluateStructure(ShipStructure st, HullHeat hh, StructBuildInput build, StructSnap snap)
    {
        long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
        if (build != null)
        {
            var nst = ShipStructure.Build(build.BoxBlock, build.Lo, build.Hi, build.Mass, build.Mat, build.Mounts);
            if (build.BreakAt.Count == nst.Count) nst.BreakAt = build.BreakAt.ToArray();
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
        ApiHeatOverride(st, hh);   // (another heat model's block temperatures, when one sets them: StructureServer.Api.cs)
        // the loads, the solve (each piece free on them), the damage to do (Structure/Model/StructureEval: offline-simulated)
        StructureEval.Apply(st, hh, L);
        st.SolveFree(L.AngVel);
        UpdateSteerCap(st, hh, L, snap.Dt);
        if (StructureLoads.DumpLoadsAbove > 0f && st.MaxUtil > StructureLoads.DumpLoadsAbove) { StructureLoads.DumpLoadsAbove = -1f; DumpLoads(L, st.MaxUtil); }
        // (BY SECTION, 2026-10-15: whole cuts wear and fail - _stCuts the failed ones, their members' entries together)
        StructureEval.Damage(st, hh, snap.Dt, snap.StructDamage, StructureLoads.HeatDamage, StructureLoads.BreakUtil, StructureLoads.WearPerSecond, StructureLoads.MaxBreaksPerEval, _stDamage, _stWhere, _stCuts);
        // (PERMANENT BENDS, 2026-10-14: a worn cut past its last peak sets the structure outboard of it - Structure/Model/StructureBend;
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
        ApiPublishStructure(st, hh, build?.Blocks ?? _stBlocks);   // (the public API's failures and block state: StructureServer.Api.cs)
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

    readonly List<(Vector3 at, Vector3 dir, float util)> _stWhere = new();
    /// <summary>The last evaluation's failed cuts (StructureEval.CutFail: centre, way out, members' entries in _stDamage).</summary>
    readonly List<StructureEval.CutFail> _stCuts = new();
    /// <summary>NO ZIPPER (2026-10-15): seconds of evaluation the structure's damage still waits after a cut failed, and the
    /// block version it failed at (a rebuild from a later one ends the wait).</summary>
    float _stBreakHold; int _stHoldVersion;

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
    /// <summary>The thruster list's generation (_thrusterGen) LastThrustShares was computed for: the list is rebuilt in the
    /// simulation job, the shares come later in the frame (AttitudeJob) - between, the old shares and the new list.</summary>
    internal int LastThrustGen = -1;
    internal Vector3 LastGyroTorque;
    internal bool LastGyroAware;
}
