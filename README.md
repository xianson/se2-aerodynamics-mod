# Aerodynamics Mod for Space Engineers 2

> *"The real barrier wasn't in the sky, but in our knowledge and experience of supersonic flight."* - Chuck Yeager

Real aerodynamics for Space Engineers 2. Every ship flies by its own shape: a sleek hull slips through the air, a brick
fights it, wings give lift and stall, the sound barrier pushes back, and re-entry heats the hull until it glows. The
air also loads the ship's structure, so pulling too hard can bend, crack and tear it apart. There's nothing to set up
on your blocks: the whole ship's shape counts, wings or no wings. The mod also adds aircraft parts, from control
surfaces and retractable gear to helicopter rotors, ramjets and solid rockets.

**Version 1.0.1** · **[Wiki](https://github.com/xianson/se2-aerodynamics-mod/wiki)** ·
**Steam Workshop:** [subscribe](https://steamcommunity.com/sharedfiles/filedetails/?id=3822388536) ·
**[Report a bug](https://github.com/xianson/se2-aerodynamics-mod/issues)**

## Features

- **Drag and lift from the shape:** every exposed face meets the air (pressure, shadowing, skin friction, rotation
  damping, supersonic wave drag). Wings are found from the blocks, with stall and Mach effects. Wind, ground effect, and
  damage that changes the forces at once.
- **Offset thrust:** every thruster pushes from where it sits, so unbalanced thrust turns the ship.
- **Flight controls through real parts:** control surfaces move with your steering. Gyros, thrusters, surfaces and
  rotors fly the ship together, aware of the air. Thrust acts where each thruster sits, and nothing turns the ship by
  magic.
- **Gyros that don't cheat the air:** gyros give a quarter of their vanilla torque by default (`Gyros.TorqueScale`), so
  lopsided thrust, the air and your control surfaces matter; they still trim and steady the ship and lead at low speed.
  Held hard in one direction they saturate and fade, and recover when eased off (`Gyros.Saturation`).
- **Honest dampeners:** the game's dampeners delete gravity for free (a ship that falls with them off floats and
  climbs with them on). Here they hold the ship up with real thrust against the gravity it really feels (the planet's pull
  x the world's `World.GravityMultiplier`: 1 with this mod, 2 in vanilla), so **a ship needs thrust for its real weight to
  hover**, and super dampeners brake with real thrust. Turn `Physics.HonestDampeners` off (Settings > Mods > Flight model > Advanced) for the game's own dampeners.
- **Engines by speed:** turboprops rule subsonic flight, vanilla atmospheric thrusters (jets) push through Mach 1 (108 %)
  and gain from ram air to about 2x at Mach 1.6, and ramjets own Mach 2-6. Jets and ramjets care about the air's angle.
- **Aircraft parts** (G menu > Aerodynamics > Aircraft Parts): control surfaces, retractable landing gear, clamshell
  airbrakes, turboprops, helicopter rotors with collective / cyclic and real torque, the Reversible Rotor, ramjets,
  stackable solid rocket motors, and the Autopilot.
- **Retractable Gear - Pad Foot:** the retractable gear with a vanilla landing-gear model as its foot. The suspension (airshock,
  oleo, retraction) runs on a hidden wheel; the foot is hinged at the ankle, tilts to the ground under it and folds flat into the
  bay with the leg. No magnet. The feet reuse vanilla meshes as placeholders.

  | Block | Housing | Foot (vanilla model) | Foot size (m) | Wheel body | Sized for |
  |---|---|---|---|---|---|
  | Retractable Gear (Pad) 0.5 m | 0.5 m | Landing gear (1 m), x0.5 | 0.25 x 0.50 x 0.50 | 150 kg | 1.5 t a gear |
  | Retractable Gear (Pad) 1 m | 1 m | Landing gear (1 m) | 0.50 x 1.00 x 1.00 | 250 kg | 5 t |
  | Retractable Gear (Pad) 1.5 m | 1.5 m | Landing gear (1 m), x1.5 | 0.75 x 1.50 x 1.50 | 600 kg | 20 t |
  | Retractable Gear (Pad) 2.5 m | 2.5 m | Landing gear (5 m), x0.5 | 1.26 x 2.50 x 2.50 | 1200 kg | 60 t |

  Stroke 0.125 / 0.25 / 0.4 / 0.6 m. "Sized for": the sag at the default stiffness stays within half the stroke.
- **Wheel shocks (a stopgap until the game's wheels take impacts better):** every wheel and gear gets an airshock (firm
  just before a touchdown) and an oleo strut rated for its size (1 m wheel 15 t, 1.5 m 34 t, 2.5 m 94 t at 2 g), so
  landings stop bottoming and bouncing. `Wheels.Shocks`, `Wheels.OleoRating` (Settings > Mods > Wheels and landing gear).
- **Structure and damage:** air, thrust, gyro and impact loads spread through the ship you fly. Overloaded sections
  wear, fatigue, bend, crack across and tear off, with creaks, shake and smoke.
- **Rotor strikes:** rotors and props are damaged by what they hit, and damage it back. Weld to repair.
- **Re-entry:** plasma, vapour cones, wingtip trails, hull heating with heat damage, and scorch marks.
- **Warnings:** a stall warning and a predicted loss-of-control warning (on the Orbital Mechanics Mod's HUD), and an optional Mach
  readout.
- **Autopilot block** (Aircraft Parts): its control panel holds a hover (place and height; your keys fly it at up to
  10 m/s and it stops where you let go) or just the height. A helicopter left hovering keeps hovering, also after a
  reload. Protected against stalls, overload, loss of control and the ground; it flies only through the ship's real
  controls.
  - **Flight Hold:** heading (or wings level), altitude with a climb rate, speed. **Level Off** recovers and holds.
  - **Navigate:** a route of GPS markers or coordinates, once (then a loiter) or round; terrain kept under it. **Return**
    flies back to where you engaged.
  - **Engage:** From the Depths' attack modes on a target - attack run, U-turn run, broadside, circle, hover above / below,
    point at, standoff, pursuit lead / pure / lag, optional jinking. Auto picks one.
  - **Flies like the ship:** airplane (banks to turn), helicopter (leans toward the motion) or six-axis (thrusters, the
    nose free) - detected, or set on the panel.
  - **Aims fixed guns:** the nose on the target's lead point; aim error and "in cone" for weapon mods. It never fires.
  - **Target:** what you locked (or what the turrets see, else the nearest within 4 km), a GPS marker, a grid by name,
    or another mod. Works on unpiloted ships (NPC-style).
  - **Collision avoidance:** steps aside or pulls up from other ships, asteroids and its own target (every mode).
  - **Target filter:** hostile only, + neutral, or everything - the game's own relation, as turrets judge it.
  - **Formation:** holds a slot beside any leader grid, piloted or not. Airplane, helicopter or six-axis.
  - **Dogfight** and **Rocket Guidance:** Engage behaviours with their own panel values.
  - **Enemy:** makes the ship - with everything on its rotors, hinges and pistons - the game's hostile NPC's: its turrets
    and Engage fight players. Off gives each part back to its owner. World setting: off / creative or admin (default) / everyone; an enemy's panel is creative or admin only.

- **Thrust vectoring:** solid rocket nozzles swivel within a cone to steer any ship (the nozzle's Thrust Vectoring switch).

- **Flight assists** (off by default; Settings > Mods > Flight assists): presets Realistic / Assisted / Forgiving.
  - Stall protection in manual flight: fly-by-wire limits on your steering. No physics change.
  - Gentler stall and more control bite past it. Changes physics.
  - Less uncancelled offset-thrust torque. Unrealistic.
- **Quick launch (creative):** seated in a cockpit, `Ctrl` + `T` lifts the ship (and everything joined to it) 1 km
  over the ground, turns it level and sends it flying along its nose at cruise speed. Creative worlds only; never into
  the ground, never a station. Rebind it or turn it off under Settings > Mods > Aerodynamics (Controls, Quick launch).
- **Test enemies (creative or admin):** `Ctrl` + `M` spawns an enemy missile, `Ctrl` + `B` an enemy bogey, `Ctrl` + `N`
  removes them.
  - From your blueprints named "Missile" and "Bogey"; each needs an Autopilot block.
  - It appears 2 km / 4 km away and goes for you (Rocket Guidance / Dogfight).
  - Distance, direction, salvo, speed, keys: Settings > Mods > Aerodynamics (Test spawn).
- **Settings:** an in-game menu (pause menu > Settings > Mods), plus host override files.

## Requirements

- **Space Engineers 2, version 4.0.x** (made and tested on 4.0.0.3151; 1.0.2 and older: 2.4.x).
- **Add `-loadScripts` to the game's launch options.** Steam > Library > Space Engineers 2 > right-click > Properties >
  General > Launch Options: type `-loadScripts`. Without it the game loads the mod's blocks and effects but never runs
  its scripts, and ships fly exactly as in the unmodded game. (It turns scripts on for every mod you load.)
- No other mod is needed. **Optional:** the Orbital Mechanics Mod, whose flight HUD shows this mod's warnings and whose railed
  re-entries heat the hull.
- **Single player or hosting only.** SE2 (4.0 as 2.4) runs the server inside the game, so a remote client sees the game's normal
  flight.

## Install

- **Steam Workshop:** [subscribe](https://steamcommunity.com/sharedfiles/filedetails/?id=3822388536). Subscribe, then add the mod to your world's mod list.

## Documentation (wiki)

| | |
|---|---|
| Start | [Getting Started](https://github.com/xianson/se2-aerodynamics-mod/wiki/Getting-Started) · [How Flight Works](https://github.com/xianson/se2-aerodynamics-mod/wiki/How-Flight-Works) · [Speed and Engines](https://github.com/xianson/se2-aerodynamics-mod/wiki/Speed-and-Engines) |
| Blocks | [Control Surfaces](https://github.com/xianson/se2-aerodynamics-mod/wiki/Control-Surfaces) · [Airbrakes](https://github.com/xianson/se2-aerodynamics-mod/wiki/Airbrakes) · [Landing Gear](https://github.com/xianson/se2-aerodynamics-mod/wiki/Landing-Gear) · [Turboprops](https://github.com/xianson/se2-aerodynamics-mod/wiki/Turboprops) · [Helicopter Rotors](https://github.com/xianson/se2-aerodynamics-mod/wiki/Helicopter-Rotors) · [Reversible Rotor](https://github.com/xianson/se2-aerodynamics-mod/wiki/Reversible-Rotor) · [Ramjets](https://github.com/xianson/se2-aerodynamics-mod/wiki/Ramjets) · [Solid Rocket Motors](https://github.com/xianson/se2-aerodynamics-mod/wiki/Solid-Rocket-Motors) · [Vanilla Thrusters in Atmosphere](https://github.com/xianson/se2-aerodynamics-mod/wiki/Vanilla-Thrusters-in-Atmosphere) |
| More | [Rotor Strikes](https://github.com/xianson/se2-aerodynamics-mod/wiki/Rotor-Strikes) · [Settings](https://github.com/xianson/se2-aerodynamics-mod/wiki/Settings) · [Troubleshooting and FAQ](https://github.com/xianson/se2-aerodynamics-mod/wiki/Troubleshooting-and-FAQ) · [For Modders](https://github.com/xianson/se2-aerodynamics-mod/wiki/For-Modders) · [Changelog](https://github.com/xianson/se2-aerodynamics-mod/wiki/Changelog) |

**Quick troubleshooting:** no **Aerodynamics** section under Settings > Mods means `-loadScripts` is missing. To report
a problem, attach the newest log from `%APPDATA%\SpaceEngineers2\Temp\Logs\` (the mod's lines start with `[AERO`).

---

## Credits

By xianson.

## Licence

Free to use in any world, server or video, and free to depend on. Re-uploads only unlisted, private or
friends-only. Code may be reused except in a competing aerodynamics or orbital mod; credit appreciated, not required.
Commercial use (including by game developers) needs a paid licence. Full text: [LICENSE](LICENSE).

---
Developing or building the mod yourself? See [docs/DEVELOPMENT.md](docs/DEVELOPMENT.md).
