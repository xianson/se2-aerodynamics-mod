#pragma warning disable CS0649, CS8632, CS1591, CS1573, CS1570, CS1574, CS0419, CS1734
using System;
using Keen.VRage.Core;

namespace AeroMod;

/// <summary>
/// Scripted atmospheric flight test -- an autopilot that flies a grid on synthetic pilot input
/// so aerodynamics can be measured without a human on the stick.
///
/// WHY THIS EXISTS, AND HOW IT DIFFERED FROM AeroSpeedSpike (a dev spike, removed 2026-10-18)
///
/// AeroSpeedSpike answered a different question: can we exceed the physics speed cap by keeping
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
///   CRUISE   0-600     full forward throttle, angle of attack held at CruiseAoa
///   CLIMB    600-1200  full throttle, ClimbAoa -- does lift and AoA respond?
///   GLIDE    1200-2400 throttle cut, GlideAoa -- sink rate and lift-to-drag ratio
///
/// THE TRIM (2026-10-11): an ideal elevator. A real aircraft trims its pitching moment with its tail; a block-built
/// one has gyros, and on the Jetliner the aero moment (~1.6 MN m nose-down at 2 deg) beat them - neutral stick dived it
/// into the ground before the glide began, so nothing past the cruise measured anything. Each tick a pitch-rate servo
/// (rate toward the angle of attack wanted, at most MaxRate) gives the grid the angular impulse that brings its pitch
/// rate there, in its own axes - that is the only write after setup, and it is logged ("trim" kN m, beside the gyros'
/// maximum) so a run says what a real build would need to fly it. AoaHold false: the old open-loop stick.
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
        /// <summary>Bind only to a grid whose name contains this (null: the most wings) - a world's load order otherwise
        /// decides between two winged grids, and an A/B flew different ships.</summary>
        public static string Only;

        public const float TargetAltitude = 1500f;  // metres of ground clearance to start from
        public const float EntrySpeed = 200f;       // m/s at entry (the Jetliner, 261 t on 200 m2 of wing, needs ~200-260 in Verdure's thin air)
        private const float EntryPitchDeg = 3f;     // nose above the horizon at entry: some angle of attack to cruise on

        public static bool AoaHold = true;
        public static float CruiseAoa = 6f, ClimbAoa = 9f, GlideAoa = 6f;   // degrees (the Jetliner's best L/D is near 8-12 on the bench)
        private const float AoaGain = 1.0f, MaxRate = 0.15f, ServoFrac = 0.25f;   // (1/s per rad of error; rad/s; of the rate error closed per tick)
        private static double _trimSum; private static int _trimN;
        private static Vector3 _lastVel; private static bool _hasLastVel;
        private static long _subjectSeen;   // (Stopwatch ticks: the bound component's last tick)

        private const int CruiseEnd = 600;          // 10 s
        private const int ClimbEnd = 1200;          // 20 s
        private const int GlideEnd = 2400;          // 40 s
        private const float PitchRate = 0.1f;
        // The aircraft's own axes, grid-local (a grid's -Z is not its nose: the Jetliner's is +X, and the rig flew it
        // sideways): the nose along the largest wing's chord, away from its mid chord (behind the CoM on a stable
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
                _subject = null; _best = null; _bestScore = 0; _choosingSince = 0; _warnedNone = false; _frame = -1; _trimSum = 0; _trimN = 0; _hasLastVel = false;
            }

            // Bind to the grid with the most wings seen in the first 2 s (the first one found was arbitrary: in a
            // world with an airliner and wrecks it could pick a wreck).
            // (the server's copy only: both scenes' copies tick here and each has the wings - bound to the client's, the
            //  servo steered and the telemetry read a copy the server's state overwrites)
            if (!aero.IsServerScene) return;
            if (_subject == null)
            {
                int wings = aero._model?.Wings?.Count ?? 0;
                if (Only != null && (aero.Entity?.DebugName ?? "").IndexOf(Only, StringComparison.OrdinalIgnoreCase) < 0) wings = -1000;
                int comps = aero._components?.Count ?? 0;
                if (_choosingSince == 0) _choosingSince = System.Diagnostics.Stopwatch.GetTimestamp();
                if (wings + comps > _bestScore) { _bestScore = wings + comps; _best = aero; }
                if (System.Diagnostics.Stopwatch.GetTimestamp() - _choosingSince < System.Diagnostics.Stopwatch.Frequency * 2) return;
                if (_best == null)
                {
                    if (!_warnedNone) { _warnedNone = true; Log.Default?.Info($"[FLIGHT] no subject: no grid has wings or aero components (this grid: wings={wings} comps={comps} faces={aero._surface?.FaceCount ?? 0})"); }
                    return;
                }
                _subject = _best; _subjectSeen = System.Diagnostics.Stopwatch.GetTimestamp();
                Log.Default?.Info($"[FLIGHT] subject bound: '{_subject.Entity?.DebugName}' wings={_subject._model?.Wings?.Count ?? 0} " +
                                  $"aeroComponents={_subject._components?.Count ?? 0} faces={_subject._surface?.FaceCount ?? 0} mass={_subject.LastMass:F0} server={_subject.IsServerScene}");
            }
            if (!ReferenceEquals(aero, _subject))
            {
                // (the same grid's new component: the setup's teleport made the server add the grid to its scene again -
                //  the bound one never ticked after, and the run logged nothing past SETUP. Its successor carries on the
                //  profile where it was.)
                long now = System.Diagnostics.Stopwatch.GetTimestamp();
                // (same name, wings, and its mass: a grid's subgrids and split-off pieces share its name)
                if (aero.Entity?.DebugName == null || aero.Entity.DebugName != _subject.Entity?.DebugName
                    || now - _subjectSeen < System.Diagnostics.Stopwatch.Frequency / 2   // (still ticking: another copy)
                    || (aero._model?.Wings?.Count ?? 0) == 0 || MathF.Abs(aero.LastMass - _subject.LastMass) > 0.05f * _subject.LastMass) return;
                Log.Default?.Info($"[FLIGHT] subject re-added to the scene: following its new component at frame {_frame}");
                _subject = aero;
            }
            _subjectSeen = System.Diagnostics.Stopwatch.GetTimestamp();

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

            if (AoaHold)
            {
                float want = phase == "CRUISE" ? CruiseAoa : phase == "CLIMB" ? ClimbAoa : GlideAoa;
                float rate = Hold(aero, wt, want);
                GridPhysics.TrySetControlData(aero.Entity, movement, Vector3.Zero);
                aero.Data.Set(new Keen.Game2.Simulation.WorldObjects.Movement.AngularControlData { TargetAngularVelocity = PitchAxisL * rate });
            }
            else
            {
                GridPhysics.TrySetControlData(aero.Entity, movement, rotation);
                // The stick: the game's gyros steer an unpiloted grid to AngularControlData (grid-local rad/s, +X nose up),
                // and a rate command releases the aero mod's attitude hold. (ControlData's Rotation alone steered nothing.)
                aero.Data.Set(new Keen.Game2.Simulation.WorldObjects.Movement.AngularControlData { TargetAngularVelocity = PitchAxisL * (-rotation.X * PitchRate) });
            }

            if (_frame % 60 == 0)
                Report(aero, wt, phase);
        }

        /// <summary>The trim servo: the pitch rate that brings the angle of attack to want (deg), and the angular impulse,
        /// about the grid's own pitch axis, that closes ServoFrac of the gap to it this tick. Returns the rate (for the gyros).</summary>
        private static float Hold(AeroGridComponent aero, in WorldTransform wt, float want)
        {
            if (!GridPhysics.TryGetVelocity(aero.Data, out Vector3 vel, out Vector3 angW)) return 0f;
            float aoa = Aoa(vel, wt, out _);
            float rate = Math.Clamp(AoaGain * (want - aoa) * MathF.PI / 180f, -MaxRate, MaxRate);
            var pitchL = Vector3.Normalize(PitchAxisL);
            float q = Vector3.Dot(WorldTransform.TransformDirectionInv(angW, wt), pitchL);
            if (!GridPhysics.TryGetInertiaData(aero.Data, out Vector3 invI, out Quaternion major)) return rate;
            // (the pitch axis in the principal axes: 1 / I about it = sum of its squared components x the inverse moments)
            var a = Vector3.Transform(pitchL, Quaternion.Inverse(major));
            float invIa = a.X * a.X * invI.X + a.Y * a.Y * invI.Y + a.Z * a.Z * invI.Z;
            if (!(invIa > 0f)) return rate;
            float impulse = ServoFrac * (rate - q) / invIa;
            GridPhysics.ApplyTorqueImpulse(aero.Data, pitchL * impulse, wt);
            _trimSum += impulse * 60f; _trimN++;
            return rate;
        }

        /// <summary>The game's whole thrust command (gravity hold and dampening included - ThrustTorque.GameThrust) along the
        /// travel and up, kN.</summary>
        private static string Thrust(AeroGridComponent aero, in WorldTransform wt, Vector3 vel, Vector3 up)
        {
            var tw = WorldTransform.TransformDirection(ThrustTorque.GameThrust(aero.Data, aero.IsServerScene), wt);
            float sp = vel.Length();
            return $" thrust fwd={(sp > 1f ? Vector3.Dot(tw, vel / sp) : 0f) / 1000f:F0}kN up={Vector3.Dot(tw, up) / 1000f:F0}kN";
        }

        /// <summary>Angle of attack (deg, + nose above the flight path) and the nose's pitch above the horizon.</summary>
        private static float Aoa(Vector3 vel, in WorldTransform wt, out Vector3 nose)
        {
            nose = WorldTransform.TransformDirection(_noseL, wt);
            float speed = vel.Length();
            if (speed < 1f) return 0f;
            var pitchW = WorldTransform.TransformDirection(Vector3.Normalize(PitchAxisL), wt);
            Vector3 vhat = vel / speed;
            // (signed in the grid's own pitch plane: the nose's turn from the travel about the pitch axis, nose-up +)
            float sin = Vector3.Dot(Vector3.Cross(vhat, nose), pitchW), cos = Vector3.Dot(vhat, nose);
            return MathF.Atan2(sin, cos) * 180f / MathF.PI;
        }

        private static void Setup(AeroGridComponent aero, WorldTransform wt)
        {
            // Gravity gives us "down"; the horizon is anything perpendicular to it.
            Vector3 g = GridPhysics.GetGravityDirection(aero.Data);
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
            GridPhysics.TrySetOrientation(aero.Data, Quaternion.CreateFromForwardUp(ToWorld(new Vector3(0f, 0f, -1f)), ToWorld(new Vector3(0f, 1f, 0f))));
            GridPhysics.TrySetPosition(aero.Data, startPos);
            GridPhysics.TrySetVelocity(aero.Data, velDir * EntrySpeed, Vector3.Zero);


            GridPhysics.TryGetMassProperties(aero.Data, out float mass, out Vector3 com);
            var wl = aero._model?.Wings;
            if (wl != null)
                foreach (var w in wl)
                    Log.Default?.Info($"[FLIGHT] {w} normal={w.Normal} span axis={w.SpanAxis} centroid - CoM={w.Centroid - com} (CoM {com})");
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
            GridPhysics.TryGetMassProperties(aero.Data, out _, out Vector3 com);
            Vector3 up = Vector3.Normalize(w.Normal);
            if (Vector3.Dot(up, WorldTransform.TransformDirectionInv(worldUp, wt)) < 0f) up = -up;
            Vector3 chord = Vector3.Normalize(Vector3.Cross(w.SpanAxis, up));
            // (the nose: away from the largest wing's mid chord - a main wing sits aft of the centre of mass. It read the
            //  quarter chord from the low end of the grid axis, which the lift model then also used whatever the flight
            //  direction; the model now takes the leading edge from the air - 2026-10-18, fix a-wing-edge)
            if (Vector3.Dot(w.Centroid - com, chord) > 0f) chord = -chord;
            _noseL = chord; _upL = up;
            Log.Default?.Info($"[FLIGHT] axes: nose {_noseL} up {_upL} (from the largest wing)");
        }

        private static void Report(AeroGridComponent aero, WorldTransform wt, string phase)
        {
            GridPhysics.TryGetVelocity(aero.Data, out Vector3 vel, out Vector3 angVel);
            float speed = vel.Length();

            Vector3 g = GridPhysics.GetGravityDirection(aero.Data);
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

            string trim = "";
            if (AoaHold && _trimN > 0)
            {
                trim = $" trim={_trimSum / _trimN / 1000.0,7:F0}kNm gyroMax={GridPhysics.TryGetGyroMaxTorque(aero.Data) / 1000f:F0}kNm";
                _trimSum = 0; _trimN = 0;
            }
            // THE RESIDUAL: the force the measured acceleration (over the last second) needs beyond gravity and the aero
            // model's own - thrust, dampeners, the trim servo's (angular only), anything unaccounted for; along the
            // travel (+ forward) and up (kN)
            string resid = "";
            GridPhysics.TryGetMassProperties(aero.Data, out float massR, out _);
            if (_hasLastVel && massR > 0f && aero.HasResult && speed > 1f)
            {
                Vector3 accel = vel - _lastVel;   // (per second: the report is once a second)
                Vector3 aeroW = WorldTransform.TransformDirection(aero.LastResult.Force, wt);
                float gm = GridPhysics.GravityMultiplier();   // (the engine's: a world at 2 flies in twice GravitySum)
                Vector3 extra = accel * massR - aeroW - g * gm * massR;
                resid = $" resid fwd={Vector3.Dot(extra, vel / speed) / 1000f,6:F0}kN up={Vector3.Dot(extra, up) / 1000f,6:F0}kN g={glen:F2}x{gm:F1} mult D{AeroTuning.DragMultiplier:F2} L{AeroTuning.LiftMultiplier:F2}";
            }
            _lastVel = vel; _hasLastVel = true;
            float lift = aero.HasResult ? aero.LastResult.LiftMagnitude : 0f;
            // signed (the result's DragMagnitude is an absolute value: it hid the wings pushing forward)
            float drag = aero.HasResult ? (aero._model is LiftingSurfaceModel lms ? lms.LastInnerDrag + lms.LastWingsDrag : aero.LastResult.DragMagnitude) : 0f;
            float ld = MathF.Abs(drag) > 1f ? lift / drag : 0f;
            double mach = aero.HasResult ? aero.LastResult.Mach : 0.0;
            double q = aero.HasResult ? aero.LastResult.DynamicPressure : 0.0;

            Log.Default?.Info(
                $"[FLIGHT] t={_frame / 60,3}s {phase,-6} spd={speed,6:F1} vs={vs,7:F1} alt={aero.GroundHeight,7:F0} " +
                $"aoa={aoa,6:F1} pitch={pitch,6:F1} lift={lift,9:F0} drag={drag,9:F0} L/D={ld,5:F2} " +
                $"M={mach,4:F2} q={q,8:F0} angV={angVel.Length(),5:F2}{trim}{Thrust(aero, wt, vel, up)}{resid}" +
                (aero._model is LiftingSurfaceModel lm ? $" | faceD={lm.LastInnerDrag:F0} wingD={lm.LastWingsDrag:F0} floor={lm.LastFloorAdd:F0}" : "") +
                $" wings={aero._model?.Wings?.Count ?? 0} faces={aero._surface?.FaceCount ?? 0} rebuild={aero.Rebuilding}");
        }
    }
}
