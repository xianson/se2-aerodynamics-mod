using System.Reflection;
using AeroMod;

// THE HARNESS'S PLUG-IN POINT. Scenarios live anywhere in this project (a new file - Scenarios/*.cs, say - never this one): a class
// implementing IScenarioPack is found by reflection at start and asked to Register its scenarios; each is a name, fast (in the
// gate, tools/test_fast.sh) or full (the "full" sweep), and an Action that flies Runs and calls Sim.Check / Sim.Note. A scenario
// builds its ship (Ships.*, or its own SimShip), its terrain (SimTerrain.*), its moving references (SimTarget.*), an external goal
// source for the autopilot (IApGoalSource - the core's hook for navigation legs, formation slots, landing pads) and its own
// per-run metrics (Run.Each, Run.Metrics).

/// <summary>A set of scenarios defined outside the harness's own files.</summary>
public interface IScenarioPack { void Register(ScenarioRegistry r); }

/// <summary>Where scenarios are registered (names unique; registration order is run order).</summary>
public sealed class ScenarioRegistry
{
    internal readonly List<(string name, bool fast, Action run)> All = new();
    public void Add(string name, bool fast, Action run)
    {
        if (All.Any(s => s.name == name)) throw new InvalidOperationException("scenario registered twice: " + name);
        All.Add((name, fast, run));
    }
    /// <summary>Every IScenarioPack in this assembly, asked in name order.</summary>
    internal void Discover()
    {
        foreach (var t in Assembly.GetExecutingAssembly().GetTypes().Where(t => typeof(IScenarioPack).IsAssignableFrom(t) && !t.IsInterface && !t.IsAbstract).OrderBy(t => t.FullName))
            ((IScenarioPack)Activator.CreateInstance(t)).Register(this);
    }
}

/// <summary>The checks and notes a scenario reports through (PASS/FAIL count toward the exit code).</summary>
public static class Sim
{
    public static void Check(bool ok, string what) => Program.CheckPublic(ok, what);
    public static void Note(string what) => Program.NotePublic(what);
    /// <summary>A plane scenario without its polar: skipped, said aloud (Tests/data is local - the dumped ships are not committed;
    /// make it: dotnet run --project Tests/AeroBench -c Release -- appolar NAME).</summary>
    public static void NoPolar(string name) { Program.Skipped++; Console.WriteLine($"skip {name}: no force polar (Tests/data/polars - dotnet run --project Tests/AeroBench -c Release -- appolar {name})"); }
}

/// <summary>
/// A MOVING REFERENCE in the sim (a formation leader, a moving landing pad, a ship to follow): a point flying a scripted path -
/// straight, a constant turn, a weave, accelerating - stepped with the plant each tick. Run.Rel gives the ship's state relative to it.
/// </summary>
public sealed class SimTarget
{
    public string Name = "ref"; public Vector3D P; public Vector3 V; public Vector3 A;
    /// <summary>The acceleration it flies at time t (world m/s2) - the script.</summary>
    public Func<float, SimTarget, Vector3> Script = (t, s) => Vector3.Zero;
    public void Step(float t, float dt) { A = Script(t, this); V += A * dt; P += (Vector3D)(V * dt); }

    public static SimTarget Straight(Vector3D p, Vector3 v) => new() { Name = "straight", P = p, V = v };
    /// <summary>A level turn at 'rate' rad/s about the vertical.</summary>
    public static SimTarget Turn(Vector3D p, Vector3 v, float rate) => new() { Name = $"turn {rate:0.00} rad/s", P = p, V = v, Script = (t, s) => Vector3.Cross(Vector3.UnitY * rate, s.V) };
    /// <summary>A weave across its path: 'amp' m/s2 sideways, period 'period' s.</summary>
    public static SimTarget Weave(Vector3D p, Vector3 v, float amp, float period) => new()
    {
        Name = $"weave {amp:0} m/s2 / {period:0} s", P = p, V = v,
        Script = (t, s) => { var side = Vector3.Cross(Vector3.UnitY, s.V); return side.LengthSquared() > 1e-6f ? Vector3.Normalize(side) * amp * MathF.Sin(2f * MathF.PI * t / period) : Vector3.Zero; },
    };
    /// <summary>Accelerating along its path at 'acc' m/s2 from t0 for 'len' s.</summary>
    public static SimTarget Accelerate(Vector3D p, Vector3 v, float acc, float t0, float len) => new()
    {
        Name = $"accelerate {acc:0.0} m/s2", P = p, V = v,
        Script = (t, s) => t >= t0 && t <= t0 + len && s.V.LengthSquared() > 1e-6f ? Vector3.Normalize(s.V) * acc : Vector3.Zero,
    };
}

/// <summary>The ship relative to a reference: the vector to it, its velocity relative to the ship, the range, the closing speed,
/// and the line of sight's rate (rad/s).</summary>
public struct RelState { public Vector3 R, Vr; public float Range, Closing, LosRate; }

/// <summary>An external goal source flying the autopilot's hover to a moving reference (a slot offset from it): Track kind.</summary>
public sealed class FollowSource : IApGoalSource
{
    public SimTarget Ref; public Vector3 Offset;
    public bool Step(in ApSense s, ref ApExternal g)
    {
        g.Kind = ApExternalKind.Track; g.Position = Ref.P + (Vector3D)Offset; g.Velocity = Ref.V;
        return true;
    }
}
