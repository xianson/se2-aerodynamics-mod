using AeroMod;

// THE GYRO REBALANCE (GyroBalance, 2026-10-18 - the user: "just do 1, maybe a lil 2. have them both as settings"): the torque
// scale's write rule and the mild saturation store, game-free. (The scale's default is measured on the real ships:
// Tests/AeroBench gyros.)
static partial class Program
{
    static void GyroBalanceChecks()
    {
        const float dt = GyroBalance.Period / 60f;
        float scale0 = GyroBalance.TorqueScale; bool sat0 = GyroBalance.Saturation; float sec0 = GyroBalance.SaturationSeconds;
        try
        {
            // 1. the fade: none below FadeFrom, smooth (no cliff) and falling to Floor at full, never 0
            float worstStep = 0f; bool falling = true;
            for (float f = 0f; f < 1.2f; f += 0.001f) { float a = GyroBalance.Fade(f), b = GyroBalance.Fade(f + 0.001f); worstStep = MathF.Max(worstStep, a - b); if (b > a + 1e-6f) falling = false; }
            Check(GyroBalance.Fade(0f) == 1f && GyroBalance.Fade(GyroBalance.FadeFrom) == 1f && MathF.Abs(GyroBalance.Fade(1f) - GyroBalance.Floor) < 1e-6f
                && GyroBalance.Fade(5f) == GyroBalance.Floor && GyroBalance.Floor > 0f && falling && worstStep < 0.005f && GyroBalance.Fade(float.NaN) == 1f,
                $"gyro fade: 1 up to {GyroBalance.FadeFrom} full, smooth (largest step per 0.1% fill {worstStep:F4}) down to {GyroBalance.Floor} at full, never 0");
            // 2. the factor: the scale x the fade; aero off: vanilla; the scale held to 0.05..1; not finite: vanilla
            GyroBalance.TorqueScale = 0.25f; GyroBalance.Saturation = true;
            Check(GyroBalance.Factor(false, 1f) == 1f && GyroBalance.Factor(true, 0f) == 0.25f && MathF.Abs(GyroBalance.Factor(true, 1f) - 0.25f * GyroBalance.Floor) < 1e-6f,
                "gyro factor: scale x fade (0.25 empty, 0.25 x floor full); aero off: 1");
            GyroBalance.Saturation = false;
            Check(GyroBalance.Factor(true, 1f) == 0.25f, "gyro factor: saturation off - the scale alone, whatever the store");
            GyroBalance.TorqueScale = 0f; float lo = GyroBalance.Factor(true, 0f);
            GyroBalance.TorqueScale = 3f; float hi = GyroBalance.Factor(true, 0f);
            GyroBalance.TorqueScale = float.NaN; float nan = GyroBalance.Factor(true, 0f);
            Check(lo == 0.05f && hi == 1f && nan == 1f, $"gyro factor: the scale held to 0.05..1 ({lo}, {hi}), NaN vanilla ({nan})");
            GyroBalance.TorqueScale = 0.25f; GyroBalance.Saturation = true;

            // 3. the write rule: from the rating, never compounding; exactly the rating back; tolerance; no rating no write
            {
                const float R = 217980f;   // (Gyroscope50's MaxTorque)
                float cur = R; int writes = 0;
                for (int k = 0; k < 50; k++) if (GyroBalance.Target(R, cur, 0.25f, out float w)) { cur = w; writes++; }
                bool once = writes == 1 && cur == R * 0.25f;
                bool tol = !GyroBalance.Target(R, cur, 0.25f * 1.005f, out _) && GyroBalance.Target(R, cur, 0.25f * 1.1f, out float w2) && w2 == R * 0.25f * 1.1f;
                bool back = GyroBalance.Target(R, R * 0.999f, 1f, out float w3) && w3 == R && !GyroBalance.Target(R, R, 1f, out _);
                bool none = !GyroBalance.Target(0f, 5f, 0.25f, out _) && !GyroBalance.Target(R, R, float.NaN, out _);
                Check(once && tol && back && none, $"gyro write: once to the scaled rating ({writes} writes over 50 updates, {cur:F0} N m), within {GyroBalance.WriteTolerance:P0} left alone, the rating exactly back at 1, no rating / NaN: no write");
            }

            // 4. the store (a grid flying: the torque it asks, given up to its full x fade - the faded maxima feed back)
            const float full = 1.0e6f, S = 15f;
            GyroBalance.SaturationSeconds = S;
            float Fly(ref Vector3 store, Func<float, Vector3> ask, float seconds, out float lastGiven, out float firstFadeAt)
            {
                float fill = 0f; lastGiven = 0f; firstFadeAt = -1f;
                for (float t = 0f; t < seconds; t += dt)
                {
                    float cap = full * GyroBalance.Fade(fill);
                    Vector3 a = ask(t), given = a.Length() > cap ? a * (cap / a.Length()) : a;
                    lastGiven = given.Length();
                    fill = GyroBalance.Step(ref store, given, full, S, dt);
                    if (firstFadeAt < 0f && GyroBalance.Fade(fill) < 0.999f) firstFadeAt = t;
                }
                return fill;
            }
            {   // holding a steady full moment: fades from ~FadeFrom x S, settles at the floor
                var st = Vector3.Zero;
                float fill = Fly(ref st, _ => new Vector3(full, 0, 0), 120f, out float given, out float fadeAt);
                Check(MathF.Abs(fadeAt - GyroBalance.FadeFrom * S) < 1f && fill > 0.99f && MathF.Abs(given / full - GyroBalance.Floor) < 0.02f,
                    $"gyro store: a steady full moment starts fading at {fadeAt:F1} s (~{GyroBalance.FadeFrom * S:F1}), full later, the gyros then give {given / full:P0} (floor {GyroBalance.Floor:P0})");
                // ... and recovers unloaded: empty again in ~S / Recover
                float back = -1f, f2 = fill;
                for (float t = 0f; t < 120f && back < 0f; t += dt) { f2 = GyroBalance.Step(ref st, Vector3.Zero, full, S, dt); if (f2 <= 0f) back = t; }
                Check(MathF.Abs(back - S / GyroBalance.Recover) < 1f, $"gyro store: unloaded it empties in {back:F1} s (~{S / GyroBalance.Recover:F0} = seconds / Recover)");
            }
            {   // a small trim (10% of full, held for 10 minutes): never fills
                var st = Vector3.Zero;
                float fill = Fly(ref st, _ => new Vector3(0, 0.1f * full, 0), 600f, out _, out float fadeAt);
                Check(fill < 1e-3f && fadeAt < 0f, $"gyro store: a 10% trim held 10 min stays empty (fill {fill:F4})");
            }
            {   // manoeuvring: full torque one way 2 s, the other way 2 s (a turn and its stop), for 5 minutes: never fades
                var st = Vector3.Zero;
                float fill = Fly(ref st, t => new Vector3(0, 0, ((int)(t / 2f) % 2 == 0 ? 1f : -1f) * full), 300f, out _, out float fadeAt);
                Check(fadeAt < 0f && fill < 0.2f, $"gyro store: turn-and-stop at full torque for 5 min never fades (fill {fill:F2}) - the store is signed");
            }
            {   // half the full torque held: fills, but slower than full
                var st = Vector3.Zero;
                Fly(ref st, _ => new Vector3(0.5f * full, 0, 0), 120f, out _, out float fadeAt);
                Check(fadeAt > GyroBalance.FadeFrom * S * 1.5f && fadeAt < 60f, $"gyro store: half the full torque held starts fading at {fadeAt:F1} s");
            }
            {   // not finite / no gyros: emptied, never NaN
                var st = new Vector3(5f, 0, 0);
                float a = GyroBalance.Step(ref st, new Vector3(float.NaN, 0, 0), full, S, dt); bool e1 = st == Vector3.Zero && a == 0f;
                st = new Vector3(5f, 0, 0); float b = GyroBalance.Step(ref st, Vector3.Zero, 0f, S, dt); bool e2 = st == Vector3.Zero && b == 0f;
                Check(e1 && e2, "gyro store: a NaN torque or no gyros empties it (never NaN)");
            }
            // 5. gyros lost (a cut, a gyro ground off): the store keeps the remaining gyros' share; gaining gyros keeps it
            {
                var st = new Vector3(8f, 0, 6f); GyroBalance.Lost(ref st, 4f, 2f); bool half = MathF.Abs(st.Length() - 5f) < 1e-4f;
                var st2 = new Vector3(8f, 0, 6f); GyroBalance.Lost(ref st2, 2f, 4f); GyroBalance.Lost(ref st2, 0f, 4f); bool kept = st2 == new Vector3(8f, 0, 6f);
                var st3 = new Vector3(8f, 0, 6f); GyroBalance.Lost(ref st3, 4f, 0f);
                Check(half && kept && st3 == Vector3.Zero, "gyro store: half the gyros lost keeps half the store; more gyros keep it; all lost empties it");
            }
            // 6. the design figures: the faded budget taken back to the scaled one
            Check(GyroBalance.Unfaded(250f, 0.5f) == 500f && GyroBalance.Unfaded(250f, 1f) == 250f && GyroBalance.Unfaded(250f, 0f) == 250f,
                "gyro design figure: the fade taken out (not the scale)");
            // 7. the defaults (the measured choice and the mild saturation)
            Check(scale0 == 0.25f && sat0 && sec0 == 15f, $"gyro defaults: scale {scale0}, saturation {sat0}, {sec0} s");
        }
        finally { GyroBalance.TorqueScale = scale0; GyroBalance.Saturation = sat0; GyroBalance.SaturationSeconds = sec0; }
    }
}
