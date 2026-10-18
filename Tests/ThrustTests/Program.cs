using System.IO;
using AeroMod;

// Thruster steering (Propulsion/ThrustAllocator.cs), offline: the literal mod source, in a 60 Hz rigid-body flight
// simulation with the game's own target gyro controller (GridGyroscopesComponent.ComputeTorqueTarget, ported below)
// and the game's dampened thrust at the centre of mass, as in the game; the mod adds only the thrusters' torque.
//  1. allocator: net thrust kept to the game's, forces within their limits, the torque asked for, the offset torque of
//     an off-centre centre of mass cancelled;
//  2. flight: hover level, yaw / pitch / roll while hovering, a turn in forward flight, holding against an air torque
//     and against an unstable (weathervaning backwards) air torque, all with steering ON and OFF (gyros only), on a
//     ship with its centre of mass off its thrusters' centre. Pass/fail on settle time, overshoot, altitude, cross-axis
//     upset; and ON may never be worse than OFF.
// Run: dotnet run -c Release [-- verbose]. Exit 0 = all pass.
static partial class Program
{
    static int _fails, _checks;
    static bool _verbose;
    static bool Trace;   // FLYTRACE=name: the named fixture's air runs, sample by sample
    static void Check(bool ok, string what) { _checks++; if (!ok) { _fails++; Console.WriteLine("FAIL " + what); } else if (_verbose) Console.WriteLine("ok   " + what); }

    const float Deg = MathF.PI / 180f;

    // ---------------------------------------------------------------- ships

    sealed class Ship
    {
        public string Name;
        public float Mass;
        public Vector3 Principal;    // principal moments (kg m2)
        public Quaternion InertiaRotation = Quaternion.Identity;   // principal axes -> grid (RigidBodyMassProperties.InertiaMajorAxisRotation)
        /// <summary>I v and I^-1 v in grid axes (the game's ComputeDeltaAngularVelocity: rotate in, scale, rotate out).</summary>
        public Vector3 I(Vector3 v) => Vector3.Transform(Vector3.Transform(v, Quaternion.Inverse(InertiaRotation)) * Principal, InertiaRotation);
        public Vector3 Iinv(Vector3 v) => Vector3.Transform(Vector3.Transform(v, Quaternion.Inverse(InertiaRotation)) / Principal, InertiaRotation);
        public Vector3 MaxThrustPos, MaxThrustNeg;   // the game's capacity per push direction (fixtures)
        public List<(Vector3 dir, Vector3 force, Vector3 torque)> Air = new();   // the hull at q 6125 Pa (fixtures)
        public string Source = "synthetic";
        public float G = 9.81f;      // the gravity it hovers in (m/s2): a fixture's own, else 1 g
        public Vector3 Size;         // bounding box (m)
        public Vector3 Com;          // grid-local
        public float GyroTorque;     // the target gyro's torque limit (N m): min(coef m^2, MaxTorque x multiplier)
        public List<(Vector3 pos, Vector3 dir, float cap)> Thrusters = new();
    }

    /// <summary>A box ship: thrusters for each push direction spread over the opposite face's corners, totals per
    /// direction as given (the Hercules' from griddamp: +{781196 2565800 668609} -{781196 1785592 1003407} N, 126 t).</summary>
    static Ship BoxShip(string name, float mass, Vector3 size, Vector3 com, float gyro, Vector3 capPos, Vector3 capNeg)
    {
        var s = new Ship { Name = name, Mass = mass, Size = size, Com = com, GyroTorque = gyro };
        s.Principal = new Vector3(mass / 12f * (size.Y * size.Y + size.Z * size.Z), mass / 12f * (size.X * size.X + size.Z * size.Z), mass / 12f * (size.X * size.X + size.Y * size.Y));
        Vector3 h = size * 0.4f;   // thrusters a little in from the corners
        void Face(Vector3 dir, float total)
        {
            // the face the thruster sits on: opposite its push (it pushes the ship +dir from the -dir face)
            int ax = MathF.Abs(dir.X) > 0.5f ? 0 : MathF.Abs(dir.Y) > 0.5f ? 1 : 2;
            float side = -(ax == 0 ? dir.X : ax == 1 ? dir.Y : dir.Z);
            var corners = new List<Vector3>();
            foreach (float a in new[] { -1f, 1f }) foreach (float b in new[] { -1f, 1f })
            {
                Vector3 p = ax == 0 ? new Vector3(side * size.X / 2f, a * h.Y, b * h.Z)
                          : ax == 1 ? new Vector3(a * h.X, side * size.Y / 2f, b * h.Z)
                          : new Vector3(a * h.X, b * h.Y, side * size.Z / 2f);
                corners.Add(p);
            }
            foreach (var p in corners) s.Thrusters.Add((p, dir, total / corners.Count));
        }
        Face(Vector3.UnitX, capPos.X); Face(-Vector3.UnitX, capNeg.X);
        Face(Vector3.UnitY, capPos.Y); Face(-Vector3.UnitY, capNeg.Y);
        Face(Vector3.UnitZ, capPos.Z); Face(-Vector3.UnitZ, capNeg.Z);
        return s;
    }

    static Ship Hercules(Vector3 com, float gyro) => BoxShip("hercules", 126000f, new Vector3(20f, 10f, 40f), com, gyro,
        new Vector3(781196f, 2565800f, 668609f), new Vector3(781196f, 1785592f, 1003407f));


    /// <summary>The hull's air at a travel direction (local, unit), at q 6125 Pa: the table's directions weighted by
    /// closeness (the three nearest, inverse angle).</summary>
    static void Hull(Ship s, Vector3 travel, out Vector3 force, out Vector3 torque)
    {
        Span<int> best = stackalloc int[3] { -1, -1, -1 }; Span<float> bd = stackalloc float[3] { -2f, -2f, -2f };
        for (int i = 0; i < s.Air.Count; i++)
        {
            float d = Vector3.Dot(s.Air[i].dir, travel);
            for (int k = 0; k < 3; k++) if (d > bd[k]) { for (int m = 2; m > k; m--) { bd[m] = bd[m - 1]; best[m] = best[m - 1]; } bd[k] = d; best[k] = i; break; }
        }
        force = torque = Vector3.Zero; float wsum = 0f;
        for (int k = 0; k < 3; k++)
        {
            if (best[k] < 0) continue;
            float ang = MathF.Acos(Math.Clamp(bd[k], -1f, 1f));
            if (ang < 1e-4f) { force = s.Air[best[k]].force; torque = s.Air[best[k]].torque; return; }
            float w = 1f / ang; wsum += w; force += s.Air[best[k]].force * w; torque += s.Air[best[k]].torque * w;
        }
        if (wsum > 0f) { force /= wsum; torque /= wsum; }
    }

    /// <summary>A ship dumped from the game (harness: flightdump ID NAME; Tests/data/NAME.flight). pushSign: +1 the push
    /// direction is the cached ThrustDirection, -1 its opposite (what ThrustTorque uses).</summary>
    static Ship LoadFixture(string path, float pushSign)
    {
        var CI = System.Globalization.CultureInfo.InvariantCulture;
        var s = new Ship { Name = Path.GetFileNameWithoutExtension(path), Source = path };
        float maxTorque = 0f, coef = 0f, damp = 0f;
        Vector3 invI = Vector3.One;
        Vector3 R(string[] p, int i) => new Vector3(float.Parse(p[i], CI), float.Parse(p[i + 1], CI), float.Parse(p[i + 2], CI));
        foreach (var raw in File.ReadAllLines(path))
        {
            var p = raw.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (p.Length == 0 || p[0].StartsWith("#")) continue;
            switch (p[0])
            {
                case "mass": s.Mass = float.Parse(p[1], CI); break;
                case "com": s.Com = R(p, 1); break;
                case "invInertia": invI = R(p, 1); break;
                case "inertiaRotation": s.InertiaRotation = new Quaternion(float.Parse(p[1], CI), float.Parse(p[2], CI), float.Parse(p[3], CI), float.Parse(p[4], CI)); break;
                case "bounds": s.Size = R(p, 1); break;
                case "gravity": { float g = R(p, 1).Length(); if (g > 0.5f) s.G = g; break; }   // (in space: tested at 1 g)
                case "gyro": maxTorque = float.Parse(p[1], CI); coef = float.Parse(p[2], CI); damp = float.Parse(p[3], CI); break;
                case "maxThrust": s.MaxThrustPos = R(p, 1); s.MaxThrustNeg = R(p, 4); break;
                case "thruster": s.Thrusters.Add((R(p, 1), R(p, 4) * pushSign, float.Parse(p[7], CI))); break;
                case "air": s.Air.Add((R(p, 1), R(p, 4), R(p, 7))); break;
            }
        }
        s.Principal = new Vector3(invI.X > 0f ? 1f / invI.X : 1e30f, invI.Y > 0f ? 1f / invI.Y : 1e30f, invI.Z > 0f ? 1f / invI.Z : 1e30f);
        // GridGyroscopesComponent.ComputeTorqueTarget: min(coefficient x mass^2, MaxTorque x TorqueDampenerPowerMultiplier)
        float byMass = coef > 0f ? coef * s.Mass * s.Mass : float.MaxValue;
        s.GyroTorque = MathF.Min(byMass, maxTorque * (damp > 0f ? damp : 1f));
        return s;
    }

    // ---------------------------------------------------------------- the simulation

    sealed class Sim
    {
        public Ship S; public bool Steering;
        /// <summary>Unpiloted, dampeners on (AeroGridComponent.AttitudeTarget "damp"): the target is where the ship points,
        /// every step - the mod's thrusters and the game's gyros both only stop the rotation.</summary>
        public bool Damp;
        /// <summary>A thruster dead (switched off, unpowered, damaged: its live maximum 0), by index; -1 none.</summary>
        public int Dead = -1;
        public Quaternion Q = Quaternion.Identity, Target = Quaternion.Identity;
        public Vector3 W;                // local rad/s
        public Vector3 Pos, Vel;         // world
        public Vector3 Gravity = new Vector3(0f, -9.81f, 0f);
        public Vector3 PilotThrust;      // local, -1..1 per axis (the game's thrust input)
        public Func<Sim, Vector3> AirTorque = _ => Vector3.Zero;   // local N m (extra, scripted)
        public float Density;            // kg/m3: the hull's air from the ship's table (fixtures)
        public Vector3 HullForce, HullTorque;   // last step's, local
        public float MaxForceMismatch;   // |mod's thrust total - game's| / |game's|
        public float Time;
        public bool Diverged; public string DivergeNote;
        public Vector3 SteerTorque, GyroDw, ExtraForce;   // last step's (traces); the thrust attitude gave up (applied)            // spun past 50 rad/s: the run is a tumble

        int n; Vector3[] arms, dirs; float[] caps, capDir = new float[6], f0, f, prev, y; float armSq; bool warm;

        public Sim(Ship s, bool steering)
        {
            S = s; Steering = steering; n = s.Thrusters.Count; Gravity = new Vector3(0f, -s.G, 0f);
            arms = new Vector3[n]; dirs = new Vector3[n]; caps = new float[n]; f0 = new float[n]; f = new float[n]; prev = new float[n]; y = new float[n];
            for (int i = 0; i < n; i++)
            {
                var t = s.Thrusters[i];
                dirs[i] = t.dir; arms[i] = Vector3.Cross(t.pos - s.Com, t.dir); caps[i] = t.cap;
                capDir[ThrustAllocator.DirIndex(t.dir)] += t.cap;
                armSq += (t.pos - s.Com).LengthSquared();
            }
        }

        /// <summary>The game's thrust (local N): dampeners hold velocity against gravity, plus the pilot's input, each
        /// axis within the thrusters facing that way.</summary>
        Vector3 GameThrust()
        {
            Vector3 gl = Vector3.Transform(Gravity, Quaternion.Inverse(Q));
            Vector3 vl = Vector3.Transform(Vel, Quaternion.Inverse(Q));
            Vector3 want = -S.Mass * gl - S.Mass * vl / 0.5f;
            if (PilotThrust.X != 0f) want.X = PilotThrust.X * (PilotThrust.X > 0 ? capDir[0] : capDir[1]);
            if (PilotThrust.Y != 0f) want.Y = PilotThrust.Y * (PilotThrust.Y > 0 ? capDir[2] : capDir[3]);
            if (PilotThrust.Z != 0f) want.Z = PilotThrust.Z * (PilotThrust.Z > 0 ? capDir[4] : capDir[5]);
            return new Vector3(Math.Clamp(want.X, -capDir[1], capDir[0]), Math.Clamp(want.Y, -capDir[3], capDir[2]), Math.Clamp(want.Z, -capDir[5], capDir[4]));
        }

        public void Step(float dt = 1f / 60f)
        {
            if (Damp) Target = Q;
            if (Dead >= 0 && caps[Dead] > 0f) { capDir[ThrustAllocator.DirIndex(dirs[Dead])] -= caps[Dead]; caps[Dead] = 0f; }
            Vector3 gameF = GameThrust();
            // the mod (ThrustTorque.Apply): the game's sharing makes the offset torque; with a target, the allocator
            Vector3 torque = ThrustAllocator.GameShare(gameF, arms, dirs, caps, capDir, f0, n);
            if (Steering)
            {
                Vector3 accel = ThrustAllocator.RateCommand(Q, Target, W, 1.2f, 0.8f, 0.3f, 0.002f, Damp ? 0.0002f : 0.002f, out _);
                Vector3 tau = S.I(accel);
                if (!warm) { for (int i = 0; i < n; i++) f[i] = f0[i]; warm = true; }
                ThrustAllocator.Solve(tau, gameF, arms, dirs, caps, f0, f, prev, y, armSq, n, 60, out float prio);
                torque = Vector3.Zero; Vector3 total = Vector3.Zero;
                for (int i = 0; i < n; i++)
                {
                    torque += arms[i] * f[i]; total += dirs[i] * f[i];
                    if (f[i] < -1e-3f || f[i] > caps[i] * 1.0001f) throw new Exception($"thruster {i} force {f[i]} outside 0..{caps[i]}");
                }
                float g = gameF.Length();
                if (g > 1000f) MaxForceMismatch = MathF.Max(MaxForceMismatch, (total - gameF).Length() / g);
                ExtraForce = prio > 0f ? total - gameF : Vector3.Zero;   // (as ThrustTorque: the thrust given up for attitude, applied)
            }
            // the game's target gyros (an angular velocity change this frame)
            Vector3 dwGyro = Diverged ? Vector3.Zero : Gyro.Impulse(S, Q, Target, W);
            SteerTorque = torque; GyroDw = dwGyro;
            // rigid body (local Euler equations), the game's thrust at the centre of mass
            HullForce = HullTorque = Vector3.Zero;
            if (Density > 0f && S.Air.Count > 0)
            {
                Vector3 vl = Vector3.Transform(Vel, Quaternion.Inverse(Q));
                float sp = vl.Length();
                if (sp > 1f) { Hull(S, vl / sp, out HullForce, out HullTorque); float k = 0.5f * Density * sp * sp / 6125f; HullForce *= k; HullTorque *= k; }
            }
            Vector3 tauAll = torque + AirTorque(this) + HullTorque;
            Vector3 Iw = S.I(W);
            Vector3 wdot = S.Iinv(tauAll - Vector3.Cross(W, Iw));
            W += wdot * dt + dwGyro;
            float wl = W.Length();
            if (wl > 1e-9f) Q = Quaternion.Normalize(Q * Quaternion.CreateFromAxisAngle(W / wl, wl * dt));
            Vector3 acc = Vector3.Transform(gameF + HullForce + ExtraForce, Q) / S.Mass + Gravity;
            Vel += acc * dt; Pos += Vel * dt; Time += dt;
            if (!float.IsFinite(W.X + W.Y + W.Z) || W.Length() > 50f) { Diverged = true; DivergeNote = $"w {W} thruster torque {torque} gyro dw {dwGyro} air {AirTorque(this)}"; W = Vector3.Zero; }
        }

        public float Use { get { float u = 0f; for (int i = 0; i < n; i++) u += Steering ? f[i] : f0[i]; return u; } }
        public float ForceOf(int i) => Steering ? f[i] : f0[i];
        public int Count => n;
        public float ErrorDeg => 2f * MathF.Acos(Math.Clamp(MathF.Abs(Quaternion.Dot(Q, Target)), 0f, 1f)) / Deg;
        /// <summary>How far the ship's up has left the target's up (deg): the cross-axis upset of a yaw.</summary>
        public float TiltFromTargetDeg
        {
            get { Vector3 u = Vector3.Transform(Vector3.UnitY, Q), ut = Vector3.Transform(Vector3.UnitY, Target); return MathF.Acos(Math.Clamp(Vector3.Dot(u, ut), -1f, 1f)) / Deg; }
        }
    }

    /// <summary>GridGyroscopesComponent.ComputeTorqueTarget + ApplyAngularImpulseTarget (game 2.4), on a ship whose
    /// principal axes are its grid axes, PerAxisDampeningMultiplier 1. Returns the angular velocity change this frame.</summary>
    static class Gyro
    {
        public static Vector3 Impulse(Ship s, Quaternion orientation, Quaternion target, Vector3 w)
        {
            float num = s.GyroTorque;
            if (num == 0f) return Vector3.Zero;
            const float num2 = 0.1f, num3 = 0.05f, num4 = 0.1f;
            Quaternion quaternion = Quaternion.Inverse(orientation) * target;
            Vector3 vector = w;
            Vector3 vector2 = Euler(quaternion);
            Vector3 vector3 = new Vector3(num * Sat(MathF.Abs(vector2.X) / num3 + num4), num * Sat(MathF.Abs(vector2.Y) / num3 + num4), num * Sat(MathF.Abs(vector2.Z) / num3 + num4));
            Vector3 vector4 = BoundsWeights(s.Size);
            vector3 = Vector3.Lerp(vector3 * vector4, vector3, Sat((Min(vector4) - 0.1f) / 0.15f));
            Vector3 impulse = vector3 * (1f / 60f);
            Vector3 acc = s.Iinv(impulse);
            var (item, item2) = TimeToReach(acc, vector, vector2);
            Vector3 vector5 = vector2;
            if (item > num2 && item != float.MaxValue)
                vector5 = Euler(Quaternion.Lerp(Quaternion.Identity, quaternion, num2 / item)) * item / num2;
            bool justDampen = item <= 1f / 60f;
            Vector3 vector7 = -s.I(w);
            Vector3 vector8 = vector7 / vector3;
            Vector3 value = Vector3.Abs(vector8);
            Vector3 vector9 = Vector3.SignSelect(vector8, -Vector3.One, Vector3.Zero, Vector3.One);
            Vector3 vector10 = Vector3.SignSelect(vector5, -Vector3.One, Vector3.Zero, Vector3.One);
            Vector3 vector11 = s.Iinv(impulse * vector10);
            Vector3 vector12 = vector + vector11;
            Vector3 vector13 = vector5 / vector, vector14 = vector5 / vector12;
            Vector3 vector15 = Vector3.SignSelect(vector, vector13, new Vector3(float.MaxValue), vector13);
            Vector3 vector16 = Vector3.SignSelect(vector12, vector14, new Vector3(float.MaxValue), vector14);
            Vector3 vector17 = Vector3.Min(Vector3.Max(item2, value), item2);
            Vector3 vector18 = vector17 / Max(vector17);
            Vector3 vector19 = new Vector3(
                Axis(vector5.X, vector15.X, vector16.X, value.X, vector.X, vector11.X, impulse.X, vector9.X, vector7.X, justDampen, vector18.X),
                Axis(vector5.Y, vector15.Y, vector16.Y, value.Y, vector.Y, vector11.Y, impulse.Y, vector9.Y, vector7.Y, justDampen, vector18.Y),
                Axis(vector5.Z, vector15.Z, vector16.Z, value.Z, vector.Z, vector11.Z, impulse.Z, vector9.Z, vector7.Z, justDampen, vector18.Z));
            Vector3 gyroImpulse = vector19 * impulse;
            if (IsZero(gyroImpulse.X) && IsZero(gyroImpulse.Y) && IsZero(gyroImpulse.Z)) return Vector3.Zero;   // (Vector3.IsZero: 1e-5 each)
            // ApplyAngularImpulseTarget
            Vector3 dw = s.Iinv(gyroImpulse);
            float a = dw.Length(), b = gyroImpulse.Length();
            return Vector3.Lerp(dw, gyroImpulse * (a / b), Min(vector4));
        }

        /// <summary>Quaternion.ConvertToEuler, its square roots kept off rounding-negative arguments (NaN at +-90 deg pitch).</summary>
        static Vector3 Euler(Quaternion q)
        {
            Vector3 r;
            r.Z = MathF.Atan2(2f * (q.W * q.Z + q.X * q.Y), 1f - 2f * (q.Z * q.Z + q.X * q.X));
            r.Y = MathF.Atan2(2f * (q.W * q.Y + q.Z * q.X), 1f - 2f * (q.X * q.X + q.Y * q.Y));
            float k = 2f * (q.W * q.X - q.Z * q.Y);
            r.X = 2f * MathF.Atan2(MathF.Sqrt(MathF.Max(0f, 1f + k)), MathF.Sqrt(MathF.Max(0f, 1f - k))) - MathF.PI / 2f;
            return r;
        }

        static float Sat(float x) => Math.Clamp(x, 0f, 1f);
        static float Min(Vector3 v) => MathF.Min(v.X, MathF.Min(v.Y, v.Z));
        static float Max(Vector3 v) => MathF.Max(v.X, MathF.Max(v.Y, v.Z));
        static bool IsZero(float x) => MathF.Abs(x) < 1e-5f;   // (the game's float.IsZero)

        static Vector3 BoundsWeights(Vector3 size, float gamma = 1.5f)
        {
            float num = (float)Math.Pow(size.X * size.Y * size.Z, 1.0 / 3.0);
            var v = new Vector3((float)Math.Pow(num / size.X, gamma), (float)Math.Pow(num / size.Y, gamma), (float)Math.Pow(num / size.Z, gamma));
            return Vector3.Min(v, Vector3.One);
        }

        static (float, Vector3) TimeToReach(Vector3 acc, Vector3 ang, Vector3 eulers)
        {
            Vector3 a = new Vector3(Quad(acc.X, ang.X, -eulers.X), Quad(acc.Y, ang.Y, -eulers.Y), Quad(acc.Z, ang.Z, -eulers.Z));
            Vector3 b = new Vector3(Quad(-acc.X, ang.X, -eulers.X), Quad(-acc.Y, ang.Y, -eulers.Y), Quad(-acc.Z, ang.Z, -eulers.Z));
            var r = new Vector3(Select(a.X, b.X), Select(a.Y, b.Y), Select(a.Z, b.Z));
            return (Max(r), r);
        }
        static float Select(float first, float second)
        {
            if (first > 0f && (first < second || second < 0f)) return first;
            if (second > 0f && (second < first || first < 0f)) return second;
            return float.MaxValue;
        }
        static float Quad(float a, float b, float c)
        {
            float d = b * b - 4f * a * c;
            if (d < 0f) return float.MaxValue;
            float sq = MathF.Sqrt(d);
            float r1 = 0.5f * (-b + sq) / a, r2 = 0.5f * (-b - sq) / a;
            if (r1 > 0f && (r1 < r2 || r2 < 0f)) return r1;
            if (r2 > 0f && (r2 < r1 || r1 < 0f)) return r2;
            return float.MaxValue;
        }

        static float Axis(float angle, float timeToReach, float timeToReachNext, float timeToStop, float localAngularVelocity, float angularVelocityLocalDelta, float maxTorquePerFrame, float dampeningDirection, float impulseToStop, bool justDampen, float torqueMultiplier)
        {
            if (!justDampen)
            {
                if (IsZero(timeToReach) && IsZero(timeToStop) && IsZero(angle)) return 0f;
                if (IsZero(localAngularVelocity) || Math.Sign(localAngularVelocity * angle) <= 0)
                {
                    float num = Math.Abs(angularVelocityLocalDelta * (1f / 60f)), num2 = Math.Abs(angle);
                    if (num > num2) return angle / num;
                    return Math.Sign(angle) * torqueMultiplier;
                }
                if (timeToReach > timeToStop)
                {
                    if (timeToReachNext < timeToStop) return Math.Sign(angle) * ((timeToStop - timeToReachNext) / (timeToReach - timeToReachNext)) * torqueMultiplier;
                    float num4 = Math.Abs(angularVelocityLocalDelta * (1f / 60f)), num5 = Math.Abs(angle);
                    if (num4 > num5) return angle / num4;
                    return Math.Sign(angle) * torqueMultiplier;
                }
            }
            float num6 = Math.Abs(impulseToStop);
            if (num6 < maxTorquePerFrame) return dampeningDirection * num6 / maxTorquePerFrame;
            return dampeningDirection;
        }
    }

    // ---------------------------------------------------------------- 1. allocator

    static void AllocatorChecks()
    {
        foreach (var com in new[] { Vector3.Zero, new Vector3(1.5f, 0.8f, -4f) })
        {
            var s = Hercules(com, 0f);
            int n = s.Thrusters.Count;
            var arms = new Vector3[n]; var dirs = new Vector3[n]; var caps = new float[n]; var capDir = new float[6];
            float armSq = 0f;
            for (int i = 0; i < n; i++)
            {
                var t = s.Thrusters[i];
                dirs[i] = t.dir; arms[i] = Vector3.Cross(t.pos - com, t.dir); caps[i] = t.cap; capDir[ThrustAllocator.DirIndex(t.dir)] += t.cap; armSq += (t.pos - com).LengthSquared();
            }
            string tag = com == Vector3.Zero ? "centred" : "off-centre";
            var hover = new Vector3(0f, s.Mass * 9.81f, 0f);
            var f0 = new float[n]; var f = new float[n]; var prev = new float[n]; var y = new float[n];
            Vector3 offset = ThrustAllocator.GameShare(hover, arms, dirs, caps, capDir, f0, n);
            if (com == Vector3.Zero) Check(offset.Length() < 1f, $"allocator {tag}: the game's sharing of a hover makes no torque ({offset.Length():F0} N m)");
            else Check(offset.Length() > 1e5f, $"allocator {tag}: the game's sharing of a hover makes an offset torque ({offset.Length():F0} N m)");

            // no torque wanted: the offset cancelled, the thrust kept
            Array.Copy(f0, f, n);
            for (int k = 0; k < 20; k++) ThrustAllocator.Solve(Vector3.Zero, hover, arms, dirs, caps, f0, f, prev, y, armSq, n, 60);
            Measure(f, arms, dirs, n, out var tq, out var tot);
            Check((tot - hover).Length() <= 0.005f * hover.Length() + 1f, $"allocator {tag}: hover thrust kept ({tot - hover})");
            Check(tq.Length() <= 0.05f * MathF.Max(offset.Length(), 1e5f), $"allocator {tag}: hover torque cancelled ({tq.Length():F0} N m, offset {offset.Length():F0})");

            // a torque about each axis, both ways: delivered (direction and >= 80% of it, when the thrusters can) with the thrust kept
            foreach (var axis in new[] { Vector3.UnitX, -Vector3.UnitX, Vector3.UnitY, -Vector3.UnitY, Vector3.UnitZ, -Vector3.UnitZ })
            {
                Vector3 want = axis * 2e6f;
                Array.Copy(f0, f, n);
                for (int k = 0; k < 20; k++) ThrustAllocator.Solve(want, hover, arms, dirs, caps, f0, f, prev, y, armSq, n, 60);
                Measure(f, arms, dirs, n, out tq, out tot);
                float along = Vector3.Dot(tq, axis);
                Check((tot - hover).Length() <= 0.005f * hover.Length() + 1f, $"allocator {tag}: torque {axis} keeps the thrust (off by {(tot - hover).Length():F0} N)");
                Check(along >= 0.8f * want.Length(), $"allocator {tag}: torque {axis}: {along:F0} of {want.Length():F0} N m delivered (off-axis {(tq - axis * along).Length():F0})");
                Check((tq - axis * along).Length() <= 0.1f * want.Length(), $"allocator {tag}: torque {axis}: off-axis {(tq - axis * along).Length():F0} N m");
                for (int i = 0; i < n; i++) if (f[i] < 0f || f[i] > caps[i] * 1.0001f) { Check(false, $"allocator {tag}: force {i} {f[i]} within 0..{caps[i]}"); break; }
            }

            // after a turn, holding still: the thrusters drain back to the hover's own (2026-10-09: couples - opposing
            // thrusters firing against each other - cost nothing in the solve, so after any turn they stayed lit at a low
            // glow on every thruster, warm-started frame to frame: the flames 'stuck on'). Steered 1 s about yaw, then
            // 3 s of no torque (60 frames a second, as in game).
            {
                ThrustAllocator.GameShare(hover, arms, dirs, caps, capDir, f0, n);
                Array.Copy(f0, f, n);
                for (int k = 0; k < 60; k++) ThrustAllocator.Solve(new Vector3(0f, 2e6f, 0f), hover, arms, dirs, caps, f0, f, prev, y, armSq, n, 60);
                float turnUse = 0f; for (int i = 0; i < n; i++) turnUse += f[i];
                for (int k = 0; k < 180; k++) ThrustAllocator.Solve(Vector3.Zero, hover, arms, dirs, caps, f0, f, prev, y, armSq, n, 60);
                float use = 0f, hoverUse = 0f, sumCap = 0f; int litDown = 0;
                for (int i = 0; i < n; i++) { use += f[i]; hoverUse += f0[i]; sumCap += caps[i]; if (dirs[i].Y < -0.5f && f[i] > 0.01f * caps[i]) litDown++; }
                Measure(f, arms, dirs, n, out tq, out tot);
                Check(use <= hoverUse * 1.05f + 0.005f * sumCap, $"allocator {tag}: after a turn, holding still drains to the hover's thrust (total {use:F0} N, the hover's {hoverUse:F0}, during the turn {turnUse:F0})");
                Check(litDown == 0, $"allocator {tag}: after a turn, no downward thruster left lit against the upward ones ({litDown} lit)");
                Check((tot - hover).Length() <= 0.005f * hover.Length() + 1f, $"allocator {tag}: after a turn, hover thrust kept ({(tot - hover).Length():F0} N off)");
            }

            // full upward thrust: nothing to rebalance with along Y (every up thruster at max) - the thrust must still be kept
            var full = new Vector3(0f, capDir[2], 0f);
            ThrustAllocator.GameShare(full, arms, dirs, caps, capDir, f0, n);
            Array.Copy(f0, f, n);
            float prio = 0f;
            for (int k = 0; k < 20; k++) ThrustAllocator.Solve(new Vector3(2e6f, 0f, 0f), full, arms, dirs, caps, f0, f, prev, y, armSq, n, 60, out prio);
            Measure(f, arms, dirs, n, out tq, out tot);
            // every up thruster at full power: pitch can only come from giving some of it up (attitude priority, bounded)
            Check((tot - full).Length() <= ThrustAllocator.MaxThrustLoss * full.Length() + 1f, $"allocator {tag}: at full thrust, at most {ThrustAllocator.MaxThrustLoss * 100f:F0}% given up for attitude ({(tot - full).Length() / full.Length() * 100f:F1}%)");
            Check(tq.X > 0.3f * 2e6f, $"allocator {tag}: at full thrust it still pitches the way asked ({tq.X:F0} of 2000000 N m, priority {prio:F2})");
        }
    }

    /// <summary>The control audit of 2026-10-10: unpiloted damping (no remembered attitude, no creep, no burn left on) and a
    /// dead thruster (never shared onto, never steered with).</summary>
    static void ControlAuditChecks()
    {
        foreach (var com in new[] { Vector3.Zero, new Vector3(1.5f, 0.8f, -4f) })
        {
            string tag = com == Vector3.Zero ? "centred" : "off-centre";
            // unpiloted, spinning: damped to rest where it ends up - and stays there (it crept back to a held attitude)
            var sim = new Sim(Hercules(com, 0f), true) { Damp = true, W = new Vector3(0.05f, 0.3f, -0.08f) };
            for (int k = 0; k < 360; k++) sim.Step();
            float w6 = sim.W.Length(); var q6 = sim.Q;
            for (int k = 0; k < 300; k++) sim.Step();
            float drift = 2f * MathF.Acos(Math.Clamp(MathF.Abs(Quaternion.Dot(q6, sim.Q)), 0f, 1f)) / Deg;
            float hoverUse = sim.S.Mass * sim.S.G;
            Check(!sim.Diverged && w6 < 0.01f, $"damp {tag}: an unpiloted spin damped to rest in 6 s ({w6:F4} rad/s)");
            Check(drift < 0.5f, $"damp {tag}: then it stays where it stopped - no creep to a held attitude ({drift:F2} deg in 5 s)");
            Check(sim.Use <= hoverUse * 1.10f, $"damp {tag}: no thrust left burning against itself at rest ({sim.Use:F0} N, the hover's {hoverUse:F0})");
            // a dead thruster: never given thrust, never steered with, the rest still hold the hover and steer
            var probe = new Sim(Hercules(com, 0f), true);
            int dead = -1;
            for (int i = 0; i < probe.Count; i++) if (Hercules(com, 0f).Thrusters[i].dir.Y > 0.5f) { dead = i; break; }
            var dsim = new Sim(Hercules(com, 0f), true) { Dead = dead };
            float worst = 0f;
            for (int k = 0; k < 240; k++) { dsim.Target = Quaternion.CreateFromAxisAngle(Vector3.UnitY, k < 120 ? 0.3f : 0f); dsim.Step(); worst = MathF.Max(worst, dsim.ForceOf(dead)); }
            Check(dead >= 0 && worst == 0f, $"dead thruster {tag}: an upward thruster switched off is never given thrust or steered with (max {worst:F1} N)");
            // (can the rest balance it at all? the hover's own torque with the dead one gone, against the best the solver finds)
            {
                var sh = Hercules(com, 0f); int nn = sh.Thrusters.Count;
                var ar = new Vector3[nn]; var di = new Vector3[nn]; var ca = new float[nn]; var cd = new float[6]; float asq = 0f;
                for (int i = 0; i < nn; i++) { var t = sh.Thrusters[i]; di[i] = t.dir; ar[i] = Vector3.Cross(t.pos - com, t.dir); ca[i] = i == dead ? 0f : t.cap; cd[ThrustAllocator.DirIndex(t.dir)] += ca[i]; asq += (t.pos - com).LengthSquared(); }
                var hov = new Vector3(0f, sh.Mass * 9.81f, 0f); var g0 = new float[nn]; var ff = new float[nn]; var pv = new float[nn]; var yy = new float[nn];
                var off = ThrustAllocator.GameShare(hov, ar, di, ca, cd, g0, nn); Array.Copy(g0, ff, nn);
                for (int k = 0; k < 40; k++) ThrustAllocator.Solve(Vector3.Zero, hov, ar, di, ca, g0, ff, pv, yy, asq, nn, 60);
                Measure(ff, ar, di, nn, out var tq, out var tt);
                float upCap = 0f; for (int i = 0; i < nn; i++) if (di[i].Y > 0.5f) upCap += ca[i];
                Console.WriteLine($"   dead {tag}: hover {hov.Y:F0} N of {upCap:F0} N up capacity; offset torque {off.Length():F0}, left after balancing {tq.Length():F0} N m (net off {(tt - hov).Length():F0} N)");
            }
            // (centred: the rest balance it and steer; off-centre with an upward one dead, no sharing that keeps it up balances
            //  it - the line above: it tilts, as it physically must - stable, the dead one still unused)
            if (com == Vector3.Zero) Check(!dsim.Diverged && dsim.ErrorDeg < 5f, $"dead thruster {tag}: the others still turn it and hold it ({dsim.ErrorDeg:F2} deg off)");
            else Check(!dsim.Diverged, $"dead thruster {tag}: unbalanceable with it gone - it tilts ({dsim.ErrorDeg:F1} deg), stays stable"); 
        }
    }

    static void Measure(float[] f, Vector3[] arms, Vector3[] dirs, int n, out Vector3 torque, out Vector3 total)
    {
        torque = Vector3.Zero; total = Vector3.Zero;
        for (int i = 0; i < n; i++) { torque += arms[i] * f[i]; total += dirs[i] * f[i]; }
    }

    // ---------------------------------------------------------------- 2. flight

    sealed class Run
    {
        public float Speed0, Speed1, MaxHullTorque;
        public float Within2 = float.NaN, Settled = float.NaN, PeakErrAfter, Overshoot, MaxAltDev, MaxTilt, MaxErr, FinalErr, Mismatch;
        public override string ToString() =>
            $"within 2 deg {(float.IsNaN(Within2) ? "never" : Within2.ToString("F1") + " s")}, settled {(float.IsNaN(Settled) ? "never" : Settled.ToString("F1") + " s")}, overshoot {Overshoot:F1} deg, " +
            $"max err {MaxErr:F1} deg, final {FinalErr:F2} deg, cross-axis {MaxTilt:F1} deg, altitude {MaxAltDev:F1} m, thrust mismatch {Mismatch * 100f:F2}%" +
            (MaxHullTorque > 0f ? $", speed {Speed0:F0} -> {Speed1:F0} m/s, hull torque up to {MaxHullTorque / 1e6f:F1} MN m" : "");
    }

    /// <summary>Fly: settle 5 s on the start orientation, then the target turns by `turn` (local axis-angle); T seconds.</summary>
    static Run Fly(Ship ship, bool steering, Vector3 turn, float T, Vector3 pilot = default, Vector3 startVel = default, Func<Sim, Vector3> air = null, bool yawTilt = true, float density = 0f)
    {
        var sim = new Sim(ship, steering) { PilotThrust = pilot, Vel = startVel, Density = density };
        if (air != null) sim.AirTorque = air;
        for (int i = 0; i < 300; i++) sim.Step();   // settle on the start orientation
        float alt0 = sim.Pos.Y + 0f;
        // (forward flight: altitude from the settled state; the target turns from the current target)
        float angle = turn.Length();
        if (angle > 0f) sim.Target = Quaternion.Normalize(sim.Target * Quaternion.CreateFromAxisAngle(turn / angle, angle));
        float t0 = sim.Time; var r = new Run { Speed0 = sim.Vel.Length() };
        float lastOut = 0f; bool crossed = false;
        sim.MaxForceMismatch = 0f;
        while (sim.Time - t0 < T && !sim.Diverged)
        {
            sim.Step();
            float t = sim.Time - t0, e = sim.ErrorDeg;
            r.MaxErr = MathF.Max(r.MaxErr, e);
            if (e > 2f) lastOut = t;
            if (float.IsNaN(r.Within2) && e <= 2f) r.Within2 = t;
            // overshoot: the error's component past the target along the turn axis
            if (angle > 0f)
            {
                Quaternion rel = Quaternion.Inverse(sim.Target) * sim.Q;
                if (rel.W < 0f) rel = new Quaternion(-rel.X, -rel.Y, -rel.Z, -rel.W);
                float past = 2f * Vector3.Dot(new Vector3(rel.X, rel.Y, rel.Z), turn / angle) / Deg;   // + : beyond the target
                r.Overshoot = MathF.Max(r.Overshoot, past);
            }
            if (yawTilt) r.MaxTilt = MathF.Max(r.MaxTilt, sim.TiltFromTargetDeg * (angle > 0f && MathF.Abs(turn.Y) < 0.5f * angle ? 0f : 1f));
            r.MaxAltDev = MathF.Max(r.MaxAltDev, MathF.Abs(sim.Pos.Y - alt0));
            r.MaxHullTorque = MathF.Max(r.MaxHullTorque, sim.HullTorque.Length());
            if (Trace && ((int)MathF.Round(t * 60f)) % 15 == 0)
                Console.WriteLine($"      t {t:F2} err {e:F1} deg w ({sim.W.X:F3} {sim.W.Y:F3} {sim.W.Z:F3}) v {sim.Vel.Length():F0} hull T ({sim.HullTorque.X / 1e6f:F2} {sim.HullTorque.Y / 1e6f:F2} {sim.HullTorque.Z / 1e6f:F2}) MN m, steer T ({sim.SteerTorque.X / 1e6f:F2} {sim.SteerTorque.Y / 1e6f:F2} {sim.SteerTorque.Z / 1e6f:F2}), gyro dw ({sim.GyroDw.X:F4} {sim.GyroDw.Y:F4} {sim.GyroDw.Z:F4})");
        }
        r.Settled = lastOut < T - 1f ? lastOut : float.NaN;
        r.Speed1 = sim.Vel.Length();
        r.FinalErr = sim.Diverged ? 180f : sim.ErrorDeg; r.Mismatch = sim.MaxForceMismatch;
        if (sim.Diverged) { r.MaxErr = 180f; r.Settled = r.Within2 = float.NaN; }
        return r;
    }

    static void FlightChecks()
    {
        // the centre of mass 4 m forward, 0.8 m up and 1.5 m right of the thrusters' centre: an offset torque the steering must hold
        Vector3 com = new Vector3(1.5f, 0.8f, -4f);
        foreach (float gyro in new[] { 1e6f, 5e6f })
        {
            var ship = Hercules(com, gyro);
            string g = $"gyro {gyro / 1e6f:F0} MN m";
            void Pair(string what, Func<bool, Run> fly, Action<Run, Run> judge)
            {
                Run on = fly(true), off = fly(false);
                Console.WriteLine($"  {g} {what}\n      ON : {on}\n      OFF: {off}");
                judge(on, off);
            }

            Pair("hover 20 s", st => Fly(ship, st, Vector3.Zero, 20f), (on, off) =>
            {
                Check(on.MaxErr < 0.5f, $"{g} hover: level within 0.5 deg (max {on.MaxErr:F2})");
                Check(on.MaxAltDev < 1f, $"{g} hover: altitude within 1 m ({on.MaxAltDev:F2})");
                Check(on.Mismatch < 0.01f, $"{g} hover: thrust kept ({on.Mismatch * 100f:F2}%)");
            });

            foreach (var (name, turn, budget) in new[] { ("yaw 45", new Vector3(0f, 45f, 0f), 12f), ("pitch 20", new Vector3(20f, 0f, 0f), 10f), ("roll 20", new Vector3(0f, 0f, 20f), 10f) })
            {
                Pair($"{name} while hovering", st => Fly(ship, st, turn * Deg, 25f), (on, off) =>
                {
                    Check(!float.IsNaN(on.Within2) && on.Within2 <= budget, $"{g} {name}: within 2 deg in {budget} s ({on.Within2:F1})");
                    Check(!float.IsNaN(on.Settled), $"{g} {name}: settles (final {on.FinalErr:F2} deg)");
                    Check(on.Overshoot < 3f, $"{g} {name}: overshoot under 3 deg ({on.Overshoot:F1})");
                    Check(on.MaxAltDev < 5f, $"{g} {name}: altitude within 5 m ({on.MaxAltDev:F1})");
                    Check(on.Mismatch < 0.01f, $"{g} {name}: thrust kept ({on.Mismatch * 100f:F2}%)");
                    Check(float.IsNaN(off.Within2) || on.Within2 <= off.Within2 * 1.1f + 0.2f, $"{g} {name}: steering not slower than gyros alone ({on.Within2:F1} vs {off.Within2:F1} s)");
                });
            }

            // forward flight: full forward thrust (-Z), then a 30 deg yaw
            Pair("yaw 30 at full forward thrust", st => Fly(ship, st, new Vector3(0f, 30f, 0f) * Deg, 20f, pilot: new Vector3(0f, 0f, -1f)), (on, off) =>
            {
                Check(!float.IsNaN(on.Within2) && on.Within2 <= 10f, $"{g} forward yaw: within 2 deg in 10 s ({on.Within2:F1})");
                Check(on.Overshoot < 3f, $"{g} forward yaw: overshoot under 3 deg ({on.Overshoot:F1})");
                Check(on.Mismatch <= ThrustAllocator.MaxThrustLoss + 0.01f, $"{g} forward yaw: thrust given up for attitude at most {ThrustAllocator.MaxThrustLoss * 100f:F0}% ({on.Mismatch * 100f:F2}%)");
            });

            // the air: a steady pitch-up torque of 1.5 MN m (a centre of pressure off the centre of mass), hovering
            Pair("hold against 1.5 MN m steady air torque", st => Fly(ship, st, Vector3.Zero, 20f, air: _ => new Vector3(1.5e6f, 0f, 0f)), (on, off) =>
            {
                Check(on.MaxErr < 3f, $"{g} steady air torque: held within 3 deg ({on.MaxErr:F2})");
                Check(on.MaxErr <= off.MaxErr + 0.1f, $"{g} steady air torque: steering not worse than gyros alone ({on.MaxErr:F2} vs {off.MaxErr:F2})");
            });

            // the air, unstable: torque grows with the pitch error (centre of pressure ahead of the centre of mass), 6 MN m/rad
            Pair("hold against unstable air (6 MN m/rad) after a 2 deg kick", st => Fly(ship, st, Vector3.Zero, 20f,
                air: s => { Quaternion e = Quaternion.Inverse(s.Target) * s.Q; float pitch = 2f * e.X * MathF.Sign(e.W == 0f ? 1f : e.W); return new Vector3(6e6f * pitch + (s.Time < 5.2f && s.Time > 5f ? 2e7f : 0f), 0f, 0f); }), (on, off) =>
            {
                Check(on.FinalErr < 1f, $"{g} unstable air: recovered within 1 deg ({on.FinalErr:F2})");
                Check(on.MaxErr < 10f, $"{g} unstable air: never past 10 deg ({on.MaxErr:F1})");
            });
        }
    }


    // ---------------------------------------------------------------- 3. real ships (Tests/data/*.flight)

    /// <summary>The most torque the steering can make about each local axis while the game hovers the ship (N m).</summary>
    static Vector3 Authority(Ship s) => Authority(s, Vector3.Zero);

    /// <summary>The same with the game also thrusting along `extra` (local N, added to the hover; each axis within the
    /// thrusters' capacity) - e.g. full forward thrust.</summary>
    static Vector3 Authority(Ship s, Vector3 extra)
    {
        var sim = new Sim(s, true);
        int n = s.Thrusters.Count;
        var arms = new Vector3[n]; var dirs = new Vector3[n]; var caps = new float[n]; var capDir = new float[6]; float armSq = 0f;
        for (int i = 0; i < n; i++) { var t = s.Thrusters[i]; dirs[i] = t.dir; arms[i] = Vector3.Cross(t.pos - s.Com, t.dir); caps[i] = t.cap; capDir[ThrustAllocator.DirIndex(t.dir)] += t.cap; armSq += (t.pos - s.Com).LengthSquared(); }
        var hover = new Vector3(0f, Math.Min(s.Mass * s.G, capDir[2]), 0f) + extra;
        hover = new Vector3(Math.Clamp(hover.X, -capDir[1], capDir[0]), Math.Clamp(hover.Y, -capDir[3], capDir[2]), Math.Clamp(hover.Z, -capDir[5], capDir[4]));
        var f0 = new float[n]; var f = new float[n]; var prev = new float[n]; var y = new float[n];
        ThrustAllocator.GameShare(hover, arms, dirs, caps, capDir, f0, n);
        Vector3 auth = Vector3.Zero;
        for (int ax = 0; ax < 3; ax++)
        {
            float worst = float.MaxValue;
            foreach (float sign in new[] { 1f, -1f })
            {
                Vector3 want = (ax == 0 ? Vector3.UnitX : ax == 1 ? Vector3.UnitY : Vector3.UnitZ) * sign * 1e12f;
                Array.Copy(f0, f, n);
                for (int k = 0; k < 30; k++) ThrustAllocator.Solve(want, hover, arms, dirs, caps, f0, f, prev, y, armSq, n, 60);
                Measure(f, arms, dirs, n, out var tq, out _);
                worst = MathF.Min(worst, sign * (ax == 0 ? tq.X : ax == 1 ? tq.Y : tq.Z));
            }
            if (ax == 0) auth.X = worst; else if (ax == 1) auth.Y = worst; else auth.Z = worst;
        }
        return auth;
    }

    static void FixtureChecks(string dataDir)
    {
        var files = Directory.Exists(dataDir) ? Directory.GetFiles(dataDir, "*.flight") : Array.Empty<string>();
        if (files.Length == 0) { Console.WriteLine("  (no Tests/data/*.flight fixtures - dump one in game: flightdump ID NAME)"); return; }
        foreach (var file in files)
        {
            var asMod = LoadFixture(file, +1f);   // ThrustTorque: push = ThrustDirection
            var asIs = LoadFixture(file, -1f);    // (the old, negated reading)
            string nm = asMod.Name;
            Console.WriteLine($"  {nm}: {asMod.Mass / 1000f:F0} t in {asMod.G:F2} m/s2 (lift needs {asMod.Mass * asMod.G / 1e6f:F2} MN), {asMod.Thrusters.Count} thrusters, gyro limit {asMod.GyroTorque / 1e6f:F2} MN m, {asMod.Air.Count} air samples");

            // a. the push directions: the thrusters grouped by push direction must add up to the game's own capacities
            Vector3 Group(Ship s, bool pos) { var g = Vector3.Zero; foreach (var t in s.Thrusters) { int k = ThrustAllocator.DirIndex(t.dir); if (k % 2 == (pos ? 0 : 1)) { if (k / 2 == 0) g.X += t.cap; else if (k / 2 == 1) g.Y += t.cap; else g.Z += t.cap; } } return g; }
            bool Matches(Ship s) => (Group(s, true) - s.MaxThrustPos).Length() <= 0.02f * s.MaxThrustPos.Length() + 1f && (Group(s, false) - s.MaxThrustNeg).Length() <= 0.02f * s.MaxThrustNeg.Length() + 1f;
            Console.WriteLine($"      game capacity +{s(asMod.MaxThrustPos)} -{s(asMod.MaxThrustNeg)}\n      ThrustTorque's grouping (ThrustDirection)  +{s(Group(asMod, true))} -{s(Group(asMod, false))}\n      negated (the old reading)       +{s(Group(asIs, true))} -{s(Group(asIs, false))}");
            if (asMod.MaxThrustPos.Length() + asMod.MaxThrustNeg.Length() < 1f) Console.WriteLine("      (the game reported no thrust capacity - thrusters unpowered when dumped: grouping not checked)");
            else Check(Matches(asMod), $"{nm}: ThrustTorque's push directions give the game's thrust capacities");
            var ship = Matches(asMod) ? asMod : Matches(asIs) ? asIs : asMod;

            // a2. the air's torque is about the centre of mass: its force's line of action passes within the ship (the arm |T|/|F|
            //     no longer than half the ship's diagonal - a table built about the grid's origin put it 70 m out on the Hercules)
            if (asMod.Air.Count > 0)
            {
                float half = asMod.Size.Length() / 2f, worstArm = 0f; Vector3 worstDir = default;
                foreach (var (dir, force, torque) in asMod.Air)
                {
                    float fl = force.Length();
                    if (fl < 1f) continue;
                    float arm = Vector3.Cross(force, torque).Length() / (fl * fl);
                    if (arm > worstArm) { worstArm = arm; worstDir = dir; }
                }
                Check(worstArm <= half * 1.05f, $"{nm}: the air's force acts within the ship (worst arm {worstArm:F1} m at travel {worstDir}, half diagonal {half:F1} m)");
            }

            // can it fly here at all? (lift short of its weight: every up thruster at full power just to slow the fall - nothing
            // left to steer with; the flight checks would test the impossible)
            float liftCap = 0f; foreach (var t in ship.Thrusters) if (ThrustAllocator.DirIndex(t.dir) == 2) liftCap += t.cap;
            if (liftCap < ship.Mass * ship.G)
            {
                Console.WriteLine($"      CANNOT HOVER in {ship.G:F2} m/s2: lift {liftCap / 1e6f:F2} MN < weight {ship.Mass * ship.G / 1e6f:F2} MN (thrusters at their vacuum maximum) - flight checks skipped");
                continue;
            }

            // b. authority
            Vector3 auth = Authority(ship);
            Console.WriteLine($"      steering authority while hovering (N m): pitch {auth.X / 1e6f:F2} M, yaw {auth.Y / 1e6f:F2} M, roll {auth.Z / 1e6f:F2} M");
            {
                int nT = ship.Thrusters.Count; float fwdCap = 0f; foreach (var t in ship.Thrusters) if (ThrustAllocator.DirIndex(t.dir) == 5) fwdCap += t.cap;
                Vector3 authF = Authority(ship, new Vector3(0f, 0f, -fwdCap));
                Console.WriteLine($"      ... and at full forward thrust: pitch {authF.X / 1e6f:F2} M, yaw {authF.Y / 1e6f:F2} M, roll {authF.Z / 1e6f:F2} M");
                Check(authF.X > 0f && authF.Y > 0f && authF.Z > 0f, $"{nm}: steering can turn both ways on every axis at full forward thrust (pitch {authF.X / 1e6f:F2}, yaw {authF.Y / 1e6f:F2}, roll {authF.Z / 1e6f:F2} MN m)");
            }

            // c. hover, turns
            foreach (var (name, turn, budget) in new[] { ("hover", Vector3.Zero, 0f), ("yaw 45", new Vector3(0f, 45f, 0f), 15f), ("pitch 20", new Vector3(20f, 0f, 0f), 12f), ("roll 20", new Vector3(0f, 0f, 20f), 12f) })
            {
                Run on = Fly(ship, true, turn * Deg, 25f), off = Fly(ship, false, turn * Deg, 25f);
                Console.WriteLine($"      {name}: steering {on}\n      {new string(' ', name.Length)}  offset only {off}");
                if (budget == 0f) { Check(on.MaxErr < 1f, $"{nm} hover: level within 1 deg ({on.MaxErr:F2})"); Check(on.MaxAltDev < 2f, $"{nm} hover: altitude within 2 m ({on.MaxAltDev:F2})"); }
                else
                {
                    Check(!float.IsNaN(on.Within2) && on.Within2 <= budget, $"{nm} {name}: within 2 deg in {budget} s ({on.Within2:F1})");
                    Check(on.Overshoot < 3f, $"{nm} {name}: overshoot under 3 deg ({on.Overshoot:F1})");
                    Check(on.MaxAltDev < 10f, $"{nm} {name}: altitude within 10 m ({on.MaxAltDev:F1})");
                }
            }

            // d. the air: level flight at full forward thrust from 100 and 200 m/s (sea-level-ish density 1.0), holding
            //    attitude, then a 30 deg yaw at speed
            if (ship.Air.Count > 0)
                foreach (float v0 in new[] { 100f, 200f })
                {
                    bool wantTrace = Environment.GetEnvironmentVariable("FLYTRACE") == nm && v0 == 100f; Trace = false;
                    Run hold = Fly(ship, true, Vector3.Zero, 20f, pilot: new Vector3(0f, 0f, -1f), startVel: new Vector3(0f, 0f, -v0), density: 1.0f);
                    Trace = wantTrace;
                    Run yaw = Fly(ship, true, new Vector3(0f, 30f, 0f) * Deg, 20f, pilot: new Vector3(0f, 0f, -1f), startVel: new Vector3(0f, 0f, -v0), density: 1.0f);
                    Trace = false;
                    Console.WriteLine($"      air {v0:F0} m/s hold: {hold}\n      air {v0:F0} m/s yaw 30: {yaw}");
                    Check(hold.MaxErr < 5f, $"{nm} air {v0:F0} m/s: attitude held within 5 deg ({hold.MaxErr:F1}; hull torque {hold.MaxHullTorque / 1e6f:F1} MN m vs authority {MathF.Min(auth.X, MathF.Min(auth.Y, auth.Z)) / 1e6f:F1})");
                    Check(!float.IsNaN(yaw.Within2) && yaw.Within2 < 15f, $"{nm} air {v0:F0} m/s: yaw 30 within 2 deg in 15 s ({yaw.Within2:F1}, max err {yaw.MaxErr:F1})");
                    Check(!float.IsNaN(yaw.Settled) && yaw.Overshoot < 5f, $"{nm} air {v0:F0} m/s: yaw 30 settles, overshoot under 5 deg (overshoot {yaw.Overshoot:F1}, final {yaw.FinalErr:F1}, cross-axis {yaw.MaxTilt:F1})");
                }
        }
        static string s(Vector3 v) => $"({v.X / 1e6f:F2} {v.Y / 1e6f:F2} {v.Z / 1e6f:F2}) MN";
    }

    /// <summary>A thruster's torque arm (OffsetThrustJob's cache, Core/CellCoords): its block centre as the game has it -
    /// CubeGridCoords.GridToLocal(AABB).Center, cell i centred at 0.25 i. (2026-10-15: (min + max + 1) / 2 cells put every
    /// thruster 0.125 m off on each axis: a symmetric ship thrusting 2 MN along -Z took a fake 250 kN m couple.)</summary>
    static void CellCentreChecks()
    {
        // the game's: GridToLocal(BoundingBoxI) = (min x 0.25 - 0.125, max x 0.25 + 0.125), its centre
        static Vector3 Game(Vector3I lo, Vector3I hi) => ((new Vector3(lo.X, lo.Y, lo.Z) * 0.25f - new Vector3(0.125f)) + (new Vector3(hi.X, hi.Y, hi.Z) * 0.25f + new Vector3(0.125f))) * 0.5f;
        var boxes = new[] { (new Vector3I(0, 0, 0), new Vector3I(0, 0, 0)), (new Vector3I(-5, 2, 7), new Vector3I(4, 11, 16)), (new Vector3I(-9, -9, -9), new Vector3I(-8, -2, 0)) };
        foreach (var (lo, hi) in boxes)
            Check((CellCoords.BlockCentre(lo, hi) - Game(lo, hi)).Length() < 1e-5f, $"cell centre: block {lo}..{hi} at {CellCoords.BlockCentre(lo, hi)}, the game's {Game(lo, hi)}");
        // two large thrusters mirrored about the centre of mass (a 1-cell block at the origin's centre: (0,0,0)), 1 MN each along -Z
        var a = CellCoords.BlockCentre(new Vector3I(-14, -4, 10), new Vector3I(-5, 4, 19));
        var b = CellCoords.BlockCentre(new Vector3I(5, -4, 10), new Vector3I(14, 4, 19));
        var couple = Vector3.Cross(a, new Vector3(0, 0, -1e6f)) + Vector3.Cross(b, new Vector3(0, 0, -1e6f));
        Check(couple.Length() < 1f, $"cell centre: mirrored thrusters make no couple ({couple.Length() / 1000f:F0} kN m)");
    }

    /// <summary>REVERSIBLE THRUSTERS (Propulsion/ReversibleThrust.cs, ReversibleThrustComponent, 2026-10-17): thrust either way,
    /// in the game's movement and in the attitude law. A 60 Hz rigid body flown as in the game: each reversible thruster's game
    /// side from ReversibleThrust.Step (its ThrustData.Direction), the game's thrust per axis on the maxima booked by side (the
    /// pilot's input x the side's maximum, else the dampeners: gravity hold + -m v x 60, clamped), shared onto the thrusters
    /// (ThrustAllocator.GameShare with the sides), and - steered, or a rate pilot on a grid with reversible thrusters or main
    /// rotors - ThrustAllocator.Solve with the reversible ones both ways; torque applied, the net force change applied only
    /// with attitudePriority above 0 (ThrustTorque.Apply).</summary>
    sealed class RevShip
    {
        public float Mass = 5000f; public Vector3 I = new Vector3(20000f, 40000f, 30000f);
        public readonly List<Vector3> Pos = new(), Dir = new(); public readonly List<float> Cap = new(); public readonly List<bool> Rev = new();
        public Vector3 Reaction;           // a main rotor's reaction torque (N m, local)
        public bool Damp = true, Gravity = true, Solve = true;
        // ── the world's PhysicsSessionConfiguration.GravityMultiplier (the engine scales the gravity it applies - CustomGravity -
        //    by it; vanilla 2) and the game's GRAVITY HOLD as the game does it (ThrustComponent.ComputeThrust): with dampeners the
        //    hold (-m g along each axis, clamped to that side's maximum) is not applied as thrust - it is taken off the grid's
        //    CustomGravity IN PLACE (up to all of it) BEFORE the engine scales that, and only the rest of the game's thrust is
        //    applied as force. So a dampened hover cancels gm x m g of real gravity with m g of booked thrust, while without
        //    dampeners the thrust must beat gm x m g (the helicopter that hovered at 0.49 of its rotor could not take off with
        //    them off, gm 2). Ground: a floor at Height 0 (the ship starts parked on it). At gm 1 every step is as before. ──
        public float GravityMultiplier = 1f; public bool Ground; public float Height, MaxHeight;
        // ── HONEST DAMPENERS (Physics.HonestDampeners, HonestHold / HonestDampeners.cs): after the game's step above, the hold re-made
        //    as real thrust against the multiplied gravity within the side maxima (HonestHold.Thrust), the gravity the physics gets
        //    written so its x multiplier + the game's impulse give exactly that (HonestHold.Gravity), the thrust change shared onto
        //    the thrusters with the game's (ThrustTorque: + HonestDelta), the reversible thrusters' side demand against the
        //    multiplied gravity (ReversibleThrustComponent). LastHonest: the honest thrust of the last step (N) ──
        public bool Honest; public Vector3 LastHonest;
        public Vector3 Input, WCmd;        // the pilot's movement input (-1..1 per axis) and turn rate (rad/s)
        public Vector3 V, W; public float Heading; public int Flips; public float LastPrio; public Vector3 LastTorque, LastNetErr;
        public float[] F, F0; ReversibleThrust.State[] _st;
        public int Add(Vector3 pos, Vector3 dir, float cap, bool rev) { Pos.Add(pos); Dir.Add(dir); Cap.Add(cap); Rev.Add(rev); return Pos.Count - 1; }
        // ── a MAIN ROTOR (Rotor >= 0: that thruster): its reaction from its thrust each step (RotorKinematics.ReactionTorque, the
        //    game's share f0 - as ThrustTorque), the controllability limiter (RotorLimiter.Targets / Step every step, the cap
        //    written into its maximum every third step beyond 0.5 % of it - AeroPropulsorComponent), the GYROS (reach per axis:
        //    the aero-aware split - the thrusters asked for what the gyros cannot give, the gyros the rest up to their reach) ──
        public int Rotor = -1; public float RotorR, Rho = 1f; public bool Limit = true; public Vector3 Gyro;
        // ── CONTROL SURFACES (2026-10-17, "any source of torque counts"): a rudder's yaw reach either way = RudderPerQ x q
        //    (N m per Pa; q from Airspeed - the forward flight it is trimmed in, m/s), given FIRST (the aware law: the surfaces
        //    first, gyros and thrusters the rest - ThrustTorque.AwareNeed) and counted by the limiter x SurfaceShare (the
        //    attitude law's planning share, ThrustTorque.SurfaceReachShare). SlowAt: the time (s) the airspeed drops to 0 ──
        public float RudderPerQ, Airspeed, SlowAt = -1f; public const float SurfaceShare = 0.75f; public float SurfacesSeen;
        public float CapNow = float.PositiveInfinity, Written = -1f, Authority, Budget, ReactionQ, MaxHeading, MinCapSeen = float.MaxValue;
        public readonly List<float> Writes = new(); public readonly List<float> WriteTimes = new();
        public int PushFlips; int _push = 1; public float Time;
        /// <summary>At this time (s) multiply a thruster's maximum (index, factor): a tail losing its authority mid-flight.</summary>
        public readonly List<(float t, int i, float k)> Events = new();
        public void Fly(float seconds)
        {
            int n = Pos.Count; const float dt = 1f / 60f, g = 9.81f;
            var arms = new Vector3[n]; var dirs = Dir.ToArray(); var caps = Cap.ToArray(); var rev = Rev.ToArray(); var side = new float[n];
            F ??= new float[n]; F0 ??= new float[n]; var prev = new float[n]; var y = new float[n]; var capDir = new float[6];
            _st ??= Enumerable.Range(0, n).Select(_ => new ReversibleThrust.State { Sign = 1 }).ToArray();
            float armSq = 0f; for (int i = 0; i < n; i++) { arms[i] = Vector3.Cross(Pos[i], dirs[i]); armSq += Pos[i].LengthSquared(); }
            Vector3 grav = Gravity ? new Vector3(0f, -g, 0f) : Vector3.Zero;
            bool warm = false;
            float rotorMax = Rotor >= 0 ? caps[Rotor] : 0f;
            if (Rotor >= 0 && Written >= 0f) caps[Rotor] = Written;
            var rIdx = new[] { Rotor }; var rR = new[] { RotorR }; var rRpm = new[] { 1f }; var rRated = new[] { rotorMax };
            float[] lAuth = new float[1], lBud = new float[1], lTgt = new float[1];
            int tail = Rev.IndexOf(true);
            for (int step = 0; step < (int)(seconds / dt); step++)
            {
                foreach (var e in Events) if (Time < e.t && Time + dt >= e.t) { caps[e.i] *= e.k; Cap[e.i] *= e.k; }
                Time += dt;
                if (SlowAt >= 0f && Time >= SlowAt) Airspeed = 0f;
                Vector3 surf = new Vector3(0f, RudderPerQ * 0.5f * Rho * Airspeed * Airspeed, 0f);   // (the rudder's reach either way, yaw)
                // the limiter (ThrustTorque.Apply -> RotorCyclic.Limit) and the propulsor's write of the cap (every third step)
                if (Rotor >= 0 && Limit)
                {
                    RotorLimiter.Targets(1, rIdx, rR, rRpm, rRated, arms, dirs, caps, rev, n, Gyro, surf * SurfaceShare, surf * SurfaceShare, Rho, lAuth, lBud, lTgt);
                    SurfacesSeen = RotorLimiter.SurfaceAuthority(dirs[Rotor], surf * SurfaceShare, surf * SurfaceShare);
                    Authority = lAuth[0]; Budget = lBud[0];
                    CapNow = RotorLimiter.Step(CapNow, lTgt[0], dt);
                    if (step % 3 == 0)
                    {
                        float want = MathF.Min(rotorMax, CapNow);
                        if (Written < 0f || MathF.Abs(want - Written) > 0.005f * rotorMax) { Written = want; caps[Rotor] = want; Writes.Add(want); WriteTimes.Add(Time); }
                    }
                }
                // the reversible thrusters' game sides (the component, before the game's thrust)
                for (int i = 0; i < n; i++)
                {
                    if (!rev[i]) { side[i] = 1f; continue; }
                    float input = Vector3.Dot(Input, dirs[i]);
                    float dem = ReversibleThrust.Demand(input, Damp, Vector3.Dot(V, dirs[i]), Vector3.Dot(grav, dirs[i]) * (Honest ? GravityMultiplier : 1f), Mass, caps[i]);
                    if (ReversibleThrust.Step(ref _st[i], dem, caps[i], MathF.Abs(input) > ReversibleThrust.InputDeadband, dt)) Flips++;
                    side[i] = _st[i].Sign;
                }
                // the game: maxima booked by side, its thrust per axis
                Array.Clear(capDir, 0, 6);
                for (int i = 0; i < n; i++) capDir[ThrustAllocator.DirIndex(dirs[i] * side[i])] += caps[i];
                Vector3 gameF = Vector3.Zero, cancel = Vector3.Zero;
                for (int ax = 0; ax < 3; ax++)
                {
                    float cp = capDir[2 * ax], cn = capDir[2 * ax + 1];
                    float u = ax == 0 ? Input.X : ax == 1 ? Input.Y : Input.Z;
                    float va = ax == 0 ? V.X : ax == 1 ? V.Y : V.Z, ga = ax == 0 ? grav.X : ax == 1 ? grav.Y : grav.Z;
                    float hold = Damp ? -Mass * ga : 0f;
                    float want = MathF.Abs(u) > 1e-3f ? hold * (MathF.Sign(u) == -MathF.Sign(hold) ? 1f - MathF.Abs(u) : 1f) + u * (u > 0f ? cp : cn)
                                                       : (Damp ? hold - Mass * va * 60f : 0f);
                    float fa = Math.Clamp(want, -cn, cp);
                    if (ax == 0) gameF.X = fa; else if (ax == 1) gameF.Y = fa; else gameF.Z = fa;
                    // (the hold the game takes off the gravity in place: the hold as the input left it, clamped to the side's
                    //  maximum, as an acceleration, at most the gravity on that axis - ThrustComponent: thrustDirections.Clamp,
                    //  Vector3.ClampMagnitude(vector7, gravity))
                    float held = Damp ? (MathF.Abs(u) > 1e-3f && hold != 0f && MathF.Sign(u) == -MathF.Sign(hold) ? hold * (1f - MathF.Min(MathF.Abs(u), 1f)) : hold) : 0f;
                    float ca = 0f;
                    if (held != 0f && ga != 0f && MathF.Sign(held) == -MathF.Sign(ga)) ca = MathF.Sign(held) * MathF.Min(MathF.Abs(Math.Clamp(held, -cn, cp)) / Mass, MathF.Abs(ga));
                    if (ax == 0) cancel.X = ca; else if (ax == 1) cancel.Y = ca; else cancel.Z = ca;
                }
                // (honest: the game's impulse is gameF - the hold it put into the gravity; the honest thrust replaces the booked command
                //  the thrusters share, and the gravity the physics applies is written as HonestDampeners.Step writes CustomGravity)
                Vector3 applied = gameF - cancel * Mass, honestGrav = grav;
                if (Honest && Damp && cancel != Vector3.Zero && GravityMultiplier != 1f)   // (HonestDampeners.Step: only where the game held, not at 1)
                {
                    Vector3 hh = HonestHold.GameHold(grav, Mass, Input);
                    Vector3 th = HonestHold.Thrust(hh, GravityMultiplier, applied, new Vector3(capDir[0], capDir[2], capDir[4]), new Vector3(capDir[1], capDir[3], capDir[5]));
                    honestGrav = HonestHold.Gravity(grav, th - applied, Mass, GravityMultiplier) - grav;   // (vs the planet's: the physics x it)
                    gameF = th;
                }
                LastHonest = gameF;
                Vector3 torque0 = ThrustAllocator.GameShare(gameF, arms, dirs, caps, capDir, F0, n, side);
                if (Rotor >= 0) { ReactionQ = RotorKinematics.ReactionTorque(F0[Rotor], RotorR, 1f, Rho); Reaction = dirs[Rotor] * ReactionQ; }
                Vector3 torque = torque0 + Reaction;
                Vector3 total = gameF, extra = Vector3.Zero; LastPrio = 0f;
                if (Solve)
                {
                    Vector3 tau = I * (WCmd - W) / 0.3f - Reaction;   // (ThrustTorque's rate branch: ResponseTime 0.3 s)
                    Vector3 sur = Vector3.Clamp(tau, -surf, surf); tau -= sur;   // (the surfaces first, the rest to gyros and thrusters)
                    Vector3 thrTau = tau - Vector3.Clamp(tau, -Gyro, Gyro);   // (with gyros: the thrusters what they cannot give)
                    if (!warm) { Array.Copy(F0, F, n); warm = true; }
                    ThrustAllocator.Solve(thrTau, gameF, arms, dirs, caps, F0, F, prev, y, armSq, n, 60, out LastPrio, rev);
                    Vector3 thr = Vector3.Zero; total = Vector3.Zero;
                    for (int i = 0; i < n; i++) { thr += arms[i] * F[i]; total += dirs[i] * F[i]; }
                    torque = Reaction + thr + Vector3.Clamp(tau - thr, -Gyro, Gyro) + sur;
                    if (LastPrio > 0f) extra = total - gameF;
                    if (tail >= 0) { int s2 = ReversibleThrust.Side(_push, F[tail], caps[tail]); if (s2 != _push) PushFlips++; _push = s2; }
                }
                else Array.Copy(F0, F, n);
                LastTorque = torque; LastNetErr = total - gameF;
                // (the force applied: the game's thrust less the part that went into the gravity; the gravity applied: what the
                //  hold left of it, x the multiplier - at gm 1 exactly gameF / m + grav, as before)
                if (GravityMultiplier == 1f) V += ((gameF + extra) / Mass + grav) * dt;   // (bit for bit as before)
                else if (Honest && Damp && cancel != Vector3.Zero) V += ((applied + extra) / Mass + (grav + honestGrav) * GravityMultiplier) * dt;
                else V += ((gameF - cancel * Mass + extra) / Mass + (grav + cancel) * GravityMultiplier) * dt;
                if (Ground) { Height += V.Y * dt; if (Height <= 0f) { Height = 0f; if (V.Y < 0f) V.Y = 0f; } MaxHeight = MathF.Max(MaxHeight, Height); }
                W += torque / I * dt;
                Heading += W.Y * dt;
                MaxHeading = MathF.Max(MaxHeading, MathF.Abs(Heading));
                if (Rotor >= 0 && float.IsFinite(CapNow)) MinCapSeen = MathF.Min(MinCapSeen, CapNow);
            }
        }
    }

    /// <summary>THE ROTOR BALANCE IN FLIGHT (2026-10-17: the rotors by disc area, the reaction calibrated to a real helicopter's
    /// tail share, the 85 kN Reversible Rotor, the controllability limiter): helicopters flown at 60 Hz with the real allocator,
    /// the limiter's law and the propulsor's cap write (RevShip). Thrust at rho 0.89 (the user's save: the maxima x 0.89).</summary>
    static void HeliLimiterChecks()
    {
        const float rho = 0.89f; float rotor15 = PropulsorBlocks.Rows.First(r => r.Name == "HelicopterRotor_150").StaticThrust * rho, rotor25 = PropulsorBlocks.Rows.First(r => r.Name == "HelicopterRotor_250").StaticThrust * rho, tailCap = PropulsorBlocks.Rows.First(r => r.Name == "HelicopterTailRotor_050").StaticThrust * rho;   // (the table's ratings: 2026-10-18 1.5x)
        RevShip Heli(float rotorMax, float R, float mass, Vector3 inertia, params (Vector3 pos, bool rev)[] tails)
        {
            var s = new RevShip { Mass = mass, I = inertia, Rho = rho, RotorR = R };
            s.Rotor = s.Add(Vector3.Zero, Vector3.UnitY, rotorMax, false);       // the main rotor's lift at the centre of mass
            foreach (var t in tails) s.Add(t.pos, Vector3.UnitX, tailCap, t.rev); // tails pushing along X (both ways when reversible)
            s.Input = new Vector3(0f, 1f, 0f);                                    // full collective
            return s;
        }
        var I15 = new Vector3(8e5f, 8e5f, 1.5e5f); const float m15 = 40000f;      // a 1.5 m-rotor helicopter: 40 t (65 % of its rotor)
        var I25 = new Vector3(6e6f, 6e6f, 1.2e6f); const float m25 = 110000f;     // a 2.5 m-rotor helicopter: 110 t
        string Pct(RevShip s, float max) => $"{s.F0[s.Rotor] / max * 100f:F0} % of its maximum";

        // 1. a 1.5 m rotor at full collective, one Reversible Rotor at its tip radius. The reported spin: the raw momentum torque
        //    (ReactionScale 1) and no limiter - the tail saturates and the ship spins away. Now: the cap keeps the reaction within
        //    Margin of the authority - and with the 85 kN Reversible Rotor (x 7.5 m x 0.8 = 510 kN m against 450 at rated) the
        //    rotor is not capped at all: its full maximum
        {
            float keep = RotorKinematics.ReactionScale;
            RotorKinematics.ReactionScale = 1f;
            var raw = Heli(rotor15, 7.5f, m15, I15, (new Vector3(0f, 0f, 7.5f), true)); raw.Limit = false; raw.Fly(10f);
            RotorKinematics.ReactionScale = keep;
            Check(raw.MaxHeading > 30f * Deg, $"heli 1.5 m, raw momentum torque, no limiter: spins ({raw.MaxHeading / Deg:F0} deg in 10 s) - the reported yaw spin");
            var s = Heli(rotor15, 7.5f, m15, I15, (new Vector3(0f, 0f, 7.5f), true)); s.Fly(10f);
            Check(s.MaxHeading < 3f * Deg && s.V.Y > 1f, $"heli 1.5 m + one Reversible Rotor at 7.5 m, full collective: heading within {s.MaxHeading / Deg:F2} deg, climbing {s.V.Y:F1} m/s");
            Check(s.ReactionQ <= RotorLimiter.Margin * s.Authority * 1.01f && s.F0[s.Rotor] >= 0.999f * rotor15,
                $"  the limiter: thrust {s.F0[s.Rotor] / 1e3f:F0} kN ({Pct(s, rotor15)}), reaction {s.ReactionQ / 1e3f:F0} kN m of authority {s.Authority / 1e3f:F0} (x {RotorLimiter.Margin})");
            Check(s.PushFlips <= 1 && s.Flips <= 1, $"  no tail chatter: push flips {s.PushFlips} (the first: from its +1 start to the side it holds), game-side flips {s.Flips}");
            var off = Heli(rotor15, 7.5f, m15, I15, (new Vector3(0f, 0f, 7.5f), true)); off.Limit = false; off.Input = Vector3.Zero; off.Fly(5f);
            Check(off.MaxHeading < 1f * Deg, $"  hover without the limiter holds too ({off.MaxHeading / Deg:F2} deg) - the cap only bites above it");
        }
        // 2. the Reversible Rotor removed: gyros only - the cap keeps the reaction within them; nothing at all - no lift, no NaN
        {
            var g = Heli(rotor15, 7.5f, m15, I15); g.Gyro = new Vector3(1e5f); g.Fly(10f);
            Check(g.MaxHeading < 3f * Deg && g.ReactionQ <= RotorLimiter.Margin * 1e5f * 1.01f,
                $"heli 1.5 m, no tail, 100 kN m gyros: heading within {g.MaxHeading / Deg:F2} deg; thrust capped to {g.F0[g.Rotor] / 1e3f:F0} kN ({Pct(g, rotor15)}), reaction {g.ReactionQ / 1e3f:F0} kN m (it cannot lift its {m15 * 9.81f / 1e3f:F0} kN: v {g.V.Y:F1} m/s)");
            var z = Heli(rotor15, 7.5f, m15, I15); z.Fly(10f);
            bool finite = float.IsFinite(z.Heading) && float.IsFinite(z.V.Y) && float.IsFinite(z.W.Y) && z.F0.All(float.IsFinite);
            Check(finite && z.F0[z.Rotor] == 0f && z.MaxHeading < 10f * Deg,
                $"heli 1.5 m, no tail, no gyros: no lift ({z.F0[z.Rotor]:F0} N), finite; only the idle blades' drag turns it ({z.ReactionQ:F0} N m: {z.MaxHeading / Deg:F1} deg in 10 s, falling)");
            var w = Heli(rotor15, 7.5f, m15, I15, (new Vector3(0f, 0f, 7.5f), false)); w.Fly(5f);   // a ONE-WAY side thruster the wrong way round
            Check(w.F0[w.Rotor] == 0f, $"  a one-way side thruster pushing the wrong way counts nothing ({w.F0[w.Rotor]:F0} N)");
            var r = Heli(rotor15, 7.5f, m15, I15, (new Vector3(0f, 0f, -7.5f), false)); r.Fly(10f);   // ... the right way round (ahead, pushing +X)
            Check(r.MaxHeading < 3f * Deg && r.F0[r.Rotor] > 0.5f * rotor15, $"  a one-way side thruster the right way round is anti-torque ({Pct(r, rotor15)}, heading {r.MaxHeading / Deg:F2} deg)");
        }
        // 3. the 2.5 m rotor: two Reversible Rotors share the load. At its tip radius (12.5 m) they hold 82 % of its rated
        //    reaction (x 0.8) - capped to ~87 % of its thrust, heading held; at 1.8 x its radius (22.5 m) it reaches its rating
        {
            var near = Heli(rotor25, 12.5f, m25, I25, (new Vector3(-1f, 0f, 12.5f), true), (new Vector3(1f, 0f, 12.5f), true)); near.Fly(10f);
            float nearPct = near.F0[near.Rotor] / rotor25;
            Check(near.MaxHeading < 3f * Deg && MathF.Abs(near.F[1] - near.F[2]) < 0.02f * tailCap && nearPct > 0.85f && nearPct < 0.89f,
                $"heli 2.5 m + two Reversible Rotors at 12.5 m: heading within {near.MaxHeading / Deg:F2} deg, they share ({near.F[1] / 1e3f:F1} / {near.F[2] / 1e3f:F1} kN); capped to {Pct(near, rotor25)} (v {near.V.Y:F1} m/s)");
            var far = Heli(rotor25, 12.5f, m25, I25, (new Vector3(-1f, 0f, 22.5f), true), (new Vector3(1f, 0f, 22.5f), true)); far.Fly(10f);
            Check(far.MaxHeading < 3f * Deg && far.F0[far.Rotor] >= 0.999f * rotor25 && far.V.Y > 1f && MathF.Abs(far.F[1] - far.F[2]) < 0.02f * tailCap,
                $"heli 2.5 m + two Reversible Rotors at 22.5 m: rated thrust ({Pct(far, rotor25)}), heading within {far.MaxHeading / Deg:F2} deg, shared {far.F[1] / 1e3f:F1} / {far.F[2] / 1e3f:F1} kN");
        }
        // 4. smooth: the cap written a few times and then left alone; when the tail loses half its authority mid-climb (thin air,
        //    damage) the cap falls without overshoot or chatter, and rises back the same way - heading held throughout
        {
            var s = Heli(rotor15, 7.5f, m15, I15, (new Vector3(0f, 0f, 7.5f), true));
            s.Events.Add((3f, 1, 0.5f)); s.Events.Add((6f, 1, 2f));
            s.Fly(12f);
            int rev = 0; for (int k = 2; k < s.Writes.Count; k++) if (MathF.Sign(s.Writes[k] - s.Writes[k - 1]) != MathF.Sign(s.Writes[k - 1] - s.Writes[k - 2])) rev++;
            int steady = s.WriteTimes.Count(t => t > 1f && t < 3f) + s.WriteTimes.Count(t => t > 10f);   // (the rise after 6 s: RiseSeconds 0.6, within 0.5 % by ~2.5 s)
            float maxStep = 0f; for (int k = 1; k < s.Writes.Count; k++) maxStep = MathF.Max(maxStep, MathF.Abs(s.Writes[k] - s.Writes[k - 1]) / rotor15);
            Check(rev <= 1 && steady == 0 && maxStep < 0.12f && s.MaxHeading < 3f * Deg && s.PushFlips <= 1,
                $"limiter smooth: {s.Writes.Count} writes, {rev} turn(s) of direction, none in steady flight ({steady}), largest step {maxStep * 100f:F1} % of the maximum; lowest cap {s.MinCapSeen / rotor15 * 100f:F0} %; heading within {s.MaxHeading / Deg:F2} deg; push flips {s.PushFlips}");
        }
        // 6. ANY SOURCE OF TORQUE COUNTS (2026-10-17): a helicopter on gyros alone (100 kN m) with a rudder - at hover the rudder
        //    has no air (q 0): the cap is the gyros'; in forward flight (60 m/s, q 1.6 kPa) its yaw reach (250 kN m) counts x the
        //    planning share - a higher cap, heading held; the air lost (slowed to a hover mid-flight) - the cap falls back
        {
            const float v = 60f; float rudder = 2.5e5f / (0.5f * rho * v * v);
            var hover = Heli(rotor15, 7.5f, m15, I15); hover.Gyro = new Vector3(1e5f); hover.RudderPerQ = rudder; hover.Fly(10f);
            var fwd = Heli(rotor15, 7.5f, m15, I15); fwd.Gyro = new Vector3(1e5f); fwd.RudderPerQ = rudder; fwd.Airspeed = v; fwd.Fly(10f);
            Check(hover.SurfacesSeen == 0f && fwd.SurfacesSeen > 0f && fwd.CapNow > 1.5f * hover.CapNow && fwd.MaxHeading < 3f * Deg && hover.MaxHeading < 3f * Deg
                  && fwd.ReactionQ <= RotorLimiter.Margin * fwd.Authority * 1.01f,
                $"rudder counts: hover cap {hover.CapNow / 1e3f:F0} kN ({Pct(hover, rotor15)}; surfaces {hover.SurfacesSeen / 1e3f:F0} kN m) < forward flight at {v} m/s cap {fwd.CapNow / 1e3f:F0} kN ({Pct(fwd, rotor15)}; authority {fwd.Authority / 1e3f:F0} kN m of which surfaces {fwd.SurfacesSeen / 1e3f:F0}); heading within {hover.MaxHeading / Deg:F2} / {fwd.MaxHeading / Deg:F2} deg");
            var slow = Heli(rotor15, 7.5f, m15, I15); slow.Gyro = new Vector3(1e5f); slow.RudderPerQ = rudder; slow.Airspeed = v; slow.SlowAt = 5f; slow.Fly(10f);
            // (coming down, the written maximum may stand up to the write tolerance - 0.5 % of the rotor's maximum - above the cap: reaction x ~1.02)
            Check(MathF.Abs(slow.CapNow - hover.CapNow) < 0.01f * hover.CapNow && slow.MaxHeading < 3f * Deg && slow.ReactionQ <= RotorLimiter.Margin * slow.Authority * 1.03f,
                $"  the air lost (slowed to a hover at 5 s): the cap back to the gyros' {slow.CapNow / 1e3f:F0} kN, heading within {slow.MaxHeading / Deg:F2} deg");
        }
        // 5. a well-balanced helicopter is left alone at full thrust: a Reversible Rotor at 1.25 x the radius and 150 kN m of gyros
        {
            var s = Heli(rotor15, 7.5f, m15, I15, (new Vector3(0f, 0f, 9.375f), true)); s.Gyro = new Vector3(1.5e5f); s.Fly(10f);
            Check(s.F0[s.Rotor] >= 0.999f * rotor15 && s.Writes.Count == 1 && s.MaxHeading < 1f * Deg,
                $"well balanced (tail at 9.4 m + gyros): full thrust {Pct(s, rotor15)}, the cap written once ({s.Writes.Count}) above the maximum; heading within {s.MaxHeading / Deg:F2} deg");
        }
    }

    /// <summary>DAMPENERS OFF (2026-10-18, the user: "if i fly with a helicopter and damps are off, i cant take off. if i fly damps on i
    /// can fly regularly"). The 1.5 m rotor (534 kN at rho 0.89) with one Reversible Rotor at 7.5 m, as heavy as the user's: it
    /// hovered at 0.49 of its rotor with dampeners on. Parked on the ground, piloted, Up held (Movement.Y = 1), the game's thrust
    /// rule (RevShip: the dampeners' gravity hold taken off the gravity in place, the engine's GravityMultiplier on what is left).
    /// THE CAUSE is the game's: the world's GravityMultiplier (vanilla 2, and the mod's PhysicsSessionConfiguration override has
    /// carried 2 since 2026-10-09) doubles the gravity the engine applies, and the dampeners' hold cancels all of it for one g of
    /// booked thrust - dampeners off, the rotor must beat twice the weight it hovered with. No mod path keys the rotor, the
    /// limiter or the solve off the dampeners (ReversibleThrust.Demand does, as the game does - its side only).</summary>
    static void HeliDampChecks()
    {
        const float rho = 0.89f, hoverLoad = 0.49f; float rotor = PropulsorBlocks.Rows.First(r => r.Name == "HelicopterRotor_150").StaticThrust * rho, tailCap = PropulsorBlocks.Rows.First(r => r.Name == "HelicopterTailRotor_050").StaticThrust * rho;
        float mass = hoverLoad * rotor / 9.81f;
        RevShip Heli(bool damp, float gm, float up = 1f, bool ground = true, float h0 = 0f)
        {
            var s = new RevShip { Mass = mass, I = new Vector3(8e5f, 8e5f, 1.5e5f), Rho = rho, RotorR = 7.5f, Damp = damp, GravityMultiplier = gm, Ground = ground, Height = h0 };
            s.Rotor = s.Add(Vector3.Zero, Vector3.UnitY, rotor, false);
            s.Add(new Vector3(0f, 0f, 7.5f), Vector3.UnitX, tailCap, true);
            s.Input = new Vector3(0f, up, 0f);
            return s;
        }
        // 1. dampeners ON: the take-off is the same whatever the multiplier (the hold hides it) - as flown now
        var on2 = Heli(true, 2f); on2.Fly(6f);
        var on1 = Heli(true, 1f); on1.Fly(6f);
        Check(on2.MaxHeight > 20f && MathF.Abs(on2.MaxHeight - on1.MaxHeight) < 1e-3f * on1.MaxHeight + 0.01f && on2.MaxHeading < 3f * Deg,
            $"heli dampeners on, Up held: takes off - {on2.MaxHeight:F0} m in 6 s at GravityMultiplier 2, {on1.MaxHeight:F0} m at 1 (the hold cancels the scaled gravity), heading within {on2.MaxHeading / Deg:F2} deg");
        // 2. dampeners OFF at GravityMultiplier 2 (the user's world): the reported bug - the rotor at its full maximum against
        //    twice its hover weight: it barely leaves the ground (here 2 % of the weight to spare; a little less rotor - a slower rpm, a
        //    thinner air, a heavier load - and none)
        var off2 = Heli(false, 2f); off2.Fly(6f);
        Check(off2.MaxHeight < 0.05f * on2.MaxHeight && off2.F0[off2.Rotor] >= 0.999f * rotor,
            $"heli dampeners off, GravityMultiplier 2: the rotor at its full {off2.F0[off2.Rotor] / 1e3f:F0} kN against {2f * mass * 9.81f / 1e3f:F0} kN of real weight - {off2.MaxHeight:F1} m in 6 s (dampeners on: {on2.MaxHeight:F0} m) - the reported can't-take-off, the game's rule");
        // 3. dampeners OFF at GravityMultiplier 1 (the fix: the mod's override back to 1, as it shipped until 2026-10-09): it climbs as with them on
        var off1 = Heli(false, 1f); off1.Fly(6f);
        Check(MathF.Abs(off1.MaxHeight - on1.MaxHeight) < 0.01f * on1.MaxHeight && off1.MaxHeading < 3f * Deg && off1.Flips <= 1,
            $"heli dampeners off, GravityMultiplier 1: takes off as with them on - {off1.MaxHeight:F0} m in 6 s (on: {on1.MaxHeight:F0}), heading within {off1.MaxHeading / Deg:F2} deg, tail game-side flips {off1.Flips}");
        // 4. no input: dampeners on hover in place at either multiplier; off, it falls at the multiplier's gravity (the game's)
        var hov = Heli(true, 2f, 0f, false, 50f); hov.Fly(5f);
        var fall = Heli(false, 2f, 0f, false, 50f); fall.Fly(1f);
        Check(MathF.Abs(hov.V.Y) < 0.01f && MathF.Abs(fall.V.Y + 2f * 9.81f * fall.Time) < 0.05f,
            $"no input: dampeners on hold height at GravityMultiplier 2 (v {hov.V.Y:F3} m/s); off, a {fall.Time:F2} s fall reaches {fall.V.Y:F2} m/s (2 g: the engine's multiplier)");
    }

    /// <summary>HONEST DAMPENERS (2026-10-18, the user: "make it honest, thats fcking ridiculous" - HonestHold / HonestDampeners.cs):
    /// the gravity hold as real thrust against the gravity the body gets (GravityMultiplier 2), within the thrusters' maxima.
    /// RevShip flies the game's rule and, Honest, the mod's correction after it. 1. the user's helicopter (the 534 kN rotor, a
    /// Reversible Rotor tail) at 1.5x and 2.5x its planet weight; 2. one thruster at the centre of mass, the numbers; 3. super
    /// dampeners: the game's braking multiplier (ComputeMaxThrusts) with the grid's flag as HonestHold.SuperDampeners sets it.</summary>
    static void HonestDampChecks()
    {
        const float rho = 0.89f, g = 9.81f; float rotor = PropulsorBlocks.Rows.First(r => r.Name == "HelicopterRotor_150").StaticThrust * rho, tailCap = PropulsorBlocks.Rows.First(r => r.Name == "HelicopterTailRotor_050").StaticThrust * rho;
        RevShip Heli(float ratio, bool damp, bool honest, float up, bool ground, float h0)
        {
            var s = new RevShip { Mass = rotor / (ratio * g), I = new Vector3(8e5f, 8e5f, 1.5e5f), Rho = rho, RotorR = 7.5f, Damp = damp, GravityMultiplier = 2f, Ground = ground, Height = h0, Honest = honest };
            s.Rotor = s.Add(Vector3.Zero, Vector3.UnitY, rotor, false);
            s.Add(new Vector3(0f, 0f, 7.5f), Vector3.UnitX, tailCap, true);
            s.Input = new Vector3(0f, up, 0f);
            return s;
        }
        // 1a. thrust 1.5x the planet's weight, parked, Up held 6 s: the game's dampeners lift it (weightless, 0.5 g to climb with),
        //     off it stays down (1.5 against 2 weights); honest it stays down either way - the thrust is short of the real weight
        var v15on = Heli(1.5f, true, false, 1f, true, 0f); v15on.Fly(6f);
        var v15off = Heli(1.5f, false, false, 1f, true, 0f); v15off.Fly(6f);
        var h15on = Heli(1.5f, true, true, 1f, true, 0f); h15on.Fly(6f);
        var h15off = Heli(1.5f, false, true, 1f, true, 0f); h15off.Fly(6f);
        Check(v15on.MaxHeight > 50f && v15off.MaxHeight < 0.01f, $"honest heli 1.5x, vanilla (setting off): dampeners on climbs {v15on.MaxHeight:F0} m in 6 s, off {v15off.MaxHeight:F2} m");
        Check(h15on.MaxHeight < 0.01f && h15off.MaxHeight < 0.01f, $"honest heli 1.5x, honest: Up held, dampeners on {h15on.MaxHeight:F2} m, off {h15off.MaxHeight:F2} m - it cannot lift twice the weight");
        // 1b. airborne at 50 m, no input, 1 s: the game's dampeners hover it; honest they give all the rotor has and it sinks at (2 - 1.5) g
        var v15h = Heli(1.5f, true, false, 0f, false, 50f); v15h.Fly(1f);
        var h15h = Heli(1.5f, true, true, 0f, false, 50f); h15h.Fly(1f);
        Check(MathF.Abs(v15h.V.Y) < 0.01f, $"honest heli 1.5x, vanilla: dampeners hover it (v {v15h.V.Y:F3} m/s)");
        Check(MathF.Abs(h15h.V.Y + 0.5f * g * h15h.Time) < 0.15f && h15h.F0[h15h.Rotor] >= 0.99f * rotor,
            $"honest heli 1.5x, honest: dampeners at the rotor's full {h15h.F0[h15h.Rotor] / 1e3f:F0} kN, it sinks to {h15h.V.Y:F2} m/s in {h15h.Time:F2} s ((2 - 1.5) g: {-0.5f * g * h15h.Time:F2})");
        // 1c. thrust 2.5x: honest hovers on the dampeners at 2/2.5 = 80 % of its rotor, and climbs with the rest (0.5 g; the game's 1.5 g)
        var h25h = Heli(2.5f, true, true, 0f, false, 50f); h25h.Fly(5f);
        Check(MathF.Abs(h25h.V.Y) < 0.01f && MathF.Abs(h25h.F0[h25h.Rotor] / rotor - 0.8f) < 0.01f && h25h.MaxHeading < 3f * Deg,
            $"honest heli 2.5x: hovers (v {h25h.V.Y:F3} m/s) at {h25h.F0[h25h.Rotor] / rotor * 100f:F1} % of its rotor, heading within {h25h.MaxHeading / Deg:F2} deg");
        var h25c = Heli(2.5f, true, true, 1f, true, 0f); h25c.Fly(6f);
        var v25c = Heli(2.5f, true, false, 1f, true, 0f); v25c.Fly(6f);
        Check(h25c.MaxHeight > 20f && h25c.MaxHeight < 0.45f * v25c.MaxHeight,
            $"honest heli 2.5x, Up held 6 s: climbs {h25c.MaxHeight:F0} m (the game's dampeners: {v25c.MaxHeight:F0} m)");

        // 2. one thruster at the centre of mass (no tail, no rotor law): the numbers
        RevShip One(float ratio, bool honest, float up, float gm = 2f)
        {
            var s = new RevShip { Mass = 10000f, GravityMultiplier = gm, Honest = honest, Solve = false, Height = 100f };
            s.Add(Vector3.Zero, Vector3.UnitY, ratio * 10000f * g, false);
            s.Input = new Vector3(0f, up, 0f);
            return s;
        }
        var o25 = One(2.5f, true, 0f); o25.Fly(3f);
        Check(MathF.Abs(o25.V.Y) < 1e-3f && MathF.Abs(o25.LastHonest.Y - 2f * 10000f * g) < 1f, $"honest one thruster 2.5x: hovers (v {o25.V.Y:F4}) on {o25.LastHonest.Y / 1e3f:F1} kN = 2 weights");
        var o25u = One(2.5f, true, 1f); o25u.Fly(2f);
        var v25u = One(2.5f, false, 1f); v25u.Fly(2f);
        Check(MathF.Abs(o25u.V.Y - 0.5f * g * 2f) < 0.02f * 0.5f * g * 2f && MathF.Abs(v25u.V.Y - 1.5f * g * 2f) < 0.02f * 1.5f * g * 2f,
            $"honest one thruster 2.5x, Up held 2 s: {o25u.V.Y:F2} m/s (0.5 g: {0.5f * g * 2f:F2}); the game's {v25u.V.Y:F2} (1.5 g: {1.5f * g * 2f:F2})");
        var o15 = One(1.5f, true, 0f); o15.Fly(1f);
        Check(MathF.Abs(o15.V.Y + 0.5f * g) < 0.15f && MathF.Abs(o15.LastHonest.Y - 1.5f * 10000f * g) < 1f, $"honest one thruster 1.5x: sinks at {-o15.V.Y:F2} m/s after 1 s (0.5 g) on its full {o15.LastHonest.Y / 1e3f:F1} kN");
        var o1 = One(2.5f, true, 1f, 1f); o1.Fly(2f);
        var v1 = One(2.5f, false, 1f, 1f); v1.Fly(2f);
        Check(o1.V == v1.V, $"honest at GravityMultiplier 1: the game's step as it is ({o1.V.Y:F3} vs {v1.V.Y:F3} m/s)");
        var pd = One(1.5f, true, -0.5f); pd.Fly(1f);
        var pdv = One(1.5f, false, -0.5f); pdv.Fly(1f);
        // (Down at half: the game's rule halves the hold - half the real weight held either way, here no down thrusters: 1 g down)
        Check(MathF.Abs(pd.V.Y - pdv.V.Y) < 0.01f && MathF.Abs(pd.V.Y + g * pd.Time) < 0.2f, $"honest one thruster 1.5x, Down half: half the real weight held, as the game's ({pd.V.Y:F2} vs {pdv.V.Y:F2} m/s)");

        // 3. SUPER DAMPENERS: in space, dampeners braking from 100 m/s, 5 m/s2 of thrust. The game's law: inertia dampening
        //    (ComputeInertiaDampening, the grid MovementDefinition: 1.75 / 65 / 0.01), clamped to the braking maximum - x
        //    Remap(|v| / the speed limit, 2..5) with MovementData.SuperDampeners (ComputeMaxThrusts; UseDampenersWorldOption true)
        float Stop(bool honest, float limit)
        {
            bool sd = HonestHold.SuperDampeners(true, honest);
            float m = 10000f, cap = 5f * m, v = 100f, x = 0f; const float dt = 1f / 60f;
            for (int i = 0; i < 60 * 120 && v > 0f; i++)
            {
                float d = v > 0.01f ? m * 1e-4f * 65f * (v + 1.75f) * (v + 1.75f) / 2f * 60f : m * v * 60f;
                float mult = sd ? 2f + 3f * MathF.Min(v / limit, 1f) : 1f;
                v = MathF.Max(0f, v - MathF.Min(d, cap * mult) / m * dt);
                x += v * dt;
            }
            return x;
        }
        float hs = Stop(true, 300f), vs = Stop(false, 300f);
        Check(MathF.Abs(hs - 1000f) < 30f && vs < 0.5f * hs, $"super dampeners: honest stops from 100 m/s in {hs:F0} m (v^2 / 2a: 1000), the world option's {vs:F0} m");
    }

    static void ReversibleChecks()
    {
        const float rotorCap = 100000f, cap = 25000f;
        float Q = RotorKinematics.ReactionTorque(5000f * 9.81f, 2.5f, 1f, 1f);
        RevShip Heli(bool rev = true)
        {
            var s = new RevShip { Reaction = new Vector3(0f, Q, 0f) };
            s.Add(Vector3.Zero, Vector3.UnitY, rotorCap, false);              // the main rotor's lift at the centre of mass
            s.Add(new Vector3(0f, 0f, 6f), Vector3.UnitX, cap, rev);          // a reversible thruster 6 m aft, pushing along X
            return s;
        }
        // 1. an ORDINARY reversible thruster (no rotor) translates both ways - the game's own thrust, its side turned round
        foreach (float u in new[] { 1f, -1f })
        {
            var s = new RevShip { Solve = false };
            s.Add(Vector3.Zero, Vector3.UnitY, rotorCap, false);
            s.Add(Vector3.Zero, Vector3.UnitX, cap, true);
            s.Input = new Vector3(u, 0f, 0f); s.Fly(1f);
            Check(MathF.Sign(s.V.X) == MathF.Sign(u) && MathF.Abs(MathF.Abs(s.F0[1]) - cap) < 1f && MathF.Sign(s.F0[1]) == MathF.Sign(u),
                $"reversible: strafe {u:+0;-0} - {s.F0[1]:F0} N, v {s.V.X:F2} m/s (flips {s.Flips})");
            // forward / back on another axis (Z): the same
            var z = new RevShip { Solve = false, Gravity = false };
            z.Add(new Vector3(0f, 0f, 0f), -Vector3.UnitZ, cap, true);
            z.Input = new Vector3(0f, 0f, -u); z.Fly(1f);
            Check(MathF.Sign(z.V.Z) == -MathF.Sign(u) && MathF.Abs(MathF.Abs(z.F0[0]) - cap) < 1f, $"reversible: forward/back {(u > 0 ? "forward" : "back")} - v {z.V.Z:F2} m/s");
        }
        // ... and the dampeners stop a drift from either side with it
        foreach (float v0 in new[] { 2f, -2f })
        {
            var s = new RevShip { Solve = false };
            s.Add(Vector3.Zero, Vector3.UnitY, rotorCap, false); s.Add(Vector3.Zero, Vector3.UnitX, cap, true);
            s.V = new Vector3(v0, 0f, 0f); s.Fly(2f);
            Check(MathF.Abs(s.V.X) < 0.02f && s.Flips <= 1, $"reversible: dampeners stop {v0:+0;-0} m/s (v {s.V.X:F3}, flips {s.Flips})");
        }
        // 2. heading hold, no gyros, against the main rotor's reaction (a rate pilot, no input)
        {
            var s = Heli(); s.Fly(20f);
            Check(MathF.Abs(s.Heading) < 1f * Deg && MathF.Abs(s.W.Y) < 1e-3f, $"reversible: no gyros, hover - heading held against the rotor's {Q:F0} N m ({s.Heading / Deg:F2} deg in 20 s; side drift {s.V.X:F2} m/s)");
            Check(s.F[1] * 6f < 0f && MathF.Abs(s.F[1] * 6f + Q) < 0.05f * Q && s.LastPrio > 0f, $"reversible: its push {s.F[1]:F0} N x 6 m answers it, a real side force (applied: priority {s.LastPrio:F3})");
            var off = Heli(false); off.Fly(20f);
            Check(MathF.Abs(off.Heading) > 30f * Deg, $"one-way in its place: the helicopter yaws away ({off.Heading / Deg:F0} deg)");
        }
        // 3. yaw both ways
        foreach (float u in new[] { 1f, -1f })
        {
            var s = Heli(); s.WCmd = new Vector3(0f, 0.5f * u, 0f); s.Fly(4f);
            Check(MathF.Sign(s.Heading) == MathF.Sign(u) && MathF.Abs(s.W.Y - 0.5f * u) < 0.1f, $"reversible: yaw {u:+0;-0} - {s.W.Y:F3} rad/s (asked {0.5f * u:F2})");
        }
        // 4. pitch / roll when mounted at an arm on those axes
        foreach (float u in new[] { 1f, -1f })
        {
            var p = new RevShip { Gravity = false }; p.Add(new Vector3(0f, 6f, 0f), Vector3.UnitZ, cap, true);   // arm (6, 0, 0): pitch
            p.WCmd = new Vector3(0.3f * u, 0f, 0f); p.Fly(2f);
            Check(MathF.Sign(p.W.X) == MathF.Sign(u) && MathF.Abs(p.W.X - 0.3f * u) < 0.06f, $"reversible: pitch {u:+0;-0} - {p.W.X:F3} rad/s");
            var r = Heli(); r.Add(new Vector3(6f, 0f, 0f), Vector3.UnitY, cap, true);   // arm (0, 0, 6): roll, its lift change taken back by the main rotor
            r.WCmd = new Vector3(0f, 0f, 0.3f * u); r.Fly(2f);
            Check(MathF.Sign(r.W.Z) == MathF.Sign(u) && MathF.Abs(r.W.Z - 0.3f * u) < 0.06f && MathF.Abs(r.V.Y) < 0.05f, $"reversible: roll {u:+0;-0} - {r.W.Z:F3} rad/s, height kept (v {r.V.Y:F3} m/s)");
        }
        // 5. zero arm: no torque from it (it keeps the game's share)
        {
            var s = Heli(); s.Pos[1] = new Vector3(6f, 0f, 0f);   // on its own axis through the centre of mass
            s.WCmd = new Vector3(0f, 0.5f, 0f); s.Fly(1f);
            Check(MathF.Abs(s.F[1] - s.F0[1]) < 1f, $"reversible: zero arm - its force stays the game's ({s.F[1]:F0} / {s.F0[1]:F0} N)");
            // ... strafing while the law steers: it still gives the game's thrust (translation), no torque
            var t = Heli(); t.Pos[1] = new Vector3(6f, 0f, 0f); t.Input = new Vector3(-1f, 0f, 0f); t.WCmd = new Vector3(0f, 0.5f, 0f); t.Fly(1f);
            Check(MathF.Abs(t.F[1] + cap) < 1f && MathF.Abs(t.F0[1] + cap) < 1f && t.V.X < -4f, $"reversible: zero arm, strafing while steered - the game's {t.F0[1]:F0} N kept ({t.F[1]:F0} N, v {t.V.X:F2} m/s)");
        }
        // 6. net force kept when the one-way thrusters can take it back
        {
            var s = Heli(); s.Add(Vector3.Zero, Vector3.UnitX, 20000f, false); s.Add(Vector3.Zero, -Vector3.UnitX, 20000f, false);
            s.Fly(5f);
            Check(MathF.Abs(s.Heading) < 1f * Deg && s.LastNetErr.Length() < 1f && s.LastPrio == 0f && MathF.Abs(s.V.X) < 0.01f,
                $"reversible: with one-way side thrusters - heading held, net force kept (err {s.LastNetErr.Length():F2} N, drift {s.V.X:F3} m/s)");
        }
        // 7. two reversible thrusters share the load: fore / aft opposite and equal; side by side equal halves
        {
            var one = Heli(); one.Fly(5f);
            var fa = Heli(); fa.Add(new Vector3(0f, 0f, -6f), Vector3.UnitX, cap, true); fa.Fly(5f);
            Check(MathF.Abs(fa.F[1] + fa.F[2]) < 0.02f * MathF.Abs(one.F[1]) && MathF.Abs(fa.F[1] - 0.5f * one.F[1]) < 0.05f * MathF.Abs(one.F[1]) && MathF.Abs(fa.Heading) < 1f * Deg,
                $"reversible: fore and aft share it ({fa.F[1]:F0} / {fa.F[2]:F0} N, one alone {one.F[1]:F0})");
            var sb = Heli(); sb.Add(new Vector3(0f, 0f, 6f), Vector3.UnitX, cap, true); sb.Fly(5f);
            Check(MathF.Abs(sb.F[1] - sb.F[2]) < 0.02f * MathF.Abs(one.F[1]) && MathF.Abs(sb.F[1] - 0.5f * one.F[1]) < 0.05f * MathF.Abs(one.F[1]),
                $"reversible: side by side share it ({sb.F[1]:F0} + {sb.F[2]:F0} N)");
        }
        // 8. clamped at its maximum both ways
        {
            var s = Heli(); s.WCmd = new Vector3(0f, 5f, 0f); s.Fly(0.5f);
            Check(s.F[1] <= cap + 1e-3f && s.F[1] > 0.95f * cap, $"reversible: clamped at its maximum ({s.F[1]:F0} N)");
            s = Heli(); s.WCmd = new Vector3(0f, -5f, 0f); s.Fly(0.5f);
            Check(s.F[1] >= -cap - 1e-3f && s.F[1] < -0.95f * cap, $"reversible: clamped at its maximum the other way ({s.F[1]:F0} N)");
        }
        // 9. a thruster WITHOUT the component: exactly as before (rev all false / null: bit-identical solve and share)
        {
            int n = 5; var rnd = new Random(7);
            var arms = new Vector3[n]; var dirs = new Vector3[n]; var caps = new float[n];
            for (int i = 0; i < n; i++) { var p = new Vector3((float)rnd.NextDouble() * 8 - 4, (float)rnd.NextDouble() * 8 - 4, (float)rnd.NextDouble() * 8 - 4); dirs[i] = i % 2 == 0 ? Vector3.UnitX : -Vector3.UnitY; arms[i] = Vector3.Cross(p, dirs[i]); caps[i] = 1000f + 500f * i; }
            var capDir = new float[6]; for (int i = 0; i < n; i++) capDir[ThrustAllocator.DirIndex(dirs[i])] += caps[i];
            var gf = new Vector3(800f, -1500f, 0f);
            float[] a0 = new float[n], b0 = new float[n];
            var ta = ThrustAllocator.GameShare(gf, arms, dirs, caps, capDir, a0, n);
            var tb = ThrustAllocator.GameShare(gf, arms, dirs, caps, capDir, b0, n, new float[] { 1, 1, 1, 1, 1 });
            float[] fa = (float[])a0.Clone(), fb = (float[])b0.Clone();
            ThrustAllocator.Solve(new Vector3(300f, -200f, 900f), gf, arms, dirs, caps, a0, fa, new float[n], new float[n], 40f, n, 60, out float pa);
            ThrustAllocator.Solve(new Vector3(300f, -200f, 900f), gf, arms, dirs, caps, b0, fb, new float[n], new float[n], 40f, n, 60, out float pb, new bool[n]);
            Check(ta == tb && a0.SequenceEqual(b0) && fa.SequenceEqual(fb) && pa == pb, "reversible: a thruster without it - the share and solve exactly as before");
        }
        // 10. its push side (what a block draws): the sign of its force past SideShare of its maximum, else as it was
        Check(ReversibleThrust.Side(1, -0.01f * cap, cap) == 1 && ReversibleThrust.Side(1, -0.05f * cap, cap) == -1 && ReversibleThrust.Side(-1, 0f, cap) == -1,
            "reversible: its push side follows its force, with hysteresis through zero");
        // 11. contact jitter never turns its game side; the pilot's reversals one turn each
        {
            var st = new ReversibleThrust.State { Sign = 1 };
            for (int k = 0; k < 300; k++) ReversibleThrust.Step(ref st, ReversibleThrust.Demand(0f, true, k % 2 == 0 ? 0.01f : -0.01f, 0f, 5000f, cap), cap, false, 1f / 60f);
            Check(st.Flips == 0, $"reversible: contact jitter never turns it ({st.Flips})");
            var st2 = new ReversibleThrust.State { Sign = 1 }; int turns = 0;
            for (int k = 0; k < 60; k++) { float u = (k / 10) % 2 == 0 ? 1f : -1f; if (ReversibleThrust.Step(ref st2, ReversibleThrust.Demand(u, true, 0f, 0f, 5000f, cap), cap, true, 1f / 60f)) turns++; }
            Check(turns == 5, $"reversible: six pilot reversals - five turns ({turns})");
        }
    }

    static int Main(string[] args)
    {
        _verbose = args.Contains("verbose");
        if (args.Contains("trace"))
        {
            var sim = new Sim(Hercules(new Vector3(1.5f, 0.8f, -4f), float.Parse(args[1])), args[2] == "on") { PilotThrust = new Vector3(0f, 0f, -1f) };
            for (int i = 0; i < 900 && !sim.Diverged; i++)
            {
                if (i == 300) sim.Target = Quaternion.CreateFromAxisAngle(Vector3.UnitY, 30f * Deg);
                if (i >= 300 && (i % 6 == 0 || i < 330)) Console.WriteLine($"t {sim.Time:F2} w ({sim.W.X:F3} {sim.W.Y:F3} {sim.W.Z:F3}) err {sim.ErrorDeg:F2} v {sim.Vel.Length():F1} gyro dw {Gyro.Impulse(sim.S, sim.Q, sim.Target, sim.W)}");
                sim.Step();
            }
            Console.WriteLine(sim.Diverged ? $"diverged at {sim.Time:F2}: {sim.DivergeNote}" : "ok");
            return 0;
        }
        Console.WriteLine("1. allocator");
        AllocatorChecks();
        ControlAuditChecks();
        CellCentreChecks();
        ReversibleChecks();
        HeliLimiterChecks();
        HeliDampChecks();
        HonestDampChecks();
        Console.WriteLine("1b. the vanilla atmospherics' Mach curve and the power ladder");
        JetChecks();
        InletChecks();
        PowerLadderChecks();
        PropulsorMachChecks();
        RamjetChecks();
        Console.WriteLine("2. flight (60 Hz, the game's target gyros + the mod's thruster steering)");
        FlightChecks();
        Console.WriteLine("3. real ships (Tests/data/*.flight, dumped from the game)");
        FixtureChecks(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "data"));
        Console.WriteLine(_fails == 0 ? $"ThrustTests: all {_checks} checks passed" : $"ThrustTests: {_fails} of {_checks} checks FAILED");
        return _fails == 0 ? 0 : 1;
    }
}
