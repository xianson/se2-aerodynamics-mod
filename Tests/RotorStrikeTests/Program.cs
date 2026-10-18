#pragma warning disable
using AeroMod;
using S = AeroMod.RotorStrike;
using K = AeroMod.AeroPropulsorComponent.Kinds;

/// <summary>RotorStrikeTests: the rotor strikes' game-free laws (Propulsion/RotorStrike.cs), offline (exit 0 = all pass).</summary>
static class Program
{
    static int _checks, _fails;
    static void Check(bool ok, string what) { _checks++; if (!ok) { _fails++; if (_fails <= 40) Console.WriteLine("FAIL " + what); } }
    const float Full = 200f;   // (RotorKinematics.TipSpeed: every governed propulsor's tips at 100 %)

    static int Main()
    {
        Check(RotorKinematics.TipSpeed == Full, $"tip speed {RotorKinematics.TipSpeed} m/s");
        Coverage();
        WearLaw();
        BreakThreshold();
        LightTouch();
        Characters();
        Blocks();
        Sizing();
        OwnParts();
        Repair();
        HillSkim();
        StructureHook();
        Console.WriteLine($"RotorStrikeTests: {_checks - _fails}/{_checks} passed");
        return _fails == 0 ? 0 : 1;
    }

    static string SimDir([System.Runtime.CompilerServices.CallerFilePath] string here = "") => System.IO.Path.GetFullPath(System.IO.Path.Combine(System.IO.Path.GetDirectoryName(here), "..", "..", "Scripts", "Simulation"));

    // ── the structure hook (2026-10-17): the strike's impulses into the structure (the representation's checks: StructureTests ImpulseLoads) ──
    static void StructureHook()
    {
        string srv = System.IO.File.ReadAllText(System.IO.Path.Combine(SimDir(), "Propulsion", "RotorStrikeServer.cs"));
        Check(srv.Contains("StructureImpulse(ship, rot.Entity, s.Pos, onShip, broke ? s.Spin * L : Vector3.Zero);"), "strike: the ship's reaction and the break's angular momentum into its structure at the rotor block");
        Check(srv.Contains("aero.PublishBlockImpulse(rotor, WorldTransform.TransformDirectionInv(j, swt), (Vector3)WorldTransform.TransformInv(pos, swt), WorldTransform.TransformDirectionInv(l, swt));"),
              "strike: published as an impulse in the ship's frame (gathered and taken once by the next snapshot - PublishBlockImpulse)");
        Check(srv.Contains("hitAero.PublishBlockImpulse(block.Entity,") && srv.Contains("if (ReferenceEquals(hit, ship)) hitAero.PublishBlockImpulse(rot.Entity,"),
              "strike: the struck block takes the push (another grid's, or the ship's own with the reaction at the rotor - an internal pair)");
        Check(srv.Contains("if (AeroPhysics.IsDynamic(ship)) aero.NoteKnownImpulse(j);") && srv.Contains("else if (AeroPhysics.IsDynamic(hit)) hitAero.NoteKnownImpulse(push);"),
              "strike: each body's impulse noted for its contact balance (not read as the wheels' or plates')");
        Check(!srv.Contains("plugs in here when it exists"), "strike: the hook is in (the placeholder note gone)");
        // the load a rock strike makes at the crush time (StructureContactLoads.CrushSeconds 0.05 s), for every striking propulsor at full rpm
        foreach (var row in Striking())
        {
            float j = S.StrikeImpulse(S.Material.Rock, row.Radius, row.Radius, Full);
            Check(j > 0f && float.IsFinite(j / 0.05f), $"{row.Name}: a full-rpm rock strike at the tip loads {j / 0.05f / 1000f:F1} kN for one snapshot");
        }
    }

    static IEnumerable<PropulsorBlocks.Row> Striking() => PropulsorBlocks.Rows.Where(r => S.Strikes(r.Kind));

    // ── 1. the probes: the budget, the tip circle covered in RingChords / ChordsPerTick ticks, the inside by the spokes ──
    static void Coverage()
    {
        Check(S.RaysPerTick == 4, $"probe budget {S.RaysPerTick} rays per rotor per tick");
        int ticks = S.RingChords / S.ChordsPerTick;
        Check(ticks == 4, $"tip circle covered every {ticks} ticks");
        Check(Striking().Count() == 7 && Striking().All(r => r.Kind != K.Fan && r.Kind != K.Ramjet), "strikers: 3 rotors, the Reversible Rotor, 3 turboprops (no fans, no ramjets)");
        foreach (var row in Striking())
        {
            float R = row.Radius;
            // from any start of the round robin, `ticks` ticks of chords cross every azimuth's radial line between 0.96 R and the tip
            for (int start = 0; start < S.RingChords; start++)
            {
                int chord = start, spoke = 0;
                var segs = new List<(Vector2 a, Vector2 b)>();
                Span<Vector2> f = stackalloc Vector2[S.RaysPerTick], t = stackalloc Vector2[S.RaysPerTick];
                for (int k = 0; k < ticks; k++) { S.TickRays(ref chord, ref spoke, R, f, t); for (int j = 0; j < S.ChordsPerTick; j++) segs.Add((f[j], t[j])); }
                float worst = float.MaxValue;
                for (float deg = 0f; deg < 360f; deg += 0.25f)
                {
                    var d = new Vector2(MathF.Cos(deg * MathF.PI / 180f), MathF.Sin(deg * MathF.PI / 180f));
                    float best = 0f;
                    foreach (var (a, b) in segs) { float r = RayHit(d, a, b); if (r > best) best = r; }
                    worst = MathF.Min(worst, best);
                }
                Check(worst >= 0.96f * R && worst <= R * 1.0001f, $"{row.Name} start {start}: tip circle reached at >= {worst / R:F3} R in {ticks} ticks");
            }
            // every chord's ends on the tip circle
            for (int c = 0; c < S.RingChords; c++) { S.Chord(c, R, out var a, out var b); Check(MathF.Abs(a.Length() - R) < 1e-3f * R && MathF.Abs(b.Length() - R) < 1e-3f * R, $"{row.Name} chord {c} ends on the tip"); }
        }
        // the spokes: SpokeSteps distinct directions, every one in SpokeSteps ticks; after 4 ticks the four quarters
        var angles = Enumerable.Range(0, S.SpokeSteps).Select(i => S.SpokeAngle(i) * 180f / MathF.PI).ToList();
        Check(angles.Distinct().Count() == S.SpokeSteps, "spokes: every direction once per round");
        Check(MaxGap(angles.Take(4)) <= 90.01f, $"spokes: 4 ticks leave at most {MaxGap(angles.Take(4)):F1} deg between them");
        Check(MaxGap(angles) <= 22.51f, $"spokes: a round leaves at most {MaxGap(angles):F1} deg between them");
        S.Spoke(3, 7.5f, out var s0, out var s1);
        Check(MathF.Abs(s0.Length() - S.InnerFrac * 7.5f) < 1e-4f && MathF.Abs(s1.Length() - 7.5f) < 1e-4f, "a spoke runs from InnerFrac R to the tip");
    }

    /// <summary>Where the ray from the hub along d crosses segment a-b (its radius), 0 if it does not.</summary>
    static float RayHit(Vector2 d, Vector2 a, Vector2 b)
    {
        var e = b - a;
        float den = d.X * (-e.Y) - d.Y * (-e.X);
        if (MathF.Abs(den) < 1e-9f) return 0f;
        // d s = a + e u  ->  s d - u e = a
        float s = (a.X * (-e.Y) - a.Y * (-e.X)) / den;
        float u = (d.X * a.Y - d.Y * a.X) / den;
        return u >= -1e-6f && u <= 1f + 1e-6f && s > 0f ? s : 0f;
    }

    static float MaxGap(IEnumerable<float> deg)
    {
        var a = deg.Select(x => (x % 360f + 360f) % 360f).OrderBy(x => x).ToList();
        float g = 0f;
        for (int i = 0; i < a.Count; i++) g = MathF.Max(g, (i + 1 < a.Count ? a[i + 1] : a[0] + 360f) - a[i]);
        return g;
    }

    // ── 2. the blades' wear: ~ v^2, rock > metal > character, nothing under the strike speed ──
    static void WearLaw()
    {
        var mats = new[] { S.Material.Rock, S.Material.Metal, S.Material.Character };
        foreach (var m in mats)
        {
            float prev = -1f;
            for (float v = 0f; v <= Full; v += 5f)
            {
                float w = S.BladeWear(m, v);
                Check(w >= prev - 1e-7f, $"{m}: wear grows with speed ({v} m/s: {w:F4})");
                if (v >= S.MinStrikeSpeed) Check(MathF.Abs(w - S.StrikeWearFull * S.Hardness(m) * (v / Full) * (v / Full)) < 1e-6f, $"{m}: wear ~ (v / tip speed)^2 at {v} m/s");
                else Check(w == 0f, $"{m}: no wear under {S.MinStrikeSpeed} m/s ({v})");
                prev = w;
            }
            Check(MathF.Abs(S.BladeWear(m, 100f) / S.BladeWear(m, 50f) - 4f) < 1e-3f, $"{m}: twice the speed, four times the wear");
        }
        foreach (float v in new[] { 20f, 100f, 200f })
            Check(S.BladeWear(S.Material.Rock, v) > S.BladeWear(S.Material.Metal, v) && S.BladeWear(S.Material.Metal, v) > S.BladeWear(S.Material.Character, v), $"rock > metal > character at {v} m/s");
        Check(MathF.Abs(S.BladeWear(S.Material.Rock, Full) - 0.34f) < 1e-6f && MathF.Abs(S.BladeWear(S.Material.Metal, Full) - 0.204f) < 1e-6f && MathF.Abs(S.BladeWear(S.Material.Character, Full) - 0.051f) < 1e-6f,
            "full-speed wear: rock 0.34, metal 0.204, character 0.051");
        // the speed at a radius: rpm x tip speed x r / R, any size
        foreach (var row in Striking())
        {
            Check(MathF.Abs(S.SpeedAt(1f, row.Radius, row.Radius) - Full) < 1e-3f, $"{row.Name}: tip at 100 % = {Full} m/s");
            Check(MathF.Abs(S.SpeedAt(0.5f, row.Radius * 0.5f, row.Radius) - Full * 0.25f) < 1e-3f, $"{row.Name}: half radius at 50 % rpm = a quarter");
        }
    }

    // ── 3. the break: rock 3 full-speed strikes, metal 5, a character 20; never below 0 ──
    static void BreakThreshold()
    {
        foreach (var (m, n) in new[] { (S.Material.Rock, 3), (S.Material.Metal, 5), (S.Material.Character, 20) })
        {
            float h = 1f; int k = 0;
            while (!S.IsBroken(h) && k < 1000) { h = S.AfterWear(h, S.BladeWear(m, Full)); k++; }
            Check(k == n && h == 0f, $"{m}: broken by {k} full-speed strikes (want {n}), health {h}");
        }
        Check(!S.IsBroken(1e-4f) && S.IsBroken(0f), "broken at no health only");
        Check(S.AfterWear(0.1f, 5f) == 0f && S.AfterWear(0.5f, -1f) == 0.5f, "health clamps at 0, a negative wear heals nothing");
    }

    // ── 4. light touches: a brush at low rpm only nicks ──
    static void LightTouch()
    {
        foreach (var row in Striking())
        {
            float R = row.Radius;
            float atTip30 = S.BladeWear(S.Material.Rock, S.SpeedAt(0.3f, R, R));
            Check(atTip30 <= 0.05f, $"{row.Name}: one rock brush at 30 % rpm: {atTip30 * 100f:F1} % of the blades (a nick, <= 5 %)");
            float idle = S.BladeWear(S.Material.Rock, S.SpeedAt(0.05f, R, R));
            Check(idle < 0.001f, $"{row.Name}: a rock brush at 5 % rpm: {idle * 100f:F3} %");
            Check(!S.IsStrike(S.SpeedAt(0.005f, R, R)), $"{row.Name}: a barely turning disc (0.5 % rpm, {S.SpeedAt(0.005f, R, R):F1} m/s) does not strike");
        }
    }

    // ── 5. characters: lethal at full rpm, a bruise at low; the knock-back capped ──
    static void Characters()
    {
        const float hp = 100f;
        foreach (var row in Striking())
        {
            float R = row.Radius;
            // lethal: one strike anywhere past a quarter of the radius at full rpm
            Check(S.CharacterDamage(S.SpeedAt(1f, 0.25f * R, R), hp) >= hp, $"{row.Name}: one strike at 0.25 R, 100 % rpm kills ({S.CharacterDamage(S.SpeedAt(1f, 0.25f * R, R), hp):F0} of {hp})");
            float bruise = S.CharacterDamage(S.SpeedAt(0.05f, R, R), hp);
            Check(bruise > 0f && bruise < 0.1f * hp, $"{row.Name}: 5 % rpm at the tip bruises ({bruise:F1} of {hp})");
            float knock = S.CharacterKnock(R, R, Full, 100f) / 100f;
            Check(knock > 0f && knock <= S.KnockMaxSpeed + 1e-3f, $"{row.Name}: knock-back {knock:F1} m/s (<= {S.KnockMaxSpeed})");
        }
        Check(S.CharacterDamage(S.LethalSpeed, hp) == hp, "one strike at the lethal speed takes the whole health");
        Check(S.CharacterDamage(1f, hp) == 0f, "under the strike speed: nothing");
        Check(S.CharacterDamage(Full, hp) <= 2f * hp, "the damage is bounded (2x health)");
        Check(S.CharacterKnock(7.5f, 7.5f, Full, 0f) == S.CharacterKnock(7.5f, 7.5f, Full, S.CharacterMass), "no mass read: a 100 kg character");
    }

    // ── 6. the struck block: ~ v^2, heavier blades cut deeper (capped) ──
    static void Blocks()
    {
        float b15 = S.BlockDamageShare(7.5f, 7.5f, Full);
        Check(MathF.Abs(b15 - S.BlockShareFull * MathF.Min(S.EffectiveMass(7.5f, 7.5f) / S.BlockRefMass, S.BlockMassCap)) < 1e-6f, $"1.5 m rotor tip, full speed: {b15 * 100f:F1} % of a block");
        Check(S.BlockDamageShare(12.5f, 12.5f, Full) > b15 && S.BlockDamageShare(1.25f, 1.25f, Full) < b15, "a bigger blade cuts deeper, a tail rotor's less");
        Check(S.BlockDamageShare(12.5f, 12.5f, Full) <= S.BlockShareFull * S.BlockMassCap + 1e-6f, "the cut is capped");
        Check(MathF.Abs(S.BlockDamageShare(7.5f, 7.5f, 100f) - b15 / 4f) < 1e-6f, "half the speed, a quarter of the cut");
        Check(S.BlockDamageShare(7.5f, 7.5f, 1f) == 0f, "under the strike speed: nothing");
    }

    // ── 7. the impulse and the break's angular momentum, sized from the blades (the table printed: docs) ──
    static void Sizing()
    {
        Console.WriteLine("  block                    R m   blade kg  I kg m^2   omega rad/s  m_eff tip kg  J rock tip N s  J metal N s  L break N m s  gap ticks");
        foreach (var row in Striking())
        {
            float R = row.Radius, m = S.BladeMass(R), I = S.RotorInertia(R), w = S.Omega(1f, R), me = S.EffectiveMass(R, R);
            float jr = S.StrikeImpulse(S.Material.Rock, R, R, Full), jm = S.StrikeImpulse(S.Material.Metal, R, R, Full), L = S.BreakAngularMomentum(1f, R);
            int gap = S.GapTicks(1f, R, 1f / 60f);
            Console.WriteLine($"  {row.Name,-24} {R,5:F2} {m,9:F1} {I,9:F0} {w,12:F1} {me,13:F1} {jr,15:F0} {jm,12:F0} {L,14:F0} {gap,10}");
            Check(MathF.Abs(m - S.BladeMassPerR2 * R * R) < 1e-3f, $"{row.Name}: blade mass ~ R^2");
            Check(MathF.Abs(I - 3f * m * R * R / 3f) < 1e-2f * I, $"{row.Name}: I = 3 blades x m R^2 / 3");
            Check(MathF.Abs(me - m / 3f) < 1e-3f, $"{row.Name}: a blade's mass seen at its tip is a third of it");
            Check(S.EffectiveMass(0.05f * R, R) <= m + 1e-4f, $"{row.Name}: never more than the blade near the hub");
            Check(MathF.Abs(jr - me * Full) < 1e-2f * jr, $"{row.Name}: a rock strike at the tip stops the tip's share of the blade: m/3 x v");
            Check(jm < jr && S.StrikeImpulse(S.Material.Character, R, R, Full) < jm, $"{row.Name}: impulse rock > metal > character");
            Check(MathF.Abs(L - I * w) < 1e-3f * L, $"{row.Name}: the break dumps L = I omega");
            Check(MathF.Abs(L - S.BladeMassPerR2 * R * R * R * Full) < 1e-3f * L, $"{row.Name}: L = 1.75 R^3 x tip speed (3 blades, uniform)");
            Check(gap >= S.GapTicksMin && gap <= 60, $"{row.Name}: strikes {gap} ticks apart at 100 %");
        }
        // the 1.5 m rotor: blade 98 kg, L 148 kN m s; its strikes a blade pass apart (5 ticks at 100 %)
        Check(MathF.Abs(S.BladeMass(7.5f) - 98.4f) < 0.1f, $"HelicopterRotor_150 blade {S.BladeMass(7.5f):F1} kg");
        Check(MathF.Abs(S.BreakAngularMomentum(1f, 7.5f) - 147656f) < 10f, $"HelicopterRotor_150 break: {S.BreakAngularMomentum(1f, 7.5f):F0} N m s");
        Check(S.GapTicks(1f, 7.5f, 1f / 60f) == 5, $"HelicopterRotor_150: a blade pass {S.GapTicks(1f, 7.5f, 1f / 60f)} ticks");
        Check(S.GapTicks(0.1f, 7.5f, 1f / 60f) > S.GapTicks(1f, 7.5f, 1f / 60f), "slower: strikes further apart");
        // the blade's motion: the spin axis x the radius (a rotor turning about -Y: at +X the blade moves toward +Z... the right hand)
        var t = S.BladeMotion(new Vector3(0f, -1f, 0f), new Vector3(1f, 0f, 0f));
        Check((t - new Vector3(0f, 0f, 1f)).Length() < 1e-5f, $"rotor at +X moves {t} (clockwise from above: -Y x +X = +Z)");
        Check(MathF.Abs(S.RadiusOf(new Vector3(3f, 2f, 4f), Vector3.UnitY) - 5f) < 1e-5f, "a point's radius in the disc ignores its height");
        // the kinds' frames: the disc's basis is orthogonal to the spin axis
        foreach (var k in new[] { K.Rotor, K.TailRotor, K.Turboprop })
        {
            S.DiscBasis(k, out var u, out var w);
            var a = S.SpinAxis(k);
            Check(MathF.Abs(Vector3.Dot(u, a)) < 1e-6f && MathF.Abs(Vector3.Dot(w, a)) < 1e-6f && MathF.Abs(Vector3.Dot(u, w)) < 1e-6f, $"{k}: disc basis in the disc");
            Check(MathF.Abs(MathF.Abs(Vector3.Dot(S.HubAxis(k), a)) - 1f) < 1e-6f, $"{k}: the hub sits along the spin axis");
        }
    }

    // ── 8. the rotor's own parts: its own cells (one cell of slack) never struck ──
    static void OwnParts()
    {
        var min = new Vector3I(-2, 0, -2); var max = new Vector3I(2, 4, 2);
        Check(S.IsOwnCell(new Vector3I(0, 2, 0), min, max) && S.IsOwnCell(new Vector3I(3, 5, -3), min, max), "its own cells and the cell around them: itself");
        Check(!S.IsOwnCell(new Vector3I(4, 2, 0), min, max) && !S.IsOwnCell(new Vector3I(0, 6, 0), min, max) && !S.IsOwnCell(new Vector3I(0, 2, -4), min, max), "two cells out: the ship's other blocks (struck)");
        // and the probes never start inside the hub: the spokes begin at InnerFrac R, the chords on the tip
        foreach (var row in Striking())
            Check(S.InnerFrac * row.Radius >= 0.15f, $"{row.Name}: spokes start {S.InnerFrac * row.Radius:F2} m out");
    }

    // ── 9. the repair rule: welded to full integrity, the blades are whole; the wear marks the block ──
    static void Repair()
    {
        Check(S.Repaired(0f, 1f) == 1f && S.Repaired(0.4f, S.RepairIntegrity) == 1f, "full integrity: whole again");
        Check(S.Repaired(0f, 0.99f) == 0f && S.Repaired(0.4f, 0.5f) == 0.4f, "under full integrity: as it was");
        Check(S.Repaired(1f, 0.2f) == 1f, "whole blades stay whole on a damaged block");
        // every accepted wear leaves its mark on the block (at least MinBlockWear), so a worn rotor is never 'welded' before welding
        foreach (float w in new[] { 1e-6f, 0.001f, 0.05f, 0.34f })
            Check(S.RotorBlockWear(w) >= S.MinBlockWear && 1f - S.RotorBlockWear(w) < S.RepairIntegrity, $"wear {w}: the block loses {S.RotorBlockWear(w) * 100f:F1} %, below the repair line");
        Check(S.RotorBlockWear(0f) == 0f, "no wear: no block damage");
        // a broken rotor: its blades were half the block - it lost at least half its health before it broke
        float block = 1f, h = 1f;
        while (!S.IsBroken(h)) { float w = S.BladeWear(S.Material.Rock, Full); block -= S.RotorBlockWear(w); h = S.AfterWear(h, w); }
        Check(block <= 1f - S.BladeShareOfBlock + 1e-3f, $"broken by rock: the block at {block:F2} of its health");
        // the round trip a welder makes: integrity rises back to 1, the blades come back
        float hh = 0f;
        for (int i = 0; i <= 10; i++)
        {
            float integ = block + (1f - block) * i / 10f;   // (the welder's passes, the last at full integrity)
            hh = S.Repaired(hh, i == 10 ? 1f : integ);
            if (i < 10) Check(hh == 0f, $"welding at {integ:F2}: still broken");
        }
        Check(hh == 1f, "welded up: whole");
    }

    // ── 10. a 1.5 m rotor at full rpm skims a hillside (a wall 0.3 m into its disc), flown tick by tick with the probes; then a light
    //        brush; then a character standing in the disc ──
    static void HillSkim()
    {
        const float R = 7.5f, dt = 1f / 60f;
        // the wall: x >= 7.2 in the disc's (u, w) plane
        float Hit(Vector2 a, Vector2 b, out Vector2 p)
        {
            const float X = 7.2f; p = default;
            if (a.X >= X) { p = a; return 0f; }
            if (b.X < X) return -1f;
            float u = (X - a.X) / (b.X - a.X); p = a + (b - a) * u; return u;
        }
        (int ticksToBreak, int strikes, float impulse, float firstWear) Fly(float rpm, int maxTicks, int wallTicks)
        {
            float h = 1f, J = 0f, first = -1f; int chord = 0, spoke = 0, gap = 0, n = 0;
            Span<Vector2> f = stackalloc Vector2[S.RaysPerTick], t = stackalloc Vector2[S.RaysPerTick];
            for (int tick = 0; tick < maxTicks; tick++)
            {
                if (S.IsBroken(h)) return (tick, n, J, first);
                if (tick >= wallTicks) continue;
                if (gap > 0) { gap--; continue; }
                S.TickRays(ref chord, ref spoke, R, f, t);
                for (int k = 0; k < S.RaysPerTick; k++)
                {
                    if (Hit(f[k], t[k], out var p) < 0f) continue;
                    float r = p.Length(), v = S.SpeedAt(rpm, r, R);
                    if (!S.IsStrike(v)) continue;
                    float w = S.BladeWear(S.Material.Rock, v);
                    if (first < 0f) first = w;
                    h = S.AfterWear(h, w); J += S.StrikeImpulse(S.Material.Rock, r, R, v); n++;
                    gap = S.GapTicks(rpm, R, dt);
                    break;
                }
            }
            return (S.IsBroken(h) ? maxTicks : -1, n, J, first);
        }
        var full = Fly(1f, 600, 600);
        Console.WriteLine($"  hill skim (1.5 m rotor, 100 %, a wall 0.3 m into the disc): broken in {full.ticksToBreak} ticks, {full.strikes} strikes, {full.impulse:F0} N s + {S.BreakAngularMomentum(1f, R):F0} N m s");
        Check(full.ticksToBreak > 0 && full.ticksToBreak <= 30, $"hill skim at 100 %: broken in {full.ticksToBreak} ticks ({full.ticksToBreak * dt:F2} s), {full.strikes} strikes, {full.impulse:F0} N s into the ship");
        Check(full.strikes == 3, $"hill skim: 3 rock strikes break the blades ({full.strikes})");
        Check(full.impulse > 15000f, $"hill skim: the ship is hit with {full.impulse:F0} N s plus the break's {S.BreakAngularMomentum(1f, R):F0} N m s");
        // a light brush: 30 % rpm, the wall there for one tick only
        var brush = Fly(0.3f, 120, 1);
        Check(brush.ticksToBreak < 0 && brush.strikes <= 1 && brush.firstWear <= 0.05f, $"a brush at 30 % for one tick: {brush.strikes} strike, {brush.firstWear * 100f:F1} % of the blades - a nick");
        // a character standing in the disc at r 5 m: the first probe that crosses it kills it at full rpm
        float v5 = S.SpeedAt(1f, 5f, R);
        Check(S.CharacterDamage(v5, 100f) >= 100f, $"a character at 5 m in a turning 1.5 m rotor: {v5:F0} m/s - lethal");
        Check(S.CharacterDamage(S.SpeedAt(0.05f, 5f, R), 100f) < 10f, $"at 5 % rpm: {S.CharacterDamage(S.SpeedAt(0.05f, 5f, R), 100f):F1} - a bruise");
    }
}
