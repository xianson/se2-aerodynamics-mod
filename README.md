# Aerodynamics Mod for Space Engineers 2

> *"The real barrier wasn't in the sky, but in our knowledge and experience of supersonic flight."* - Chuck Yeager

Real aerodynamics for Space Engineers 2. Every ship flies by its own shape: a sleek hull slips through the air, a brick
fights it, wings give lift and stall, the sound barrier pushes back, and re-entry heats the hull until it glows. The
air also loads the ship's structure, so pulling too hard can bend, crack and tear it apart. There's nothing to set up
on your blocks: the whole ship's shape counts, wings or no wings. The mod also adds aircraft parts, from control
surfaces and retractable gear to helicopter rotors, ramjets and solid rockets.

**Version 1.0.0** · **[Wiki](https://github.com/xianson/se2-aerodynamics-mod/wiki)** ·
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
- **Honest dampeners:** the game's dampeners delete gravity for free (a ship that falls with them off floats and
  climbs with them on). Here they hold the ship up with real thrust against the gravity it really feels (2x the planet's
  pull in vanilla), so **a ship needs thrust for twice its planet weight to hover**, and super dampeners brake with real
  thrust. Turn `Physics.HonestDampeners` off (Settings > Mods > Flight model > Advanced) for the game's own dampeners.
- **Engines by speed:** turboprops rule subsonic flight, vanilla atmospheric thrusters (jets) pinch through Mach 1 and
  gain from ram air above it, and ramjets own Mach 2-6. Jets and ramjets care about the air's angle.
- **Aircraft parts** (G menu > Aerodynamics > Aircraft Parts): control surfaces, retractable landing gear, clamshell
  airbrakes, turboprops, helicopter rotors with collective / cyclic and real torque, the Reversible Rotor, ramjets, and
  stackable solid rocket motors.
- **Structure and damage:** air, thrust, gyro and impact loads spread through the ship you fly. Overloaded sections
  wear, fatigue, bend, crack across and tear off, with creaks, shake and smoke.
- **Rotor strikes:** rotors and props are damaged by what they hit, and damage it back. Weld to repair.
- **Re-entry:** plasma, vapour cones, wingtip trails, hull heating with heat damage, and scorch marks.
- **Warnings:** a stall warning and a predicted loss-of-control warning (on the Orbital Mod's HUD), and an optional Mach
  readout.
- **Settings:** an in-game menu (pause menu > Settings > Mods), plus host override files.

## Requirements

- **Space Engineers 2, version 2.4.x** (made and tested on 2.4.0.95).
- **Add `-loadScripts` to the game's launch options.** Steam > Library > Space Engineers 2 > right-click > Properties >
  General > Launch Options: type `-loadScripts`. Without it the game loads the mod's blocks and effects but never runs
  its scripts, and ships fly exactly as in the unmodded game. (It turns scripts on for every mod you load.)
- No other mod is needed. **Optional:** the Orbital Mod, whose flight HUD shows this mod's warnings and whose railed
  re-entries heat the hull.
- **Single player or hosting only.** SE2 2.4 runs the server inside the game, so a remote client sees the game's normal
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
