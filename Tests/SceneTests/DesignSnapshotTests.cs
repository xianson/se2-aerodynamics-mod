// The design probe's snapshots (Scripts/Simulation/Design/DesignSnapshot.cs, compiled here as it is): the watch window the
// probe's asks push and the server grid's own step refreshes on, the overlay's box hand-over, the ship's frames (checked
// against WorldTransform's own rules, written out here with matrices) and its sums (mass, thrust along, thrust hold, gyros).
using AeroMod;

static class DesignSnapshotTests
{
    const long F = 1_000_000;   // (ticks per second for the tests)

    public static void Run(Action<bool, string> check)
    {
        Watch(check); Boxes(check); Frames(check); Sums(check);
    }

    static bool Near(Vector3 a, Vector3 b, float tol = 1e-3f) => (a - b).Length() <= tol;

    static void Watch(Action<bool, string> check)
    {
        var w = new DesignWatch();
        long t = 10 * F;
        check(!w.On(t) && !w.Due(t, F) && w.Fresh(t, F) == null, "watch: nobody asked - off, nothing due, no snapshot");
        w.Ask(t, F);
        check(w.On(t) && w.Due(t, F), "watch: an ask turns it on, and a snapshot is due (none yet)");
        check(w.Fresh(t, F) == null, "watch: the first ask after a quiet spell has no snapshot (the probe answers 'not ready')");
        check(w.Settling(t + F / 4, F) && !w.Settling(t + F, F), "watch: settling for SettleSeconds after the window opened");
        w.Snap = new DesignSnapshot { At = t + 1 };
        check(!w.Due(t + 1 + F / 8, F) && w.Due(t + 1 + F / 4, F), "watch: refreshed at most every RefreshSeconds");
        check(w.Fresh(t + F / 2, F) != null && w.Fresh(t + 1 + F + 1, F) == null, "watch: a snapshot is used up to FreshSeconds old");
        w.Ask(t + F, F);   // (inside the window: Since stays)
        check(!w.Settling(t + F, F), "watch: an ask inside the window does not reopen it");
        check(w.On(t + 2 * F) && !w.On(t + 3 * F + 1), "watch: on until WatchSeconds after the last ask, then off");
        check(!w.Due(t + 4 * F, F), "watch: off - nothing due though the snapshot is old");
        w.Ask(t + 5 * F, F);
        check(w.Settling(t + 5 * F, F), "watch: an ask after the window closed opens a new one (settling again)");
    }

    static void Boxes(Action<bool, string> check)
    {
        var w = new DesignWatch();
        check(!w.BoxesDue(out _), "boxes: none asked - the step reads nothing");
        check(w.BoxesFor(7) == null, "boxes: the overlay's first ask has none");
        check(w.BoxesDue(out long k) && k == 7, "boxes: the step sees the key asked");
        var list = new List<(Vector3I, Vector3I)> { (new Vector3I(0, 0, 0), new Vector3I(1, 1, 1)) };
        w.PublishBoxes(7, list);
        check(!w.BoxesDue(out _), "boxes: read once per key");
        var b = w.BoxesFor(7);
        check(b != null && ReferenceEquals(b.List, list), "boxes: the overlay gets what the step read, for its key");
        check(w.BoxesFor(8) == null && w.BoxesDue(out k) && k == 8, "boxes: a new shape's key asks again");
        w.PublishBoxes(8, new List<(Vector3I, Vector3I)>());
        w.ReleaseBoxes(b);   // (an older build letting go must not drop the newer boxes)
        check(w.BoxesFor(8) != null, "boxes: an old release leaves newer boxes");
        w.ReleaseBoxes(w.BoxesFor(8));
        check(System.Threading.Volatile.Read(ref w.BoxesPub) == null && !w.BoxesDue(out _), "boxes: released - nothing held, and no read again for a key built");
    }

    // WorldTransform's rules (VRage.Core): Transform(p) = Pos + rotate(p, q); TransformDirection = rotate(d, q);
    // TransformDirectionInv = rotate(d, Inverse(q)) - here as matrices, independently of the quaternion code under test.
    static Vector3 Rot(Vector3 v, Quaternion q) => Vector3.TransformNormal(v, Matrix.CreateFromQuaternion(q));
    static Vector3 RotInv(Vector3 v, Quaternion q) => Vector3.TransformNormal(v, Matrix.Transpose(Matrix.CreateFromQuaternion(q)));

    static DesignShip Ship(out DesignSnapshot root, out DesignSnapshot sub)
    {
        root = new DesignSnapshot { Pos = new Vector3D(1000, 50, -20), Orient = Quaternion.Normalize(Quaternion.CreateFromYawPitchRoll(0.3f, -0.2f, 0.1f)) };
        sub = new DesignSnapshot { Pos = new Vector3D(1004, 52, -25), Orient = Quaternion.Normalize(Quaternion.CreateFromYawPitchRoll(-1.1f, 0.6f, 0.4f)) };
        var s = new DesignShip();
        s.S.Add(root); s.S.Add(sub);
        return s;
    }

    static void Frames(Action<bool, string> check)
    {
        var s = Ship(out var root, out var sub);
        var d = Vector3.Normalize(new Vector3(0.2f, -0.5f, 0.8f)); var p = new Vector3(3f, -1f, 2f);
        check(s.ToRootDir(0, d) == d && s.ToMemberDir(0, d) == d && s.ToRootPoint(0, p) == p, "frames: the aimed grid's own figures pass as they are");
        check(Near(s.ToRootDir(1, d), RotInv(Rot(d, sub.Orient), root.Orient)), "frames: a member's direction into the aimed grid's axes (WorldTransform's rule)");
        check(Near(s.ToMemberDir(1, d), RotInv(Rot(d, root.Orient), sub.Orient)), "frames: the aimed grid's direction into a member's axes");
        check(Near(s.ToMemberDir(1, s.ToRootDir(1, d)), d), "frames: there and back is the same direction");
        var world = (Vector3)(sub.Pos - root.Pos) + Rot(p, sub.Orient);
        check(Near(s.ToRootPoint(1, p), RotInv(world, root.Orient)), "frames: a member's point into the aimed grid's axes (about its origin)");
    }

    static void Sums(Action<bool, string> check)
    {
        // ── mass ──
        var s = Ship(out var root, out var sub);
        root.HasMass = true; root.Mass = 1000f; root.Com = new Vector3(0, 0, 1);
        sub.HasMass = true; sub.Mass = 3000f; sub.Com = new Vector3(2, 0, 0);
        float m = s.Mass(out var com);
        var want = (root.Com * 1000f + s.ToRootPoint(1, sub.Com) * 3000f) / 4000f;
        check(m == 4000f && Near(com, want), "mass: the members' masses and their centre in the aimed grid's axes");
        sub.HasMass = false;
        check(s.Mass(out com) == 1000f && Near(com, root.Com), "mass: a member without mass data is left out");
        root.HasMass = false;
        check(s.Mass(out com) == 0f && com == Vector3.Zero, "mass: none - 0 and no centre");

        // ── thrust along (one grid) ──
        var one = new DesignSnapshot
        {
            ThrPos = new[] { new Vector3(0, 0, 5), new Vector3(0, 2, 5), new Vector3(1, 0, 0), new Vector3(0, 0, -5) },
            ThrDir = new[] { new Vector3(0, 0, -1), Vector3.Normalize(new Vector3(0, 1, -1)), new Vector3(0, -0.8660254f, -0.5f), new Vector3(0, 0, 1) },
            ThrSeaMax = new[] { 100f, 200f, 300f, 400f },
            ThrProfile = new ThrustProfile[] { null, new ThrustProfile("Half at Mach 1", new[] { (0.0, 1.0), (1.0, 0.5) }), null, null },
        };
        var ship1 = new DesignShip(); ship1.S.Add(one);
        float f0 = ship1.ThrustAlong(new Vector3(0, 0, -2), 0f, out var c0);
        float a1 = 0.70710677f, w0 = 100f + 200f * a1 + 300f * 0.5f;
        check(MathF.Abs(f0 - w0) < 1e-2f, $"thrust along: each pointing at least half that way, by how much ({f0:F2} vs {w0:F2})");
        var cw = (new Vector3(0, 0, 5) * 100f + new Vector3(0, 2, 5) * 200f * a1 + new Vector3(1, 0, 0) * 150f) / w0;
        check(Near(c0, cw), "thrust along: the centre is the thrust-weighted mean position");
        float f1 = ship1.ThrustAlong(new Vector3(0, 0, -1), 1f, out _);
        check(MathF.Abs(f1 - (100f + 100f * a1 + 150f)) < 1e-2f, "thrust along: a thruster's profile at the Mach (half at Mach 1)");
        check(ship1.ThrustAlong(new Vector3(1, 0, 0), 0f, out var cn) == 0f && cn == Vector3.Zero, "thrust along: nothing that way - 0, no centre");

        // ── thrust along (a ship: the member's thruster along the turned direction, its centre turned back) ──
        var s2 = Ship(out root, out sub);
        sub.ThrPos = new[] { new Vector3(0, 1, 0) }; sub.ThrDir = new[] { new Vector3(0, 0, -1) }; sub.ThrSeaMax = new[] { 500f }; sub.ThrProfile = new ThrustProfile[1];
        var dirRoot = s2.ToRootDir(1, new Vector3(0, 0, -1));
        float fs = s2.ThrustAlong(dirRoot, 0f, out var cs);
        check(MathF.Abs(fs - 500f) < 1e-2f && Near(cs, s2.ToRootPoint(1, new Vector3(0, 1, 0))), "thrust along: a subgrid's thruster, its direction and centre in the aimed grid's axes");

        // ── thrust hold: r x F about an axis, each sign apart ──
        var h = new DesignSnapshot
        {
            ThrPos = new[] { new Vector3(0, 0, 5), new Vector3(0, 0, -5) },
            ThrDir = new[] { new Vector3(1, 0, 0), new Vector3(1, 0, 0) },
            ThrSeaMax = new[] { 100f, 40f }, ThrProfile = new ThrustProfile[2],
        };
        var sh = new DesignShip(); sh.S.Add(h);
        sh.ThrustHold(Vector3.UnitY, Vector3.Zero, out float pos, out float neg);
        check(MathF.Abs(pos - 500f) < 1e-2f && MathF.Abs(neg - 200f) < 1e-2f, $"thrust hold: (0,0,5) x 100 X = +500 about Y, (0,0,-5) x 40 X = -200 ({pos:F1}, {neg:F1})");
        sh.ThrustHold(Vector3.UnitY, new Vector3(0, 0, 5), out pos, out neg);
        check(pos < 1e-2f && MathF.Abs(neg - 400f) < 1e-2f, "thrust hold: about the centre of mass given");
        // (a reversible thruster - ReversibleThrustComponent - holds and thrusts both ways)
        h.ThrRev = new[] { false, true };
        sh.ThrustHold(Vector3.UnitY, Vector3.Zero, out pos, out neg);
        check(MathF.Abs(pos - 700f) < 1e-2f && MathF.Abs(neg - 200f) < 1e-2f, $"thrust hold: a reversible thruster counted both ways ({pos:F1}, {neg:F1})");
        float back = h.ThrustAlong(new Vector3(-1, 0, 0), 0f, out var cb);
        check(MathF.Abs(back - 40f) < 1e-3f && (cb - new Vector3(0, 0, -5)).Length() < 1e-4f && MathF.Abs(h.ThrustAlong(new Vector3(1, 0, 0), 0f, out _) - 140f) < 1e-3f,
            $"thrust along: a reversible thruster's maximum either way ({back:F1} N back)");

        // ── gyros ──
        var g = Ship(out root, out sub);
        root.GyroMax = 2000f; sub.GyroMax = -1f;
        check(g.GyroMax() == 2000f, "gyros: unreadable (-1) left out");
        sub.GyroMax = 500f;
        check(g.GyroMax() == 2500f, "gyros: every member's summed");
    }
}
