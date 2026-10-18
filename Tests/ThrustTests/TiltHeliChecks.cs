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
// Fixed (RotorTrim): the cyclic planned before the solve against the thrusters' torque at the game's share (the offset torque);
// within its reach the solve weighs in-plane torque low (RotorTrim.InPlaneWeight: it gives the yaw, near the game's share
// otherwise), after it the cyclic given what the solved thrusters left (their side effects too). (730e48a planned on the LAST solve instead: the solve
// then held in-plane what it gave the tick before, the split froze - the Z tail pinned at -123 kN against the dampeners' +128 -
// and the "hover" ran off sideways at ~5 m/s2: LastSolve below reproduces it.)
// The sim: a 6-dof body at 60 Hz - the game's per-axis law (RevShip's), the honest hold (HonestHold), the game's share
// (GameShare), the rotor's reaction, the damping rate law (RateCommand: ThrustTorque's Kp 1.2 / MaxRate 0.8 / 0.3 s /
// DampRestRate; a rate pilot's I (wCmd - w) / 0.3), the mod's RotorTrim (Reach, Plan, ProjectArms, Give, TiltForce, Resting -
// the same calls in ThrustTorque.Apply's order; Apply itself needs the engine) and the solve with the reversible tails - on H25's
// layout (positions from the spawn file, centre of mass estimated).
static partial class Program
{
    sealed class TiltHeli
    {
        public float Mass = 29714f, G = 10.67f, Gm = 2f, Rho = 0.96f;
        public Vector3 I = new Vector3(3.5e5f, 2.0e5f, 2.2e5f);
        public bool Honest = true, Damp = true, OldCyclic, LastSolve;
        /// <summary>A rate pilot (no target orientation, dampeners off): the collective (Movement.Y) and the turn rate asked.</summary>
        public bool RatePilot; public float InputY; public Vector3 WCmd;
        /// <summary>A lit solid rocket (its thrust fixed, at its position, pushing +Y): an outside torque in the cyclic's own.</summary>
        public Vector3 SolidPos; public float SolidThrust;
        public float MaxHorizontal, MaxYawRate; public int Rested;
        public Quaternion Q = Quaternion.Identity; public Vector3 W, V;
        public float Time, MaxTilt, Load, TiltAt60 = -1f, LoadAt60, AupAt60, Aup;
        // thrusters (centre-of-mass relative, grid axes): the rotor (0), the anti-torque tail (+X, reversible), the Z tail (reversible)
        public Vector3[] Pos = { new(-0.07f, 3.8f, -0.8f), new(1.7f, 1.3f, 6.7f), new(-0.07f, 1.3f, 8.45f) };
        public Vector3[] Dir = { Vector3.UnitY, Vector3.UnitX, Vector3.UnitZ };
        public float[] Cap = { 947278f, 128000f, 128000f };
        public bool[] Rev = { false, true, true };
        const float RotorR = 7.5f, Hub = 0.74f, MaxTilt10 = 10f * MathF.PI / 180f, HubMoment = 0.15f;
        ReversibleThrust.State[] _st;
        float[] _f, _f0, _prev, _y; bool _warm;
        public float[] F => _f;

        public float TiltDeg => MathF.Acos(Math.Clamp(Vector3.Transform(Vector3.UnitY, Q).Y, -1f, 1f)) / Deg;

        public void Fly(float seconds)
        {
            int n = Pos.Length; const float dt = 1f / 60f;
            var arms = new Vector3[n]; float armSq = 0f;
            for (int i = 0; i < n; i++) { arms[i] = Vector3.Cross(Pos[i], Dir[i]); armSq += Pos[i].LengthSquared(); }
            _st ??= Enumerable.Range(0, n).Select(_ => new ReversibleThrust.State { Sign = 1 }).ToArray();
            _f ??= new float[n]; _f0 ??= new float[n]; _prev ??= new float[n]; _y ??= new float[n];
            var side = new float[n]; var capDir = new float[6]; var projArms = new Vector3[n];
            Vector3 gWorld = new Vector3(0f, -G, 0f);
            float vy0 = V.Y, t0 = Time; Vector3 lastLaw = Vector3.Zero;
            for (int step = 0; step < (int)(seconds / dt); step++)
            {
                Time += dt;
                var qi = Quaternion.Inverse(Q);
                Vector3 g = Vector3.Transform(gWorld, qi), v = Vector3.Transform(V, qi);
                for (int i = 0; i < n; i++)
                {
                    if (!Rev[i]) { side[i] = 1f; continue; }
                    float dem = ReversibleThrust.Demand(RatePilot ? Vector3.Dot(new Vector3(0f, InputY, 0f), Dir[i]) : 0f, Damp, Vector3.Dot(v, Dir[i]), Vector3.Dot(g, Dir[i]) * (Honest ? Gm : 1f), Mass, Cap[i]);
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
                    float u = RatePilot && ax == 1 ? InputY : 0f;
                    float fa = Math.Clamp(u != 0f ? hold + u * (u > 0f ? cp : cn) : Damp ? hold - Mass * va * 60f : 0f, -cn, cp);
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
                // ThrustTorque.Apply: the game's share, the rotor's reaction, the ask (unpiloted: damping, target = now; a rate pilot:
                // the rate error), the cyclic's plan, the solve, the cyclic given
                Vector3 shareTau = ThrustAllocator.GameShare(gameF, arms, Dir, Cap, capDir, _f0, n, side);
                Vector3 solidF = Vector3.UnitY * SolidThrust, steerTau = Vector3.Cross(SolidPos, solidF);
                Vector3 reaction = Dir[0] * RotorKinematics.ReactionTorque(_f0[0], RotorR, 1f, Rho);
                Vector3 ask;
                if (RatePilot) ask = I * (WCmd - W) / 0.3f - reaction;
                else { Vector3 accel = ThrustAllocator.RateCommand(Q, Q, W, 1.2f, 0.8f, 0.3f, 0.002f, 0.0002f, out _); ask = (accel == Vector3.Zero ? Vector3.Zero : I * accel) - reaction; }
                float sinT = MathF.Sin(MaxTilt10), hHub = Vector3.Dot(Pos[0], Dir[0]) + Hub;
                float reach = RotorTrim.Reach(_f0[0], hHub, RotorR, sinT, HubMoment);
                Vector3 netA = (applied + solidF + lastLaw) / Mass + gravApplied;   // (+ the law's forces last step: ThrustTorque's Resting)
                bool trim = !OldCyclic && !RotorTrim.Resting(V.Length(), W.Length(), netA.Length());
                if (!trim) Rested++;
                if (!_warm) { Array.Copy(_f0, _f, n); _warm = true; }
                Vector3 own = shareTau + steerTau;
                if (LastSolve) { own = steerTau; for (int i = 0; i < n; i++) own += arms[i] * _f[i]; }   // (730e48a: the last solve's)
                Vector3 plan = RotorTrim.Plan(ask, own, Dir[0], reach, trim, out bool project);
                bool proj = project && !LastSolve;
                if (proj) RotorTrim.ProjectArms(arms, Dir[0], projArms, n);
                ThrustAllocator.Solve(proj ? RotorTrim.ProjectTorque(ask - plan, Dir[0]) : ask - plan, gameF, proj ? projArms : arms, Dir, Cap, _f0, _f, _prev, _y, armSq, n, 60, out float prio, Rev);
                Vector3 thrTau = Vector3.Zero, total = Vector3.Zero;
                for (int i = 0; i < n; i++) { thrTau += arms[i] * _f[i]; total += Dir[i] * _f[i]; }
                Vector3 rotorTau = RotorTrim.Give(ask, thrTau + steerTau, plan, Dir[0], reach, trim);
                Vector3 tiltF = RotorTrim.TiltForce(rotorTau, Dir[0], _f0[0], hHub, reach, sinT);
                Vector3 torque = thrTau + reaction + rotorTau + steerTau, extra = prio > 0f ? total - gameF : Vector3.Zero;
                applied += solidF; lastLaw = extra + tiltF;
                Load = _f[0] / Cap[0];
                // the step: the game's impulse + the attitude law's force changes + the gravity the physics applies (grid axes)
                Vector3 a = (applied + extra + tiltF) / Mass + gravApplied;
                V += Vector3.Transform(a, Q) * dt;
                W += (torque - Vector3.Cross(W, I * W)) / I * dt;
                Q = Quaternion.Normalize(Q + Q * new Quaternion(W * (0.5f * dt), 0f));   // (body rates: dq = q (0, w) dt / 2)
                float tilt = TiltDeg; MaxTilt = MathF.Max(MaxTilt, tilt);
                MaxHorizontal = MathF.Max(MaxHorizontal, new Vector2(V.X, V.Z).Length()); MaxYawRate = MathF.Max(MaxYawRate, MathF.Abs(W.Y));
                if (TiltAt60 < 0f && tilt >= 60f) { TiltAt60 = Time; LoadAt60 = Load; AupAt60 = Vector3.Transform(a, Q).Y; }
            }
            Aup = (V.Y - vy0) / MathF.Max(Time - t0, 1e-3f);
        }
    }

    /// <summary>H25 with a second anti-torque tail ahead (both reversible along X, 13.4 m apart): a pure yaw couple - no tail
    /// thrust left over to drift the ship sideways (one tail's yaw thrust is a real sideways force: the translating tendency).</summary>
    static TiltHeli Twin(TiltHeli h)
    {
        h.Pos = new Vector3[] { new(-0.07f, 3.8f, -0.8f), new(1.7f, 1.3f, 6.7f), new(1.7f, 1.3f, -6.7f), new(-0.07f, 1.3f, 8.45f) };
        h.Dir = new[] { Vector3.UnitY, Vector3.UnitX, Vector3.UnitX, Vector3.UnitZ };
        h.Cap = new[] { 947278f, 128000f, 128000f, 128000f }; h.Rev = new[] { false, true, true, true };
        return h;
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
        // (730e48a: planned on the last solve - the split froze, the hover ran off sideways)
        var last = new TiltHeli { LastSolve = true }; last.Fly(20f);
        Check(last.MaxHorizontal > 20f, $"tilt heli, the cyclic planned on the LAST solve (730e48a) REPRODUCED: level ({last.MaxTilt:F2} deg) but it runs off sideways - V ({last.V.X:F1}, {last.V.Y:F1}, {last.V.Z:F1}) m/s after 20 s");
        string Hv(TiltHeli h) => $"V ({h.V.X:F2}, {h.V.Y:F2}, {h.V.Z:F2}) m/s (horizontal at most {h.MaxHorizontal:F2}), yaw rate at most {h.MaxYawRate:F4} rad/s";
        bool Still(TiltHeli h, float tilt = 2f) => h.MaxTilt < tilt && MathF.Abs(h.V.Y) < 0.5f && h.MaxHorizontal < 0.5f && h.MaxYawRate < 0.01f;
        // H25 itself (ONE tail): level and hovering; sideways only the tail's own thrust - its yaw force is a real sideways force
        // (a real helicopter rolls into it; unpiloted no attitude is held) - the translating tendency, there before the trim
        // (an on-axis rotor with the old cyclic tipped over too); along Z nothing
        var newH = new TiltHeli(); newH.Fly(20f);
        var newV = new TiltHeli { Honest = false }; newV.Fly(20f);
        float load3 = 2f * 29714f * 10.67f / 947278f;
        Check(newH.MaxTilt < 2f && MathF.Abs(newH.V.Y) < 0.5f && MathF.Abs(newH.V.X - 20f * newH.F[1] / newH.Mass) < 3f && MathF.Abs(newH.V.Z) < 3f && newH.MaxYawRate < 0.01f && MathF.Abs(newH.Load - load3) < 0.03f,
            $"tilt heli honest (H25, one tail), trimmed: level within {newH.MaxTilt:F2} deg for 20 s, {Hv(newH)} - V.X the tail's sideways {newH.F[1] / 1e3f:F0} kN x 20 s ({20f * newH.F[1] / newH.Mass:F1}), rotor at {newH.Load * 100f:F1} % (2 weights / 3.25: {load3 * 100f:F1})");
        Check(newV.MaxTilt < 2f && MathF.Abs(newV.V.Y) < 0.5f && MathF.Abs(newV.V.Z) < 3f && MathF.Abs(newV.Load - load3 / 2f) < 0.03f,
            $"tilt heli vanilla (H25, honest off), trimmed: level within {newV.MaxTilt:F2} deg, {Hv(newV)}, rotor at {newV.Load * 100f:F1} %");
        // TWO tails (a pure yaw couple): it holds its place - |V.xz| under 0.5 m/s for 20 s
        var tw = Twin(new TiltHeli()); tw.Fly(20f);
        var twOld = Twin(new TiltHeli { OldCyclic = true }); twOld.Fly(20f);
        var twLast = Twin(new TiltHeli { LastSolve = true }); twLast.Fly(20f);
        Check(Still(tw) && MathF.Abs(tw.Load - load3) < 0.03f && twOld.MaxTilt > 30f && twLast.MaxHorizontal > 20f,
            $"tilt heli honest, two tails: trimmed level within {tw.MaxTilt:F2} deg, {Hv(tw)}, rotor {tw.Load * 100f:F1} % (the old cyclic: {twOld.MaxTilt:F0} deg; planned on the last solve: V ({twLast.V.X:F1}, {twLast.V.Z:F1}))");
        var twV = Twin(new TiltHeli { Honest = false }); twV.Fly(20f);
        Check(Still(twV), $"tilt heli vanilla, two tails: level within {twV.MaxTilt:F2} deg, {Hv(twV)}");
        // (kicked: 3 deg/s of pitch and roll at the release - the damping takes it out and it hovers where it stopped)
        var kick = Twin(new TiltHeli { W = new Vector3(0.05f, 0f, 0.05f) }); kick.Fly(20f);
        Check(kick.MaxTilt < 5f && kick.W.Length() < 1e-3f && MathF.Abs(kick.V.Y) < 0.5f && kick.MaxHorizontal < 1.5f,
            $"tilt heli honest, two tails, kicked 3 deg/s pitch + roll: damped (w {kick.W.Length():F4} rad/s after 20 s; unpiloted no attitude is held - it stays where the damping stopped it, {kick.TiltDeg:F2} deg), at most {kick.MaxTilt:F2} deg, {Hv(kick)}");
        // 3. the 1.5x one (thrust 1.5 planet weights, honest): level, it sinks at (2 - 1.5) g on the full rotor
        var h15 = Twin(new TiltHeli { Mass = 947278f / (1.5f * 10.67f) }); h15.Fly(2f);
        Check(h15.MaxTilt < 2f && MathF.Abs(h15.Aup + 0.5f * 10.67f) < 0.6f && h15.Load > 0.98f && h15.MaxHorizontal < 0.5f,
            $"tilt heli 1.5x honest, two tails: level within {h15.MaxTilt:F2} deg, sinks at {-h15.Aup:F2} m/s2 (0.5 g: {0.5f * 10.67f:F2}) on {h15.Load * 100f:F0} % of its rotor, {Hv(h15)}");
        // 4. THE RATE BRANCH, no turn input (dampeners off, a pilot holding the collective at the hover share): trimmed as well
        //    (before, the cyclic was asked only with a rate input - the same tip-over). Sideways: with dampeners off nothing holds
        //    its place - the trim's tilt force (the thrust tilted to pass the centre of mass) moves it, as a real one's
        var rOld = Twin(new TiltHeli { RatePilot = true, Damp = false, InputY = load3, OldCyclic = true }); rOld.Fly(10f);
        var rNew = Twin(new TiltHeli { RatePilot = true, Damp = false, InputY = load3 }); rNew.Fly(10f);
        Check(rOld.MaxTilt > 20f && rNew.MaxTilt < 2f && rNew.MaxYawRate < 0.01f && MathF.Abs(rNew.V.Y) < 0.5f,
            $"tilt heli, rate pilot, no turn input, dampeners off, collective {load3 * 100f:F0} %: before {rOld.MaxTilt:F0} deg in 10 s; trimmed level within {rNew.MaxTilt:F2} deg, {Hv(rNew)} (no dampeners: the trim's tilt force is not held)");
        // 5. A LIT SOLID ROCKET off-centre (an outside torque, out of the solve): trimmed out with the offset torque
        var sol = Twin(new TiltHeli { SolidPos = new Vector3(0.6f, -2f, 0.4f), SolidThrust = 60000f, Mass = 29714f + 60000f / (2f * 10.67f) }); sol.Fly(20f);
        Check(Still(sol), $"tilt heli honest, two tails + a lit solid rocket 0.7 m off the axis (60 kN): level within {sol.MaxTilt:F2} deg, {Hv(sol)}");
        // 6. RESTING (RotorTrim.Resting): parked, held up by the ground - no trim; hovering, sinking, turning - trimmed
        Check(RotorTrim.Resting(0f, 0f, 9.8f) && !RotorTrim.Resting(0f, 0f, 0.01f) && !RotorTrim.Resting(0.5f, 0f, 9.8f) && !RotorTrim.Resting(0f, 0.05f, 9.8f)
              && tw.Rested <= 1 && newH.Rested <= 1 && h15.Rested <= 3,
            $"resting: parked (no speed, 1 g unbalanced) not trimmed; a hover, a sinking ship past 0.2 m/s, a turning one trimmed (the 3x hovers rested {tw.Rested} / {newH.Rested} steps - the first, the law's forces not yet known - the 1.5x sink {h15.Rested} at its release)");
        // 7. a structure steering cap / no trim: the plan given as planned, no pick-up after the solve
        {
            Vector3 ask = new(1e5f, 2e4f, -3e4f), own = new(-4e4f, 0f, 1e4f), axis = Vector3.UnitY;
            Vector3 plan = RotorTrim.Plan(ask, own, axis, 2e5f, true, out bool pr);
            Vector3 capped = RotorTrim.Give(ask, own * 0.5f, plan, axis, 2e5f, false), picked = RotorTrim.Give(ask, own * 0.5f, plan, axis, 2e5f, true);
            Vector3 far = RotorTrim.Plan(ask * 10f, own, axis, 2e5f, true, out bool pf);
            Check(pr && !pf && capped == plan && MathF.Abs(Vector3.Dot(picked, axis)) < 1e-3f && MathF.Abs(far.Length() - 2e5f) < 1f && (picked - (ask - own * 0.5f) + axis * Vector3.Dot(ask - own * 0.5f, axis)).Length() < 1f,
                $"trim law: within reach projected ({pr}), beyond not ({pf}, the plan the reach {far.Length() / 1e3f:F0} kN m); no pick-up gives the plan; picked up in-plane only");
        }
    }
}
