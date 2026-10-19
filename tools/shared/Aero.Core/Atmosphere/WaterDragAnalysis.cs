// ═══════════════════════════════════════════════════════════════════════════════
// SE2 Water Drag System — Reverse-Engineering Analysis
// ═══════════════════════════════════════════════════════════════════════════════
//
// Source files analyzed:
//   GridHitReceiverComponent.cs    — per-block force application (ReceiveWaterHit)
//   WaterBuoyancy.cs               — drag & buoyancy math helpers
//   WaterColliderSessionComponent.cs — session-level orchestration
//   WaterHit.cs                    — parameter struct
//   CubeBlockComponent.cs          — EffectivePermeability
//   CubeBlockDefinition.cs         — Permeability definition
//   RigidBodyDataFunctions.cs      — ApplyImpulseAt, GetLinearVelocityAtPoint
//
// Status: PLACEHOLDER — numbers need validation against live game. This analysis
//         is based on decompiled code and may not reflect runtime tuning values.
//
// ═══════════════════════════════════════════════════════════════════════════════
//
// ┌─────────────────────────────────────────────────────────────────────────────┐
// │ 1. SYSTEM OVERVIEW                                                         │
// └─────────────────────────────────────────────────────────────────────────────┘
//
// The water drag system is NOT a face-based or surface-based model. It is a
// volumetric corner-sampling approach:
//
//   For each block in the submerged region:
//     For each of the 8 AABB corners:
//       1. Check if corner is underwater (depth > 0)
//       2. Compute submersion fraction from depth
//       3. Compute "area normal" as direction from block center to corner
//       4. Apply buoyancy + drag impulse at the corner's world position
//
// There is NO exposure/occlusion check. Interior blocks receive the same
// forces as surface blocks. A solid 10×10×10 cube applies 8000 force samples
// when only ~600 surface blocks are physically relevant.
//
// ┌─────────────────────────────────────────────────────────────────────────────┐
// │ 2. CALL CHAIN                                                              │
// └─────────────────────────────────────────────────────────────────────────────┘
//
// WaterColliderSessionComponent.ApplyForcesToDynamicBodies()          [every 5 ticks]
//   └─ For each dynamic entity intersecting water:
//        └─ GridHitReceiverComponent.ReceiveWaterHit(WaterHit)
//             ├─ GetWritePtr<RigidBodyData>()                        [mutable physics]
//             ├─ GetReadPtr<RigidBodyMassProperties>()               [mass, CoM, inertia]
//             ├─ GetGridOceanBox() → BoundingBoxI queryRange         [spatial filter]
//             └─ For each block in CubeBlockStorage.EnumerateBlocks(queryRange):
//                  └─ For each corner of block.AABB:
//                       ├─ WaterBuoyancy.GetBuoyancy(power, volume, gravityDir)
//                       ├─ WaterBuoyancy.GetDrag(power, area, areaNormal, velocity)
//                       └─ rb.ApplyImpulseAt(mass, worldTransform, cornerPos, impulse)
//
// Scheduling: [SparseUpdate(5)] = runs every 5 simulation ticks, NOT every frame.
//
// ┌─────────────────────────────────────────────────────────────────────────────┐
// │ 3. THE DRAG FORMULA                                                        │
// └─────────────────────────────────────────────────────────────────────────────┘
//
// WaterBuoyancy.GetDrag(power, area, areaNormal, velocity):
//
//   v²    = velocity.LengthSquared()
//   v̂     = normalize(velocity)
//   dot   = v̂ · areaNormal
//
//   if dot > 0:                              ← only corners "facing" the velocity
//     F_drag = -v̂ × (v²/2) × (area × dot) × power
//   else:
//     F_drag = 0
//
// Expanding:
//   F_drag = -v̂ × power × area × (v̂ · n̂) × (v²/2)
//
// This is structurally identical to:
//   F = -v̂ × Cd × A_projected × q
//
// where:
//   Cd       = power         (the "Drag" multiplier from WaterHit)
//   A_proj   = area × cos(θ) (projected area of this corner's share)
//   q        = v²/2          (dynamic pressure — BUT MISSING DENSITY!)
//
// ┌─────────────────────────────────────────────────────────────────────────────┐
// │ 4. CRITICAL OBSERVATION: NO DENSITY IN DRAG                                │
// └─────────────────────────────────────────────────────────────────────────────┘
//
// The standard drag equation is:  F = ½ρv² × Cd × A
//
// SE2's water drag is:            F = (v²/2) × Cd × A_proj
//
// There is NO fluid density (ρ) term. The "power" multiplier from WaterHit.Drag
// is a session-wide tuning constant, not a physical density. This means:
//
//   - Drag force scales with v², not with ½ρv²
//   - The magnitude is controlled entirely by the WaterHit.Drag multiplier
//   - Water density has no physical meaning in the drag calculation
//   - The same formula would give the same drag on a light moon as a heavy planet
//     (gravity affects buoyancy direction but not drag magnitude)
//
// For our atmospheric model, we MUST include ρ: F = ½ρv² × Cp × A
//
// ┌─────────────────────────────────────────────────────────────────────────────┐
// │ 5. THE "AREA NORMAL" HACK                                                  │
// └─────────────────────────────────────────────────────────────────────────────┘
//
// areaNormal = normalize(cornerWorldPos - blockCenterWorldPos)
//
// This is NOT a face normal. It's always a diagonal direction pointing toward
// one of 8 corners, with components ≈ (±0.577, ±0.577, ±0.577) for a cube.
//
// Effects:
//   - Maximum dot(v̂, n̂) for any corner is ~0.577 (vs 1.0 for a real face)
//   - The dot product distributes force across corners based on alignment
//     with velocity, creating a crude directional bias
//   - For face-on flow (+X), the 4 corners with +X component contribute;
//     the 4 with -X component are filtered out by the dot > 0 check
//   - Corner force acts at the corner position, creating torque about CoM
//
// Geometric consequence: a block moving face-on gets drag contributions from
// 4 corners (the +X half), each with dot ≈ 0.577. A block moving corner-on
// (+X,+Y,+Z) gets drag from 1 corner with dot ≈ 1.0 and 3 corners with
// dot ≈ 0.333. The total projected area is NOT the same — the model does
// not correctly reproduce the directional drag variation of a cube.
//
// ┌─────────────────────────────────────────────────────────────────────────────┐
// │ 6. SUBMERSION MODEL                                                        │
// └─────────────────────────────────────────────────────────────────────────────┘
//
// depth   = water surface distance (from ocean sphere or volumetric water)
// halfExt = block AABB half-extents diagonal length
// frac    = clamp(depth, 0, halfExt) / halfExt          ← [0, 1]
//
// Per corner:
//   volume_share = (block.Volume / 8) × frac            ← for buoyancy
//   area_share   = (block.SurfaceArea / 8) × frac       ← for drag
//
// A fully submerged corner (frac = 1.0) gets 1/8 of the block's total surface
// area and volume. All 8 corners fully submerged = full block contribution.
//
// Partial submersion (waterline cuts through block) naturally produces fewer
// contributing corners, giving a smooth transition at the surface.
//
// ┌─────────────────────────────────────────────────────────────────────────────┐
// │ 7. BUOYANCY                                                                │
// └─────────────────────────────────────────────────────────────────────────────┘
//
// WaterBuoyancy.GetBuoyancy(power, volume, gravityDir):
//   F_buoyancy = -gravityDir × volume × power
//
// Where:
//   power    = WaterHit.Buoyancy × (1 - block.EffectivePermeability)
//   volume   = volume_share (see above)
//
// Permeability: blocks flagged as "permeable" (hollow/grated) in their
// definition reduce buoyancy but NOT drag. A grate block gets full drag
// but reduced buoyancy — physically backwards (a grate should have less
// drag and similar buoyancy if it traps air).
//
// EffectivePermeability = round(1 - (isPerm ? 0 : 1) × buildProgress)
// So a fully built non-permeable block has permeability 0 (full buoyancy),
// and a permeable block always has permeability 1 (zero buoyancy).
//
// ┌─────────────────────────────────────────────────────────────────────────────┐
// │ 8. PER-CORNER VELOCITY                                                     │
// └─────────────────────────────────────────────────────────────────────────────┘
//
// velocity = rb.GetLinearVelocityAtPoint(mass, worldTransform, cornerWorldPos)
//          = linearVelocity + angularVelocity × (cornerPos - CoM)
//
// This is one thing they got right. Each corner sees a different velocity due
// to rotation, so a spinning block experiences different drag at each corner.
// This creates aerodynamic damping of angular velocity — a tumbling ship will
// have its rotation slowed by water drag, which is physically correct.
//
// For our aero model, we should do the same: compute velocity at each face
// center, not just use the grid's CoM velocity. This gives us rotational
// damping for free.
//
// ┌─────────────────────────────────────────────────────────────────────────────┐
// │ 9. DENSITY PENALTY                                                         │
// └─────────────────────────────────────────────────────────────────────────────┘
//
// if (block.Definition.Density.MassCurveModifier >= hitData.DensityPenaltyThreshold)
//   volume_share *= DensityPenaltyMultiplier;
//
// Blocks above a density threshold get their buoyancy volume reduced. This
// makes heavy blocks (armor, heavy machinery) sink while lighter blocks
// (scaffolding, structural) float. Only affects buoyancy, not drag.
//
// ┌─────────────────────────────────────────────────────────────────────────────┐
// │ 10. WATER DAMAGE                                                           │
// └─────────────────────────────────────────────────────────────────────────────┘
//
// Alongside forces, the system applies damage to blocks hitting water at speed:
//
//   if corner is NOT deeply submerged (frac < DamageSubmergedThreshold):
//     speed_into_water = dot(velocity, gravityDir)
//     if speed_into_water >= DamageVelocityThreshold:
//       damage = clamp(DamageMultiplier × speed, 0, MaxDamage)
//       deformation = clamp(Deformation × speed × area_share, 0, MaxDeformation)
//
// Only shallow corners (near the waterline) take damage — deep corners are
// already cushioned. This gives belly-flop / water-impact behavior.
//
// ┌─────────────────────────────────────────────────────────────────────────────┐
// │ 11. FORCE APPLICATION                                                      │
// └─────────────────────────────────────────────────────────────────────────────┘
//
// impulse = buoyancy + drag + waterFlow
//
// waterFlow = WaterSessionComponent.GetWaterFlow(cornerPos) × FlowMultiplier
//   → currents push objects; applied uniformly to all submerged corners
//
// Applied via:
//   rb.ApplyImpulseAt(mass, worldTransform, cornerWorldPos, impulse)
//
// ApplyImpulseAt does:
//   linearVelocity  += impulse × invMass
//   angularVelocity += invInertiaTensor × ((cornerPos - CoM) × impulse)
//
// So each corner impulse affects both translation AND rotation.
// This means an asymmetrically submerged ship will torque — correct behavior.
//
// Guard: if impulse.Max() > 100000, it's skipped (explosion protection).
//
// ┌─────────────────────────────────────────────────────────────────────────────┐
// │ 12. MAX TORQUE BYPASS                                                      │
// └─────────────────────────────────────────────────────────────────────────────┘
//
// if (grid.MaxTorqueData.MaxTorque > 0) → flag2 = false → skip buoyancy/drag
//
// Grids with MaxTorque > 0 (gyroscope-stabilized?) skip water forces entirely.
// Only damage is still applied. This might be a landing gear / locked grid
// optimization, or a bug. Needs runtime verification.
//
// ┌─────────────────────────────────────────────────────────────────────────────┐
// │ 13. SCHEDULING                                                             │
// └─────────────────────────────────────────────────────────────────────────────┘
//
// [SparseUpdate(5)] on ApplyForcesToDynamicBodies means water forces are
// applied every 5 simulation ticks, not every tick. At 60 ticks/sec this is
// 12 Hz. The WaterHit.Drag/Buoyancy multipliers presumably account for this
// (i.e., they are 5× what they'd be at 60 Hz), but this is unconfirmed.
//
// Our aero system could use similar amortization — recalculate forces every
// N ticks and scale impulses accordingly.
//
// ┌─────────────────────────────────────────────────────────────────────────────┐
// │ 14. SUMMARY OF PHYSICAL ERRORS                                             │
// └─────────────────────────────────────────────────────────────────────────────┘
//
// ┌────────────────────────────┬───────────────────────────────────────────────┐
// │ Issue                      │ Impact                                        │
// ├────────────────────────────┼───────────────────────────────────────────────┤
// │ No exposure check          │ Interior blocks contribute forces they        │
// │                            │ shouldn't. Solid shapes get too much drag.    │
// ├────────────────────────────┼───────────────────────────────────────────────┤
// │ No fluid density (ρ)       │ Drag has no physical basis. Tuning constants  │
// │                            │ do all the work. Can't model different fluids.│
// ├────────────────────────────┼───────────────────────────────────────────────┤
// │ Diagonal normals           │ Force direction is always diagonal, never     │
// │                            │ axis-aligned. A flat plate doesn't generate   │
// │                            │ a purely opposing force as it should.         │
// ├────────────────────────────┼───────────────────────────────────────────────┤
// │ No shape awareness         │ All blocks are AABBs. Slopes, wedges, and     │
// │                            │ non-cube shapes are treated as full cubes.    │
// ├────────────────────────────┼───────────────────────────────────────────────┤
// │ Permeability backwards     │ Permeable blocks reduce buoyancy but not      │
// │                            │ drag. Physically a grate has LESS drag, not   │
// │                            │ less buoyancy.                                │
// ├────────────────────────────┼───────────────────────────────────────────────┤
// │ O(blocks) not O(surface)   │ Performance scales with total block count,    │
// │                            │ not surface area. 10× penalty for solid grids.│
// └────────────────────────────┴───────────────────────────────────────────────┘
//
// ┌─────────────────────────────────────────────────────────────────────────────┐
// │ 15. WHAT WE CAN LEARN FROM IT                                              │
// └─────────────────────────────────────────────────────────────────────────────┘
//
// Despite the errors, the system demonstrates some good patterns:
//
//   ✓ Per-point velocity (GetLinearVelocityAtPoint) — rotation-aware drag
//   ✓ ApplyImpulseAt for both force and torque — single call does both
//   ✓ SparseUpdate for amortization — don't compute every tick
//   ✓ Spatial query filter (GetGridOceanBox) — only iterate relevant blocks
//   ✓ Smooth waterline transition via per-corner depth fraction
//   ✓ Water current as additive force — simple and effective
//   ✓ Impact damage at waterline — separate from drag forces
//
// Our atmospheric drag should adopt these patterns while fixing the physics:
//   - Exposed faces only (surface cache), not all blocks
//   - Real face normals, not diagonal corner directions
//   - Include air density (ρ) from AtmosphereModel
//   - Shape-aware normals for slopes, corners, rounds
//   - O(surface faces) or O(unique normals), not O(total blocks)
//
// ═══════════════════════════════════════════════════════════════════════════════
