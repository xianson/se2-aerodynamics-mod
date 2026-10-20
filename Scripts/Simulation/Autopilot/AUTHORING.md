# Writing the Dogfight and Rocket Guidance behaviours

This guide covers the two Engage behaviours whose logic you write: **Dogfight** (`ApBehaviour.Dogfight` = 13) and **Rocket
Guidance** (`ApBehaviour.RocketGuidance` = 14). Everything around them is already built: the panel, the save, the status line, the
API, Auto's arbiter, the protection, terrain and collision avoidance, the attitude law and the actuators. You write a decision each
tick, and the core flies it through the ship's real controls.

## Data flow

```
panel (ApPanel) + instruments (ApSense) + target track -> ApPilot.FillSlot -> ApSlotCtx -> YOUR Score / Step -> ApSlotOut -> ApPilot (nav goal: style executor | direct goal: as is; rocket: lateral g cap) -> FlightDirector (protection, terrain, avoidance) -> ApCommand (attitude + keys) -> ThrustTorque (gyros, thrusters, cyclic, surfaces, gimbals)
```

## The files you write

| Slot | File | State class | Saved in |
|---|---|---|---|
| Dogfight | `Scripts/Simulation/Autopilot/ApDogfight.cs` (`ApDogfight`) | `ApDogfightState` (same file) | `ApPanel.DfSaved`, save key value 66 |
| Rocket Guidance | `Scripts/Simulation/Autopilot/ApRocketGuidance.cs` (`ApRocketGuidance`) | `ApRocketState` (same file) | `ApPanel.RgSaved`, save key value 67 |

Each slot class has these members. Everything runs on the simulation thread every tick. Don't allocate, and don't use try/catch,
in `Score` or `Step`.

| Member | When it runs | What to write |
|---|---|---|
| `static bool Implemented` | Engage begins; Auto | Return `true` once `Step` flies. While it returns `false`, choosing the behaviour is refused (`ApReason.NotImplemented`, "this behaviour is not written yet") and Auto never picks it. |
| `void Begin(ApSlotCtx c, string saved)` | The behaviour takes over (chosen, or Auto switched to it) | `State.Reset()`, then `State.Load(saved)` when `saved` isn't empty. Then catch whatever the start needs (the merge geometry, the launch time). |
| `float Score(ApSlotCtx c)` | Auto, every tick, while `Implemented` | Your utility, 0..1, on the built-in scale: AttackRun 0.2..0.7, PursuitLead 0.25..0.8, Circle 0.3. The active behaviour gets +0.15 hysteresis. `NaN` means not available, and Auto skips it. |
| `bool Step(ApSlotCtx c, ref ApSlotOut o)` | Every tick while active | Fill `o` (it arrives as `ApSlotOut.None`) and return `true`. Return `false`, or set `o.LetGo`, to let go. Chosen on the panel, the mode ends with that reason. Picked by Auto, the slot isn't asked again this engage and a built-in behaviour flies instead. |
| `State` (`readonly`) | Kept across ticks | Your own fields (phase, timers, last decision). |

The state class (`ApDogfightState` / `ApRocketState`):

| Member | What to write |
|---|---|
| fields | Anything kept between ticks. |
| `void Reset()` | Every field back to rest. Also clear `Dirty`. |
| `string Save()` | What is worth keeping across a world save, as one line of text (`""` for nothing). The core escapes it. |
| `void Load(string s)` | Read `Save()`'s text back. Tolerate junk, since a hand-edited save can hold anything. |
| `bool Dirty` | Set it when `Save()`'s text would change. At most twice a second the core copies `Save()` to the panel (`DfSaved` / `RgSaved`), and it's saved with the block. The core clears `Dirty` after copying. |

## What `ApSlotCtx` gives you (every member)

It's one instance per pilot (`ApPilot.Slot`), filled before `Score` / `Step`. Everything is world frame and SI. Read it during the
call. Don't keep references to its insides across ticks, except the track objects.

| Member | Type | What |
|---|---|---|
| `Dt` | float | The tick (s). |
| `Time` | float | Time since this behaviour took over (s). |
| `S` | `ApSense` | Own instruments: `Orient`, `Pos`, `Vel`, `AngVel` (local rad/s), `Gravity` (the hold's) / `GravityTrue`, `Mass`, `Radius`, `Belly`, `Agl` (-1 not known), `ThrustPos` / `ThrustNeg` (N per local side), `SidePos` / `SideNeg` (translation authority 0..1), `AirVel` (local), `Density`, `SpeedOfSound`, `Nose`, `GunAxis`, `MuzzleSpeed`, `GunRange`, `HasShipStatus` + `FuelShare` / `IntegrityShare` (0..1), `Lim` (the protection's limits), the stall evaluation (`StallFresh`, `StallState`, `StallMarginDeg`, `StallLiftInUse`, `StallG`, `LiftDir`, `StallFlow`), the loss-of-control probe (`LoacFresh`, `LoacProbe`, `LoacHere`), `AttAccel`, `AttErrDeg`, `CtrlShare`, `Piloted`, `PilotMove`, `SpeedLimit`, `Dampeners`. |
| `Style` | `ApStyle` | Airplane / Helicopter / SixAxis. |
| `Up` | Vector3 | Unit vector, against gravity. |
| `G` | float | The gravity the body falls with (m/s2). |
| `Height` | float | The pilot's altimeter (m over the engage datum). |
| `Agl` | float | Height over the ground (m; < 0 not known). |
| `AirSpeed` | float | m/s. |
| `Mach` | float | 0 when no speed of sound is known. |
| `MainAxisW` | Vector3 | The main thrust axis (world unit). |
| `MainThrust` | float | Its full thrust (N). |
| `ThrustToWeight` | float | That over the weight. |
| `Perf` | `ApPerf` | Own performance now (`FlightPerf.Evaluate`): `Valid`, `AlphaMax`, `NAtAlphaMax`, `NInst`, `NSust`, `TurnRateInst`, `TurnRateSust`, `NormalAccelMax`, `DragDecelNow`, `DragDecelAtMax`, `ExcessAccel`, `Ps`, `CornerSpeed`, `TurnRateCorner`, `SpeedRef`, `ThetaMax`, `GLimit`, `G`, `Thrust`, `Mass`. `Valid` false: not known (slow, no air). |
| `Samples` | `PerfSamples` | The stall sweep's lift and drag. Pass with `Perf` to `FlightPerf.NInstAt`, `TurnRateInstAt`, `NSustAt`, `TurnRateSustAt`, `PsAt`, `SpeedAfter` for any speed. |
| `LiftNMax` | float | The most load a Lift goal may pull now (g): `Perf.NInst`, else the g limit. |
| `AlphaMax` | float | Most nose-up from the flow (rad; NaN not known). |
| `GLimit` | float | The g limit (g). |
| `HasTarget` | bool | A target is held. |
| `Target` | `ApTargetTrack` | The source's track: `Pos`, `Vel`, `Acc`, `Range`, `Closing`, `LosRate`, `AngleOff` (0: you're behind it), `Los`, `Name`, `Id`, `Radius`, `Lost` (s). `Target.Motion` is the observed-motion track. |
| `Track` | `ApMotionTrack` | `= Target.Motion`, estimated from observed positions only: `Valid`, `Pos`, `Vel`, `Acc`, `TurnRate`, `Speed`, `TurnRateMag`, `EnergyHeight(refPos)`, `SpecificPower`, `LoadFactor`, `Confidence` (0..1), `Age` (s), `Samples`, `Watched`, `Predict(t)`. |
| `Terrain` | `ITerrainQuery` | `Below(p, down, reach, out height, out normal)`, `Cast(p, dir, len, out dist, out normal)`. Null when none. |
| `Ground` | `TerrainWatch` | The flight director's watch: `HasGround`, `Agl`, `Normal`, `GroundRate`, `Tti` (s to impact), `TtiStraight`, `TtiPath`, `AheadDist`, `AheadNormal`. |
| `Panel` | `ApPanel` | The whole panel, clamped. |
| `Dogfight` | `ApDogfightParams` | The dogfight's panel values (table below). |
| `Rocket` | `ApRocketParams` | The rocket's panel values (table below). |
| `Lead(u, out dir, out tof, useTrack)` | method | The lead point for a projectile at muzzle speed `u` (m/s; 0 uses the panel's, else the guns'): the aim (world unit) and the time of flight (s). `useTrack` uses the observed motion instead of the source's. False: no solution. |
| `GunErrorDeg(dirW)` | method | Angle (deg) between a world direction and the gun axis (the nose without guns). |
| `GroundBelow(p, reach, out height)` | method | The ground's height under a point (m down along `-Up`). False: none within `reach`. |

## What `ApSlotOut` accepts

Start from `ApSlotOut.None` (that's what `Step` receives): nothing asked, the throttle and load NaN, `TargetClearM` NaN.

| Field | Meaning |
|---|---|
| `UseNav` + `Nav` | A **nav goal** (`ApNavGoal`), flown by the style's own executor: L1 + TECS for an airplane, the hover's lean for a helicopter, thrusters for six-axis. The terrain minimum and the hard deck / ceiling are kept. `Nav.Kind`: `Point` (`Point`, `PointVel`, `PointAcc`; `Line` + `From` flies along From -> Point), `Circle` (`Point`, `Radius`, `Dir` +1 right), `Course` (`Course`), or `None`. Also `Height` (altimeter m; NaN keeps the path), `Speed` (m/s; NaN not held), `Max` (full throttle), `Aim` (nose on the lead), `Face` (a hover's heading). |
| `Goal` | A **direct goal** (`ApExternal`, used when `UseNav` is false), flown as it is through every protection, the terrain and avoidance. |
| `Goal.Kind = Track` | `Position`, `Velocity` (fed forward), `Accel` (the place's own acceleration), `Heading` (world; zero keeps the one held), `MaxSpeed` (m/s; 0 uses the hover speed). Hovering styles only. |
| `Goal.Kind = Accel` | `Accel`: world m/s2 without gravity (the autopilot adds what holds against it). The main thrust points along it, or the lift flies it. |
| `Goal.Kind = Attitude` | `Attitude`: a world quaternion. |
| `Goal.Kind = Lift` | `Lift`: the lift direction (world unit; its part across the path is used). `Load`: g of the real weight (NaN: the most the protection gives). With no lift (slow, no air, a rocket), the main thrust tilts to give the push. `ApLift.FromAccel` / `ToAccel` / `Banked` convert. |
| `Goal.Throttle` | 0..1 along the main thrust. NaN: the autopilot's own for Accel and Lift (the speed held), the pilot's for Attitude. |
| `Goal.AimAxis`, `AimDir`, `Blend` | Any kind: turn this local axis toward this world direction, `Blend` (0..1) of the way. The protection still clips. |
| `LetGo` | Not `None`: the behaviour lets go with this reason. Ones you'll use: `Bingo`, `TimedOut`, `LostTarget`, `NotImplemented`, `GoalDone`. The status line says it (`ApText.Say`). |
| `Status` | Your words for the status line ("Dogfight - *Status* on X 1.2 km"). Keep a cached string; don't build one per tick. |
| `AimReport` | Ask for the pilot's aim report against the target's lead each tick (`ApPilot.AimErrDeg` / `InCone` / `InRange`, `AeroApi.TryGetAutopilotAim`). |
| `AimNose` | ... and blend the nose onto the lead, as the built-in behaviours do (a nav goal, or a direct goal without its own aim). |
| `TargetClearM` | The clearance avoidance keeps from the target (m). NaN: half the range. A slot that rams sets it small. It's never below the target's own size. |

The core also caps Rocket Guidance's direct goal at the panel's **Max Lateral g**: a Lift goal's `Load`, and an Accel goal's part
across the path. Rocket Guidance in Engage isn't refused for low airspeed (it launches from rest).

## The panel fields (label -> panel member -> params member)

Dogfight (`ApDogfightParams`, `c.Dogfight`), shown when Behaviour = Dogfight (`ApPanelLayout.ShowDogfight`):

| Label | `ApPanel` | `ApDogfightParams` | Range / default |
|---|---|---|---|
| Aggressiveness | `Aggression` | `Aggression` | 0..1 / 0.5 (shared with the built-in behaviours) |
| Preferred Fight | `DfFight` | `Fight` (`ApFightPref`: Auto / Angles / Energy) | Auto |
| Head-on Passes | `DfHeadOn` | `HeadOn` | off |
| Respect Enemy Guns | `DfRespectGuns` | `RespectGuns` | 0..1 / 0.5 |
| Skill | `DfSkill` | `Skill` | 0..1 / 0.7 |
| Bingo Fuel (%) | `DfBingoFuelPct` | `BingoFuelPct` | 0..100 / 10 (0 off; compare `c.S.FuelShare`) |
| Bingo Damage (%) | `DfBingoDamagePct` | `BingoDamagePct` | 0..100 / 40 (0 off; compare `c.S.IntegrityShare`) |
| Hard Deck (m over ground) | `DeckM` | `DeckM` | 0..5000 / 0 off (the core keeps it for a nav goal; a direct goal is yours) |
| Combat Ceiling (m) | `CeilingM` | `CeilingM` | 0..20000 / 0 off (same) |

Rocket Guidance (`ApRocketParams`, `c.Rocket`), shown when Behaviour = Rocket Guidance (`ApPanelLayout.ShowRocket`):

| Label | `ApPanel` | `ApRocketParams` | Range / default |
|---|---|---|---|
| Guidance Law | `RgLaw` | `Law` (`ApGuidanceLaw`: ProNav / WhipNav / HybridNav / QuadraticIntercept / AugmentedPN / Pursuit) | ProNav |
| Navigation Constant | `RgNavConst` | `NavConst` | 1..8 / 3 |
| Target Accel Gain | `RgTargetAccelGain` | `TargetAccelGain` | 0..3 / 1.5 |
| Max Lateral g | `RgMaxLatG` | `MaxLatG` | 1..60 on the panel (100 in the clamp) / 20 (the core applies it) |
| Gravity Compensation | `RgGravityComp` | `GravityComp` | on |
| Arm Distance (m) | `RgArmM` | `ArmM` | 0..5000 / 50 (a value only) |
| Let-go Timeout (s) | `RgTimeoutS` | `TimeoutS` | 0..600 / 60 (0 off; a value only) |

Both slots also get the target fields: **Target Source** (`TargetSrc`), **Target (GPS / grid name)** (`TargetText`) and **Target Filter**
(`TargetFilter`). Dogfight also gets the aim fields (`AimOn`, `MuzzleSpeed`, `ConeDeg`). Read those from `c.Panel`.

## Adding a panel field

Use a dogfight field `DfFoo` (a float, 0..10, default 2) as the example. Do every step in the same change.

1. **The value**: `ApPanel.cs`, beside the other `Df*` fields: `public float DfFoo;`.
2. **The default**: `ApPanel.Defaults`: `DfFoo = 2f,`.
3. **The clamp**: `ApPanel.Clamped()`: `p.DfFoo = Fin(p.DfFoo, d.DfFoo, 0f, 10f);`. Add a `const` for the range if the panel shares it.
4. **The params member**: `ApSlots.cs`, `ApDogfightParams`: `public float Foo;`, and in `Of`: `Foo = p.DfFoo,`.
5. **The panel property**: `Game/AeroAutopilotBlockDetailModel.cs`, in the dogfight block:
   ```csharp
   [Slider(null)] [Range<float>(0f, 10f, RangeMode.InIn)] [Label("Foo (units)")] [Increment(0.1f, 0.5f, 1f)] [ShowInInspectorWhen("ShowDogfight", ShowWhen.True)]
   public float DfFoo { get => P.DfFoo; set => Edit((ref ApPanel p) => p.DfFoo = value); }
   ```
   Use a `bool` property for a switch and an enum property for a choice, with the same `[Label]` and `[ShowInInspectorWhen]`. Also add
   `nameof(DfFoo)` to the `ModDetailTypeInfo.Build<...>` list at the top, in the order the panel should show it.
6. **The layout**: the switch is `ApPanelLayout.ShowDogfight` (the "ShowDogfight" string above). A new switch needs a method in
   `ApPanelLayout`, a case in `ApPanelLayout.Cond`, a `[HideInInspector] public bool ShowX => ApPanelLayout.ShowX(P);` property on
   the model, and its name in the model's `_notify` list. Then add `"DfFoo"` to the dogfight's expected member list in
   `Tests/AutopilotSim/Scenarios/PanelLayoutPack.cs` (the `slots` table, in panel order) and to the `own` list there.
7. **The label text**: add `"Foo (units)": "Foo (units)",` to both `Assets/Texts/en-US/AeroMod.loc-texts` and
   `Content/Texts/en-US/AeroMod.loc-texts`.
8. **The save**: `Blocks/AeroBlockSaveForm.cs`. In `Autopilot(in ApPanel p)` append `+ " " + F(p.DfFoo)` at the **end** (after
   `Esc(p.RgSaved)`; never insert in the middle). In `TryReadAutopilot` read it at the next index, 68: `p.DfFoo = Fl(68, p.DfFoo);`.
   A key without it then reads the default. Bump `AutopilotFormat` (2 -> 3) and say so in its comment. Add a round trip to
   `Tests/BlockSaveTests/Program.cs` (the autopilot section).
9. Run the gate: `bash tools/test_fast.sh`.

## Testing your logic offline

Scenario packs go in `Tests/AutopilotSim/UserScenarios/`. Read the README there, and copy `TemplatePack.cs` to start. A pack is any
class implementing `IScenarioPack`. It's found at start, with no registration step. Fly your behaviour by choosing it on a panel:

```csharp
var cfg = ApPanel.Defaults; cfg.Behaviour = ApBehaviour.Dogfight; cfg.DfSkill = 0.9f;
var p = Flight.Pilot(me, cfg, ApMode.Engage, Flight.Target(other));   // (or new SimTargetSource { T = SimTarget.Weave(...) })
f.Each(ff => ff.Rms("aim err", p.AimErrDeg));
```

`p.Slot` is the `ApSlotCtx` your `Step` got. `p.SlotOut` is what it returned, `p.SlotText` its status words, and `p.Dogfight.State`
/ `p.Rocket.State` your state. For a rocket, `Rockets.Finned(...)` is a small finned rocket with a gimballed motor.

Run from the mod's folder:

```
dotnet run --project Tests/AutopilotSim -c Release -- "user:"                       # all your scenarios
dotnet run --project Tests/AutopilotSim -c Release -- "user: my dogfight" verbose   # one, with its notes and every check
dotnet run --project Tests/AutopilotSim -c Release -- "user:" csv C:/temp/ap        # plus a CSV trace per ship
bash tools/test_fast.sh                                                             # the whole gate (fast scenarios)
```

`Tests/AutopilotSim/Scenarios/SlotPack.cs` checks the slots' integration. The refusal check skips a slot whose `Implemented` is
true. The Auto check ("Auto never picks an unwritten slot") still expects `Implemented` false. Once yours returns true, change it
to what your slot should do (for example, that Auto picks it when its score is highest).
