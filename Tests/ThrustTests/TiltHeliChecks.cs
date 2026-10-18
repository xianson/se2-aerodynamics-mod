using AeroMod;

// THE UNPILOTED HELICOPTER THAT TIPPED OVER (in game 2026-10-18, release-ingame-2 l1g: "H25", 29.7 t, one 947 kN rotor = 3.0x
// its planet weight at 10.67 m/s2, GravityMultiplier 2, dampeners on, nobody seated). Honest it was expected to hover at 2/3
// of its rotor; it fell at 12.5 m/s2 with the rotor at 26 % - and the probe ([AERO-THRUST-IN]) showed why: its gravity in
// grid axes went (0, -10.67, 0) -> (-1.47, -4.25, 9.75) - it had pitched over ~66 deg, at a steady rate (vanilla too, ~2 deg/s;
// honest ~8). The hold is per axis (the game's, ComputeGravityDampening): along the rotor only g_Y's share, along Z only the
// tail's 136 kN - the vertical force 0.76 of the weight against 2 weights of real gravity.
// The tip-over: unpiloted with dampeners on ThrustTorque damps the rotation (mode "damp"); the cyclic (RotorCyclic.Give) was
// asked for the damping torque alone, the solve for the thrusters' torque - and a lone rotor's lift off the centre of mass
// cannot be shifted to another thruster with the net force kept: its offset torque stood, a steady rate of 0.3 x it / I.
// Fixed: the cyclic planned against the thrusters' torque (as last solved: the warm start) before the solve and given what the solved thrusters left
// after it (ThrustAllocator.CyclicWant, both times).
// The sim: a 6-dof body at 60 Hz - the game's per-axis law (RevShip's), the honest hold (HonestHold), the game's share
// (GameShare), the rotor's reaction, the damping rate law (RateCommand: ThrustTorque's Kp 1.2 / MaxRate 0.8 / 0.3 s /
// DampRestRate), RotorCyclic.Give's reach law (replica: T x (h sin 10 deg + 0.15 R), in-plane, the tilt's force) and
// the solve with the reversible tails - on H25's layout (positions from the spawn file, centre of mass estimated).
static partial class Program
{
    sealed class TiltHeli
    {
        public float Mass = 29714f, G = 10.67f, Gm = 2f, Rho = 0.96f;
        public Vector3 I = new Vector3(3.5e5f, 2.0e5f, 2.2e5f);
        public bool Honest = true, Damp = true, OldCyclic;
        public Quaternion Q = Quaternion.Identity; public Vector3 W, V;
        public float Time, MaxTilt, Load, TiltAt60 = -1f, LoadAt60, AupAt60, Aup;
        // thrusters (centre-of-mass relative, grid axes): the rotor (0), the anti-torque tail (+X, reversible), the Z tail (reversible)
        public readonly Vector3[] Pos = { new(-0.07f, 3.8f, -0.8f), new(1.7f, 1.3f, 6.7f), new(-0.07f, 1.3f, 8.45f) };
        public readonly Vector3[] Dir = { Vector3.UnitY, Vector3.UnitX, Vector3.UnitZ };
        public readonly float[] Cap = { 947278f, 128000f, 128000f };
        public readonly bool[] Rev = { false, true, true };
        const float RotorR = 7.5f, Hub = 0.74f, MaxTilt10 = 10f * MathF.PI / 180f, HubMoment = 0.15f;
        ReversibleThrust.State[] _st;
        float[] _f, _f0, _prev, _y; bool _warm;

        public float TiltDeg => MathF.Acos(Math.Clamp(Vector3.Transform(Vector3.UnitY, Q).Y, -1f, 1f)) / Deg;

        public void Fly(float seconds)
        {
            int n = Pos.Length; const float dt = 1f / 60f;
            var arms = new Vector3[n]; float armSq = 0f;
            for (int i = 0; i < n; i++) { arms[i] = Vector3.Cross(Pos[i], Dir[i]); armSq += Pos[i].LengthSquared(); }
            _st ??= Enumerable.Range(0, n).Select(_ => new ReversibleThrust.State { Sign = 1 }).ToArray();
            _f ??= new float[n]; _f0 ??= new float[n]; _prev ??= new float[n]; _y ??= new float[n];
            var side = new float[n]; var capDir = new float[6];
            Vector3 gWorld = new Vector3(0f, -G, 0f);
            float vy0 = V.Y, t0 = Time;
            for (int step = 0; step < (int)(seconds / dt); step++)
            {
                Time += dt;
                var qi = Quaternion.Inverse(Q);
                Vector3 g = Vector3.Transform(gWorld, qi), v = Vector3.Transform(V, qi);
                for (int i = 0; i < n; i++)
                {
                    if (!Rev[i]) { side[i] = 1f; continue; }
                    float dem = ReversibleThrust.Demand(0f, Damp, Vector3.Dot(v, Dir[i]), Vector3.Dot(g, Dir[i]) * (Honest ? Gm : 1f), Mass, Cap[i]);
                    ReversibleThrust.Step(ref _st[i], dem, Cap[i], false, dt);
                    side[i] = _st[i].Sign;
                }
                Array.Clear(capDir, 0, 6);
                for (int i = 0; i < n; i++) capDir[ThrustAllocator.DirIndex(Dir[i] * side[i])] += Cap[i];
                // the game's per-axis law (RevShip's): the hold + the inertia dampening, clamped; the hold taken off the gravity
                Vector3 gameF = Vector3.Zero, cancel = Vector3.Zero;
                for (int ax = 0; ax < 3; ax++)
                {
                    float cp = capDir[2 * ax], cn = capDir[2 * ax + 1];
                    float va = ax == 0 ? v.X : ax == 1 ? v.Y : v.Z, ga = ax == 0 ? g.X : ax == 1 ? g.Y : g.Z;
                    float hold = Damp ? -Mass * ga : 0f;
                    float fa = Math.Clamp(Damp ? hold - Mass * va * 60f : 0f, -cn, cp);
                    float ca = hold != 0f && ga != 0f ? MathF.Sign(hold) * MathF.Min(MathF.Abs(Math.Clamp(hold, -cn, cp)) / Mass, MathF.Abs(ga)) : 0f;
                    if (ax == 0) { gameF.X = fa; cancel.X = ca; } else if (ax == 1) { gameF.Y = fa; cancel.Y = ca; } else { gameF.Z = fa; cancel.Z = ca; }
                }
                Vector3 applied = gameF - cancel * Mass, gravApplied = (g + cancel) * Gm;   // (the game's impulse; the gravity the physics applies)
                if (Honest && Damp && cancel != Vector3.Zero)
                {
                    Vector3 th = HonestHold.Thrust(HonestHold.GameHold(g, Mass, Vector3.Zero), Gm, applied,
                        new Vector3(capDir[0], capDir[2], capDir[4]), new Vector3(capDir[1], capDir[3], capDir[5]));
                    gravApplied = HonestHold.Gravity(g, th - applied, Mass, Gm) * Gm;
                    gameF = th;
                }
                // ThrustTorque.Apply: the game's share, the rotor's reaction, damping (unpiloted: target = now), the cyclic, the solve
                Vector3 shareTau = ThrustAllocator.GameShare(gameF, arms, Dir, Cap, capDir, _f0, n, side);
                Vector3 reaction = Dir[0] * RotorKinematics.ReactionTorque(_f0[0], RotorR, 1f, Rho);
                Vector3 accel = ThrustAllocator.RateCommand(Q, Q, W, 1.2f, 0.8f, 0.3f, 0.002f, 0.0002f, out _);
                Vector3 tauTarget = (accel == Vector3.Zero ? Vector3.Zero : I * accel) - reaction;
                // the cyclic: before, asked for the want and given it at once (the solve the rest); now planned for the want less
                // the thrusters' offset torque (the solve the rest of it), and given after the solve what the thrusters left of the
                // want as solved (ThrustAllocator.CyclicWant both times)
                if (!_warm) { Array.Copy(_f0, _f, n); _warm = true; }
                Vector3 lastTau = Vector3.Zero; for (int i = 0; i < n; i++) lastTau += arms[i] * _f[i];   // (the thrusters' torque as last solved: the warm start)
                Vector3 plan = Cyclic(OldCyclic ? tauTarget : ThrustAllocator.CyclicWant(tauTarget, lastTau), out Vector3 tiltF);
                ThrustAllocator.Solve(tauTarget - plan, gameF, arms, Dir, Cap, _f0, _f, _prev, _y, armSq, n, 60, out float prio, Rev);
                Vector3 thrTau = Vector3.Zero, total = Vector3.Zero;
                for (int i = 0; i < n; i++) { thrTau += arms[i] * _f[i]; total += Dir[i] * _f[i]; }
                Vector3 rotorTau = OldCyclic ? plan : Cyclic(ThrustAllocator.CyclicWant(tauTarget, thrTau), out tiltF);
                Vector3 torque = thrTau + reaction + rotorTau, extra = prio > 0f ? total - gameF : Vector3.Zero;
                // RotorCyclic.Give (replica): in-plane, up to its reach; the tilt's part moves the ship
                Vector3 Cyclic(Vector3 want, out Vector3 force)
                {
                    force = Vector3.Zero;
                    float T = MathF.Max(_f0[0], 0f), h = Vector3.Dot(Pos[0], Dir[0]) + Hub;
                    float cyc = T * (MathF.Max(h, 0f) * MathF.Sin(MaxTilt10) + HubMoment * RotorR);
                    Vector3 plane = want - Dir[0] * Vector3.Dot(want, Dir[0]);
                    float pl = plane.Length();
                    if (!(pl > 0f && cyc > 0f)) return Vector3.Zero;
                    Vector3 gp = plane * MathF.Min(1f, cyc / pl);
                    if (h > 0.25f) force = Vector3.Cross(gp * (h * MathF.Sin(MaxTilt10) * T / cyc), Dir[0]) / h;
                    return gp;
                }
                Load = _f[0] / Cap[0];
                // the step: the game's impulse + the attitude law's force changes + the gravity the physics applies (grid axes)
                Vector3 a = (applied + extra + tiltF) / Mass + gravApplied;
                V += Vector3.Transform(a, Q) * dt;
                W += (torque - Vector3.Cross(W, I * W)) / I * dt;
                Q = Quaternion.Normalize(Q + Q * new Quaternion(W * (0.5f * dt), 0f));   // (body rates: dq = q (0, w) dt / 2)
                float tilt = TiltDeg; MaxTilt = MathF.Max(MaxTilt, tilt);
                if (TiltAt60 < 0f && tilt >= 60f) { TiltAt60 = Time; LoadAt60 = Load; AupAt60 = Vector3.Transform(a, Q).Y; }
            }
            Aup = (V.Y - vy0) / MathF.Max(Time - t0, 1e-3f);
        }
    }

    static void TiltHeliChecks()
    {
        // 1. the in-game state, the law alone: the logged grid-axis gravity at 66 deg, mass and maxima -> the logged numbers
        {
            const float m = 29714f, gm = 2f; Vector3 g = new(-1.47f, -4.25f, 9.75f);
            Vector3 pos = new(135780f, 951132f, 0f), neg = new(0f, 0f, 135780f);
            Vector3 th = HonestHold.Thrust(HonestHold.GameHold(g, m, Vector3.Zero), gm, Vector3.Zero, pos, neg);
            Vector3 up = -Vector3.Normalize(g);
            float held = Vector3.Dot(th, up) / (m * g.Length()), aUp = Vector3.Dot(th, up) / m - gm * g.Length();
            Check(MathF.Abs(th.Y - 252.8e3f) < 1e3f && MathF.Abs(th.Y / pos.Y - 0.266f) < 0.01f && MathF.Abs(held - 0.76f) < 0.03f && aUp < -12f && aUp > -14.5f,
                $"tilted heli, the in-game state (gravity (-1.47, -4.25, 9.75) in grid axes: 66 deg): the per-axis honest hold gives the rotor {th.Y / 1e3f:F1} kN ({th.Y / pos.Y * 100f:F1} % - in game 26 %), {held:F2} of the weight up - it falls at {-aUp:F1} m/s2 (in game 12.5 with drag)");
        }
        // 2. the flight: H25 released level, unpiloted, dampeners on. Before (the cyclic asked for the damping alone): it tips over
        //    at a steady rate, faster honest (2 weights of lift on the arm) than vanilla, and falls; after: level, hovering at 2/3
        var oldH = new TiltHeli { OldCyclic = true }; oldH.Fly(20f);
        var oldV = new TiltHeli { OldCyclic = true, Honest = false }; oldV.Fly(20f);
        Console.WriteLine($"      tilt heli, the cyclic asked for the damping alone: honest {oldH.MaxTilt:F0} deg in 20 s (60 deg at {oldH.TiltAt60:F1} s: rotor {oldH.LoadAt60 * 100f:F0} %, a_up {oldH.AupAt60:F1} m/s2), vanilla {oldV.MaxTilt:F0} deg");
        Check(oldH.TiltAt60 > 0f && oldH.AupAt60 < -8f && oldH.LoadAt60 < 0.4f && oldV.MaxTilt < oldH.MaxTilt,
            $"tilt heli REPRODUCED (the cyclic not told the offset torque): honest it tips past 60 deg at {oldH.TiltAt60:F1} s and falls ({oldH.AupAt60:F1} m/s2 on {oldH.LoadAt60 * 100f:F0} % of its rotor); vanilla tips slower ({oldV.MaxTilt:F0} deg in 20 s)");
        var newH = new TiltHeli(); newH.Fly(20f);
        var newV = new TiltHeli { Honest = false }; newV.Fly(20f);
        Check(newH.MaxTilt < 2f && MathF.Abs(newH.V.Y) < 0.5f && MathF.Abs(newH.Load - 2f * 29714f * 10.67f / 947278f) < 0.03f,
            $"tilt heli honest, the cyclic answering the offset torque: level within {newH.MaxTilt:F2} deg for 20 s, v {newH.V.Y:F2} m/s, rotor at {newH.Load * 100f:F1} % (2 weights / 3.25: {2f * 29714f * 10.67f / 947278f * 100f:F1})");
        Check(newV.MaxTilt < 2f && MathF.Abs(newV.V.Y) < 0.5f,
            $"tilt heli vanilla (honest off), the cyclic answering the offset torque: level within {newV.MaxTilt:F2} deg, v {newV.V.Y:F2} m/s, rotor at {newV.Load * 100f:F1} %");
        // (kicked: 3 deg/s of pitch and roll at the release - the damping takes it out, it comes back near level and hovers)
        var kick = new TiltHeli { W = new Vector3(0.05f, 0f, 0.05f) }; kick.Fly(10f);
        Check(kick.MaxTilt < 5f && kick.W.Length() < 1e-3f && MathF.Abs(kick.V.Y) < 0.5f,
            $"tilt heli honest, kicked 3 deg/s pitch + roll: damped (w {kick.W.Length():F4} rad/s after 10 s; no attitude held unpiloted - it stays where the damping stopped it), at most {kick.MaxTilt:F2} deg, v {kick.V.Y:F2} m/s");
        // 3. the 1.5x one (thrust 1.5 planet weights, honest): level, it sinks at (2 - 1.5) g on the full rotor
        var h15 = new TiltHeli { Mass = 947278f / (1.5f * 10.67f) }; h15.Fly(2f);
        Check(h15.MaxTilt < 2f && MathF.Abs(h15.Aup + 0.5f * 10.67f) < 0.6f && h15.Load > 0.98f,
            $"tilt heli 1.5x honest: level within {h15.MaxTilt:F2} deg, sinks at {-h15.Aup:F2} m/s2 (0.5 g: {0.5f * 10.67f:F2}) on {h15.Load * 100f:F0} % of its rotor");
    }
}
