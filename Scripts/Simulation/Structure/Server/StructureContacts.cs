#pragma warning disable
using System;
using System.Collections.Generic;
using Keen.VRage.Core;
using Keen.VRage.Physics.Data;
using Keen.Game2.Simulation.WorldObjects.CubeBlocks;
using Keen.Game2.Simulation.WorldObjects.CubeBlocks.MechanicalBlocks;
using Keen.Game2.Simulation.WorldObjects.CubeBlocks.MechanicalBlocks.Wheels;

namespace AeroMod;

/// <summary>Contact loads in the structure (AeroSettings: Structure.ContactLoads).</summary>
public static class StructureContactLoads
{
    public static bool Enabled = true;
    /// <summary>A rigid hit's impulse (one physics step's) is taken as delivered over this long (s): the crush.</summary>
    public static float CrushSeconds = 0.05f;
    /// <summary>Contacts deeper than this (m of penetration) are dropped: an arrival overlapping something, not a load.</summary>
    public static float MaxPenetration = 0.25f;
    /// <summary>Physics steps a moved grid's contacts are ignored for (a teleport, a frame shift, a stow/arrive).</summary>
    public static int MaskSteps = 3;
    /// <summary>The most the contacts are scaled up to the physics' support (they can under-read: the 1 N s floor).</summary>
    public static float ScaleMax = 3f;
    /// <summary>The physics' speed cap (m/s): the engine's VelocityLimitProvider.LinearVelocityLimit - the world definition's
    /// Physics.MaximumSpeedLinear, which the physics world clamps every body to - read by the speed job each tick
    /// (AeroSpeedUncap); 0 until read (no cap rule in the balance then). At it, the balance's part along the velocity is
    /// the clamp's, not a contact's (ContactBalance).</summary>
    public static float PhysicsSpeedCap;
    // diagnostics
    public static string Last = "-", LastHeld = "-";
    public static int Masked, Hull, Wheel, OrbitalWrites, OrbitalMoves, AtCapSteps;
    public static string Counters => $"hull points {Hull}, wheel manifolds {Wheel}, masked steps {Masked} (Orbital's moves {OrbitalMoves}), steps with Orbital's velocity writes {OrbitalWrites}, at the speed cap {AtCapSteps} (cap {PhysicsSpeedCap:F0} m/s; {OrbitalLink.EditsStatus})";
}

/// <summary>
/// COLLISIONS AND GROUND CONTACT AS FORCES IN THE STRUCTURE (2026-10-14, the user: "collisions and ground contact
/// forces resolved as forces in our structure tree"; earlier: "don't make any assumptions about where contact forces
/// originate from, just detect it" - and teleports never count). Read from the physics itself, read-only, every tick on
/// the simulation thread before the physics step, for the grid a player flies - the physics' lists hold the last step's
/// contacts until it clears them at the next step's start (PhysicsUpdate.OnClearContacts):
///  - the HULL: the grid's ContactImpulseData (every contact point over 1 N s: world point, normal into this grid, the
///    normal impulse); each point to its cell;
///  - the WHEELS (a wheel is a body of its own - a ship on its gear has no hull contact at all): each suspension block's
///    wheel entity's own ContactImpulseData (every point), on the suspension block;
///  - HOW MUCH comes from the physics' own balance each step (the velocity's change less the known forces: exactly the
///    weight at rest), the contacts scaled to it - they say WHERE (the wheels' own list read 2.0x the weight);
///  - what neither shows - LOCKED landing gear / magnetic plates are a fixed constraint, not a contact - as THE
///    REMAINDER: the velocity's change less every known force (gravity, the engines, the air, the contacts above), shared
///    over the locked plates.
/// Per cell over the evaluation window: the impulse summed and the single step's peak kept; at the evaluation each cell's
/// force is the larger of the mean (resting: the support) and the peak over CrushSeconds (a hit).
/// Not seen: friction (the wheels' gripping friction reads 0, the hull's none at all).
/// Teleports: a grid moved by someone (AeroApi.NoteMoved - the Orbital mod's push; its move stamp - OrbitalApi.TryGetGridEdits,
/// the pull) or seen to jump (its position against its velocity: the fallback) is ignored for MaskSteps steps; deep
/// penetrations are dropped.
/// OTHER MODS' VELOCITY WRITES (2026-10-14): the Orbital mod writes the physics velocity after the step (a frame anchor's
/// motion drained into the rails every tick, warp's rescale and steering, differential gravity, a spinning planet's
/// fictitious forces) - none is a force, all would read as contacts (an anchor's own thrust read as plate load, a warp
/// switch as a giant impulse). Their running sum (OrbitalApi.TryGetGridEdits) is read beside the velocity and its change
/// since the last read taken off the balance; at the physics' speed cap the clamp's part is dropped (ContactBalance).
/// Costs, flown grid only: a list walk per step (empty in flight), the wheels' (a few), nothing else.
/// </summary>
public partial class AeroGridComponent
{
    struct ContactAcc { public Vector3 SumJ, Peak; }
    readonly Dictionary<Vector3I, ContactAcc> _cAcc = new();
    float _cWindow;
    Vector3D _cPrevPos; Vector3 _cPrevVel, _cPrevCoM, _cPrevGrav, _cPrevExtra; float _cPrevMass; bool _cHavePrev;
    /// <summary>The Orbital mod's writes as of the last read (OrbitalApi.TryGetGridEdits): their running sum and the move stamp.</summary>
    Vector3D _cPrevWSum; long _cPrevStamp; bool _cHaveEdits;
    internal int ContactMask;
    /// <summary>Steps since the grid was last moved by someone (a teleport): deep penetrations are dropped while short.</summary>
    int _cSinceMove = int.MaxValue;
    /// <summary>The attitude control's extra force (world, N), applied after this grid's tick (ThrustTorque).</summary>
    internal Vector3 LastExtraForceWorld;
    bool _cFault;
    // the remainder (for locked plates, grid-local): its impulse over the window, its crushed peak
    Vector3 _cResJ, _cResPeak; float _cResT;
    // the supports: suspension blocks (their wheels) and magnetic plates, refreshed when the blocks change (at most 1/s)
    readonly List<WheelSupport> _wheels = new();
    sealed class WheelSupport
    {
        public WheelCornerModuleComponent cm; public Vector3I cell; public bool gear; public Vector3 upLocal, posLocal;
        public GearTouch touch; public Vector3 stepJ; public bool touching;
    }

    /// <summary>
    /// GEAR TOUCHDOWNS (2026-10-14, for aero-2c's gear collapse - the user: "hard landings collapse gear legs"): per suspension
    /// block, keyed by its cube AABB (Min, Max) as AeroGearComponent keys its gears. Server, simulation thread, the flown
    /// grid only; read a tick later by the gear's own job (job groups run in turn: no lock).
    ///  - TouchdownSink: the block's descent speed along its own up axis (m/s, positive = down) at the step its wheel first
    ///    touched - set once per touchdown;
    ///  - PeakLoadN: the touchdown's highest wheel load as the structure sees a hit (the step's impulse over CrushSeconds),
    ///    StepLoadN the latest step's real force (impulse / dt) - both after the physics balance's scaling (true magnitudes);
    ///  - Stamp: bumped on every change; Touching: a wheel contact this step.
    /// </summary>
    public sealed class GearTouch { public float TouchdownSink, PeakLoadN, StepLoadN; public long Stamp; public bool Touching; }
    internal readonly Dictionary<(Vector3I min, Vector3I max), GearTouch> GearTouches = new();
    /// <summary>The share of contact loads a gear block with a retractable gear (AeroGearComponent) takes in the structure:
    /// its leg folds first (aero-2c's collapse, from TouchdownSink / PeakLoadN), the block breaking stays the extreme case.</summary>
    public static float GearContactShare = 1f;   // (1: as any block - the user, 2026-10-14: "landing gear collapses easily enough")
    readonly HashSet<Vector3I> _gearCells = new();
    readonly List<(MagneticBlockComponent mg, Vector3I cell)> _plates = new();
    int _supportsVersion = -1; long _supportsAt;
    readonly HashSet<Keen.VRage.DCS.Accessors.DEntity> _cOwn = new();   // (this grid's and its wheels' bodies: self-contacts)
    int _cOwnVersion = -2;
    // diagnostics: the window's impulses (world)
    Vector3 _cHullJ, _cWheelJ, _cSupJ; float _cSupT;
    // the step's contacts, summed per cell (grid-local N s)
    readonly Dictionary<Vector3I, Vector3> _cStep = new();

    void RefreshSupports()
    {
        if (_supportsVersion == StructBlockVersion || _octree == null) return;
        long now = System.Diagnostics.Stopwatch.GetTimestamp();
        if (_supportsVersion >= 0 && now - _supportsAt < System.Diagnostics.Stopwatch.Frequency) return;   // (a crash: not every tick)
        _supportsVersion = StructBlockVersion; _supportsAt = now; _wheels.Clear(); _plates.Clear(); _gearCells.Clear();
        foreach (var b in _octree.GetAllCubeBlocks())
        {
            var e = b?.Entity; if (e == null) continue;
            var cm = e.TryGet<WheelCornerModuleComponent>(); var mg = e.TryGet<MagneticBlockComponent>();
            if (cm == null && mg == null) continue;
            // (a cell of the block's own, nearest its middle: the load must land in it)
            var bb = b.AABB;
            var mid = new Vector3I((bb.Min.X + bb.Max.X) / 2, (bb.Min.Y + bb.Max.Y) / 2, (bb.Min.Z + bb.Max.Z) / 2);
            var g = b.GetTransformedOccupiedCellGroup(0);
            var cell = Vector3I.Clamp(mid, g.Min, g.Max);
            if (cm != null)
            {
                var key = (bb.Min, bb.Max);
                if (!GearTouches.TryGetValue(key, out var touch)) GearTouches[key] = touch = new GearTouch();
                var rel = e.Data.GetRelativeTransform();
                bool gear = e.TryGet<AeroGearComponent>() != null;
                if (gear) _gearCells.Add(cell);
                _wheels.Add(new WheelSupport { cm = cm, cell = cell, gear = gear, upLocal = Vector3.Transform(Vector3.Up, rel.Orientation), posLocal = (Vector3)rel.Position, touch = touch });
            }
            if (mg != null) _plates.Add((mg, cell));
        }
    }

    static Vector3I CellOf(Vector3 local) => new Vector3I((int)MathF.Round(local.X * 4f), (int)MathF.Round(local.Y * 4f), (int)MathF.Round(local.Z * 4f));

    void AddContact(Vector3I cell, Vector3 j)
    {
        _cAcc.TryGetValue(cell, out var a);
        a.SumJ += j;
        if (j.LengthSquared() > a.Peak.LengthSquared()) a.Peak = j;
        _cAcc[cell] = a;
    }

    void StepContact(Vector3I cell, Vector3 jLocal) { _cStep.TryGetValue(cell, out var j); _cStep[cell] = j + jLocal; }

    /// <summary>A grid moved by someone else (AeroApi.NoteMoved; any thread): its next steps are not loads.</summary>
    internal void MaskContacts(int steps)
    {
        int cur;
        do { cur = System.Threading.Volatile.Read(ref ContactMask); if (cur >= steps) return; }
        while (System.Threading.Interlocked.CompareExchange(ref ContactMask, steps, cur) != cur);
    }

    void ClearContactWindow()
    {
        _cAcc.Clear(); _cWindow = 0f; _cResJ = _cResPeak = Vector3.Zero; _cResT = 0f;
        _cHullJ = _cWheelJ = _cSupJ = Vector3.Zero; _cSupT = 0f;
    }

    /// <summary>Every tick (simulation thread, before the physics step), the ship a player flies only. Never takes the game
    /// down: a fault turns this grid's contact loads off (logged).</summary>
    void AccumulateContacts(float dt, Vector3 airLocal)
    {
        if (!StructureContactLoads.Enabled || _cFault || dt <= 0f) return;
        try { AccumulateContactsCore(dt, airLocal); }
        catch (Exception e)
        {
            _cFault = true; ClearContactWindow();
            Log.Default?.Warning($"[AERO-STRUCT] grid {Entity?.DebugName}: contact loads off after a fault: {e.GetType().Name}: {e.Message} {e.StackTrace?.Split(System.Environment.NewLine)[0]}");
        }
    }

    void AccumulateContactsCore(float dt, Vector3 airLocal)
    {
        if (System.Diagnostics.Stopwatch.GetTimestamp() >= StructFocusUntil && !Data.Has<Keen.Game2.Simulation.WorldObjects.Movement.TargetControlData>())
        {
            if (_cHavePrev) { _cHavePrev = false; ClearContactWindow(); }   // (nothing stale applied when flown again)
            return;
        }
        var wt = Data.GetWorldTransform();
        // (the other mod's writes read right beside the velocity: both as of the same moment)
        bool edits = OrbitalLink.TryGetGridEdits(Entity, out Vector3D wSum, out long wStamp);
        Vector3 vel = Data.TryGet<RigidBodyData>(out var rb) ? rb.LinearVelocity : Vector3.Zero;
        float m = LastMass;
        if (!float.IsFinite(vel.X + vel.Y + vel.Z)) return;
        // WHAT THE ORBITAL MOD WROTE since the last read (its writes land after the physics step, between two reads): taken
        // off the balance. A changed stamp is a teleport or a turn - masked as NoteMoved masks (the push may come later, or
        // not at all for an older Orbital). A record that APPEARED since the last read was made by a write in between (its
        // sum unknown before it): masked too, and re-baselined (Orbital's stamps are never reused: a record made anew reads
        // as a move).
        Vector3 written = Vector3.Zero;
        if (edits)
        {
            if (_cHaveEdits && wStamp == _cPrevStamp)
            { written = (Vector3)(wSum - _cPrevWSum); if (written.LengthSquared() > 0f) StructureContactLoads.OrbitalWrites++; }
            else if (_cHaveEdits || _cHavePrev) { MaskContacts(StructureContactLoads.MaskSteps); StructureContactLoads.OrbitalMoves++; }
            _cPrevWSum = wSum; _cPrevStamp = wStamp;
        }
        _cHaveEdits = edits;
        // THE BALANCE'S PAIRING (the order in a tick: the game's thrust and this mod's air are put into the velocity BEFORE
        // this read - ThrustComponent.OnApplyLinearImpulse, AeroSimJob; the attitude control's extra force AFTER it - the
        // attitude job; gravity and the contacts IN the physics step after). So the velocity's change since the last read
        // holds: the last read's extra force and gravity, the step's contacts, and this tick's thrust and air.
        Vector3 thrJ = Data.TryGet<Keen.Game2.Simulation.WorldObjects.Movement.ActiveThrustData>(out var at) ? WorldTransform.TransformDirection(at.ComputedThrustPerFrame, wt) : Vector3.Zero;
        Vector3 airJ = WorldTransform.TransformDirection(airLocal, wt) * dt;
        Vector3 grav = PhysicsHack.GetGravityDirection(Data);
        bool had = _cHavePrev;
        // (the mass or the centre of mass changed - a block lost, cargo: the centre's velocity jumps with no force)
        if (had && (MathF.Abs(m - _cPrevMass) > 1e-4f * MathF.Max(m, 1f) || Vector3.DistanceSquared(LastCoM, _cPrevCoM) > 1e-4f)) had = false;
        Vector3 v0 = _cPrevVel, g0 = _cPrevGrav, x0 = _cPrevExtra;
        // a jump the velocity cannot explain: a teleport (no flag for it). The body moves at its velocity after the step,
        // so a hard crash (the velocity's big change) is NOT a jump - nor a velocity the other mod wrote after the move
        if (_cHavePrev)
        {
            var moved = wt.Position - _cPrevPos;
            var expect = ContactBalance.ExpectedMove(vel, written, thrJ, airJ, m, dt);
            double tol = Math.Max(0.5, 0.2 * vel.Length() * dt);
            if ((moved - expect).Length() > tol) MaskContacts(StructureContactLoads.MaskSteps);
        }
        _cPrevPos = wt.Position; _cPrevVel = vel; _cPrevGrav = grav; _cPrevExtra = LastExtraForceWorld; _cPrevMass = m; _cPrevCoM = LastCoM; _cHavePrev = true;
        if (System.Threading.Volatile.Read(ref ContactMask) > 0)
        {
            System.Threading.Interlocked.Decrement(ref ContactMask); StructureContactLoads.Masked++; _cSinceMove = 0; _cHavePrev = false;
            return;
        }
        if (_cSinceMove < int.MaxValue) _cSinceMove++;
        bool justMoved = _cSinceMove < 60;   // (a second after a move: deep overlaps are the arrival's, not a crash)
        _cWindow += dt;
        RefreshSupports();
        if (_cOwnVersion != _supportsVersion)
        {
            _cOwnVersion = _supportsVersion; _cOwn.Clear(); _cOwn.Add(Entity.DEntity);
            foreach (var w in _wheels) { var we = w.cm.WheelEntity; if (we != null) _cOwn.Add(we.DEntity); }
        }
        Vector3 contactJ = Vector3.Zero; float contactAbs = 0f;   // (world, N s: the step's contacts; and their magnitudes' sum)
        _cStep.Clear();

        // the hull
        if (Data.TryGet<ContactImpulseData>(out var cid))
        {
            // (by the buffer's enumerator, as the game reads it)
            foreach (var c in cid.ImpulsesFromLastFrame)
            {
                if (!(c.ImpulseMagnitude > 0f) || (justMoved && c.MinDistance < -StructureContactLoads.MaxPenetration) || _cOwn.Contains(c.OtherBody)) continue;
                Vector3 jw = c.Normal * c.ImpulseMagnitude;
                contactJ += jw; contactAbs += c.ImpulseMagnitude; _cHullJ += jw;
                // (the point nudged half a cell into this grid, as the game finds the hit cell)
                Vector3 local = (Vector3)WorldTransform.TransformInv(c.Position + (Vector3D)(c.Normal * 0.125f), wt);
                StepContact(CellOf(local), WorldTransform.TransformDirectionInv(jw, wt));
                StructureContactLoads.Hull++;
            }
        }
        // the wheels
        foreach (var ws in _wheels)
        {
            var cell = ws.cell; ws.stepJ = Vector3.Zero;
            var we = ws.cm.WheelEntity;
            // (the wheel body's own ContactImpulseData - the wheel collector registers it at 0 N s: every point, world normal
            //  into the wheel; the wheel carries it to the suspension block. ReferenceWheelContact is only the strongest
            //  point, in the wheel's frame; the raw WheelContactData an inline buffer that crashed when copied)
            if (we == null || !we.Data.TryGet<ContactImpulseData>(out var wc)) continue;
            foreach (var c in wc.ImpulsesFromLastFrame)
            {
                if (!(c.ImpulseMagnitude > 0f) || (justMoved && c.MinDistance < -StructureContactLoads.MaxPenetration) || _cOwn.Contains(c.OtherBody)) continue;
                Vector3 jw = c.Normal * c.ImpulseMagnitude;
                contactJ += jw; contactAbs += c.ImpulseMagnitude; _cWheelJ += jw; ws.stepJ += jw;
                StepContact(cell, WorldTransform.TransformDirectionInv(jw, wt));
                StructureContactLoads.Wheel++;
            }
        }
        // HOW MUCH: what the physics says held it over the last step (all supports together - the velocity's change less the
        // known impulses, paired as above); the contact lists say WHERE. Their sum is not the truth (the wheels' read 2.0x
        // the weight at rest, the physics' own balance 1.000x), so the step's contacts are scaled to the support along
        // them (0..ScaleMax). Contacts that mostly cancel (wedged, pinched) say nothing of the total: as read.
        float scale = 1f; Vector3 sup = Vector3.Zero;
        if (had && m > 0f)
        {
            // (Structure/ContactBalance: m (v - v0 - written) less the known impulses; the clamp's part dropped at the cap)
            float cap = StructureContactLoads.PhysicsSpeedCap;
            sup = ContactBalance.Support(m, vel, v0, written, thrJ, airJ, g0, x0, dt, cap);
            if (ContactBalance.AtCap(ContactBalance.AfterStep(vel, written, thrJ, airJ, m), cap)) StructureContactLoads.AtCapSteps++;
            _cSupJ += sup; _cSupT += dt;
            float c2 = contactJ.LengthSquared();
            if (c2 > 1e-6f && contactJ.Length() >= 0.5f * contactAbs)
            {
                float k = Vector3.Dot(sup, contactJ) / c2;
                if (float.IsFinite(k)) scale = Math.Clamp(k, 0f, StructureContactLoads.ScaleMax);
            }
        }
        // (per cell: the step's total, then into the window - a cell's peak is its whole step, not its largest point; a
        //  retractable gear's block takes GearContactShare of it: its leg folds first)
        foreach (var kv in _cStep) AddContact(kv.Key, kv.Value * (scale * (_gearCells.Contains(kv.Key) ? GearContactShare : 1f)));
        contactJ *= scale;
        // the gears' touchdowns (for the gear's collapse)
        if (_wheels.Count > 0)
        {
            Vector3 w = rb.AngularVelocity;
            Vector3 comW = (Vector3)WorldTransform.Transform(LastCoM, wt);
            foreach (var ws in _wheels)
            {
                bool now = ws.stepJ.LengthSquared() > 1e-6f;
                var t = ws.touch;
                if (now)
                {
                    float jMag = ws.stepJ.Length() * scale;
                    if (!ws.touching)
                    {
                        // (touchdown: the block's own velocity - the ship's at that point - along its up axis, before this step's hit
                        //  slowed it: the last read's velocity)
                        Vector3 pW = (Vector3)WorldTransform.Transform(ws.posLocal, wt);
                        Vector3 vPoint = v0 + Vector3.Cross(w, pW - comW);
                        Vector3 upW = WorldTransform.TransformDirection(ws.upLocal, wt);
                        t.TouchdownSink = -Vector3.Dot(vPoint, upW);
                        t.PeakLoadN = 0f;
                    }
                    t.StepLoadN = jMag / dt;
                    float pk = jMag / MathF.Max(StructureContactLoads.CrushSeconds, 1e-3f);
                    if (pk > t.PeakLoadN) t.PeakLoadN = pk;
                    t.Touching = true; t.Stamp++;
                }
                else if (ws.touching) { t.Touching = false; t.StepLoadN = 0f; t.Stamp++; }
                ws.touching = now;
            }
        }
        // the remainder over the last step (locked plates; grid-local): the support less the contacts, its peak crushed as
        // the hull's are (a step's impulse over CrushSeconds)
        if (had && m > 0f && _plates.Count > 0)
        {
            Vector3 resJ = WorldTransform.TransformDirectionInv(sup - contactJ, wt);
            _cResJ += resJ; _cResT += dt;
            var pk = resJ / MathF.Max(StructureContactLoads.CrushSeconds, 1e-3f);
            if (pk.LengthSquared() > _cResPeak.LengthSquared()) _cResPeak = pk;
        }
    }

    /// <summary>At an evaluation's snapshot: the window's contacts as forces (grid-local point, N), and a fresh window.</summary>
    void TakeContacts(StructureEval.Loads L)
    {
        L.ContactP.Clear(); L.ContactF.Clear();
        float T = MathF.Max(_cWindow, 1e-3f), crush = MathF.Max(StructureContactLoads.CrushSeconds, 1e-3f);
        Vector3 total = Vector3.Zero;
        foreach (var kv in _cAcc)
        {
            var mean = kv.Value.SumJ / T; var hit = kv.Value.Peak / crush;
            var f = hit.LengthSquared() > mean.LengthSquared() ? hit : mean;
            L.ContactP.Add(new Vector3(kv.Key.X, kv.Key.Y, kv.Key.Z) * 0.25f);
            L.ContactF.Add(f);
            total += f;
        }
        if (_cAcc.Count > 0) StructureContactLoads.Last = $"{Entity?.DebugName}: {_cAcc.Count} contact cells, {total.Length() / 1000f:F1} kN as loaded; mean hull {_cHullJ.Length() / T / 1000f:F1} kN, wheels {_cWheelJ.Length() / T / 1000f:F1} kN, weight {LastMass * PhysicsHack.GetGravityDirection(Data).Length() / 1000f:F1} kN, the physics' support {(_cSupT > 0f ? _cSupJ.Length() / _cSupT : 0f) / 1000f:F1} kN, over {T:F2} s";
        // the remainder onto the locked plates, equally (the larger of the window's mean and its peak step)
        if (_cResT > 0.05f)
        {
            int locked = 0; foreach (var (mg, _) in _plates) if (mg.IsLocked) locked++;
            if (locked > 0)
            {
                Vector3 mean = _cResJ / _cResT;
                Vector3 held = _cResPeak.LengthSquared() > mean.LengthSquared() ? _cResPeak : mean;
                Vector3 each = held / locked;
                foreach (var (mg, cell) in _plates)
                {
                    if (!mg.IsLocked) continue;
                    L.ContactP.Add(new Vector3(cell.X, cell.Y, cell.Z) * 0.25f);
                    L.ContactF.Add(each);
                }
                StructureContactLoads.LastHeld = $"{Entity?.DebugName}: {locked} locked, holding {mean.Length() / 1000f:F1} kN (peak {_cResPeak.Length() / 1000f:F1}; weight {LastMass * PhysicsHack.GetGravityDirection(Data).Length() / 1000f:F1} kN)";
            }
        }
        ClearContactWindow();
    }
}
