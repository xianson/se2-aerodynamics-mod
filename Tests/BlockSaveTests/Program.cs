#pragma warning disable
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text;
using AeroMod;
using Keen.Game2.Simulation.GameSystems;
using Keen.VRage.DCS.Components;
using Keen.VRage.Library.Reflection;
using Keen.VRage.Library.Serialization;
using Keen.VRage.Library.Serialization.Binary;
using Keen.VRage.Library.Serialization.Validation.ErrorRecovery;
using Keen.VRage.Library.Utils;

/// <summary>BlockSaveTests: the aero blocks' own saved data, offline (exit 0 = all pass).</summary>
static class Program
{
    const string Game2 = @"C:\Program Files (x86)\Steam\steamapps\common\SpaceEngineers2\Game2";
    static int _checks, _fails;
    static void Check(bool ok, string what) { _checks++; if (!ok) { _fails++; if (_fails <= 30) Console.WriteLine("FAIL " + what); } }
    static bool Same(float a, float b) => BitConverter.SingleToInt32Bits(a) == BitConverter.SingleToInt32Bits(b);
    static readonly CultureInfo C = CultureInfo.InvariantCulture;

    static int Main()
    {
        AppDomain.CurrentDomain.AssemblyResolve += (s, e) =>
        {
            var p = Path.Combine(Game2, new AssemblyName(e.Name).Name + ".dll");
            return File.Exists(p) ? Assembly.LoadFrom(p) : null;
        };
        Keys();
        Legacy();
        Engine();
        DevForm();
        Console.WriteLine($"BlockSaveTests: {_checks - _fails}/{_checks} passed");
        return _fails == 0 ? 0 : 1;
    }

    static readonly float[] Floats = { 0f, -0f, 20f, 35f, -35f, 0.5f, 1f, 1f / 3f, 12.345678f, float.Epsilon, -3.5f, 1e-7f };

    // ── 1. each block kind's key: written, read back bit-exact; what a reader refuses ──
    static void Keys()
    {
        foreach (float max in Floats)
            foreach (float trim in Floats)
                for (int axes = 0; axes < 8; axes++)
                {
                    bool p = (axes & 1) != 0, y = (axes & 2) != 0, r = (axes & 4) != 0;
                    string k = AeroBlockSaveForm.Surface(p, y, r, max, trim);
                    Check(k.StartsWith(AeroBlockSaveForm.SurfaceKey + "1 ") && !k.Contains('\n'), $"surface key shape '{k}'");
                    Check(AeroBlockSaveForm.TryReadSurface(k, out bool p2, out bool y2, out bool r2, out float m2, out float t2)
                          && p2 == p && y2 == y && r2 == r && Same(m2, max) && Same(t2, trim), $"surface round trip '{k}'");
                }
        foreach (bool down in new[] { true, false })
            foreach (bool shock in new[] { true, false })
            {
                string k = AeroBlockSaveForm.Gear(down, shock);
                Check(AeroBlockSaveForm.TryReadGear(k, out bool d2, out bool s2) && d2 == down && s2 == shock, $"gear round trip '{k}'");
            }
        foreach (float f in Floats)
        {
            Check(AeroBlockSaveForm.TryReadAirbrake(AeroBlockSaveForm.Airbrake(f), out float b) && Same(b, f), $"airbrake round trip {f}");
            float want = Math.Clamp(f, 0f, 1f);
            Check(AeroBlockSaveForm.TryReadThrust(AeroBlockSaveForm.Thrust(f), out float t) && Same(t, want), $"thrust round trip {f} -> {t} (want {want})");
        }
        // the culture never leaks in (a German PC writes "0,5" with ToString())
        var was = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("de-DE");
            string k = AeroBlockSaveForm.Thrust(0.25f);
            Check(k == "aero-thrust:1 0.25" && AeroBlockSaveForm.TryReadThrust(k, out float t) && t == 0.25f, $"invariant culture: '{k}'");
        }
        finally { CultureInfo.CurrentCulture = was; }
        Check(AeroBlockSaveForm.TryReadThrust("aero-thrust:1 7", out float tc) && tc == 1f, "thrust clamped to 1");
        // a thruster's gimbal cone (2026-10-20): format 2 only with one; read back, clamped 0..15; a format-1 key has none
        Check(AeroBlockSaveForm.Thrust(0.25f, 0f) == "aero-thrust:1 0.25" && AeroBlockSaveForm.Thrust(0.5f, 6.5f) == "aero-thrust:2 0.5 6.5", $"thrust with a gimbal: '{AeroBlockSaveForm.Thrust(0.5f, 6.5f)}'");
        Check(AeroBlockSaveForm.TryReadThrust("aero-thrust:2 0.5 6.5", out float tf, out float tg) && tf == 0.5f && tg == 6.5f && AeroBlockSaveForm.TryReadThrust("aero-thrust:2 0.5 6.5", out float tf1) && tf1 == 0.5f, "thrust format 2 read back, and by the old reader");
        Check(AeroBlockSaveForm.TryReadThrust("aero-thrust:1 0.3", out _, out tg) && tg == 0f && AeroBlockSaveForm.TryReadThrust("aero-thrust:2 0.3 40", out _, out tg) && tg == GimbalLimits.MaxDeg
            && AeroBlockSaveForm.TryReadThrust("aero-thrust:2 0.3 -2", out _, out tg) && tg == 0f, "the gimbal: none in format 1, clamped 0..15");

        // refusals: another tag, too few values, format 0 / not a number, NaN, null
        bool b1, b2, b3; float f1, f2;
        Check(!AeroBlockSaveForm.TryReadSurface(AeroBlockSaveForm.Gear(true, true), out b1, out b2, out b3, out f1, out f2), "a gear key read as a surface");
        Check(!AeroBlockSaveForm.TryReadSurface("aero-surface:1 1 1 1 20", out b1, out b2, out b3, out f1, out f2), "a surface key short of trim read");
        Check(!AeroBlockSaveForm.TryReadSurface("aero-surface:0 1 1 1 20 0", out b1, out b2, out b3, out f1, out f2), "format 0 read");
        Check(!AeroBlockSaveForm.TryReadSurface("aero-surface:x 1 1 1 20 0", out b1, out b2, out b3, out f1, out f2), "format x read");
        Check(!AeroBlockSaveForm.TryReadSurface("aero-surface:1 1 1 1 NaN 0", out b1, out b2, out b3, out f1, out f2), "NaN max read");
        Check(!AeroBlockSaveForm.TryReadGear("aero-gear:1 1", out b1, out b2), "a gear key without airshock read");
        Check(!AeroBlockSaveForm.TryReadAirbrake(null, out f1) && !AeroBlockSaveForm.TryReadThrust("", out f1) && !AeroBlockSaveForm.TryReadThrust("aero-thrust:1 Infinity", out f1), "null / empty / infinite read");
        // a later format that appends values still reads (an older mod on a newer save keeps what it knows)
        Check(AeroBlockSaveForm.TryReadGear("aero-gear:2 0 1 0.75 extra", out b1, out b2) && !b1 && b2, "format 2 with more values");
        Check(AeroBlockSaveForm.TryReadSurface("aero-surface:3 0 1 0 12.5 -4 9 9", out b1, out b2, out b3, out f1, out f2) && !b1 && b2 && !b3 && f1 == 12.5f && f2 == -4f, "surface format 3");

        // the key among others in a block's map; none -> null
        var map = new Dictionary<string, object> { ["something-else:1"] = null, [AeroBlockSaveForm.Airbrake(12f)] = null };
        Check(AeroBlockSaveForm.Find(map, AeroBlockSaveForm.AirbrakeKey) == "aero-airbrake:1 12", "Find the airbrake key");
        Check(AeroBlockSaveForm.Find(map, AeroBlockSaveForm.GearKey) == null && AeroBlockSaveForm.Find<object>(null, AeroBlockSaveForm.GearKey) == null, "Find: none / null map");
        Check(AeroBlockSaveForm.Find(new Dictionary<string, object>(), AeroBlockSaveForm.ThrustKey) == null, "Find: an empty map (a new block, or one from an old save after the migration's insert)");
        // ROTOR STRIKES (2026-10-17): the blades' health - whole blades carry no key; worn and broken round-trip bit-exact; clamped 0..1
        Check(AeroBlockSaveForm.Blades(1f) == null && AeroBlockSaveForm.Blades(float.NaN) == null, "whole blades: no key");
        foreach (float h in new[] { 0f, 1e-7f, 0.25f, 1f / 3f, 0.66f, 0.999f })
            Check(AeroBlockSaveForm.TryReadBlades(AeroBlockSaveForm.Blades(h), out float h2) && Same(h2, h), $"blades round trip {h} -> {h2}");
        Check(AeroBlockSaveForm.Blades(0f) == "aero-blades:1 0" && AeroBlockSaveForm.TryReadBlades("aero-blades:1 0", out float hb) && hb == 0f, "broken blades saved and loaded broken");
        Check(AeroBlockSaveForm.TryReadBlades("aero-blades:1 -3", out float hn) && hn == 0f && AeroBlockSaveForm.TryReadBlades("aero-blades:1 7", out float hx) && hx == 1f, "blades clamped 0..1");
        Check(!AeroBlockSaveForm.TryReadBlades(null, out float hz) && hz == 1f && !AeroBlockSaveForm.TryReadBlades("aero-blades:1 NaN", out hz) && !AeroBlockSaveForm.TryReadBlades(AeroBlockSaveForm.Thrust(0.25f), out hz), "blades: none / NaN / another tag refused (whole)");
        Check(AeroBlockSaveForm.Find(new Dictionary<string, object> { [AeroBlockSaveForm.Blades(0.5f)] = null }, AeroBlockSaveForm.BladesKey) == "aero-blades:1 0.5", "Find the blades key");
        // per-blade breaks (2026-10-23): each blade's state and wear after the health; an older reader still reads the health
        {
            byte[] cuts = { 1, 0, 2 }; float[] wear = { 0f, 0.25f, 0f };
            string k = AeroBlockSaveForm.Blades(0.42f, cuts, wear);
            Check(k == "aero-blades:1 0.42 1 0 2 0 0.25 0", $"per-blade key: {k}");
            byte[] c2 = new byte[3]; float[] w2 = new float[3];
            Check(AeroBlockSaveForm.TryReadBladeCuts(k, c2, w2) && c2[0] == 1 && c2[1] == 0 && c2[2] == 2 && Same(w2[1], 0.25f), "per-blade round trip");
            Check(AeroBlockSaveForm.TryReadBlades(k, out float hk) && Same(hk, 0.42f), "per-blade key: the health still read alone (an older reader)");
            Check(AeroBlockSaveForm.Blades(1f, new byte[3], new float[3]) == null, "per-blade: whole and unworn - no key");
            Check(AeroBlockSaveForm.Blades(1f, new byte[3], new float[] { 0f, 0.1f, 0f }) != null, "per-blade: worn blades saved though the lift is whole");
            byte[] c3 = { 9, 9, 9 };
            Check(!AeroBlockSaveForm.TryReadBladeCuts("aero-blades:1 0.5", c3, new float[3]) && c3[0] == 9, "an older single-health key: no blade states (mapped by the rotor), the array untouched");
            Check(AeroBlockSaveForm.TryReadBladeCuts("aero-blades:1 0 7 -1 2 2 0.5 0", c3, new float[3]) && c3[0] == 2 && c3[1] == 0, "blade states clamped 0..2");
        }
        // a main rotor's turn (2026-10-23): clockwise (the default) writes no key; counter-rotating round trips
        Check(AeroBlockSaveForm.RotorSpin(false) == null && AeroBlockSaveForm.RotorSpin(true) == "aero-rotorspin:1 1", "rotor spin: clockwise no key, counter-rotating 'aero-rotorspin:1 1'");
        Check(AeroBlockSaveForm.TryReadRotorSpin(AeroBlockSaveForm.RotorSpin(true), out bool ccw) && ccw && AeroBlockSaveForm.TryReadRotorSpin("aero-rotorspin:1 0", out bool cw) && !cw, "rotor spin round trip");
        Check(!AeroBlockSaveForm.TryReadRotorSpin(null, out bool cz) && !cz && !AeroBlockSaveForm.TryReadRotorSpin(AeroBlockSaveForm.Blades(0.5f), out cz) && !cz, "rotor spin: none / another tag refused (clockwise)");
        // THE AUTOPILOT BLOCK (2026-10-19): its whole panel round-trips bit-exact; every mode and reference; an older / shorter key keeps
        // the defaults for what it lacks; out-of-range, NaN and unknown values are clamped or read as off
        var ap = ApPanel.Defaults; ap.Mode = ApMode.HoverHold; ap.HeightM = 123.456f; ap.HeightRef = ApHeightRef.Inertial; ap.HoverSpeed = 7.25f; ap.Protection = false;
        ap.GLimit = 4.5f; ap.AoaMarginDeg = 2.5f; ap.FloorM = 42f; ap.LookAhead = false; ap.Unattended = false;
        Check(AeroBlockSaveForm.TryReadAutopilot(AeroBlockSaveForm.Autopilot(ap), out var ap2) && ap2.Mode == ap.Mode && Same(ap2.HeightM, ap.HeightM) && ap2.HeightRef == ap.HeightRef
              && Same(ap2.HoverSpeed, ap.HoverSpeed) && ap2.Protection == ap.Protection && Same(ap2.GLimit, ap.GLimit) && Same(ap2.AoaMarginDeg, ap.AoaMarginDeg) && Same(ap2.FloorM, ap.FloorM)
              && ap2.LookAhead == ap.LookAhead && ap2.Unattended == ap.Unattended, $"autopilot panel round trip: '{AeroBlockSaveForm.Autopilot(ap)}'");
        foreach (ApMode m in new[] { ApMode.Off, ApMode.HoverHold, ApMode.HeightHold })
            foreach (ApHeightRef hr in new[] { ApHeightRef.Ground, ApHeightRef.Inertial })
            {
                var q = ApPanel.Defaults; q.Mode = m; q.HeightRef = hr;
                Check(AeroBlockSaveForm.TryReadAutopilot(AeroBlockSaveForm.Autopilot(q), out var q2) && q2.Mode == m && q2.HeightRef == hr, $"autopilot mode {m}, reference {hr} round trip");
            }
        Check(AeroBlockSaveForm.Autopilot(ApPanel.Defaults).StartsWith("aero-autopilot:3 0 0 0 10 1 3 3 15 1 1 "), $"autopilot defaults key: '{AeroBlockSaveForm.Autopilot(ApPanel.Defaults)}'");
        // the flight pilot's values (appended 2026-10-19): every one round-trips; texts with spaces, '%', ';' and line breaks intact;
        // an older key (the first 11 values) reads the pilot's defaults
        {
            var f = ApPanel.Defaults; f.Mode = ApMode.Engage; f.Style = ApStyle.SixAxis; f.HeadingDeg = 271.5f; f.HeadingOn = false; f.AltM = 1234.5f; f.AltOn = false;
            f.VsMs = 7.5f; f.SpeedMs = 155f; f.SpeedOn = false; f.BankDeg = 33f; f.RouteLoop = true; f.LoiterM = 777f; f.MinAglM = 88f; f.Behaviour = ApBehaviour.PursuitLag;
            f.RangeM = 999f; f.AbortM = 321f; f.RerunS = 45f; f.AngleDeg = 120f; f.AltOffsetM = -55f; f.Aggression = 0.8f; f.Evasive = true; f.TargetSrc = ApTargetSrc.Grid;
            f.AimOn = false; f.MuzzleSpeed = 950f; f.ConeDeg = 1.5f; f.HasHome = true; f.HomeX = 123456.789; f.HomeY = -9876.5; f.HomeZ = 0.25; f.ReturnHome = true;
            f.Route = "Base One; 100 200 300\nGPS:Pad:1:2:3: 50%"; f.TargetText = "Red Raider %20 ";
            string key = AeroBlockSaveForm.Autopilot(f);
            Check(AeroBlockSaveForm.TryReadAutopilot(key, out var g) && g.Mode == f.Mode && g.Style == f.Style && g.HeadingDeg == f.HeadingDeg && g.HeadingOn == f.HeadingOn
                  && g.AltM == f.AltM && g.AltOn == f.AltOn && g.VsMs == f.VsMs && g.SpeedMs == f.SpeedMs && g.SpeedOn == f.SpeedOn && g.BankDeg == f.BankDeg && g.RouteLoop
                  && g.LoiterM == f.LoiterM && g.MinAglM == f.MinAglM && g.Behaviour == f.Behaviour && g.RangeM == f.RangeM && g.AbortM == f.AbortM && g.RerunS == f.RerunS
                  && g.AngleDeg == f.AngleDeg && g.AltOffsetM == f.AltOffsetM && g.Aggression == f.Aggression && g.Evasive && g.TargetSrc == f.TargetSrc && !g.AimOn
                  && g.MuzzleSpeed == f.MuzzleSpeed && g.ConeDeg == f.ConeDeg && g.HasHome && g.HomeX == f.HomeX && g.HomeY == f.HomeY && g.HomeZ == f.HomeZ && g.ReturnHome
                  && g.Route == f.Route && g.TargetText == f.TargetText, $"autopilot flight pilot round trip: '{key}'");
            Check(!key.Substring(AeroBlockSaveForm.AutopilotKey.Length).Contains('\n') && key.Split(' ').Length == 71, $"autopilot: one line, 70 values after the format ({key.Split(' ').Length - 1})");
            Check(AeroBlockSaveForm.TryReadAutopilot("aero-autopilot:1 4 0 0 10 1 3 3 15 1 1", out var old) && old.Mode == ApMode.Navigate && old.Style == ApStyle.Auto && old.Route == ""
                  && old.RangeM == ApPanel.Defaults.RangeM && !old.AimOn && old.Facing == ApFacing.Travel && old.AimPriority == 0.5f, "autopilot: a key of the first release reads the pilot's defaults");
            Check(AeroBlockSaveForm.TryReadAutopilot(key.Replace(" 3 ", " 99 "), out var bad) && AeroBlockSaveForm.TryReadAutopilot("aero-autopilot:1 5 0 0 10 1 3 3 15 1 1 9 NaN 1 -5 1 0 99999 1 0 1 0 0 77", out var bad2)
                  && bad2.Style == ApStyle.Auto && bad2.HeadingDeg == 0f && bad2.AltM == 0f && bad2.SpeedMs == ApPanel.SpeedMax && bad2.BankDeg == ApPanel.BankMin && bad2.Behaviour == ApBehaviour.Auto,
                  "autopilot: the pilot's unknown style / behaviour read as auto, NaN and out-of-range numbers clamped");
        }
        // THE FACING (format 3, 2026-10-20, appended 68..70: the facing, the aim priority, the movement Follow ship keeps): round trip;
        // an older key's aim switch is its facing (on: the nose); Point At / Broadside read as their movement and facing (ApPanel.Migrate);
        // Engage with the formation reads as Follow ship and Attack gives the movement back
        {
            foreach (ApFacing fc in Enum.GetValues(typeof(ApFacing)))
            {
                var f = ApPanel.Defaults; f.Mode = ApMode.Engage; f.Behaviour = ApBehaviour.Circle; f.Facing = fc; f.AimPriority = 0.35f;
                string k = AeroBlockSaveForm.Autopilot(f);
                Check(k.StartsWith("aero-autopilot:3 ") && AeroBlockSaveForm.TryReadAutopilot(k, out var g) && g.Facing == fc && g.AimPriority == 0.35f && g.AimOn == (fc == ApFacing.Nose)
                      && g.Behaviour == ApBehaviour.Circle, $"autopilot facing {fc} round trip: '{k}'");
            }
            var fs = ApPanel.Defaults; fs.Behaviour = ApBehaviour.PursuitLead; ApPanel.Choose(ref fs, ApModeChoice.FollowShip); fs = fs.Clamped();
            Check(AeroBlockSaveForm.TryReadAutopilot(AeroBlockSaveForm.Autopilot(fs), out var fs2) && ApPanel.ChoiceOf(in fs2) == ApModeChoice.FollowShip && fs2.AttackBh == ApBehaviour.PursuitLead,
                  "autopilot: Follow ship round trip, the attack movement it keeps saved");
            // (a format-2 key: the panel as the last release wrote it - the facing and the priority absent)
            string Old(ApPanel q) => string.Join(' ', AeroBlockSaveForm.Autopilot(q).Split(' ').Take(68)).Replace("aero-autopilot:3 ", "aero-autopilot:2 ");
            var o = ApPanel.Defaults; o.Mode = ApMode.Engage; o.Behaviour = ApBehaviour.PursuitPure;
            // (value 32 is the aim switch: an old key wrote 1 for it on; 23 the behaviour, 27 the broadside angle)
            string[] w = Old(o).Split(' '); w[32] = "1"; string kOn = string.Join(' ', w);
            w[32] = "0"; string kOff = string.Join(' ', w);
            Check(AeroBlockSaveForm.TryReadAutopilot(kOn, out var on) && on.Facing == ApFacing.Nose && on.AimOn && on.AimPriority == 0.5f && on.Behaviour == ApBehaviour.PursuitPure,
                  $"autopilot: a format-2 key with the aim switch on reads the nose facing ({on.Facing}): '{kOn}'");
            Check(AeroBlockSaveForm.TryReadAutopilot(kOff, out var off) && off.Facing == ApFacing.Travel && !off.AimOn, $"autopilot: a format-2 key with the aim switch off reads the way it flies ({off.Facing})");
            w[23] = ((int)ApBehaviour.PointAt).ToString();
            Check(AeroBlockSaveForm.TryReadAutopilot(string.Join(' ', w), out var pa) && pa.Behaviour == ApBehaviour.Standoff && pa.Facing == ApFacing.Nose && ApPanel.MoveOf(pa.Behaviour) == ApMove.KeepDistance,
                  $"autopilot: an old Point At reads Keep distance with the nose at the target ({pa.Behaviour}, {pa.Facing})");
            foreach (var (ang, want) in new[] { (90f, ApFacing.SideRight), (60f, ApFacing.SideRight), (120f, ApFacing.SideLeft), (10f, ApFacing.Nose) })
            {
                w[23] = ((int)ApBehaviour.Broadside).ToString(); w[27] = ang.ToString(System.Globalization.CultureInfo.InvariantCulture);
                Check(AeroBlockSaveForm.TryReadAutopilot(string.Join(' ', w), out var bs) && bs.Behaviour == ApBehaviour.Circle && bs.Facing == want && ApPanel.MoveOf(bs.Behaviour) == ApMove.Circle,
                      $"autopilot: an old Broadside at {ang} deg reads Circle it with {want} ({bs.Behaviour}, {bs.Facing})");
            }
            w[23] = ((int)ApBehaviour.Formation).ToString();
            string kF = string.Join(' ', w);
            Check(AeroBlockSaveForm.TryReadAutopilot(kF, out var fm) && fm.Mode == ApMode.Engage && ApPanel.ChoiceOf(in fm) == ApModeChoice.FollowShip,
                  $"autopilot: an old Engage + Formation reads Follow ship ({ApPanel.ChoiceOf(in fm)})");
            ApPanel.Choose(ref fm, ApModeChoice.Attack); fm = fm.Clamped();
            Check(fm.Mode == ApMode.Engage && fm.Behaviour == ApBehaviour.Auto && ApPanel.ChoiceOf(in fm) == ApModeChoice.Attack, $"autopilot: and Attack from there flies Auto (no movement kept from before: {fm.Behaviour})");
            var mig = ApPanel.Defaults; mig.Behaviour = ApBehaviour.PointAt; mig = mig.Clamped(); var again = mig.Clamped();
            Check(again.Behaviour == mig.Behaviour && again.Facing == mig.Facing, "autopilot: the migration is idempotent (Clamped twice)");
        }
        // the target filter and the Enemy toggle (2026-10-19, appended 42..44): round trip; an older key (41 values) reads hostile
        // only, no enemy; an unknown filter reads hostile only; the owner before kept exactly (an identity's 64-bit number)
        {
            foreach (var flt in new[] { ApTargetFilter.Hostile, ApTargetFilter.HostileNeutral, ApTargetFilter.Everything })
                foreach (bool en in new[] { false, true })
                {
                    var e = ApPanel.Defaults; e.Mode = ApMode.Engage; e.TargetFilter = flt; e.Enemy = en; e.EnemyPrevOwner = en ? 0x7123456789ABCDEFL : 0;
                    string k = AeroBlockSaveForm.Autopilot(e);
                    Check(AeroBlockSaveForm.TryReadAutopilot(k, out var e2) && e2.TargetFilter == flt && e2.Enemy == en && e2.EnemyPrevOwner == e.EnemyPrevOwner && e2.Mode == ApMode.Engage,
                          $"autopilot: filter {flt}, enemy {en} round trip: '{k}'");
                }
            var f0 = ApPanel.Defaults; f0.Route = "a b"; string k41 = AeroBlockSaveForm.Autopilot(f0);
            k41 = string.Join(' ', k41.Split(' ').Take(42));   // (the key as the first pilot release wrote it)
            Check(AeroBlockSaveForm.TryReadAutopilot(k41, out var o41) && o41.TargetFilter == ApTargetFilter.Hostile && !o41.Enemy && o41.EnemyPrevOwner == 0 && o41.Route == "a b",
                  $"autopilot: a key without the filter / enemy reads hostile only, no enemy: '{k41}'");
            Check(AeroBlockSaveForm.TryReadEnemyPrev(AeroBlockSaveForm.EnemyPrev(3, 0x7000000000000123L), out long ep) && ep == 0x7000000000000123L
                  && !AeroBlockSaveForm.TryReadEnemyPrev(AeroBlockSaveForm.Autopilot(ApPanel.Defaults), out _) && !AeroBlockSaveForm.TryReadAutopilot(AeroBlockSaveForm.EnemyPrev(0, 5), out _)
                  && AeroBlockSaveForm.EnemyPrev(0, 5) != AeroBlockSaveForm.EnemyPrev(1, 5), "autopilot: a grid's owner before (enemy) round trip, apart from the panel's key, one key per entry");
            var av = ApPanel.Defaults; av.AvoidOn = false; av.AvoidS = 7.5f;
            Check(AeroBlockSaveForm.TryReadAutopilot(AeroBlockSaveForm.Autopilot(av), out var av2) && !av2.AvoidOn && av2.AvoidS == 7.5f, "autopilot: collision avoidance off, 7.5 s round trip");
            Check(AeroBlockSaveForm.TryReadAutopilot(k41, out var av3) && av3.AvoidOn && av3.AvoidS == ApPanel.Defaults.AvoidS, "autopilot: an older key reads the avoidance on, its default look-ahead");
            // (format 2, 2026-10-19: the combat slots' and the formation's values - every one round-trips; a format-1 key (46 values)
            //  reads their defaults; unknown choices and out-of-range numbers clamped)
            {
                var s = ApPanel.Defaults; s.Mode = ApMode.Engage;
                s.DfFight = ApFightPref.Energy; s.DfHeadOn = true; s.DeckM = 333f; s.CeilingM = 7777f; s.DfBingoFuelPct = 22f; s.DfBingoDamagePct = 55f; s.DfRespectGuns = 0.25f; s.DfSkill = 0.9f;
                s.RgLaw = ApGuidanceLaw.QuadraticIntercept; s.RgNavConst = 4.5f; s.RgTargetAccelGain = 2.25f; s.RgMaxLatG = 33f; s.RgGravityComp = false; s.RgArmM = 120f; s.RgTimeoutS = 90f;
                s.FmRightM = -45f; s.FmUpM = 12.5f; s.FmBackM = 60f; s.FmCatchUpMs = 42f; s.DfSaved = "phase 3; t 1.5\nx"; s.RgSaved = "armed %";
                foreach (var b in new[] { ApBehaviour.Dogfight, ApBehaviour.RocketGuidance, ApBehaviour.Formation })
                {
                    s.Behaviour = b; string k2 = AeroBlockSaveForm.Autopilot(s);
                    Check(k2.StartsWith("aero-autopilot:3 ") && AeroBlockSaveForm.TryReadAutopilot(k2, out var t) && t.Behaviour == b && t.DfFight == s.DfFight && t.DfHeadOn && t.DeckM == s.DeckM && t.CeilingM == s.CeilingM
                          && t.DfBingoFuelPct == s.DfBingoFuelPct && t.DfBingoDamagePct == s.DfBingoDamagePct && t.DfRespectGuns == s.DfRespectGuns && t.DfSkill == s.DfSkill
                          && t.RgLaw == s.RgLaw && t.RgNavConst == s.RgNavConst && t.RgTargetAccelGain == s.RgTargetAccelGain && t.RgMaxLatG == s.RgMaxLatG && !t.RgGravityComp
                          && t.RgArmM == s.RgArmM && t.RgTimeoutS == s.RgTimeoutS && t.FmRightM == s.FmRightM && t.FmUpM == s.FmUpM && t.FmBackM == s.FmBackM && t.FmCatchUpMs == s.FmCatchUpMs
                          && t.DfSaved == s.DfSaved && t.RgSaved == s.RgSaved, $"autopilot format 2: {b} and every slot / formation value round trip: '{k2}'");
                }
                var d = ApPanel.Defaults; string k46 = string.Join(' ', AeroBlockSaveForm.Autopilot(s).Split(' ').Take(47)).Replace("aero-autopilot:2 ", "aero-autopilot:1 ");
                Check(AeroBlockSaveForm.TryReadAutopilot(k46, out var o1) && o1.Behaviour == ApBehaviour.Formation && o1.DfFight == d.DfFight && o1.DeckM == d.DeckM && o1.RgLaw == d.RgLaw
                      && o1.RgNavConst == d.RgNavConst && o1.RgGravityComp == d.RgGravityComp && o1.FmRightM == d.FmRightM && o1.FmBackM == d.FmBackM && o1.DfSaved == "" && o1.RgSaved == "",
                      $"autopilot: a format-1 key reads the slots' and the formation's defaults: '{k46}'");
                var w = AeroBlockSaveForm.Autopilot(s).Split(' '); w[47] = "9"; w[55] = "42"; w[56] = "NaN"; w[58] = "1e9"; w[62] = "-1e9"; w[23] = "77";
                Check(AeroBlockSaveForm.TryReadAutopilot(string.Join(' ', w), out var c2) && c2.DfFight == ApFightPref.Auto && c2.RgLaw == ApGuidanceLaw.ProNav && c2.RgNavConst == d.RgNavConst
                      && c2.RgMaxLatG == ApPanel.LatGMax && c2.FmRightM == -ApPanel.SlotMax && c2.Behaviour == ApBehaviour.Auto,
                      "autopilot format 2: an unknown fight / law / behaviour read as their first, NaN the default, out-of-range numbers clamped");
                var e12 = ApPanel.Defaults; e12.Behaviour = ApBehaviour.Extend;
                Check(AeroBlockSaveForm.TryReadAutopilot(AeroBlockSaveForm.Autopilot(e12), out var e13) && e13.Behaviour == ApBehaviour.Auto, "autopilot: the arbiter's own Extend is never a saved choice (read as Auto)");
            }
            var f9 = ApPanel.Defaults; string k9 = AeroBlockSaveForm.Autopilot(f9); var w9 = k9.Split(' '); w9[42] = "9"; w9[44] = "x";
            Check(AeroBlockSaveForm.TryReadAutopilot(string.Join(' ', w9), out var o9) && o9.TargetFilter == ApTargetFilter.Hostile && o9.EnemyPrevOwner == 0,
                  "autopilot: an unknown filter reads hostile only, a bad owner number none");
        }

        Check(AeroBlockSaveForm.TryReadAutopilot("aero-autopilot:1 1", out var ap3) && ap3.Mode == ApMode.HoverHold && ap3.GLimit == ApPanel.Defaults.GLimit && ap3.Unattended,
              "autopilot: a short key keeps the defaults for the rest (a later format may append)");
        Check(AeroBlockSaveForm.TryReadAutopilot("aero-autopilot:2 9 NaN 7 999 0 -3 99 -1 0 0 extra", out var ap4) && ap4.Mode == ApMode.Off && ap4.HeightM == 0f && ap4.HeightRef == ApHeightRef.Ground
              && ap4.HoverSpeed == ApPanel.HoverSpeedMax && ap4.GLimit == ApPanel.GLimitMin && ap4.AoaMarginDeg == ApPanel.AoaMarginMax && ap4.FloorM == ApPanel.FloorMin && !ap4.Protection && !ap4.LookAhead && !ap4.Unattended,
              "autopilot: unknown mode / reference off / ground, NaN height 0, every number clamped, a later format's extra value ignored");
        Check(!AeroBlockSaveForm.TryReadAutopilot(null, out var ap5) && ap5.Mode == ApMode.Off && !AeroBlockSaveForm.TryReadAutopilot(AeroBlockSaveForm.Airbrake(3f), out ap5) && !AeroBlockSaveForm.TryReadAutopilot("aero-autopilot:x 1", out ap5),
              "autopilot: none / another tag / a bad format refused (the defaults: off)");
        Check(AeroBlockSaveForm.Find(new Dictionary<string, object> { ["x:1"] = null, [AeroBlockSaveForm.Autopilot(ap)] = null }, AeroBlockSaveForm.AutopilotKey) == AeroBlockSaveForm.Autopilot(ap), "Find the autopilot key");
        // tags do not prefix each other, nor the old lines' tags
        string[] tags = { AeroBlockSaveForm.SurfaceKey, AeroBlockSaveForm.GearKey, AeroBlockSaveForm.AirbrakeKey, AeroBlockSaveForm.ThrustKey, AeroBlockSaveForm.BladesKey,
                          AeroBlockSaveForm.SolidNozzleKey, AeroBlockSaveForm.AutopilotKey,
                          AeroBlockSaveForm.LegacySurfaceTag, AeroBlockSaveForm.LegacyGearTag, AeroBlockSaveForm.LegacyBrakeTag, AeroBlockSaveForm.LegacyThrustTag };
        for (int i = 0; i < tags.Length; i++)
            for (int j = 0; j < tags.Length; j++)
                if (i != j) Check(!tags[i].StartsWith(tags[j]), $"tag '{tags[i]}' starts with '{tags[j]}'");
    }

    // ── 2. the old lines (what saves before 2026-10-15 hold): read, then migrated into the new keys ──

    // (the removed writers, word for word: AeroSurfaceSaved.Text and the components' SaveLine)
    static string P(Vector3D p) => string.Join(" ", p.X.ToString("R", C), p.Y.ToString("R", C), p.Z.ToString("R", C));
    static string OldSurface(Vector3D p, bool pi, bool y, bool r, float max, float trim) => "surface " + P(p) + " " + (pi ? "1" : "0") + " " + (y ? "1" : "0") + " " + (r ? "1" : "0") + " " + max.ToString("R", C) + " " + trim.ToString("R", C);
    static string OldGear(Vector3D p, bool down) => "gear " + P(p) + " " + (down ? "1" : "0");
    static string OldBrake(Vector3D p, float max) => "airbrake " + P(p) + " " + max.ToString("R", C);
    static string OldThrust(Vector3D p, float f) => "thrust " + P(p) + " " + f.ToString("R", C);

    static void Legacy()
    {
        var a = new Vector3D(1.5, -2.25, 3e7); var b = new Vector3D(-1234567.875, 0.125, 6.02e6); var c = new Vector3D(0, 0, 0);
        string text = "Drag.Scale=0.8\nStructure.Rate = 2"
            + "\n" + OldSurface(a, true, false, true, 20f, -3.5f)
            + "\n" + OldSurface(b, false, true, false, 1f / 3f, 35f)
            + "\n" + OldGear(a, false) + "\n" + OldGear(c, true)
            + "\n" + OldBrake(b, 12.5f)
            + "\n" + OldThrust(c, 0.25f) + "\n" + OldThrust(a, 3f)
            + "\nsurface 1 2 3 1 1"            // (short: skipped)
            + "\nthrust 1 2 3 NaN"             // (not finite: skipped)
            + "\ngear x 2 3 1"                 // (bad position: skipped)
            + "\n  airbrake 4 5 6 7  \r";      // (trimmed: read)
        var s = new List<(Vector3D at, bool p, bool y, bool r, float max, float trim, double since)>();
        var g = new List<(Vector3D at, bool down, double since)>();
        var br = new List<(Vector3D at, float max, double since)>();
        var th = new List<(Vector3D at, float frac, double since)>();
        int n = AeroBlockSaveForm.ParseLegacy(text, s, g, br, th);
        Check(n == 8 && s.Count == 2 && g.Count == 2 && br.Count == 2 && th.Count == 2, $"legacy: read {n} lines (s {s.Count} g {g.Count} b {br.Count} t {th.Count}), want 8 (2 each)");
        Check(s.Count == 2 && s[0].at == a && s[0].p && !s[0].y && s[0].r && s[0].max == 20f && s[0].trim == -3.5f && s[0].since < 0, "legacy surface 1");
        Check(s.Count == 2 && s[1].at == b && !s[1].p && s[1].y && !s[1].r && Same(s[1].max, 1f / 3f) && s[1].trim == 35f, "legacy surface 2 (positions and floats bit-exact)");
        Check(g.Count == 2 && g[0].at == a && !g[0].down && g[1].at == c && g[1].down, "legacy gears");
        Check(br.Count == 2 && br[0].at == b && br[0].max == 12.5f && br[1].at == new Vector3D(4, 5, 6) && br[1].max == 7f, "legacy airbrakes (the padded line too)");
        Check(th.Count == 2 && th[0].frac == 0.25f && th[1].frac == 1f, "legacy thrusts (3 clamped to 1)");
        Check(AeroBlockSaveForm.ParseLegacy(null, s, g, br, th) == 0 && AeroBlockSaveForm.ParseLegacy("Drag.Scale=1", s, g, br, th) == 0, "legacy: no lines");
        // a new save's world text (no block lines) reads none - nothing to claim
        Check(AeroBlockSaveForm.ParseLegacy("Drag.Scale=0.8\nAero.GroundEffect=0", s, g, br, th) == 0, "legacy: the settings text alone");

        // migration: a block with no data of its own takes the old line (AeroSurfaceSaved.ApplyPending), and its first save
        // writes the new key, which reads back to the same state - the old line is then gone for good
        var s0 = s[0];
        string sk = AeroBlockSaveForm.Surface(s0.p, s0.y, s0.r, s0.max, s0.trim);
        Check(AeroBlockSaveForm.TryReadSurface(sk, out bool p2, out bool y2, out bool r2, out float m2, out float t2) && p2 == s0.p && y2 == s0.y && r2 == s0.r && m2 == s0.max && t2 == s0.trim, "migrate surface: old line -> block -> key");
        string gk = AeroBlockSaveForm.Gear(g[0].down, true);   // (airshock was never in the old lines: the block's default, on)
        Check(AeroBlockSaveForm.TryReadGear(gk, out bool d2, out bool sh2) && d2 == g[0].down && sh2, "migrate gear: old line -> block -> key (airshock default on)");
        Check(AeroBlockSaveForm.TryReadAirbrake(AeroBlockSaveForm.Airbrake(br[0].max), out float b2) && b2 == br[0].max, "migrate airbrake");
        Check(AeroBlockSaveForm.TryReadThrust(AeroBlockSaveForm.Thrust(th[0].frac), out float f2) && f2 == th[0].frac, "migrate thrust");
    }

    // ── 3. through the ENGINE's serializer, as a world save writes a block's slot: binary + Brotli (EntityContainerSerializer),
    //       polymorphic (EntityObjectBuilder.ObjectBuilders is object-typed), with NO mod assembly in the type context ──
    static void Engine()
    {
        if (!File.Exists(Path.Combine(Game2, "Game2.Simulation.dll"))) { Console.WriteLine("SKIP  engine round trip: no SE2 install at " + Game2); return; }
        Keen.VRage.Library.Memory.BumpAllocator.Instance.Initialize();
        // (only the game: the type table can name nothing of ours - what a world sees with the mod removed)
        Singleton<MetadataManager>.Instance.PushContext(new[] { Assembly.Load("SpaceEngineers2") });

        string[] keys = { AeroBlockSaveForm.Surface(true, false, true, 17.5f, -4.25f), AeroBlockSaveForm.Gear(false, false),
                          AeroBlockSaveForm.Airbrake(22f), AeroBlockSaveForm.Thrust(0.8f) };
        foreach (string key in keys)
        {
            // (the value is the block itself in game; offline there is no Entity, so null - the read is what is checked)
            var ob = new EntityNameSessionComponentObjectBuilder { NamedEntities = new Dictionary<string, Entity> { [key] = null } };
            foreach (var fmt in new[] { SerializerFormat.Binary, SerializerFormat.Json })
            {
                var ms = new MemoryStream();
                var services = ServiceLocatorContext.Builder.Create(Array.Empty<object>()).Add(new BinarySerializationParameters { MainChunkCompression = Keen.VRage.Library.Serialization.Binary.Archive.CompressionType.Brotli });
                using (var ctx = new SerializationContext(ms, "save", services)) SerializationHelper.Serialize<object>(ctx, ob, fmt);
                byte[] bytes = ms.ToArray();
                if (fmt == SerializerFormat.Json)
                {
                    string json = Encoding.UTF8.GetString(bytes);
                    if (Environment.GetEnvironmentVariable("BLOCKSAVE_SHOW") == "1") Console.WriteLine(json);
                    Check(json.Contains("\"Game2:Keen.Game2.Simulation.GameSystems.EntityNameSessionComponentObjectBuilder\""), $"json names the engine builder ({json.Length} chars)");
                    Check(!json.Contains("AeroMod") && !json.Contains("BlockSaveTests") && !json.Contains("Aerodynamics"), "json names nothing of the mod");
                    int bs = json.IndexOf("\"$Bundles\""), be = bs < 0 ? -1 : json.IndexOf('}', bs);
                    string bundles = bs < 0 || be < 0 ? "" : json.Substring(bs, be - bs);
                    foreach (var line in bundles.Split('\n'))
                    {
                        var t = line.Trim();
                        if (t.StartsWith("\"") && !t.StartsWith("\"$Bundles"))
                            Check(t.StartsWith("\"Game2\"") || t.StartsWith("\"VRage\"") || t.StartsWith("\"System."), $"bundle {t}: only the game's and the runtime's");
                    }
                }
                // read back as a world load does: the deferred-error contexts (EntityContainerDeserializer.TryReadFile)
                object back;
                var read = DeferredErrorHandlingSerializationContext.AddContexts(ServiceLocatorContext.Builder.Create(Array.Empty<object>()));
                try
                {
                    using var rctx = new SerializationContext(new MemoryStream(bytes), "load", read);
                    back = SerializationHelper.Deserialize<object>(rctx, fmt);
                }
                catch (Exception e) { back = null; Console.WriteLine($"      {fmt} read threw {e.GetBaseException().Message}"); }
                var eob = back as EntityNameSessionComponentObjectBuilder;
                Check(eob != null && eob.NamedEntities != null && eob.NamedEntities.Count == 1 && eob.NamedEntities.ContainsKey(key), $"{fmt} ({bytes.Length} bytes): the engine builder reads back whole, mod absent: '{key}'");
                Check(eob != null && AeroBlockSaveForm.Find(eob.NamedEntities, key.Substring(0, key.IndexOf(':') + 1)) == key, $"{fmt}: the mod finds its key again");
            }
        }
    }

    // ── 4. the harness's set requests and dump tokens (Dev/AeroBlockSaveDevForm.cs: test_blocksave.sh) ──
    static void DevForm()
    {
        bool P(string t, out AeroBlockSaveDevForm.SetRequest r) => AeroBlockSaveDevForm.TryParseSet(t, out r, out _);
        Check(P("SSTO|thrust|3|0.73", out var r) && r.Grid == "SSTO" && r.Kind == AeroBlockSaveDevForm.Kind.Thrust && !r.ByCell && r.Index == 3 && Same(r.Value, 0.73f), "set: thrust by index");
        Check(P("My_Ship|surface-trim|-4,2,10|-7.5", out r) && r.Grid == "My_Ship" && r.Kind == AeroBlockSaveDevForm.Kind.SurfaceTrim && r.ByCell && r.X == -4 && r.Y == 2 && r.Z == 10 && Same(r.Value, -7.5f), "set: surface trim by cell");
        Check(P("SSTO|surface-max|0|27", out r) && r.Kind == AeroBlockSaveDevForm.Kind.SurfaceMax && Same(r.Value, 27f), "set: surface max");
        Check(P("SSTO|airbrake-max|1|12.5", out r) && r.Kind == AeroBlockSaveDevForm.Kind.AirbrakeMax && r.Index == 1, "set: airbrake max");
        foreach (var (v, down) in new[] { ("up", false), ("down", true), ("0", false), ("1", true), ("UP", false) })
            Check(P("SSTO|gear|0|" + v, out r) && r.Kind == AeroBlockSaveDevForm.Kind.Gear && r.Flag == down, $"set: gear {v}");
        foreach (var (v, on) in new[] { ("off", false), ("on", true), ("0", false), ("true", true) })
            Check(P("SSTO|airshock|0|" + v, out r) && r.Kind == AeroBlockSaveDevForm.Kind.Airshock && r.Flag == on, $"set: airshock {v}");
        Check(P(" SSTO | THRUST | 0 | 1 ", out r) && r.Grid == "SSTO" && r.Kind == AeroBlockSaveDevForm.Kind.Thrust, "set: trims and case");
        // (refused, with a reason)
        foreach (var bad in new[] { null, "", "SSTO", "SSTO|thrust|0", "SSTO|thrust|0|0.5|x", "|thrust|0|0.5", "SSTO|wing|0|1", "SSTO|thrust|-1|0.5",
                                    "SSTO|thrust|a|0.5", "SSTO|thrust|1,2|0.5", "SSTO|thrust|1,2,x|0.5", "SSTO|thrust|0|1.5", "SSTO|thrust|0|-0.1",
                                    "SSTO|thrust|0|NaN", "SSTO|surface-trim|0|Infinity", "SSTO|gear|0|sideways", "SSTO|airshock|0|maybe", "SSTO|surface-max|0|1,5" })
            Check(!AeroBlockSaveDevForm.TryParseSet(bad, out _, out string why) && !string.IsNullOrEmpty(why), $"set refused: '{bad}'");
        Check(AeroBlockSaveDevForm.BlockKind(AeroBlockSaveDevForm.Kind.Airshock) == "gear" && AeroBlockSaveDevForm.BlockKind(AeroBlockSaveDevForm.Kind.SurfaceMax) == "surface"
              && AeroBlockSaveDevForm.BlockKind(AeroBlockSaveDevForm.Kind.AirbrakeMax) == "airbrake" && AeroBlockSaveDevForm.BlockKind(AeroBlockSaveDevForm.Kind.Thrust) == "thrust", "set: block kinds");
        Check(AeroBlockSaveDevForm.NameMatches("My Ship", "My_Ship") && AeroBlockSaveDevForm.NameMatches("SSTO", "SSTO") && !AeroBlockSaveDevForm.NameMatches("SSTO 2", "SSTO")
              && !AeroBlockSaveDevForm.NameMatches(null, "SSTO"), "grid names as findgrids prints them");
        Check(AeroBlockSaveDevForm.CompareCells(0, 5, 5, 1, 0, 0) < 0 && AeroBlockSaveDevForm.CompareCells(1, 0, 9, 1, 1, 0) < 0 && AeroBlockSaveDevForm.CompareCells(1, 1, 2, 1, 1, 1) > 0
              && AeroBlockSaveDevForm.CompareCells(-3, 0, 0, -3, 0, 0) == 0, "cell order x, y, z");
        // (dump tokens: no spaces - the script splits the answer on them - and every value read back)
        string s = AeroBlockSaveDevForm.SurfaceToken(-4, 2, 10, 27f, -7.5f, true, false, true, true);
        Check(s == "surface@-4,2,10=max:27;trim:-7.5;p:1;y:0;r:1;rb:1", $"surface token '{s}'");
        string g = AeroBlockSaveDevForm.GearToken(1, -2, 3, false, false, true);
        Check(g == "gear@1,-2,3=down:0;shock:0;rb:1", $"gear token '{g}'");
        string a = AeroBlockSaveDevForm.AirbrakeToken(0, 0, 0, 12.5f, false), t = AeroBlockSaveDevForm.ThrustToken(5, 6, 7, 0.73f, true);
        Check(a == "airbrake@0,0,0=max:12.5;rb:0" && t == "thrust@5,6,7=f:0.73;rb:1", $"airbrake / thrust tokens '{a}' '{t}'");
        Check(AeroBlockSaveDevForm.TokenValue(s, "trim") == "-7.5" && AeroBlockSaveDevForm.TokenValue(g, "down") == "0" && AeroBlockSaveDevForm.TokenValue(t, "f") == "0.73"
              && AeroBlockSaveDevForm.TokenValue(t, "max") == null && AeroBlockSaveDevForm.TokenValue("nonsense", "f") == null, "token values");
        foreach (float f in Floats)
        {
            string k = AeroBlockSaveDevForm.ThrustToken(0, 0, 0, f, false);
            Check(!k.Contains(' ') && float.TryParse(AeroBlockSaveDevForm.TokenValue(k, "f"), NumberStyles.Float, C, out float back) && Same(back, f), $"thrust token round trip '{k}'");
        }
        var old = System.Threading.Thread.CurrentThread.CurrentCulture;
        System.Threading.Thread.CurrentThread.CurrentCulture = new CultureInfo("de-DE");
        Check(AeroBlockSaveDevForm.TryParseSet("SSTO|thrust|0|0.73", out r, out _) && Same(r.Value, 0.73f) && AeroBlockSaveDevForm.ThrustToken(0, 0, 0, 0.73f, false).EndsWith("f:0.73;rb:0"),
              "invariant culture under a comma locale");
        System.Threading.Thread.CurrentThread.CurrentCulture = old;
    }
}
