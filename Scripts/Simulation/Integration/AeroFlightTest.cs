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
        public const float EntrySpeed = 200f;       // m/s at entry (the Jetliner, 261 t on 200 m2 of wing, needs ~200-260 in Verdure's thin air)
        private const float EntryPitchDeg = 3f;     // nose above the horizon at entry: some angle of attack to cruise on

        private const int CruiseEnd = 600;          // 10 s
        private const int ClimbEnd = 1200;          // 20 s
        private const int GlideEnd = 2400;          // 40 s
        private const float PitchRate = 0.1f;
        // The aircraft's own axes, grid-local (a grid's -Z is not its nose: the Jetliner's is +X, and the rig flew it
        // sideways): the nose along the largest wing's chord, away from its aero centre (behind the CoM on a stable
        // aircraft); up along its normal, as it sits; pitch-up rotation about nose x up. Without wings: -Z, +Y.
        private static Vector3 _noseL = new Vector3(0f, 0f, -1f), _upL = new Vector3(0f, 1f, 0f);
        private static Vector3 PitchAxisL => Vector3.Cross(_noseL, _upL);       // rad/s per unit of stick (0.5 stick: ~3 deg/s)

        private static AeroGridComponent _subject, _best;
        private static int _bestScore;
        private static long _choosingSince;
        private static bool _warnedNone, _wasEnabled;
        private static int _frame = -1;

        public static void Tick(AeroGridComponent aero, WorldTransform wt)
        {
            if (!Enabled) { _wasEnabled = false; return; }
            if (!_wasEnabled)
            {
                // switched on (again): start over, a fresh subject and profile
                _wasEnabled = true;
                _subject = null; _best = null; _bestScore = 0; _choosingSince = 0; _warnedNone = false; _frame = -1;
            }

            // Bind to the grid with the most wings seen in the first 2 s (the first one found was arbitrary: in a
            // world with an airliner and wrecks it could pick a wreck).
            if (_subject == null)
            {
                int wings = aero._model?.Wings?.Count ?? 0;
                int comps = aero._components?.Count ?? 0;
                if (_choosingSince == 0) _choosingSince = System.Diagnostics.Stopwatch.GetTimestamp();
                if (wings + comps > _bestScore) { _bestScore = wings + comps; _best = aero; }
                if (System.Diagnostics.Stopwatch.GetTimestamp() - _choosingSince < System.Diagnostics.Stopwatch.Frequency * 2) return;
                if (_best == null)
                {
                    if (!_warnedNone) { _warnedNone = true; Log.Default?.Info($"[FLIGHT] no subject: no grid has wings or aero components (this grid: wings={wings} comps={comps} faces={aero._surface?.FaceCount ?? 0})"); }
                    return;
                }
                _subject = _best;
                Log.Default?.Info($"[FLIGHT] subject bound: '{_subject.Entity?.DebugName}' wings={_subject._model?.Wings?.Count ?? 0} " +
                                  $"aeroComponents={_subject._components?.Count ?? 0} faces={_subject._surface?.FaceCount ?? 0} mass={_subject.LastMass:F0}");
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
                {
                    aero.Data.Set(new Keen.Game2.Simulation.WorldObjects.Movement.AngularControlData { TargetAngularVelocity = Vector3.Zero });
                    Log.Default?.Info("[FLIGHT] profile complete -- hands off");
                }
                return;
            }

            // ── Fly it: synthetic stick only, no position or velocity writes ──
            Vector3 movement = Vector3.Zero;   // translation input, -Z is forward
            Vector3 rotation = Vector3.Zero;   // X = pitch, Y = yaw, Z = roll
            string phase;

            if (_frame <= CruiseEnd)
            {
                phase = "CRUISE";
                movement = _noseL;
            }
            else if (_frame <= ClimbEnd)
            {
                phase = "CLIMB";
                movement = _noseL;
                rotation = new Vector3(-0.5f, 0f, 0f);   // nose up
            }
            else
            {
                phase = "GLIDE";                          // throttle cut, stick neutral
            }

            PhysicsHack.TrySetControlData(aero.Entity, movement, rotation);
            // The stick: the game's gyros steer an unpiloted grid to AngularControlData (grid-local rad/s, +X nose up),
            // and a rate command releases the aero mod's attitude hold. (ControlData's Rotation alone steered nothing.)
            aero.Data.Set(new Keen.Game2.Simulation.WorldObjects.Movement.AngularControlData { TargetAngularVelocity = PitchAxisL * (-rotation.X * PitchRate) });

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
            FindAxes(aero, wt, up);

            // Point along the horizontal part of wherever the nose already faces.
            Vector3 nose = WorldTransform.TransformDirection(_noseL, wt);
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
            // (unknown ground height - the probe has not reported yet - counts as on the ground)
            double raise = ground < 0f ? TargetAltitude : ground < TargetAltitude ? TargetAltitude - ground : 0.0;
            Vector3D startPos = wt.Position + (Vector3D)(up * (float)raise);

            Vector3 velDir = fwd;
            float ep = EntryPitchDeg * MathF.PI / 180f;
            fwd = Vector3.Normalize(fwd * MathF.Cos(ep) + up * MathF.Sin(ep));
            up = Vector3.Normalize(up - fwd * Vector3.Dot(up, fwd));
            // The grid's own -Z and +Y, where its nose goes to fwd and its up to up.
            Vector3 sideL = Vector3.Cross(_noseL, _upL), sideW = Vector3.Cross(fwd, up);
            Vector3 ToWorld(Vector3 l) => fwd * Vector3.Dot(l, _noseL) + up * Vector3.Dot(l, _upL) + sideW * Vector3.Dot(l, sideL);
            PhysicsHack.TrySetOrientation(aero.Data, Quaternion.CreateFromForwardUp(ToWorld(new Vector3(0f, 0f, -1f)), ToWorld(new Vector3(0f, 1f, 0f))));
            PhysicsHack.TrySetPosition(aero.Data, startPos);
            PhysicsHack.TrySetVelocity(aero.Data, velDir * EntrySpeed, Vector3.Zero);


            PhysicsHack.TryGetMassProperties(aero.Data, out float mass, out Vector3 com);
            var wl = aero._model?.Wings;
            if (wl != null)
                foreach (var w in wl)
                    Log.Default?.Info($"[FLIGHT] {w} normal={w.Normal} span axis={w.SpanAxis} aero centre - CoM={w.AeroCenter - com} (CoM {com})");
            Log.Default?.Info($"[FLIGHT] SETUP raise={raise:F0}m entry={EntrySpeed:F0}m/s mass={mass:F0}kg " +
                              $"groundBefore={ground:F0}");
        }

        private static void FindAxes(AeroGridComponent aero, in WorldTransform wt, Vector3 worldUp)
        {
            _noseL = new Vector3(0f, 0f, -1f); _upL = new Vector3(0f, 1f, 0f);
            var wl = aero._model?.Wings;
            if (wl == null || wl.Count == 0) return;
            var w = wl[0];
            foreach (var x in wl) if (x.PlanformArea > w.PlanformArea) w = x;
            PhysicsHack.TryGetMassProperties(aero.Data, out _, out Vector3 com);
            Vector3 up = Vector3.Normalize(w.Normal);
            if (Vector3.Dot(up, WorldTransform.TransformDirectionInv(worldUp, wt)) < 0f) up = -up;
            Vector3 chord = Vector3.Normalize(Vector3.Cross(w.SpanAxis, up));
            if (Vector3.Dot(w.AeroCenter - com, chord) > 0f) chord = -chord;
            _noseL = chord; _upL = up;
            Log.Default?.Info($"[FLIGHT] axes: nose {_noseL} up {_upL} (from the largest wing)");
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
            Vector3 nose = WorldTransform.TransformDirection(_noseL, wt);
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
            // signed (the result's DragMagnitude is an absolute value: it hid the wings pushing forward)
            float drag = aero.HasResult ? (aero._model is LiftingSurfaceModel lms ? lms.LastInnerDrag + lms.LastWingsDrag : aero.LastResult.DragMagnitude) : 0f;
            float ld = MathF.Abs(drag) > 1f ? lift / drag : 0f;
            double mach = aero.HasResult ? aero.LastResult.Mach : 0.0;
            double q = aero.HasResult ? aero.LastResult.DynamicPressure : 0.0;

            Log.Default?.Info(
                $"[FLIGHT] t={_frame / 60,3}s {phase,-6} spd={speed,6:F1} vs={vs,7:F1} alt={aero.GroundHeight,7:F0} " +
                $"aoa={aoa,6:F1} pitch={pitch,6:F1} lift={lift,9:F0} drag={drag,9:F0} L/D={ld,5:F2} " +
                $"M={mach,4:F2} q={q,8:F0} angV={angVel.Length(),5:F2}" +
                (aero._model is LiftingSurfaceModel lm ? $" | faceD={lm.LastInnerDrag:F0} wingD={lm.LastWingsDrag:F0} floor={lm.LastFloorAdd:F0}" : "") +
                $" wings={aero._model?.Wings?.Count ?? 0} faces={aero._surface?.FaceCount ?? 0} rebuild={aero.Rebuilding}");
        }
    }
}
