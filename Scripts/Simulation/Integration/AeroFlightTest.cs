#pragma warning disable
using System;
using Keen.VRage.Core;

namespace AeroMod;

/// <summary>
/// Scripted atmospheric flight test -- an autopilot that flies a grid on synthetic pilot input
/// so aerodynamics can be measured without a human on the stick.
///
/// WHY THIS EXISTS, AND HOW IT DIFFERS FROM AeroSpeedSpike
///
/// AeroSpeedSpike answers a different question: can we exceed the physics speed cap by keeping
/// real velocity at 50 m/s and teleporting the grid forward 200/60 m every tick, so ground speed
/// reads ~250 m/s while Havok only ever sees 50? It is a speed-cap workaround, not flight -- it
/// overwrites position and velocity every frame, so the aero model's forces never actually move
/// the grid (|F| reads 0 while it is engaged). It also costs ~4 s main-thread stalls, because
/// teleporting a multi-thousand-block grid every tick forces cluster/streaming work.
///
/// This rig does the opposite. It NEVER writes position or velocity after setup. It flies by
/// injecting the same ControlData a player's stick would produce, and then lets the aero model
/// and the engine's physics do everything else. That makes the telemetry meaningful: if the
/// aircraft climbs, it climbed on lift.
///
/// PROFILE (frames at the fixed 60 Hz sim step)
///   SETUP    once      place at TargetAltitude, level, pointed at the horizon, at EntrySpeed
///   CRUISE   0-600     full forward throttle, neutral stick -- does it hold altitude?
///   CLIMB    600-1200  full throttle + nose-up -- does lift and AoA respond?
///   GLIDE    1200-2400 throttle cut, neutral stick -- sink rate and lift-to-drag ratio
///
/// Telemetry is one line per second, tagged [FLIGHT], so a run can be read straight from the log.
/// </summary>
public partial class AeroGridComponent
{
    /// <summary>
    /// Nested so it can reach the private _model / _components / _surface caches, exactly as
    /// AeroSimJob does -- those are private to AeroGridComponent, and a free-standing static
    /// class cannot see them (CS0122).
    /// </summary>
    internal static class AeroFlightTest
    {
        /// <summary>Off by default. This teleports once during setup, so never leave it on casually.</summary>
        public static bool Enabled = false;

        public const float TargetAltitude = 1500f;  // metres of ground clearance to start from
        public const float EntrySpeed = 120f;       // m/s at entry -- comfortably flying, not stalled

        private const int CruiseEnd = 600;          // 10 s
        private const int ClimbEnd = 1200;          // 20 s
        private const int GlideEnd = 2400;          // 40 s

        private static AeroGridComponent _subject;
        private static int _frame = -1;

        public static void Tick(AeroGridComponent aero, WorldTransform wt)
        {
            if (!Enabled) return;

            // Bind to the first grid that actually has aerodynamic surfaces to test.
            if (_subject == null)
            {
                int wings = aero._model?.Wings?.Count ?? 0;
                int comps = aero._components?.Count ?? 0;
                if (wings == 0 && comps == 0) return;
                _subject = aero;
                Log.Default?.Info($"[FLIGHT] subject bound: wings={wings} aeroComponents={comps} " +
                                  $"faces={aero._surface?.FaceCount ?? 0}");
            }
            if (!ReferenceEquals(aero, _subject)) return;

            _frame++;

            if (_frame == 0)
            {
                Setup(aero, wt);
                return;
            }
            if (_frame > GlideEnd)
            {
                if (_frame == GlideEnd + 1)
                    Log.Default?.Info("[FLIGHT] profile complete -- hands off");
                return;
            }

            // ── Fly it: synthetic stick only, no position or velocity writes ──
            Vector3 movement = Vector3.Zero;   // translation input, -Z is forward
            Vector3 rotation = Vector3.Zero;   // X = pitch, Y = yaw, Z = roll
            string phase;

            if (_frame <= CruiseEnd)
            {
                phase = "CRUISE";
                movement = new Vector3(0f, 0f, -1f);
            }
            else if (_frame <= ClimbEnd)
            {
                phase = "CLIMB";
                movement = new Vector3(0f, 0f, -1f);
                rotation = new Vector3(-0.5f, 0f, 0f);   // nose up
            }
            else
            {
                phase = "GLIDE";                          // throttle cut, stick neutral
            }

            PhysicsHack.TrySetControlData(aero.Entity, movement, rotation);

            if (_frame % 60 == 0)
                Report(aero, wt, phase);
        }

        private static void Setup(AeroGridComponent aero, WorldTransform wt)
        {
            // Gravity gives us "down"; the horizon is anything perpendicular to it.
            Vector3 g = PhysicsHack.GetGravityDirection(aero.Data);
            float glen = g.Length();
            if (glen < 1e-3f)
            {
                Log.Default?.Info("[FLIGHT] ABORT: no gravity here, this is an atmospheric test");
                Enabled = false;
                return;
            }
            Vector3 down = g / glen;
            Vector3 up = -down;

            // Point along the horizontal part of wherever the nose already faces.
            Vector3 nose = WorldTransform.TransformDirection(new Vector3(0f, 0f, -1f), wt);
            Vector3 fwd = nose - up * Vector3.Dot(nose, up);
            if (fwd.LengthSquared() < 1e-4f)
            {
                // Nose was straight up or down; pick any horizontal direction.
                Vector3 seed = Math.Abs(up.X) < 0.9f ? new Vector3(1f, 0f, 0f) : new Vector3(0f, 1f, 0f);
                fwd = seed - up * Vector3.Dot(seed, up);
            }
            fwd = Vector3.Normalize(fwd);

            // Climb to test altitude. This is the ONLY teleport in the rig.
            float ground = aero.GroundHeight;
            double raise = (ground >= 0f && ground < TargetAltitude) ? (TargetAltitude - ground) : 0.0;
            Vector3D startPos = wt.Position + (Vector3D)(up * (float)raise);

            PhysicsHack.TrySetOrientation(aero.Data, Quaternion.CreateFromForwardUp(fwd, up));
            PhysicsHack.TrySetPosition(aero.Data, startPos);
            PhysicsHack.TrySetVelocity(aero.Data, fwd * EntrySpeed, Vector3.Zero);


            PhysicsHack.TryGetMassProperties(aero.Data, out float mass, out _);
            Log.Default?.Info($"[FLIGHT] SETUP raise={raise:F0}m entry={EntrySpeed:F0}m/s mass={mass:F0}kg " +
                              $"groundBefore={ground:F0}");
        }

        private static void Report(AeroGridComponent aero, WorldTransform wt, string phase)
        {
            PhysicsHack.TryGetVelocity(aero.Data, out Vector3 vel, out Vector3 angVel);
            float speed = vel.Length();

            Vector3 g = PhysicsHack.GetGravityDirection(aero.Data);
            float glen = g.Length();
            Vector3 up = glen > 1e-3f ? -(g / glen) : new Vector3(0f, 1f, 0f);

            // Vertical speed straight off the velocity vector; climb is positive.
            float vs = Vector3.Dot(vel, up);

            // Angle of attack: angle between the flight path and the wing chord line (nose).
            Vector3 nose = WorldTransform.TransformDirection(new Vector3(0f, 0f, -1f), wt);
            float aoa = 0f;
            if (speed > 1f)
            {
                Vector3 vhat = vel / speed;
                float cos = Math.Clamp(Vector3.Dot(vhat, nose), -1f, 1f);
                aoa = (float)(Math.Acos(cos) * 180.0 / Math.PI);
                // Sign it: negative when the nose is below the flight path.
                if (Vector3.Dot(Vector3.Cross(vhat, nose), Vector3.Cross(vhat, up)) < 0f) aoa = -aoa;
            }

            // Pitch of the nose above the horizon.
            float pitch = (float)(Math.Asin(Math.Clamp(Vector3.Dot(nose, up), -1f, 1f)) * 180.0 / Math.PI);

            float lift = aero.HasResult ? aero.LastResult.LiftMagnitude : 0f;
            float drag = aero.HasResult ? aero.LastResult.DragMagnitude : 0f;
            float ld = drag > 1f ? lift / drag : 0f;
            double mach = aero.HasResult ? aero.LastResult.Mach : 0.0;
            double q = aero.HasResult ? aero.LastResult.DynamicPressure : 0.0;

            Log.Default?.Info(
                $"[FLIGHT] t={_frame / 60,3}s {phase,-6} spd={speed,6:F1} vs={vs,7:F1} alt={aero.GroundHeight,7:F0} " +
                $"aoa={aoa,6:F1} pitch={pitch,6:F1} lift={lift,9:F0} drag={drag,9:F0} L/D={ld,5:F2} " +
                $"M={mach,4:F2} q={q,8:F0} angV={angVel.Length(),5:F2}");
        }
    }
}
