# Propulsor blocks: design specs

These are the agreed specs for every propulsor block. Modelling follows [MODELLING_RULES.md](MODELLING_RULES.md).
Blender convention: +Y is the block's forward (the thrust direction), +Z is up.

## Common to all propulsors

- **Blades:** three per rotor or prop. They're thick in section (nothing flat or thin), with angular planforms, smooth shading, and a hazard-stripe band on every tip.
- **Spinners and hubs:** round noses.
- **Materials:** from the SE2 library:
  - PaintedMetalColorable for the body;
  - PaintedMetalDarker for recesses and blades;
  - BrushedMetal and MetalDull for mechanical parts;
  - WarningStripes for the blade tips.
- **Style:** SE hard-surface: boxy, grid-aligned, chamfered solids. Mount faces are flush with the bounding box.
- **Spinning parts** (blades and disc) are separate from the static body and sit inside the block's occupied cells.

## Turboprop: 3 sizes

| Block | Cell | Nacelle (1 × 1 × 2 cells) | Prop disc (3 × 3 × 1 cells, directly ahead of the nacelle) |
|---|---|---|---|
| Turboprop_050 | 0.5 m | 0.5 × 0.5 × 1.0 m | 1.5 m across, 0.5 m deep |
| Turboprop_150 | 1.5 m | 1.5 × 1.5 × 3.0 m | 4.5 m across, 1.5 m deep |
| Turboprop_250 | 2.5 m | 2.5 × 2.5 × 5.0 m | 7.5 m across, 2.5 m deep |

The prop tips reach 1.2 cells from the axis, inside the disc's ±1.5 cells. This layout replaces the earlier 3 × 1 × 1 nacelle with a 5 × 5 disc (2026-10-16, the rigged models).

- The occupancy is T-shaped: the nacelle cells plus the disc layer.
- **Nacelle layout, front to back.** The 0.5 m block is designed first; the others scale from it.
  1. **Spinner:** a round nose, with a dark recess around it in the cowl's front face.
  2. **Cowl:** an octagon with big corners. Its front tapers in a frustum to the spinner face.
  3. **Groove:** a recessed ring between the cowl and the mid body.
  4. **Mid body:**
     - smaller corner chamfers than the cowl;
     - a framed window on each side, with compressor discs visible inside;
     - toward the back the sides pull in (the waist) while the top stays flush;
     - the belly sweeps up toward the tail.
  5. **Rear block:** the mount. It's flush with the box on all sides and the back, with chamfered corners and ends.
  6. **Chin intake:** under the cowl, with an angled face and a dark mouth. It sits **inside** the bounding box.
- **Mount faces:** the top (one long flush spine) and the rear block.
- **No** exhaust nozzle.

## Helicopter rotors: 3 sizes, plus a tail rotor

| Block | Cell | Mast (1 × 1 × 1 cell) | Rotor disc (15 × 15 × 1 cells, on top) | Blade tip radius | Hinge radius |
|---|---|---|---|---|---|
| HelicopterRotor_050 | 0.5 m | 0.5 × 0.5 × 0.5 m | 7.5 m across, 0.5 m deep | 3.5 m (7 cells) | 0.25 m |
| HelicopterRotor_100 | 1 m | 1 × 1 × 1 m | 15 m across, 1 m deep | 7 m (7 cells) | 0.5 m |
| HelicopterRotor_150 | 1.5 m | 1.5 × 1.5 × 1.5 m | 22.5 m across, 1.5 m deep | 10.5 m (7 cells) | 0.75 m |
| HelicopterRotor_250 | 2.5 m | 2.5 × 2.5 × 2.5 m | 37.5 m across, 2.5 m deep | 17.5 m (7 cells) | 1.25 m |

The 1 m rotor (2026-10-23, the user) is the 0.5 m rotor scaled 2x whole (`tools/meshgen/rotor_scale_size.py`): 400 kN by disc area, 316 kW. Longer blades (2026-10-23, the user): the tip went from 5 to 7 cells, thrust kept. Only the blade's constant-chord middle was stretched; the root grip and the striped tip are unchanged (`tools/meshgen/rotor_lengthen.py` on the export scenes).
| HelicopterTailRotor_050 | 0.5 m | 0.5 × 0.5 × 0.5 m | 2.5 m across (5 × 5 × 1 cells), 0.5 m deep | 1.25 m (2.5 cells) | 0.25 m |

- **Sizes:** the 1.5 m rotor is designed first. The 2.5 m one is an exact × 5/3 copy, and the 0.5 m one an exact × 1/3 copy.
- **Disc:** the blade tips stop half a cell inside the disc layer's edge.
- **Parts:**
  - a full-size gearbox standing on the block floor;
  - a thin mast;
  - a swashplate whose stationary disc sits on the gearbox's top rim;
  - a hub with three blade grips and a Jesus nut;
  - 3 blades on flap hinges.
- **Tail rotor:** the 0.5 m rotor with half-length blades, on a 5 × 5 disc. It's a reversible thruster along its spin axis. Its own G-menu group, "Reversible Rotors" (a group's tile shows only its first block kind).
- **Animation:** governed RPM, collective pitch, cyclic tilt, blade coning, and a blur disc at speed.
  - The head (hub, swashplate, pitch links) spins as one part.
  - Each blade pitches about its own span at its flap hinge.
  - The blur disc runs from the hinge radius to the tips.
- **Static thrust:**
  - 100 kN for HelicopterRotor_050, 900 kN for _150 and 2.5 MN for _250: one disc loading (2.6 kN/m² since the longer blades, 2026-10-23 - was 5.1), by disc area from the 1.5 m rotor's 900 kN on its 10.5 m tips (2026-10-18: 1.5x for honest dampeners, where a hover costs 2x the planet weight in real thrust; was 67 kN / 600 kN / 1.667 MN). RotorKinematics.ReactionScale went 0.42 → 0.344 with it, so a tail at the tip radius still needs 10 % of rated thrust;
  - 38 kN for the tail rotor (the Reversible Rotor; 2026-10-18 the user lowered the helicopter torque: a tip-radius tail needs 3 % of rated thrust, RotorKinematics.ReactionScale 0.1032; was 128 kN, 85, 60): one of them at the 1.5 m rotor's tip radius behind its mast holds that rotor's reaction torque at its rated thrust with the limiter's 80 % margin to spare (38 kN × 10.5 m × 0.8 = 319 kN m against 203), so that rotor is never capped below 100 %. Two at the 2.5 m rotor's tip radius hold all of it (109 %). (Since the longer blades, 2026-10-23: the torque is unchanged, the tip-radius arm 1.4x, so a tip-radius tail needs 2.2 % of rated thrust.)
- **Power:** the helicopter rotors draw half the vanilla atmospheric thruster's power per newton (0.79 kW per kN); the Reversible Rotor and the ducted fans draw the vanilla 1.58 kW per kN; the turboprops 0.32 kW per kN (2026-10-17: the kings of subsonic thrust per kW, see "Speed roles").
- **Structure loads (2026-10-17):** the cyclic's tilt force, the torque it gives and the rotor's reaction torque are applied to the body at the centre of mass, and published at the rotor's hub for the structure (`RotorCyclic.PublishLoads` → `PublishBlockLoad`), with the torque adjusted so the ship's total moment is unchanged. isEngine is true: they are the rotor's thrust tilted and its drive's torque, so they switch off with Structure.ThrustLoads as its axial thrust does. The rotor already has the game's ThrusterComponent, so it is rigid anyway and no rebuild happens. The tilt force is noted for the contact balance (not a contact).
- **Cyclic trim (2026-10-18):** the cyclic also answers the thrusters' own torque - a rotor whose lift does not pass through the centre of mass, a tail thruster's pitch or roll (`RotorTrim`). Before the thruster solve it is planned against the thrusters' torque at the game's share; while that is within its reach the solve weighs in-plane torque low (it gives the yaw and stays near the game's share), and after the solve the cyclic is given what the solved thrusters left. Before, it answered only the damping or the pilot's turn, and a lone rotor's offset lift is no torque the thrusters can move with the net force kept: an unpiloted helicopter with its centre of mass 0.8 m off the rotor axis pitched over at a steady rate (honest dampeners about 8 deg/s, vanilla about 2) and fell on its per-axis hold (in game: 12.5 m/s² at 66 deg, rotor at 26 %). Now it stays level and hovers at 2 weights / its rotor (3x: 67 %). Not on the ground: a ship at rest held up by something (slow, not turning, thrust and gravity out of balance) is not trimmed, so a parked helicopter spooling up does not tilt its disc into a sideways push. A single tail rotor's yaw thrust is still a real sideways force (about 2 m/s² on the 30 t test helicopter): unpiloted nothing rolls the ship into it, so it drifts; two tails making a yaw couple hold their place. Tests/ThrustTests `TiltHeliChecks`. Hover hold (the autopilot, 2026-10-19: `Scripts/Simulation/Autopilot`) leans it into that thrust and holds its place - Tests/AutopilotSim.
- **Reaction torque:** calibrated like a real helicopter's. At rated thrust, a tail thruster at the rotor's tip radius needs 10 % of the rotor's thrust; beyond the rating the torque grows faster than the thrust.
- **Counter-rotating (2026-10-23):** a main rotor's terminal checkbox (`AeroRotorSpinComponent`, saved with the block; host / single player). On, it turns counter-clockwise seen from above:
  - its reaction torque flips (`RotorKinematics.ReactionAxis`), so an opposite pair (coaxial or tandem) cancels;
  - the limiter counts the opposite rotor's reaction as anti-torque (`RotorLimiter.Targets`): such a pair needs no tail;
  - strikes move and dump momentum the other way;
  - the client draws the mirror image with the mirrored blade set (subparts named `RotorBladeCCW`; without them the normal blades turn backwards).
- **Controllability limit:** a main rotor's thrust is capped to what the ship can hold against its torque. The cap keeps the reaction within 80 % of the yaw authority about the rotor's axis. Any source of torque counts, each once:
  - reversible thrusters at an arm, both ways;
  - one-way thrusters at an arm that push the countering way;
  - gyros;
  - control surfaces: what they have left toward the countering side from their trim, at 75 % (the share the attitude law plans with). They depend on airspeed: nothing at hover, a rudder's worth in forward flight. A reach measured in faster air is not kept once the ship slows.

  The airframe's own torque (a fin weathervaning) isn't an actuator and doesn't count. With no anti-torque at all, the rotor gives no lift.

## Ducted fans: 4 sizes × 3 housings (12 blocks)

| Size | Face | Thickness | Notes |
|---|---|---|---|
| 050 | 0.5 m | 0.25 m | The drone fan |
| 150 | 1.5 m | 0.5 m | |
| 250 | 2.5 m | 1.5 m | |
| 750 | 7.5 m | 2.5 m | |

- **Housings:** square, D-shaped and octagonal. Each has a round bore.
- **Inside:** a hub with a nose cone, 3 wide blades, and 3 stators behind the blades.
- **Fit:** the blades sit entirely inside the housing; the face and thickness are the block's bounding box.

## Ramjets: 4 sizes (built 2026-10-17)

The modeller's kitbash of each vanilla atmospheric thruster into a ramjet with an inlet spike (`D:\Blender Brojects\SE2\Ramjet`, exports in `TurbopropBlock\Export\Ramjet_<N>`). Wired by `gen_propulsor_defs.py` (`gen_ramjet`) and the mod (`RamjetLaw`, `AeroPropulsorComponent`, `AeroRamjetSpikeComponent`).

| Block | Kit | Cell | Cells | Box | Rating (N) | Hydrogen at full (per s) | Spike free travel |
|---|---|---|---|---|---|---|---|
| Ramjet_050 | Atmo100 | 0.5 m | 1 × 1 × 4 | 0.5 × 0.5 × 2 m | 60 kN | 0.25 | 37.5 mm |
| Ramjet_150 | Atmo250 | 1.5 m | 1 × 1 × 3 | 1.5 × 1.5 × 4.5 m | 430 kN | 1.595 | 112.5 mm |
| Ramjet_250 | Atmo500 | 2.5 m | 1 × 1 × 4 | 2.5 × 2.5 × 10 m | 2.27 MN | 4.79 | 537.5 mm |
| Ramjet_750 | Atmo1000 | 2.5 m | 3 × 3 × 8 | 7.5 × 7.5 × 20 m | 23.2 MN | 47.85 | 787.5 mm |

- **Frame:** as the turboprops. Thrust pushes toward the intake (VRage (0, 0, −1)); the intake is the spike's probe tip on the box's front face, the exhaust at +Z. Mount faces: the four sides only (the exported `mountpoints_` slabs).
- **Rating (the user):** 1.5 × the vanilla atmospheric thruster it was built from (40 000 / 287 136.3 / 1 516 383 / 15 465 370 N), as its thrust at the curve's 100 % (Mach 2.5–3.5). Its definition's `ThrustPower` is the rating.
- **Thrust:** max = rating × `ThrustProfiles.Ramjet`(true local Mach) × the density factor (the mod's air density, as the other propulsors: 0 in vacuum, at most × 1.25) × `InletAngle.Step` (the ramjet inlet, its own unstart state). Nothing below Mach 0.5, 1.05 at Mach 3, 0.2 at Mach 6, nothing past it. Written as the game's own `MaxThrustPower` every 3 ticks (`AeroPropulsorComponent`); no extra force. The Jet curve and the jet inlet never touch it (a propulsor block, and hydrogen class). Aero off: a plain hydrogen thruster at its rating, as every propulsor goes back to vanilla.
- **Fuel: hydrogen (the user).** Each block is a copy of the vanilla **HydrogenThruster250** chain (thrust class Hydrogen; its hydrogen `PowerableBlockDefinition` and `ConveyorComponent`), so it draws hydrogen through conveyors like a hydrogen thruster. Four conveyor ports (the vanilla hydrogen thrusters' port dummies), one on each side mount face: 0.5 m ports on the 050 / 150, 1.5 m on the 250 / 750. Recipes: its paired hydrogen thruster's.
  - **Rate:** 1/3 of its paired vanilla hydrogen thruster's hydrogen per kN (the kit's pair: Atmo100 → H50 0.0125, Atmo250 → H200 0.01113, Atmo500 → H250 0.00633, Atmo1000 → H750 0.00619 per kN per s): a ramjet carries only its fuel, its oxidiser is the air (~3 × a rocket's specific impulse). Its `ResourcesRequiredToThrust` = rating × that ÷ 3.
  - **How the game charges it:** a hydrogen thruster burns its whole `ResourcesRequiredToThrust` on every tick it fires (`ThrusterComponent`'s `SingleFrameBinaryResourceConsumptionBuffer`, on while the grid asks thrust its way - `IsThrusting` - whatever its maximum or throttle). Left alone, a ramjet below Mach 0.5 on a boosted ship would burn its full rate for no thrust.
  - **So the burn follows the thrust:** every tick, before the game's resource distributor charges it, `AeroPropulsorComponent.FuelTick` holds the game's burn off on the ticks `RamjetLaw.Burn` leaves out of each 12-tick window: it burns on round(duty × 12) of them (the remainder carried), duty = its thrust share now (max ÷ rating, at most 1). No thrust (below Mach 0.5, past 6, vacuum): no hydrogen. Per kN of the thrust it has, it always burns 1/3 of its pair's. The burn is only ever switched on while the game fires it with hydrogen supplied. Knob `RamjetLaw.FuelFollowsThrust` (off: the game's flat burn). The buffer is reached by reflection once per block (`RamjetFuelBridge`, docs/REFLECTION_AND_WHITELIST.md).
- **Inlet spike (SR-71 style):** one subpart (`Subpart01/Ramjet_<N>_S1_Spike`, entity `RamjetSpike`, dummy `RamjetSpike` of its own type at the spike's base on the axis). Rest pose fully forward. Held forward below Mach 1.6, retracts linearly aft (+Z) to its full free travel at Mach 3.2 and holds; never beyond its free travel; a full travel in 1.5 s at most. It doesn't spin. Visual only, on the client (`AeroRamjetSpikeComponent`): the grid's published flight Mach (`AeroEntryFx.TryGetFlow`, ~10 Hz). The spike is found by name, else as the block's only child, else by place.
- **Flame:** the game's flame definition stays empty (no particle effect: a reference to the vanilla hydrogen flame fails the content build - the stand-in Vanilla only carries the partial bases' chains, "Cannot locate value for definition"). The flame is the mod's own - see **Exhaust**.
- **Exhaust (2026-10-17, the user's design; `Fx/ExhaustLaw.cs`, `Fx/AeroExhaustComponent.cs`, content `gen_propulsor_defs.py` `gen_exhaust`):** an almost clear, blue-violet hydrogen flame - copies of the vanilla hydrogen flame's main, secondary, core and heat-shimmer emitters, retinted, alpha low - at the block's exhaust (the flame dummy; exit radius 0.384 x the box width: 0.19 / 0.58 / 0.96 / 2.88 m), driven by the client every frame:
  - **nothing below Mach 0.5** (gated in to full by Mach 0.65); its strength (emission, brightness) is the thrust it gives now as a share of its rating (the controller's / game's thrust share x its Mach, air and inlet factor), so it **lights up as the thrust builds**, and its length grows with it (1.5 to 7 exit radii);
  - **shock diamonds:** none up to Mach 1.1, then more with the Mach (1 to 5, all from Mach 4), each a bright knot held in the engine's frame, spaced 1.2 + 0.5 x sqrt(M^2 - 1) exit radii (shock cells lengthen with the Mach) from 1 radius aft, **sharper** (smaller, down to 0.45 x) and **brighter** with the Mach, each 0.72 x fainter down the plume; only while the flame is at least 15 %;
  - **inlet unstart:** a **pop** (the flame it had, 1.8 x bright for 70 ms), a 0.35 s **sputter**, then **out** until the inlet relights; it comes back from nothing as the inlet spools up;
  - aero off: a plain hydrogen thruster's flame (its share, no Mach gate, no diamonds). Knobs: `aeroset ExhaustLaw.*` / `AeroExhaustComponent.*` (harness: `DevMach`, `DevThrust`, `DevUnstart`).
- **Menu:** its own G-menu group, "Ramjets".
- **Speed ladder (Tests/ThrustTests `RamjetChecks`, sea level, axial):** per rated newton the ramjet passes the jet from Mach 1.98; per block of a kit (its 1.5 × rating) from Mach 1.88 (2026-10-17, after the jet's ram rise; before it 1.78 and 1.28; unchanged by the 2026-10-18 retune - the jet's curve from Mach 1.8 on is the same). It leads from Mach 2 to 6; the turboprop gives nothing past Mach 0.75. On the Atmo500 kit: Mach 1.5 - jet 2957 kN (2502 before the 2026-10-18 retune), Ramjet_250 1476 kN; Mach 2.0 - jet 1243 kN, ramjet 1930 kN; Mach 3 - jet 0, ramjet 2384 kN. In thinner air both scale alike (the jet by the game's Atmospheric efficiency, 1/3 at 0.4 kg/m³; the ramjet by ρ / 1.225, 0.33 there), so the crossovers hold at altitude.

## Speed roles (the user, 2026-10-17)

Each engine leads thrust per kW in its own band, and Mach 1 is a real wall.

| Band | King | Thrust vs its rating |
|---|---|---|
| Subsonic, up to ~Mach 0.6 | **Turboprop** | Full static thrust, then constant power (∝ 1/true airspeed past 100 m/s - from 40 on 2026-10-18); fades from Mach 0.6, gone at 0.75 (`ThrustProfiles.TurbopropFraction`, at the local Mach). 0.32 kW per kN. |
| Through the barrier, ~Mach 0.6–2 | **Vanilla atmospheric** (a jet) | The subsonic lapse, then a ram rise above the rating - **no transonic pinch** (`ThrustProfiles.Jet`, `JetMach1` 1.08, `JetRamPeak` 1.98 at `JetRamPeakMach` 1.6; 2026-10-18, the user: "1 and 2"): 100% at rest, 95% at Mach 0.6, 85% at 0.85, **108% at 1.0**, 128% at 1.1, 148% at 1.2, 168% at 1.3, 194% at 1.45, **198% at 1.6**, 185% at 1.7, 155% at 1.8, 120% at 1.9, 82% at 2.0 (below the ramjet from here), 45% at 2.2, 25% at 2.4, 0 at 2.8. 2026-10-17 to 10-18 (the pinch): 80% at 0.95, 76% at 1.0, 95% at 1.1, 112% at 1.2, 130% at 1.3, 148% at 1.4, 165% at 1.5, 177% at 1.6, 180% at 1.7. Before that: 65% at 0.95, 55% at 1.0, 60% at 1.1, 80% at 1.3, 90% at 1.6, 60% at 2.0. |
| Supersonic, ~Mach 2–6 | **Ramjet** (built 2026-10-17, see "Ramjets") | Zero static thrust; needs a boost to ~Mach 0.5; 100 % at Mach 2.5–3.5 (105 % at 3); fades by Mach 6. Burns hydrogen. |
| Hypersonic (later) | Scramjet | Mach 4–15 curve kept in `ThrustProfiles`. |

- Hydrogen and ion stay flat (rockets).
- **How the jet curve is applied (built 2026-10-17):** in normal play, each vanilla atmospheric thruster's real maximum thrust is its rating × the curve at the grid's Mach (its airspeed over the local speed of sound), updated every 3 ticks (`JetCurve`). It's the game's own thrust, with no extra force, and the game's air-density effect stacks on top: the Atmospheric thrust class gives full thrust down to 0.8 kg/m³ and none at 0.2 (`ThrustClassesConfiguration.def`, `GetThrustEfficiency`), a third at 0.4. The ram rise is written above the rating (at most `JetCurve.MaxFactor` 2×); the game's power draw is the definition's, flat, so the rise is more thrust per kW. Our propulsor blocks are atmospheric thrusters underneath but keep their own curves. When the curve is lifted (aero or thrust torque off), the rating is written back.
- **The propulsors' Mach (fixed 2026-10-17):** the turboprop, fan and rotor curves take the true airspeed (the wind out) and the Mach over the local speed of sound: the grid's `LastAirVel` / `LastMach`, the same source as the Jet curve. They used the ground speed over a fixed 330 m/s. With aero off, the propulsors' ratings are written back (plain vanilla thrusters), as the Jet curve does. The design probe (build hints, `GetThrustAlong`, the top speed) takes a propulsor's rating with its kind's curve at the probed Mach. It used to take the live maximum, which already carries the airspeed factor, rpm and torque cap.
- A bricky ship stalls out subsonic on the hull's wave drag (a brick's peaks at 3.2× at Mach 1.05).
- **The Mach wall (Tests/AeroBench `machwall`, 2026-10-17): the hull's transonic drag reshaped, then made reachable the same day ("fix this").**
  - **The first law:** the sleekest fixture, the F-6 Diamondback (43 t, streamlining 1.0 nose first, 41 m² frontal), needed a static thrust-to-weight of **8.3** at sea level to break Mach 1. Its drag rose 3.3× by Mach 1.05 and kept rising to about 4× by Mach 1.2: a wave peak of only 1.2×, but on top of the full blend to the Newtonian table (about 3.6× its subsonic drag). In level flight it topped out at Mach 1.05.
  - **The sleek hull's wall:** a sleek hull's wall is its wave drag, not the Newtonian blend.
    - The peak is sharp, at Mach 1.0 (the engines' pinch): **1.12×** its subsonic drag (`ForceTable.WavePeakNeedle`; 1.45× in the morning's version).
    - Past it the drag falls fast, as (1.0 / M)^8: 1.05× at Mach 1.1, 1.01× at 1.3.
    - The hull keeps its subsonic pressure recovery through the jet band. The blend to the Newtonian table is held back from Mach 0.75–0.85 up to 1.6, and is back in full by Mach 3: 1.5× at Mach 2, 3.6× at 3, so hypersonic flight and entry keep the Newtonian law.
    - This grows from streamlining 0.5 to 1 (`ForceTable.HullWave`, `BlendHold`). A brick is unchanged.
    - Below Mach 0.75 the drag is exactly the old law's at every streamlining. The gate checks this on the F-6, the brick and the Tu-134 (to its Mach 0.79 L/D calibration), at 1.225 and 0.4 kg/m³.
  - **The goals (the user, "fix this"):** the F-6 breaks Mach 1 in level flight at a static sea-level T/W of 1.5 in air of 0.4 kg/m³, and at 2.5 or less at sea level; Mach 1 stays the hardest point; once through, it cruises on to ≥ Mach 1.4 (preferably ~1.6) on the same thrust; a brick stalls subsonic on that thrust; the subsonic drag is untouched.
  - **The real chain at altitude:** the game's AirData density gives the mod's air (`AtmosphereBridge`: the ISA temperature of that density, so a = 298 m/s at 0.4 kg/m³) and the Mach the curve reads; the thrust is the rating × the Jet curve × the Atmospheric class's efficiency (1/3 at 0.4). The drag there is 0.25× the sea-level drag at the same Mach and the thrust 0.33×, so the altitude case asks 0.75× the sea-level T/W, and **it is the binding goal**: T/W 1.5 at 0.4 kg/m³ is T/W ~2.0 at sea level.
  - **The levers, in order:** the Jet curve's pinch 0.55 → **0.76** at Mach 1 (still its lowest point in the transonic band) and the **ram rise** (above the rating from Mach 1.2, 1.8× at Mach 1.7: past the bump the supersonic drag is the subsonic drag area again, which grows as M², so holding Mach 1.6 at T/W ~2 needs ~1.75× there); then the sleek hull's peak 1.45× → **1.12×**. With no curve at all the F-6 would need T/W 1.56 at sea level, so the pinch still costs a quarter.
  - **Before → now (the F-6, static sea-level T/W; Tests/AeroBench `machwall` prints this table):**

    | | Before | Now |
    |---|---|---|
    | T/W to break Mach 1, sea level | 3.43 | **1.94** |
    | its bottleneck Mach | 1.00 | 1.01 |
    | top level-flight Mach on that thrust | 1.50 | **1.62** |
    | T/W to break Mach 1, 0.8 kg/m³ | 2.02 | 1.15 |
    | T/W to break Mach 1, 0.4 kg/m³ | 2.58 | **1.46** |
    | its bottleneck Mach | 1.00 | 1.01 |
    | top level-flight Mach on that thrust | 1.50 | 1.62 |
    | top Mach on T/W 1.5 at 0.4 kg/m³ | 0.92 | **1.64** |
    | top Mach on T/W 2.5 at sea level | 0.95 | 1.74 |
    | a brick's stall Mach (same frontal area, on the sea-level crossing thrust) | 0.65 | **0.50** |

    (The "before" density figures use the bridge's speed of sound; the morning's report took 340 m/s at every density, hence its 2.24 at 0.8.)
  - Past the crossing, up to Mach 1.4, nothing asks more than 93 % of the crossing thrust (1.81 of 1.94, at Mach 1.05).
- **Mach 1 retuned (2026-10-18, the user: "1 and 2" - soften the pinch, scale the block wings' wave drag).** The wings' own wave drag (fix a-wing-wave, 12b97e7: each wing from its own t/c, sweep and CL - Lift/README.md) put the F-6 back behind a wall: **T/W 3.36** at sea level, 2.53 at 0.4 kg/m³, its drag at Mach 1 1.92× its subsonic. Block wings are 12-19 % thick, real fighters' 4-6 %. For reference: a real F-16 goes supersonic at sea level on about T/W 0.75; SE ships are about twice as draggy per ton (big frontal areas). Two levers:
  - **(1) No pinch.** A real afterburning engine doesn't choke through Mach 1; its sea-level thrust rises with ram from the high subsonic on. The curve now rises from its subsonic low (85 % at Mach 0.85) through Mach 1 (108 %) to 198 % at Mach 1.6. With no pinch to make Mach 1 the costly point, the rise must keep up with the drag past it (the hull's grows as M²) for the cruise to stay easier than the crossing - so it is steeper and higher than the old ram rise (194 % at 1.45 where it was ~156 %). From Mach 1.8 on it is unchanged: the ramjet still takes over at Mach 1.98 per rated newton, 1.88 per block of a kit.
  - **(2) Block wings read as thinner** (`CompressibleWingModel.BlockWingThicknessScale` 0.4: the wave drag takes 0.4 × a wing's own t/c - the F-6's 0.12-0.19 read as 0.05-0.075). Thickness and sweep still order the wings (thin swept vs thick straight: 7.8× less at Mach 1.2, 7.3× at Mach 2, as at their own t/c).
  - **The goals (Tests/AeroBench `machwall`, whole model):** the F-6 breaks Mach 1 at T/W 1.35-1.65 at sea level and ≤ 1.3 at 0.4 kg/m³, Mach 1 the bottleneck, on to ≥ Mach 1.4 on that thrust, supersonic on T/W 1.5 at 0.4 kg/m³; the wing terms still cost (above the hull wall's, and the real-t/c wings more); a brick stalls subsonic; nothing changes below Mach 0.75. The 2026-10-17 hull-only goals (≤ 2.5 / 1.5 on the pinch) are retired with the pinch.

    | the F-6 (static sea-level T/W) | 12b97e7 | Now |
    |---|---|---|
    | T/W to break Mach 1, sea level | 3.36 | **1.55** |
    | its bottleneck Mach | 1.01 | 1.02 |
    | top level-flight Mach on that thrust | 1.73 | **1.44** |
    | T/W to break Mach 1, 0.8 kg/m³ | 1.99 | 0.92 |
    | T/W to break Mach 1, 0.4 kg/m³ | 2.53 | **1.17** |
    | top Mach on T/W 1.5 at 0.4 kg/m³ | 0.89 | 1.64 |
    | top Mach on T/W 2.5 at sea level | 0.91 | 1.74 |
    | drag / subsonic drag area at Mach 0.8 / 0.9 / 1.0 / 1.2 / 1.5 / 2 / 3 | 1.12 / 1.69 / 1.92 / 1.86 / 1.56 / 1.64 / 3.58 | 1.01 / 1.17 / 1.26 / 1.17 / 1.09 / 1.51 / 3.58 |
    | a brick's stall Mach (same frontal area, on the sea-level crossing thrust) | 0.65 | **0.45** |

    With the wings' terms off the same curve asks T/W 1.37; at the wings' own t/c 2.38.
- **Power per kW (Tests/ThrustTests power ladder, 2026-10-17): the turboprop route.** The turboprops draw **0.32 kW per kN** (`gen_propulsor_defs.py`; from 1.58). Constant power starts at **100 m/s** (`ThrustProfiles.TurbopropV0`; 40 until 2026-10-18), so they lead thrust per kW (sea level, axial) against **every** vanilla atmospheric until their tip fade ends the band. The jet takes over:
  - from Mach **0.655** against the 1000 (1.03 kW/kN, the vanilla's most efficient);
  - from **0.685** against the 500 (1.58 kW/kN);
  - from **0.695** against the 100 (1.88 kW/kN);
  - from **0.705** against the 250 (2.26 kW/kN).
  (Crossovers measured with TurbopropV0 100 before the 2026-10-18 jet retune; re-run Tests/ThrustTests power ladder.)

  Each of those leads from there to Mach 2 (with the ram rise, by far: 1.98× its rating at Mach 1.6 since 2026-10-18; 1.8× at Mach 1.7 before).

  **Raw thrust too:** the 2.5 m turboprop (1.9 MN) out-thrusts the AtmosphericThruster500 (1.516 MN) up to about Mach 0.4 (at 40 m/s it was only to ~Mach 0.15).

  The ramjet profile takes over from the jet from Mach 1.98, at the vanilla 500's power per newton (1.8 before the ram rise; the jet's curve falls below the ramjet's from Mach 2.0 by design). Vanilla is unchanged.

## Solid rocket motor (the user, 2026-10-17; built 2026-10-17; the conveyor version 2026-10-17)

- **Blocks (8):** `SolidSegment_050/150/250/750` and `SolidNozzle_050/150/250/750` (the modeller's own models, `D:\Blender Brojects\SE2\SolidRocket\solid_spec.json`; the nozzle kitbashed from the vanilla thruster). Segments 1x2x1 cells (0.5 / 1.5 / 2.5 m grids; the 750 is 3x5x3 on 2.5 m), nozzles 1x3x1 (750: 3x7x3). G-menu group **Solid Rockets**. Generated by `tools/meshgen/gen_propulsor_defs.py` (`gen_solid`), which also writes `Scripts/Simulation/Blocks/SolidRocketBlocks.g.cs`.
- **The conveyor version** (the user, 2026-10-17: "do the conveyor version"; "they connect ONLY to other solid-rocket blocks, never to the vanilla conveyor network"; spent is spent; normal build components): the propellant is a **real resource of the game**, "Solid Propellant" (its own `ResourceTypeDefinition`, `RequiresConveyors`, litres = kg; the name has no localisation entry, so the text itself shows, as the mod's block names). Segments are its tanks, the nozzle draws it like a hydrogen thruster draws hydrogen, and the game carries it through the solid blocks' own conveyor ports.
- **Definitions:** a segment is a block on BasePowerableBlockBreakable (the gears' base) with no power (`ConsumedResource` null), plus a **ConveyorComponent** (the mod's own definition, no subgraph: the game builds it from the ports) and a **ResourceContainerComponent** (its tank, server-only as the game's tanks: capacity one full segment, **charging rate 0** - never refilled -, discharge rate twice the nozzle's full rate (`SolidRocket.TankDischargeRate`: a lone tank between two firing nozzles feeds both), `InfiniteSourceInCreative` false - creative and NPC grids burn too -, **built full** by its server prefab's `CurrentChargeLevel 1`; the game saves the level). It keeps its BreakableBlockComponent: a two-port block without one would become a conveyor LINE with no node and its tank would drop out of the game's groups (`ConveyorGraphPostProcessor`). A nozzle copies the vanilla HydrogenThruster250 chain with its ConveyorComponent (the mod's definition) and burns Solid Propellant: `ConsumedResource` = the resource (needs it present), `ResourcesRequiredToThrust` = one segment's mass flow × 3 (`SolidRocket.GameRate`; the mod duty-cycles it to the thrust, see Propellant). Mount faces come from the exported slabs: segments Up and Down, nozzles Up only. Recipes: segments use the vanilla hydrogen tank recipe of their class (Small for 0.5 and 1.5 m, Medium for 2.5 m, Large for 7.5 m), nozzles use their paired hydrogen thruster's. Dry mass comes from the game's density curve (the tanks' density).
- **Ports:** ConveyorHole dummies (pipeable, no hand access) on the face centres, facing out - a segment's top and bottom, a nozzle's top - just inside the face, where the modeller's red coupling hatches are (centred to 6 mm, flush; the hatches stay as modelled, visual). Each size has its **own port scale**, close to its hatch (0.26 / 0.78 / 1.30 / 1.30 m): **0.27 m (0.5 m), 0.77 m (1.5 m), 1.27 m (2.5 m), 1.37 m (7.5 m)** - the 2.5 m and 7.5 m sit on the same grid and could touch. No vanilla port has these scales (vanilla: 0.35, 0.5, 1.025, 1.5 m). The game links two ports only face to face within 0.0625 m with the same X / Y scale within 1e-4 (`ConveyorElementTranslator.GetConveyorPath`; the dummy type is never compared, no size, mount or facing check) - so a solid block links only to solid blocks of its size, never to a vanilla conveyor (`SolidRocket.PortsLink`, tested on the generated dummies).
- **Stacking:** a nozzle's motor is the continuous chain of segments of its size straight on its top face, along its Up axis, in the same grid, co-axial and touching face to face (`SolidRocket.FindStack`). A gap, a wrong-size, turned, offset or **broken** segment ends the chain. Because the ports sit on the face centres at the size's own scale, this chain IS the nozzle's conveyor group: two solid blocks link only co-axial, touching and of one size, and a segment has two ports that each meet at most one other - the network cannot branch. (The game's group itself is not read: its sets change on the game's own threads; the geometry gives the same chain without a cross-thread read.) A non-working segment drops its tank and its link for the game, and so ends the chain for the mod too. A segment outside a chain is inert, and its terminal says so. **A chain with a nozzle at each end is one grain:** the two lit nozzles share its factor (√n / 2 each - the same gas through two throats; `SolidRocket.ShareFactor`, `OtherEnd`); one lit nozzle takes it all. The stack is searched again when a solid block is added, removed, moved, turned, broken or repaired.
- **Propellant:** the game's, in the segments' tanks; the mod only reads them (`AeroSolidSegmentComponent`, server). A new block is full, and segments never refill (charging rate 0; no mod code writes a tank). **Every tank holding propellant burns at once** (the user, 2026-10-17: option 3): the game draws from all the tanks of a conveyor group together, in proportion to what each can give (equal tanks drain evenly). **The burn follows the thrust exactly:** the game charges the nozzle's `ResourcesRequiredToThrust` whole on every tick it fires (all or nothing), so the mod lets it burn on only `SolidRocket.BurnDuty` of the ticks - the nozzle's share of the stack factor × its share of full / 3 - over 12-tick windows (`DutyCycle`, shared with the ramjets; the game's consumption buffer through `RamjetFuelBridge`), so level and steering both set the burn; the average is exact, the last window can end up to one window early. **Burnout:** the game's sink is all-or-nothing, so when the tanks hold less than one tick of the nozzle's rate the game cuts the thrust itself; the motor ends there too (`SolidRocket.Usable`; a crumb under one tick stays in the tanks). **Mass falls as it burns:** the game doesn't weigh stored resources, so the segment is an `IDynamicMassProvider` of its tank's charge (1 L = 1 kg) and notifies the grid's mass computer every 2.5 % of a segment (its tanks are left out of `AeroFuelMassComponent`: one provider). **No backdoor shutdown:** the game's own tank panel (Auto / Charge / Discharge) and on / off show on a segment (generic panels no definition can hide); Charge would starve the nozzle and off would drop the tank, so the mod puts both back (Auto, on) the next tick. Old saves: the mod-tracked key `aero-solid:1 <fraction>` from before the conveyor version is read and ignored (the segment loads with its prefab's full tank).
- **Ignition:** the nozzle's terminal or toolbar button **Ignite**. The nozzle stays switched off until lit, so the game's movement, dampeners, hover and the mod's allocator never fire it (it is also left out of the mod's thruster set). When lit, the nozzle is switched on and forced with the game's own thrust override; there is no phantom force. It burns until the stack is empty with no shutdown: the player's on/off and override are put back. Then the override and switch are handed back **in one tick** and the motor is spent. (Before 2026-10-17 the end held override 1 for one tick - up to 3× the vacuum thrust for 1/60 s. Now the switch-off disables the block at once (Enabled → Working → DisabledTag, one signal chain), so the game's `OnThrustOverrideRemoved` sees a disabled thruster: it takes the overridden thrust out of its books and books nothing back; `OnThrustersChanged` then has nothing to move. Had the tag come a frame later, the removal would book the rating back and the stop take it out - balanced too. When the game cut the supply first, its stop-working runs `OnThrustOverrideChanged` before `OnThrustersChanged` (the job order), which balances as well.) A nozzle broken while lit stays lit: the game gives it no thrust and draws nothing while it is broken; repaired, it burns on. A burning motor is saved and resumes after loading. ThrustTorque takes the rocket's thrust out of what it shares among the thrusters and applies the rocket's own offset torque (r × F).
- **Power level** (the user, 2026-10-17: "once a solid is on it's on. It must be at least 50% power, which you can set in thrust override prior. It does not respond to rotational control unless it's already on, in which case it reacts to rotational demands"):
  - **Set before ignition** with the nozzle's thrust override slider: level = the slider clamped to 50..100 %; unset (0) = 100 % (`SolidRocket.LevelFrom`). The terminal shows it ("Power level (%)"). While idle the mod leaves the slider alone (the switched-off thruster books nothing from it).
  - **Latched at ignition** and saved with the burn (`aero-solidnozzle:1 1 <level>`; a key without a level reads 100 %). Ignite with an override set takes three ticks: the player's override is removed while still switched off, then the switch goes on, then the motor's own override. (An override already present when a thruster is switched on is booked into the game's controllable maximum by `GridMovementCollectorComponent.OnThrusterChanged`, and its override thrust cancels out - so it comes off first. The rest of the order is unchanged: switch on, THEN override; at the end the override removed and the switch off in one tick - see Ignition.)
  - **Base thrust** = level × the stack's full thrust (`ThrustAt(thrust × its share of the stack factor, p)`). **The burn follows the thrust** (mass flow × the share of full, `SolidRocket.BurnRate`, given to the game as `BurnDuty`), so the total impulse is the same at any level: 50 % burns twice as long.
  - **After ignition the slider no longer changes it:** the slider now shows the motor's own override (its real thrust as a share of the nozzle's ThrustPower, which is vacuum thrust × 3, so a 1-segment motor at 100 % reads about 30 %), and any change the player makes is put back next tick. The locked level is shown in the terminal. (Showing the player's slider value instead isn't possible: the override is the thrust.)
- **Structure loads (2026-10-17):** every server tick it thrusts, a nozzle publishes its whole thrust now (`ThrustNow`: the game's override on it, its base or the allocator's steer, so base + steering) along its push at its block (`AeroSolidNozzleComponent.PublishLoad` → `PublishBlockLoad`, isEngine: off with Structure.ThrustLoads; rigid as a thruster, which it is). It is counted once: the nozzle is out of the thruster cache, and the structure's thruster loads (ThrF / ThrF0) are the cache's shares of the game's thrust less the rockets' (`gameF -= solidF`). Its offset torque is the moment of that force, found from the point. The thrusters' answer to its steering (`KeepNetThrust`) is their own thrust, in ThrF.
- **Steering (lit only):** a lit nozzle is a bounded slot of the attitude allocator (`ThrustTorque.SolveWithSolids`, `ThrustAllocator.Solve` with `lo`): bounds 50..100 % of the stack's full thrust at the current air, its base preferred exactly as a reversible thruster prefers the game's share, so it holds its level and moves only for torque. Its force counts at its base in the net force the solve keeps: the base is its own push, and only its departure from the base belongs to the allocator, so the one-way thrusters take back what they can of it (`KeepNetThrust`, applied as their real thrust change). The solved share is written through the nozzle's thrust override (the game's real thrust) from its next tick, and its burn follows. Its torque is applied at the thrust the game gives it now. Both torque branches use it: steered (target orientation, aero-aware or not; with the aware law the gyros take what they can of the need beyond the rockets' base torque, so a rocket stays at its level while the gyros suffice) and rate mode (a lit rocket makes the thrusters answer the turn rate, as reversible thrusters and rotors do). Unlit (idle, clearing, enabling) a nozzle is never in the solve. A single nozzle on the centre line has no torque arm and holds its level. Strap-on boosters steer by throttling differentially. At 100 % a nozzle can only throttle down. A steer older than 3 ticks (aero off, a far grid stepped slowly) is dropped, and the motor holds its base. The client copy keeps the rocket as an outside torque (the stack is server-only).
- **Numbers (proposed; the user tunes them in `gen_propulsor_defs.py`):**

| Size | Sea-level thrust (2 × vanilla H) | Vacuum thrust (ThrustPower = this × 3) | Propellant per segment (tank, L = kg) | Burn per segment | Nozzle burn while firing (`GameRate`) | Tank discharge | Port scale |
|---|---|---|---|---|---|---|---|
| 0.5 m | 120 kN (H50 60 kN) | 132 kN | 391.6 kg | 8 s | 146.8 L/s | 293.7 L/s | 0.27 m |
| 1.5 m | 719 kN (H200 359.5 kN) | 790.9 kN | 2,346 kg | 8 s | 879.8 L/s | 1,759.6 L/s | 0.77 m |
| 2.5 m | 3.79 MN (H250 1.896 MN) | 4.169 MN | 12,367 kg | 8 s | 4,637.7 L/s | 9,275.3 L/s | 1.27 m |
| 7.5 m | 38.8 MN (H750 19.40 MN) | 42.68 MN | 126,608 kg | 8 s | 47,478 L/s | 94,956 L/s | 1.37 m |

  Thrust is flat with speed. It is +10 % in vacuum, linear in the air density on the game's scale (a vanilla planet's sea level is 1: p = density, clamped 0..1 - the grid's aero air once aero has read it, the game's before; 2026-10-17: it was density / 1.225 on the steps the force model ran and the game's raw otherwise, so p jumped 0.85 to 0.69 as a held grid moved). One full segment's total impulse is thrust × 8 s, regardless of the stack. **Stack law:** n segments burning together give √n × the thrust and burn √n × faster, so n full segments burn 8 s × √n at √n × the thrust (1: 1× for 8 s; 2: 1.41× for 11.3 s; 4: 2× for 16 s; 9: 3× for 24 s). The throat caps the factor at 3 (9 segments); a taller stack burns longer at 3×. The nozzle's ThrustPower is its vacuum thrust × 3 (room for a full-throat stack); the mod overrides it to the share it wants (`SolidRocket.StackFactor`, `MaxStackFactor`). The burn rate doesn't depend on the air. The table's thrust is for one segment.
- **Exhaust (2026-10-17, the user's design; `Fx/ExhaustLaw.cs`, `Fx/AeroExhaustComponent.cs`, content `gen_propulsor_defs.py` `gen_exhaust`):** the game's flame definition stays empty (`SOLID_FLAME_EFFECT` None on purpose - a vanilla particle reference fails the build, and the game's animator would draw the override, not the stack); the exhaust is the mod's own, at the bell's exit (exit radius from the spec: 0.19 / 0.58 / 0.96 / 2.86 m), from the client copy's `ThrustNow` (the game's overridden thrust) and the air:
  - a big bright **orange-white plume** (a white-hot core - the vanilla hydrogen main flame retinted - and the rocket launcher trail's two fire emitters) with an orange light; its **length follows the thrust**: 7 x (thrust / one segment's sea-level thrust)^0.5 exit radii, at most 16 - so the stack factor, the power level and the steering all show (one segment 7 radii, 2 segments 8.3, 9 segments 12.1, level 50 % 4.9);
  - a **thick white-grey smoke trail** laid in the air (world space, no motion inherited: it stays where it was made and lingers - each particle 1.5 to 7 s, the rate up to 90/s, plus a slower billow; at most 800 smoke particles per nozzle, `MaxSmokers` 8 trails at once), its emitter stretched along the flight at speed so the trail has no gaps;
  - **thin air / vacuum:** the plume **balloons** (a wide dim cone, strength x (1 - p)^0.7, up to 3.5 x wider; the core dims to 55 %) and the **smoke thins away** (strength x p, alpha sqrt(p), none below 2 % of sea-level pressure), p the pressure ratio;
  - **burnout:** a quick 0.7 s **sputter** from the last thrust, then nothing (the trail fades on its own).
  The thrust and its sound are the game's own.

## Rotor strikes (the user, 2026-10-17; built 2026-10-17)

For helicopter rotors (all sizes), the Reversible Rotor and turboprop props. Ducted fans and ramjets don't strike. Code: `Propulsion/RotorStrike.cs` (the game-free laws, Tests/RotorStrikeTests), `AeroRotorStrikeComponent.cs` (the probes, the blades' state and save) and `RotorStrikeServer.cs` (each strike's consequences).

- **Detection (server)**
  - **The probe pattern.** Each tick, every rotor or prop turning above 3 % rpm casts **4 rays** over its disc (`RotorStrike.RaysPerTick`):
    - 3 of the 12 chords of its blade-tip circle, round-robin;
    - 1 of 16 radial spokes, from 0.3 R to the tip, in a spread order.
  - **Coverage.** The whole tip circle is covered every **4 ticks** (1/15 s), at any rpm. The pattern is geometric, and the blades sweep the circle many times in between. A chord's middle lies 3.4 % of R inside the tip. The spokes cover the disc's inside in 16 ticks (the four quarters in 4).
  - **The rays** are `IPhysics.CastRay`, synchronous, run in the rotor's own job after the grids' systems and before the physics step. The world isn't stepping then, as for the gear's ground ray. They write into a reused native hit list, so they allocate nothing.
  - **What counts.** Every body along a ray is reported, and the closest one that isn't the rotor itself is taken.
    - Skipped: the rotor's own block (a hit on its grid inside its own cells, with one cell of slack) and its subparts' bodies.
    - Counted: the rest of its ship, other grids, terrain and characters.
  - **The disc** lies in the block's plane: X / Z for a rotor, X / Y for a prop. Coning and the cyclic's tilt aren't followed. The hub is the block's centre plus the table's hub offset.
  - **A strike** is a hit where the blade moves at **2 m/s or more**: v = rpm × 200 m/s × r / R. Every governed propulsor's tips run at 200 m/s at 100 % rpm.
  - **After a strike** the rotor's probes rest until the strike has been handled and a blade pass has gone by (3 blades, at least 4 ticks). An obstacle that stays in the disc is struck once per blade pass: every 5 ticks for the 1.5 m rotor at full rpm, every 8 for the 2.5 m one.
- **Blade damage**
  - **Wear per strike:** **0.34 × hardness × (v / 200 m/s)²** of the blades' health (1 = whole). Hardness: rock 1, metal 0.6, character 0.15. At full speed that breaks the blades in 3 rock strikes, 5 metal strikes or 20 character strikes.
  - **Light touches.** A single brush at 30 % rpm costs about 3 % (a nick), and one at 5 % rpm almost nothing.
  - **Broken** (health 0):
    - the rotor stops dead (rpm 0);
    - its maximum thrust is written as 0, whatever the aero switch;
    - the client draws only the hub / spinner as a stub (no blades, no blur, no spin).
  - **Saved** per block (`aero-blades:1 health`, only when below 1; `AeroBlockSave`).
- **Repair (the rule)**
  - **Wear marks the block.** The blades are part of the block, so each strike's wear is also dealt to the rotor block's integrity through the game's damage:
    - half the wear (the blades are half the block);
    - at least 1 % of the block's health;
    - never down to its destruction threshold.
  - **Welding restores the blades.** Welding the block back to full integrity (≥ 99.9 %) makes the blades whole. A broken rotor has lost at least half its health, so it needs real welding.
  - **Rules that refuse damage refuse wear.** If the world's rules refuse damage to the rotor block (`IGameplayRules.CanDamage`), the blades take no wear either. Blades can never be stuck broken on a block that can't be welded.
  - **After loading** the same rule applies: worn blades on a block at full integrity are whole.
- **The struck object**
  - **Blocks** (another grid's or the ship's own) lose **0.15 × (v / 200)² × min(m_eff / 30 kg, 3)** of their full health per strike, through the game's `GridDamageReceiverComponent.DealDamage`. Damage stages, sounds and destruction work as for any damage. At full speed, a strike takes this much of a block:
    - the 1.5 m rotor's tip: 16 %;
    - the 2.5 m rotor's tip: 45 %;
    - the tail rotor's tip: 0.5 %.
  - **Voxels: no scrape.** The voxel editing API isn't a mod reference, so this part is skipped.
  - **Characters (players)** take **(v / 45 m/s)²** of their full health through their own `CharacterLifeComponent.DoDamage`, with the game's Accident damage source. It's a normal death, and the respawn screen says why.
    - One strike beyond a quarter of the radius at full rpm kills.
    - A disc turning at 5 % rpm only bruises: at most 5 % of a character's health, at the tip.
    - The damage is dealt only where the world's rules allow it.
  - **Knock-back** for a character is the blade's impulse along its motion (see below), capped at 15 m/s of the character's mass. It goes through `RigidBodyDataFunctions.TryApplyImpulse`, the game's explosion knock-back path.
- **The ship.** The reactions are real impulses through the engine's `RigidBodyDataFunctions` (the AeroPhysics path). No other force is added.
- **Per-blade breaks (2026-10-23; helicopter main rotors and the Reversible Rotor - turboprops still break all at once)**
  - Each blade: **whole / tip lost / stub**, plus its own wear. The server keeps the blades' phase; a hit is the blade nearest its azimuth that still **reaches** it (`RotorStrike.Reach`: tip, split radius, stub end) - a short blade leaves a gap, and the probes reach only as far as the longest blade left.
  - A strike's wear goes to that one blade (`BladeWearOne` = the rotor's x 3). At **0.5** or more it snaps at once (full-speed rock; metal from ~0.91 of tip speed; a character never); smaller strikes chip it, and wear 1 snaps it. Outboard of the split radius a whole blade loses its **tip** (chunk B); anywhere else, or a blade already without its tip, it breaks to the **stub** (chunk A, and B).
  - **Flight:** thrust, the reaction torque and the rating the limiter and the cyclic see scale with the **lift share** left - per blade (e^3 - h^3) / (R^3 - h^3), e its reach, h the hinge (lift ~ r^2); a lost tip leaves ~27 % of that blade, a stub 0.
  - **Imbalance:** the lost pieces' missing pull, omega^2 x their mass x radius, turning with the blades, at most **50 %** of the rating (`RotorStrike.Imbalance`) - an impulse at the hub every tick, into the body and the structure. One lost tip on the 1.5 m rotor at 100 %: ~200 kN.
  - **Shootable blades (2026-10-23):** the main rotors' collision carries a thin disc out to the stubs' end (`tools/meshgen/rotor_blade_disc.py`; not spinning, always on). A shot or impact on it damages the block as any; the block's damage signal (`OnAfterTakingDamageSignal`, its impact line) gives the blade passing there 4 x the damage's share of the block's health as wear (`AeroRotorStrikeComponent.OnBlockDamaged`) - it may snap there.
  - **Depth (2026-10-23):** the struck blade's share beyond the hit (0 a graze at its end, 1 buried to its hinge): its wear x (1 + 3 x depth) (`RotorStrike.DepthWear`).
  - **Repulsion:** the ship is thrown off what it hit, along the surface's normal: half the strike's impulse x (1 + 2 x depth) (`RotorStrike.Repel`; another grid takes it back). 1.5 m rotor on rock at 190 m/s: 7.5 kN s grazing, 22.6 buried.
  - **Wear costs lift (2026-10-23):** a blade at wear w lifts 1 - 0.35 w of its length's share (`RotorStrike.WearLiftLoss`, `BladeLift`); thrust, torque and the rating follow.
  - **Uneven lift:** each blade's thrust less the mean, along the axis at its lift centroid - a moment turning with the blades, at most 25 % of thrust x radius (`LiftImbalance`). One lost tip on the 1.5 m rotor at 900 kN: ~840 kN m.
  - **Wobble (visual):** the disc tips once a turn with the lost pieces, x rpm^2, at most 3 deg (`RotorStrike.Wobble`, `AeroRotorAnimComponent`).
  - **The head stays:** a non-functional block loses every subpart (hub, stubs and all), so the blades' wear on their block stops just above its functional line - destruction threshold + `MinFunctionalBuildProgress` x the rest (`FunctionalLine`; 1 = any damage, then none is dealt) - and above 75 % (`OwnBlockFloor`: its critical fractures, the body's halves). Repair: the block damaged and welded back to full (grind it a little first if the wear left it whole).
  - **Every blade at its stub:** no thrust; the hub isn't driven and **coasts down**, the stubs turning. No stop and no angular-momentum dump (the pieces fly off with theirs).
  - **Visuals (client):** each blade shows its whole / tip-lost / stub model (the blade-break content at the same dummies; the mirrored set too); stubs drawn at any rpm; the blur disc fades with the blades left. A lost piece is thrown as **debris** (`BladeDebris`): the game's damaged-block debris prefab with the chunk's model and hull, at the blade's speed there (hub velocity + omega x r), tumbling, its table mass; gone after ~20 s, at most 24 at once; this PC only. `Fx.DebrisScale` 0: none.
  - Table: `Blocks/BladeBreakBlocks.g.cs` (generated with the content): hinge, stub and split radii, each chunk's model, mass, centre, size. Save: `aero-blades:1 health c0 c1 c2 w0 w1 w2`; an older single-health key loads as every blade worn alike (0: every blade a stub).
  - **At the strike point**, against the blade's motion: J = m_eff × v × the material's speed loss (rock 1, metal 0.7; for a character, whatever the character took).
    - m_eff is the blade's mass seen at radius r: I_blade / r² = m R² / (3 r²), at most m.
    - The blade's mass is 1.75 kg/m² × R²: a composite blade of about 27 kg/m² planform with a chord of 0.065 R. For comparison, a UH-60's 8.2 m blade weighs about 115 kg.
    - A struck dynamic grid takes the opposite impulse. A strike on the ship's own blocks is an internal force, so no impulse.
  - **When the blades break**, the rotor's angular momentum L = I ω, with I = 3 × m R² / 3, is dumped into the ship about the spin axis.
  - **The structure** (2026-10-17) feels each strike through `AeroGridComponent.PublishBlockImpulse`. A strike is one tick's impulse; the structure samples its loads 4× a second, so J / dt published for one tick would be missed at 24 of 30 sampler phases (Tests/StructureTests). Instead each block's impulses are gathered and the next snapshot takes them **exactly once**, as the hull's collisions are: the larger of the window's mean (Σ J / window: a blade grinding on) and its largest single tick over the collisions' crush time `StructureContactLoads.CrushSeconds` (0.05 s: a hit). A rock strike at full rpm on the 1.5 m rotor's tip is 6.56 kN s, so 131 kN for one snapshot; a hit is never summed with another hit.
    - The ship's reaction and the break's angular momentum load the **rotor block** at the strike point: the blade carries the hit to its hub, so the strike's moment loads the rotor's mount. Offline, that strike takes a two-small-block mast to 5.4 × yield (damaged); a 2 m/s touch to 0.05.
    - A struck grid's block takes the push. A strike on the ship's own blocks moves no body, but it is a real pair of forces inside it: the push at the struck block, the reaction at the rotor block.
    - Each body's impulse is also noted for its contact balance (`NoteKnownImpulse`), so the wheels or locked plates don't carry it a second time.

| Block | R (m) | Blade (kg) | I (kg m²) | ω at 100 % (rad/s) | m_eff at tip (kg) | J rock, tip, full (N s) | L at break (N m s) | Strikes apart (ticks) |
|---|---|---|---|---|---|---|---|---|
| HelicopterRotor_050 | 2.5 | 10.9 | 68 | 80.0 | 3.6 | 729 | 5,469 | 4 |
| HelicopterRotor_150 | 7.5 | 98.4 | 5,537 | 26.7 | 32.8 | 6,562 | 147,656 | 5 |
| HelicopterRotor_250 | 12.5 | 273.4 | 42,725 | 16.0 | 91.1 | 18,229 | 683,594 | 8 |
| Reversible Rotor (TailRotor_050) | 1.25 | 2.7 | 4 | 160.0 | 0.9 | 182 | 684 | 4 |
| Turboprop_050 | 0.60 | 0.6 | 0.2 | 332 | 0.2 | 42 | 76 | 4 |
| Turboprop_150 | 1.81 | 5.7 | 19 | 111 | 1.9 | 381 | 2,062 | 4 |
| Turboprop_250 | 3.01 | 15.9 | 144 | 66.4 | 5.3 | 1,057 | 9,545 | 4 |

  **Skimming a hill**, offline: the 1.5 m rotor at 100 % rpm with a wall 0.3 m into its disc. Three strikes break the blades in 15 ticks (0.25 s). The ship takes 19.9 kN s at the rim, then 148 kN m s about the mast.
- **Effects (client)**
  - **Sparks** at the strike point: the game's mesh-collision sparks, scaled with the strike's energy. On rock, the game's voxel-hit burst is added; on a break, the fracture debris.
  - **A crunch** from the game's FMOD impacts, chosen by what was hit: `ImpactMetalRock`, `ImpactMetalMetal` or `ImpactCharacterMetal`. A break adds `DestroyMetalBlockSmall`. The sounds play through the engine's 3D one-shot.
  - **Rate:** at most one set of effects per rotor every 0.15 s, and always on a break.
  - **Delivery:** the server publishes the effects (`TearSmoke.PublishStrike`), and the client grid that is the ship's copy plays them in-process, like the torn-edge smoke.
- **Multiplayer.** The server decides every strike and all the damage. The client draws the broken state from the published blade record (`ThrustTorque.BladeState`), in-process like the rotor record.
- **Readouts**
  - `aeroget AeroRotorStrikeComponent.Summary`: each rotor's blades, strikes, repairs, rays and probe cost (µs per probing tick).
  - `aeroget RotorStrikeServer.Status`.
  - `[AERO-STRIKE]` log lines: at most 5 per second, plus every break.
  - Knob: `aeroset RotorStrike.Enabled false`.
- **To check in game**
  - The probe cost per rotor (the µs/tick in the Summary).
  - That `DealDamage` on the rotor block lowers `HealthIntegrity` immediately (a breakable block's path), so the repair rule sees the wear.
  - That the strike sparks and sounds load (the effect GUIDs and FMOD paths).
  - That a character's knock-back through `TryApplyImpulse` reaches its character body.

## Inlet angle (the user, 2026-10-17; built 2026-10-17 for the jets and the ramjets)

A thrust factor from the angle between an engine's intake and the incoming air, on top of its Mach curve. It fades out at low airspeed (a hovering ship has no meaningful "sideways air").

| Engine | Tolerance | Beyond it |
|---|---|---|
| **Ramjet** | very intolerant: full within ~8–10° | the inlet unstarts and thrust drops to near nothing; it needs a re-light delay once the flow is straight again |
| **Jet** (vanilla atmospheric) | tolerant: full within ~25–30° | a soft compressor stall to 60% sideways, 30% with air from behind; only at real speed (from 100 m/s, full by 200 m/s). Softened by the user (2026-10-17) so VTOL lift and retro braking keep working |
| **Turboprop** | doesn't care | no factor |
| Rotors, rockets | unaffected | |

**How it's built (`Propulsion/InletAngle.cs`, game-free; Tests/ThrustTests `InletChecks`):**

- **The angle θ** is between the engine's intake (its push direction: air comes in at the front) and the grid's true airspeed vector (`AeroGridComponent.LastAirVel`, the wind out), in the thruster's own grid's frame. θ = 0 is flying straight along the push.
- **The factor** is 1 − w × (1 − shape(θ)), on top of the Mach curve.
- **The low-speed fade w (per profile):** the jet none up to 100 m/s true airspeed, full from 200 m/s (~Mach 0.6); the ramjet none up to 20 m/s, full from 60 m/s; a smoothstep between. A hovering, taxiing or landing ship is untouched.
- **Jet** (vanilla atmospherics), stateless and smooth (C1, slope at most 0.035 per degree):

  | θ | Factor |
  |---|---|
  | 0–28° | 1 |
  | 28–65° | smoothstep down to 0.6 (a soft compressor stall) |
  | 65–100° | 0.6 |
  | 100–125° | smoothstep down to 0.3 (air from behind) |
  | 125–180° | 0.3 |

- **Ramjet** (the ramjet blocks: `AeroPropulsorComponent` calls `InletAngle.Step` with one `State` per engine, every write pass):
  - full within 9° (0.95 at 10°);
  - past 10° the inlet **unstarts**: the thrust falls to 0.02 within 0.15 s;
  - it stays unstarted until θ has been back inside 6° (the hysteresis) for **1.5 s** (the re-light delay), then spools back up over 0.5 s;
  - below 20 m/s the flow counts as straight, so it relights there;
  - its stateless shape (for reports): 1 to 9°, a smoothstep to 0.02 by 16°.
- **Who gets it:** `InletAngle.Of(curve)` maps Jet, Turbojet and Turbofan to the jet's profile and Ramjet and Scramjet to the ramjet's. Turboprop, Propeller, the fans, the rotors and the rockets get none.
- **How it's applied (the ramjets):** in their own `AeroPropulsorComponent` write: rating × the Ramjet Mach curve × the density factor × `InletAngle.Step` (each engine's own unstart state, stepped every 3 ticks). Their intake is their push direction in their own grid's frame.
- **How it's applied (the jets):** through the Jet curve's write. Each vanilla atmospheric's `MaxThrustPower` = rating × the Mach curve × the inlet factor, every 3 ticks: the game's own thrust, no extra force. A zero factor is written as exactly 0. Lifted, the rating is written back.
- **Knob:** `InletAngle.Enabled`. Harness: `aeroset InletAngle.Enabled false`. It is separate from `JetCurve.Enabled`.
- **No legacy path** (2026-10-17): with ThrustTorque off the game's own thrust is untouched (no Mach curve, no inlet factor). The retired `ThrustProfile.Evaluate(mach, velocityHat, intake)` lowered the Mach sideways and took the flame's direction for the intake, so straight flight read as reverse flow.
- **Reports:** `AeroApi.GetThrusters` effectiveness includes the factor in flight (`JetFactor` = Mach × inlet). The design probe and `GetThrustAlong` assume straight-in air.
- **Gameplay consequence to check in game:** above 60 m/s, a vanilla atmospheric lift thruster on a VTOL in forward flight (θ ≈ 90°) gives 15 % of its thrust. A retro thruster used to brake (θ ≈ 180°) gives nothing until the ship is below ~60 m/s, and full thrust at 20 m/s.

## Planned: fuel through a consumption modifier (2026-10-17; not built)

**Problem.** Fuel billing today (ramjets' hydrogen, solid rockets' propellant) duty-cycles the game's all-or-nothing burn by reading the private `ThrusterComponent._buffer` (reflection, `RamjetFuelBridge`, `DutyCycle`).

**Plan:**
- **Public route:** `PowerableBlockComponent.UpdateConsumedResource()` sums `RecomputeConsumedResource()` over every component on the block that implements the public `IConsumedResourceModifier`.
- **Our component** on ramjets and solid nozzles implements it and reports exactly the fuel the current thrust needs.
- **The thruster definitions** drop `ResourcesRequiredToThrust`, so the game creates no binary buffer.
- **When the rate changes**, call `UpdateConsumedResource()` (public).
- **Burnout:** the mod zeroes thrust itself when supply fails or the tanks are empty, since the game's buffer no longer does it.
- **Gain:** no reflection, and an exact, smooth burn every tick.

**To verify:**
- the injected component is in the block composition the modifier list is built from (at init);
- mod scripts may implement the interface;
- the HUD's fuel readouts.

## Open decisions

- **Thrust:** the current values are 1.9 MN for Turboprop_250, 900 kN for HelicopterRotor_150 (the rotors by disc area from it: see above) and 10.8 MN for Fan_750; the other sizes scale from these.
- **Yaw control** for rotorcraft: a fenestron or counter-rotating rotors.
- **Drag:** whether the propulsors produce aerodynamic drag.
