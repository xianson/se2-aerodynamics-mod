using AeroMod;

/// <summary>
/// SEVERAL SHIPS IN ONE RUN (a leader and its wingmen, a ship and a moving pad that is itself a ship, any number): each member is
/// a whole Run - its own plant, autopilot, wind, timeline, hooks, metrics and CSV trace ("flight name - member name".csv) - and
/// each flies whatever it is given: a pilot mode (Pilot: Flight Hold / Navigate / Engage with any behaviour, the panel's values),
/// any external goal source (Source: an IApGoalSource of the scenario's own), an Autopilot block (Run.P.AddBlock), or nothing
/// (unattended, the plant's own laws). Fly steps every member one tick in turn (the order added; a member reads the others'
/// state as the tick before left it - the game's one-frame view of another grid), then the flight's own hooks (Each: metrics
/// across members - Min / Max / Rms / Mean into Metrics) and the flight's timeline (At). Another member as a target: Target(member)
/// (an IApTargetSource of its real flown state; the pilot's track estimates its motion from the positions alone).
/// </summary>
sealed class Flight
{
    public readonly string Name;
    public readonly List<Run> Ships = new();
    public readonly Dictionary<string, double> Metrics = new();
    readonly List<Action<Flight>> _hooks = new();
    readonly List<(float t, Action<Flight> act)> _events = new();
    readonly Dictionary<string, (double sum, long n)> _acc = new();
    public int Ticks;

    public Flight(string name) { Name = name; }

    /// <summary>The flight's clock (s): its first member's.</summary>
    public float Time => Ships.Count > 0 ? Ships[0].P.Time : 0f;

    /// <summary>A member: a new Run on this ship, named "flight - member".</summary>
    public Run Add(string member, SimShip ship) { var r = new Run($"{Name} - {member}", ship); Ships.Add(r); return r; }
    /// <summary>A member made elsewhere (FlightPack.PlaneAt's, say): flown with the others from now.</summary>
    public Run Add(Run r) { Ships.Add(r); return r; }

    /// <summary>A member flown by the flight pilot in a mode with this panel (checked: begun and taken by the flight director).</summary>
    public static ApPilot Pilot(Run r, ApPanel cfg, ApMode mode, IApTargetSource targets = null, List<ApRoute.Point> route = null) => FlightPack.Engage(r, cfg, mode, targets, route);

    /// <summary>A member flown by any external goal source (false: refused - the reason in r.P.Fd.Reason).</summary>
    public static bool Source(Run r, IApGoalSource src, ApGoal goals = ApGoal.External)
    {
        var s = r.P.SenseNow();
        if (r.P.Fd.Engaged) r.P.Fd.Disengage(ApReason.None);
        return r.P.Fd.Engage(goals, in s, out _, src);
    }

    /// <summary>Another member as a pilot's target (its flown position and velocity).</summary>
    public static ShipTargetSource Target(Run member, float radius = 8f) => new() { Ship = member, Radius = radius };

    public Flight Each(Action<Flight> hook) { _hooks.Add(hook); return this; }
    public Flight At(float t, Action<Flight> act) { _events.Add((t, act)); return this; }
    public void Min(string name, double v) { Metrics[name] = Metrics.TryGetValue(name, out var o) ? Math.Min(o, v) : v; }
    public void Max(string name, double v) { Metrics[name] = Metrics.TryGetValue(name, out var o) ? Math.Max(o, v) : v; }
    public void Rms(string name, double v) { var a = _acc.TryGetValue(name, out var o) ? o : default; a.sum += v * v; a.n++; _acc[name] = a; Metrics[name] = Math.Sqrt(a.sum / a.n); }
    public void Mean(string name, double v) { var a = _acc.TryGetValue("mean:" + name, out var o) ? o : default; a.sum += v; a.n++; _acc["mean:" + name] = a; Metrics[name] = a.sum / a.n; }
    public double Metric(string name) => Metrics.TryGetValue(name, out var v) ? v : double.NaN;

    /// <summary>Every member one tick in turn, then the flight's events due and its hooks - for 'seconds'.</summary>
    public void Fly(float seconds)
    {
        int steps = (int)MathF.Round(seconds * 60f);
        for (int k = 0; k < steps; k++)
        {
            for (int e = _events.Count - 1; e >= 0; e--) if (Time + 1e-4f >= _events[e].t) { var a = _events[e].act; _events.RemoveAt(e); a(this); }
            foreach (var r in Ships) r.Step();
            Ticks++;
            foreach (var h in _hooks) h(this);
        }
        foreach (var r in Ships) r.FlushTrace();
    }

    /// <summary>Every member's ledger checked (Program.Ledger: every force and torque from a real actuator within its limits) and
    /// its trace closed.</summary>
    public void Ledger(bool crashOk = false) { foreach (var r in Ships) Program.LedgerPublic(r, crashOk); }
}

/// <summary>A flight member as a pilot's target source: its plant's real position and velocity (the "observed" ship - the pilot's
/// ApTargetTrack estimates its acceleration and turn from these alone).</summary>
sealed class ShipTargetSource : IApTargetSource
{
    public Run Ship; public float Radius = 8f; public bool On = true; public long Id = 7;
    public bool Get(in ApSense s, out ApTargetFix f)
    {
        f = default;
        if (!On || Ship == null) return false;
        f.Valid = true; f.Pos = Ship.P.P; f.Vel = Ship.P.V; f.Name = Ship.P.S.Name; f.Id = Id; f.Radius = Radius;
        return true;
    }
}

/// <summary>An external goal source holding a slot on another flight member (Track kind: its position + Offset, its velocity) -
/// the hover's / the thrusters' moving place; FollowSource's for a ship.</summary>
sealed class FollowShip : IApGoalSource
{
    public Run Ship; public Vector3 Offset;
    public bool Step(in ApSense s, ref ApExternal g)
    {
        g.Kind = ApExternalKind.Track; g.Position = Ship.P.P + (Vector3D)Offset; g.Velocity = Ship.P.V;
        return true;
    }
}
