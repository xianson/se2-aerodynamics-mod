#pragma warning disable
using AeroMod;

/// <summary>GearTests: the retractable gear's sequence and kinematics, offline (exit 0 = all pass).</summary>
static class Program
{
    static int _checks, _fails;
    static void Check(bool ok, string what) { _checks++; if (!ok) { _fails++; if (_fails <= 20) Console.WriteLine("FAIL " + what); } }
    static bool Near(Vector3 a, Vector3 b, float tol = 1e-3f) => (a - b).Length() <= tol;
    static bool Near(Quaternion a, Quaternion b, float tol = 1e-3f) => MathF.Abs(MathF.Abs(Quaternion.Dot(Quaternion.Normalize(a), Quaternion.Normalize(b))) - 1f) <= tol;

    /// <summary>Run a sequence for seconds at 60 Hz with the wheel tracked as the caller would; counts spawns / removals.</summary>
    static (int spawns, int removes, bool wheel) Run(GearSequence s, float seconds, bool wheel, Func<bool> room, Func<bool> weight, Action<float, bool> each = null)
    {
        int sp = 0, rm = 0;
        for (int i = 0; i < (int)(seconds * 60); i++)
        {
            int act = s.Step(1f / 60f, wheel, room, weight);
            if (act > 0) { Check(!wheel, "spawned a wheel that was there"); wheel = true; sp++; }
            if (act < 0) { Check(wheel, "removed a wheel that was not there"); wheel = false; rm++; }
            Check(wheel == s.WheelWanted || s.Held.Length > 0, $"wheel {wheel} but wanted {s.WheelWanted} at {s.Progress:F3} (held: {s.Held})");
            each?.Invoke(s.Progress, wheel);
        }
        return (sp, rm, wheel);
    }

    static int Main()
    {
        Func<bool> yes = () => true, no = () => false;
        // 1. extend from up, room for the wheel: one spawn, at the leg-down mark; doors open, then shut again
        {
            var s = new GearSequence(false); s.Command(true);
            float maxDoors = 0f, lastFold = 0f; bool monotonic = true;
            var r = Run(s, 6f, false, yes, no, (p, w) => { maxDoors = MathF.Max(maxDoors, s.Doors()); if (s.Fold() < lastFold - 1e-6f) monotonic = false; lastFold = s.Fold(); });
            Check(r.spawns == 1 && r.removes == 0 && r.wheel, $"extend: {r.spawns} spawns {r.removes} removals");
            Check(s.Progress == 1f && s.Arrived, $"extend: ended at {s.Progress}");
            Check(maxDoors > 0.999f && s.Doors() < 1e-4f, $"extend: doors max {maxDoors}, end {s.Doors()}");
            Check(monotonic && s.Fold() == 1f, "extend: the leg did not fold out steadily");
        }
        // 2. retract, no weight: one removal when leaving the leg-down mark; up, doors shut
        {
            var s = new GearSequence(true); s.Command(false);
            var r = Run(s, 6f, true, yes, no);
            Check(r.removes == 1 && r.spawns == 0 && !r.wheel, $"retract: {r.spawns} spawns {r.removes} removals");
            Check(s.Progress == 0f && s.Doors() < 1e-4f && s.Fold() == 0f, $"retract: ended at {s.Progress}, doors {s.Doors()}");
        }
        // 3. extend with no room: it waits short of the mark, never spawns; room comes - it spawns and finishes
        {
            var s = new GearSequence(false); s.Command(true);
            var r = Run(s, 6f, false, no, no);
            Check(r.spawns == 0 && s.Progress < GearSequence.LegDown && s.Held.Length > 0, $"no room: spawned {r.spawns}, at {s.Progress}, held: {s.Held}");
            r = Run(s, 3f, false, yes, no);
            Check(r.spawns == 1 && s.Progress == 1f, $"room came: {r.spawns} spawns, at {s.Progress}");
        }
        // 4. retract with weight on the wheel: it stays down (wheel kept); weight gone - it retracts
        {
            var s = new GearSequence(true); s.Command(false);
            var r = Run(s, 6f, true, yes, yes);
            Check(r.removes == 0 && r.wheel && s.Progress >= GearSequence.LegDown && s.Held.Length > 0, $"weight: removed {r.removes}, at {s.Progress}");
            r = Run(s, 6f, true, yes, no);
            Check(r.removes == 1 && s.Progress == 0f, $"weight gone: {r.removes} removals, at {s.Progress}");
        }
        // 5. reversing mid-way, many times: spawns and removals pair up, the wheel only from the mark on
        {
            var s = new GearSequence(false); bool wheel = false; int sp = 0, rm = 0; var rnd = new Random(3);
            for (int k = 0; k < 400; k++)
            {
                s.Command(rnd.Next(2) == 0);
                var r = Run(s, (float)rnd.NextDouble() * 1.5f, wheel, yes, no);
                wheel = r.wheel; sp += r.spawns; rm += r.removes;
            }
            Check(sp - rm == (wheel ? 1 : 0) && sp > 10, $"reversals: {sp} spawns, {rm} removals, wheel {wheel}");
        }
        // 6. down with the wheel lost (a reload): spawned again
        {
            var s = new GearSequence(true);
            var r = Run(s, 0.1f, false, yes, no);
            Check(r.spawns == 1 && r.wheel, "down without its wheel: not spawned again");
        }
        // 7. doors stay open when down if told
        {
            var s = new GearSequence(false) { CloseDoorsWhenDown = false }; s.Command(true); Run(s, 6f, false, yes, no);
            Check(s.Doors() == 1f, $"doors open when down: {s.Doors()}");
        }

        // kinematics: a 2.5 m gear folding sideways - hinge along Z at the top of the well, hub 4.25 m below, stowed by -90 deg
        var L = new GearLayout
        {
            HingePoint = new Vector3(0, 0, 0), HingeAxis = new Vector3(0, 0, 1), StowAngle = -MathF.PI / 2f,
            DeployedHub = new Vector3(0, -4.25f, 0),
            ActuatorBase = new Vector3(1.5f, 0, 0), ActuatorLegEnd = new Vector3(0, -2f, 0),
            Doors = new[] { new GearLayout.DoorLayout(new Vector3(-1.5f, -2.5f, 0), new Vector3(0, 0, 1), -MathF.PI / 2f),
                            new GearLayout.DoorLayout(new Vector3(1.5f, -2.5f, 0), new Vector3(0, 0, 1), MathF.PI / 2f) },
        };
        Check(Near(L.Hub(1f), L.DeployedHub), $"hub down {L.Hub(1f)} is not the deployed hub");
        Check(Near(L.Hub(0f), new Vector3(-4.25f, 0, 0)), $"hub stowed {L.Hub(0f)}, want (-4.25, 0, 0) - flat in the well");
        Check(Near(L.Hub(1f, 0.5f), new Vector3(0, -3.75f, 0)), $"hub loaded 0.5 m {L.Hub(1f, 0.5f)}");
        for (int i = 0; i <= 100; i++)
        {
            float f = i / 100f;
            Check(MathF.Abs((L.Hub(f) - L.HingePoint).Length() - L.LegLength) < 1e-3f, $"leg length changes at fold {f}");
            var leg = L.OnLeg(new GearPose(new Vector3(0, -1, 0), Quaternion.Identity), f);
            Check(MathF.Abs((leg.Position - L.HingePoint).Length() - 1f) < 1e-3f, $"a leg part leaves the leg at {f}");
            var act = L.Actuator(new GearPose(L.ActuatorBase, Quaternion.Identity), new GearPose(L.ActuatorLegEnd, Quaternion.Identity), f);
            Vector3 legEnd = L.HingePoint + Vector3.Transform(L.ActuatorLegEnd - L.HingePoint, L.LegTurn(f));
            Check(Near(act.rod.Position, legEnd) && Near(act.cylinder.Position, L.ActuatorBase), $"actuator ends off at {f}");
            Check(MathF.Abs(act.length - (legEnd - L.ActuatorBase).Length()) < 1e-3f, $"actuator length at {f}");
        }
        var rest = new GearPose(new Vector3(0.3f, -4.25f, 0.2f), Quaternion.CreateFromAxisAngle(Vector3.UnitY, 0.4f));
        Check(Near(L.OnLeg(rest, 1f).Position, rest.Position) && Near(L.OnLeg(rest, 1f).Orientation, rest.Orientation), "a leg part moves with the gear down");
        Check(Near(L.OnRod(rest, 1f, 0f).Position, rest.Position), "a rod part moves with the gear down, unloaded");
        Check(Near(L.OnRod(rest, 1f, 0.4f).Position, rest.Position + new Vector3(0, 0.4f, 0)), "a rod part does not slide up by the compression");
        var a0 = L.Actuator(new GearPose(L.ActuatorBase, Quaternion.Identity), new GearPose(L.ActuatorLegEnd, Quaternion.Identity), 1f);
        Check(Near(a0.cylinder.Orientation, Quaternion.Identity) && Near(a0.rod.Orientation, Quaternion.Identity), "actuator turned with the gear down");
        var doorRest = new GearPose(new Vector3(-0.75f, -2.5f, 0), Quaternion.Identity);
        Check(Near(L.Door(0, doorRest, 0f).Position, doorRest.Position), "a shut door moved");
        Check(Near(L.Door(0, doorRest, 1f).Position, new Vector3(-1.5f, -3.25f, 0)), $"door 0 open at {L.Door(0, doorRest, 1f).Position}, want hanging down from its edge at (-1.5, -3.25, 0)");
        var rnd2 = new Random(5);
        for (int i = 0; i < 2000; i++)
        {
            var a = Vector3.Normalize(new Vector3((float)rnd2.NextDouble() - 0.5f, (float)rnd2.NextDouble() - 0.5f, (float)rnd2.NextDouble() - 0.5f));
            var b = i % 50 == 0 ? -a : Vector3.Normalize(new Vector3((float)rnd2.NextDouble() - 0.5f, (float)rnd2.NextDouble() - 0.5f, (float)rnd2.NextDouble() - 0.5f));
            Check(Near(Vector3.Transform(a, GearLayout.Between(a, b)), b, 2e-3f), $"Between {a} -> {b}");
        }
        // raked slide: the 2.5 m forward-folding blockout (retractable_gear.py), in VRage axes (Blender (x, y, z) -> (x, z, -y)):
        // hinge (0.95, 0.24, 2.50), the hub down (0.63, -3.11, 2.50), stowed (0.63, 0.24, -0.85) - leg 3.35 m, 45 deg rake
        {
            var K = new GearLayout
            {
                RakedSlide = true, HingePoint = new Vector3(0.95f, 0.24f, 2.50f), HingeAxis = Vector3.UnitX,
                DeployedHub = new Vector3(0.63f, -3.11f, 2.50f), RakeAxis = Vector3.Normalize(new Vector3(0, 3.35f, -3.35f)),
            };
            float full = 3.35f * MathF.Sqrt(2f);
            Check(Near(K.RakedHub(0f), K.DeployedHub) && Near(K.RakedHub(full), new Vector3(0.63f, 0.24f, -0.85f), 2e-3f), $"raked hub ends {K.RakedHub(full)}");
            Check(Near(K.RakedTurn(0f), Quaternion.Identity), "raked: the leg turned with the gear down");
            float a90 = 2f * MathF.Acos(MathF.Min(1f, MathF.Abs(K.RakedTurn(full).W))) * 180f / MathF.PI;
            Check(MathF.Abs(a90 - 90f) < 0.5f, $"raked: stowed leg turned {a90:F1} deg, want 90");
            var legRest = new GearPose(K.HingePoint, Quaternion.Identity);
            var rodRest = new GearPose(K.DeployedHub, Quaternion.Identity);
            float minLen = float.MaxValue;
            for (int i = 0; i <= 50; i++)
            {
                float s = full * i / 50f;
                var q = K.RakedTurn(s);
                // the turned leg points at the hub (in the turning plane), the rod sits on the hub, turned the same
                Vector3 legDir = Vector3.Transform(K.DeployedHub - K.HingePoint, q), toHub = K.RakedHub(s) - K.HingePoint;
                Vector3 pl = legDir - Vector3.UnitX * legDir.X, ph = toHub - Vector3.UnitX * toHub.X;
                Check(Vector3.Dot(Vector3.Normalize(pl), Vector3.Normalize(ph)) > 0.9999f, $"raked: the leg misses the hub at s {s:F2}");
                var rod = K.RakedRod(rodRest, s);
                Check(Near(rod.Position, K.RakedHub(s)) && Near(rod.Orientation, q), $"raked: the rod off the hub at s {s:F2}");
                Check(Near(K.RakedLeg(legRest, s).Position, K.HingePoint), "raked: the leg left its hinge");
                minLen = MathF.Min(minLen, ph.Length());
            }
            Check(MathF.Abs(minLen / 3.35f - MathF.Sqrt(0.5f)) < 0.01f, $"raked: shortest leg {minLen / 3.35f:F3} of its length, want 0.707 (mid-fold)");
        }

        // drag patches (AeroApi.SetDrag): q x CdA against the travel; a plate edge-on gives nothing, face-on all of it
        {
            var v = new Vector3(0, 0, -100f);   // (flying forward at 100 m/s: the air comes from ahead)
            var f = PatchDrag.Force(v, 1.225f, 2f, Vector3.Zero);
            Check(MathF.Abs(f.Z - 0.5f * 1.225f * 10000f * 2f) < 1f && MathF.Abs(f.X) + MathF.Abs(f.Y) < 1e-3f, $"patch drag {f}, want +12250 N along +Z");
            Check(PatchDrag.Force(v, 1.225f, 2f, new Vector3(1, 0, 0)).Length() < 1e-3f, "an edge-on plate drags");
            Check(MathF.Abs(PatchDrag.Force(v, 1.225f, 2f, new Vector3(0, 0, 1)).Z - f.Z) < 1f, "a face-on plate is not the full drag");
            Check(MathF.Abs(PatchDrag.Force(v, 1.225f, 2f, Vector3.Normalize(new Vector3(0, 1, 1))).Z - f.Z * MathF.Sqrt(0.5f)) < 1f, "a plate at 45 deg is not cos 45 of it");
            Check(PatchDrag.Force(Vector3.Zero, 1.225f, 2f, Vector3.Zero) == Vector3.Zero && PatchDrag.Force(v, 0f, 2f, Vector3.Zero) == Vector3.Zero, "drag at rest or in vacuum");
        }
        Console.WriteLine($"GearTests: {_checks - _fails}/{_checks} passed");
        return _fails == 0 ? 0 : 1;
    }
}
