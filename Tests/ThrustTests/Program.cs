using System.IO;
using AeroMod;

// Thruster steering (Integration/ThrustAllocator.cs), offline: the literal mod source, in a 60 Hz rigid-body flight
// simulation with the game's own target gyro controller (GridGyroscopesComponent.ComputeTorqueTarget, ported below)
// and the game's dampened thrust at the centre of mass, as in the game; the mod adds only the thrusters' torque.
//  1. allocator: net thrust kept to the game's, forces within their limits, the torque asked for, the offset torque of
//     an off-centre centre of mass cancelled;
//  2. flight: hover level, yaw / pitch / roll while hovering, a turn in forward flight, holding against an air torque
//     and against an unstable (weathervaning backwards) air torque, all with steering ON and OFF (gyros only), on a
//     ship with its centre of mass off its thrusters' centre. Pass/fail on settle time, overshoot, altitude, cross-axis
//     upset; and ON may never be worse than OFF.
// Run: dotnet run -c Release [-- verbose]. Exit 0 = all pass.
static class Program
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
            Vector3 gameF = GameThrust();
            // the mod (ThrustTorque.Apply): the game's sharing makes the offset torque; with a target, the allocator
            Vector3 torque = ThrustAllocator.GameShare(gameF, arms, dirs, caps, capDir, f0, n);
            if (Steering)
            {
                Vector3 accel = ThrustAllocator.RateCommand(Q, Target, W, 1.2f, 0.8f, 0.3f, 0.002f, 0.002f, out _);
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
        Console.WriteLine("2. flight (60 Hz, the game's target gyros + the mod's thruster steering)");
        FlightChecks();
        Console.WriteLine("3. real ships (Tests/data/*.flight, dumped from the game)");
        FixtureChecks(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "data"));
        Console.WriteLine(_fails == 0 ? $"ThrustTests: all {_checks} checks passed" : $"ThrustTests: {_fails} of {_checks} checks FAILED");
        return _fails == 0 ? 0 : 1;
    }
}
