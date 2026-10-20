using System.IO;
using AeroMod;

/// <summary>The wind: a steady part, (1 - cos) gusts and seeded turbulence (first-order filtered noise), world m/s.</summary>
sealed class Wind
{
    public Vector3 Steady;
    public readonly List<(float t0, float len, Vector3 peak)> Gusts = new();
    public float Turbulence, TurbTau = 2f; Vector3 _turb; readonly Random _rng;
    public Wind(int seed = 7) { _rng = new Random(seed); }
    public Vector3 At(float t, float dt)
    {
        Vector3 w = Steady;
        foreach (var (t0, len, peak) in Gusts) if (t >= t0 && t <= t0 + len) w += peak * 0.5f * (1f - MathF.Cos(2f * MathF.PI * (t - t0) / len));
        if (Turbulence > 0f)
        {
            float k = MathF.Sqrt(2f * dt / TurbTau);
            _turb += (-_turb * (dt / TurbTau)) + new Vector3(N(), N() * 0.3f, N()) * (Turbulence * k);
            w += _turb;
        }
        return w;
        float N() { double u1 = 1.0 - _rng.NextDouble(), u2 = _rng.NextDouble(); return (float)(Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Cos(2 * Math.PI * u2)); }
    }
}

/// <summary>
/// ONE SCENARIO RUN: a plant, a timeline (events at times), metrics over measuring windows (the standard ones below; a scenario's
/// own by name - Each hooks writing Min / Max / Rms / Mean into Metrics), an optional CSV trace (AP_CSV=folder or "-- csv folder":
/// one file per run, every 6th tick, the scenario's own columns after the standard ones - Trace). Several ships in one run: Flight. New scenarios (fixed-wing modes, waypoints, AI
/// pilots, intercepts) are a ship, a timeline and their checks - the plant and the metrics stay.
/// </summary>
sealed class Run
{
    public readonly string Name; public readonly Plant P; public Wind Air = new();
    public static string CsvDir;
    StreamWriter _csv;
    readonly List<(float t, Action<Run> act)> _events = new();
    // the measuring window's metrics
    public Vector3D Ref; public float MaxHorizSpeed, MaxPosErr, MaxHeightErr, MaxTilt, MinAgl = float.MaxValue, MaxAgl, MaxLoac, MinStallMargin = float.MaxValue, MaxPullOverCap = float.NegativeInfinity, MaxLeanCmd;
    public ApProt ProtSeen; public int Ticks;
    public readonly List<(float t, ApReason why)> Disengaged = new();
    /// <summary>Moving references flown with the plant (formation leaders, landing pads), the per-tick hooks a scenario adds (its
    /// own metrics: they run after each tick), and the metrics they keep by name.</summary>
    public readonly List<SimTarget> Targets = new();
    public readonly List<Action<Run>> Hooks = new();
    public readonly Dictionary<string, double> Metrics = new();
    /// <summary>Add a per-tick hook (after each tick: the plant's state is the tick's result).</summary>
    public Run Each(Action<Run> hook) { Hooks.Add(hook); return this; }
    /// <summary>A metric kept as the least / the most of what it was set to.</summary>
    public void Min(string name, double v) { Metrics[name] = Metrics.TryGetValue(name, out var o) ? Math.Min(o, v) : v; }
    public void Max(string name, double v) { Metrics[name] = Metrics.TryGetValue(name, out var o) ? Math.Max(o, v) : v; }
    /// <summary>The ship relative to a reference now.</summary>
    public RelState Rel(SimTarget t)
    {
        var r = new RelState { R = (Vector3)(t.P - P.P), Vr = t.V - P.V };
        r.Range = r.R.Length(); r.Closing = r.Range > 1e-3f ? -Vector3.Dot(r.Vr, r.R / r.Range) : 0f;
        r.LosRate = r.Range > 1e-3f ? Vector3.Cross(r.R, r.Vr).Length() / (r.Range * r.Range) : 0f;
        return r;
    }

    public Run(string name, SimShip ship)
    {
        Name = name; P = new Plant(ship);
        if (CsvDir != null)
        {
            Directory.CreateDirectory(CsvDir);
            _csv = new StreamWriter(_csvPath = Path.Combine(CsvDir, Safe(name) + ".csv"));
        }
    }
    const string CsvHeader = "t,x,y,z,vx,vy,vz,tilt_deg,lean_cmd_deg,ax_cmd,az_cmd,move_x,move_y,move_z,goals,prot,tail_side_N,cyclic_share,loac_probe,stall_margin_deg,vcmd_x,vcmd_z,mode";
    bool _csvHead;
    readonly List<(string col, Func<Run, double> val)> _cols = new();
    string _csvPath;
    /// <summary>A column of the scenario's own in this run's CSV trace (after the standard ones). Added after the trace began (a
    /// trimmed start flown first): the file starts again from now, with the new header.</summary>
    public Run Trace(string column, Func<Run, double> value)
    {
        _cols.Add((column, value));
        if (_csv != null && _csvHead) { _csv.Dispose(); _csv = new StreamWriter(_csvPath); _csvHead = false; }
        return this;
    }
    readonly Dictionary<string, (double sum, long n)> _acc = new();
    /// <summary>A metric kept as the root mean square of every value given (Metrics[name]).</summary>
    public void Rms(string name, double v) { var a = _acc.TryGetValue(name, out var o) ? o : default; a.sum += v * v; a.n++; _acc[name] = a; Metrics[name] = Math.Sqrt(a.sum / a.n); }
    /// <summary>A metric kept as the mean of every value given (Metrics[name]; its own accumulator - not an Rms name).</summary>
    public void Mean(string name, double v) { var a = _acc.TryGetValue("mean:" + name, out var o) ? o : default; a.sum += v; a.n++; _acc["mean:" + name] = a; Metrics[name] = a.sum / a.n; }
    /// <summary>A metric (NaN: never set).</summary>
    public double Metric(string name) => Metrics.TryGetValue(name, out var v) ? v : double.NaN;
    static string Safe(string s) { foreach (char c in Path.GetInvalidFileNameChars()) s = s.Replace(c, '_'); return s.Replace(' ', '_'); }

    public Run At(float t, Action<Run> act) { _events.Add((t, act)); return this; }

    /// <summary>Engage goals now, from the instruments (false: refused - the reason in P.Fd.Reason).</summary>
    public bool Engage(ApGoal g) { var s = P.SenseNow(); return P.Fd.Engage(g, in s, out _); }

    /// <summary>Start measuring: the reference place (now, or the place given) and every metric cleared.</summary>
    public void Measure(Vector3D? reference = null)
    {
        Ref = reference ?? P.P; MaxHorizSpeed = MaxPosErr = MaxHeightErr = MaxTilt = MaxAgl = MaxLoac = MaxLeanCmd = 0f; MinAgl = float.MaxValue; MinStallMargin = float.MaxValue;
        MaxPullOverCap = float.NegativeInfinity; ProtSeen = ApProt.None; Ticks = 0;
    }

    public void Fly(float seconds, Action<Run> each = null)
    {
        int steps = (int)MathF.Round(seconds * 60f);
        for (int k = 0; k < steps; k++) Step(each);
        _csv?.Flush();
    }

    /// <summary>ONE TICK (1/60 s): the timeline's events due, the wind, the moving references, 'each', the plant, the metrics,
    /// the hooks, the trace. Fly is these in a row; a Flight steps several runs this way, one tick each in turn.</summary>
    public void Step(Action<Run> each = null)
    {
        {
            for (int e = _events.Count - 1; e >= 0; e--) if (P.Time + 1e-4f >= _events[e].t) { var a = _events[e].act; _events.RemoveAt(e); a(this); }
            P.Wind = Air.At(P.Time, 1f / 60f);
            foreach (var t in Targets) t.Step(P.Time, 1f / 60f);
            each?.Invoke(this);
            var was = P.Fd.Goals;
            P.Tick();
            if ((was & ~P.Fd.Goals) != 0) Disengaged.Add((P.Time, P.Fd.Reason));   // (any goal let go: its reason)
            Ticks++;
            foreach (var h in Hooks) h(this);
            var d = P.P - Ref;
            MaxHorizSpeed = MathF.Max(MaxHorizSpeed, P.HorizontalV.Length());
            MaxPosErr = MathF.Max(MaxPosErr, (float)Math.Sqrt(d.X * d.X + d.Z * d.Z));
            MaxHeightErr = MathF.Max(MaxHeightErr, (float)Math.Abs(d.Y));
            MaxTilt = MathF.Max(MaxTilt, P.TiltDeg); MinAgl = MathF.Min(MinAgl, P.Agl); MaxAgl = MathF.Max(MaxAgl, P.Agl);
            MaxLeanCmd = MathF.Max(MaxLeanCmd, P.Fd.LeanRad / ApMath.Deg);
            ProtSeen |= P.Cmd.Prot;
            if (P.Cmd.HasProbe) MaxLoac = MathF.Max(MaxLoac, P.LastLoacProbe);
            if (P.Fd.Guard.LastPullCap < float.PositiveInfinity) MaxPullOverCap = MathF.Max(MaxPullOverCap, (P.Fd.Guard.LastPull - P.Fd.Guard.LastPullCap) / ApMath.Deg);
            if (_csv != null && (Ticks % 6 == 0))
            {
                if (!_csvHead) { _csvHead = true; _csv.WriteLine(CsvHeader + string.Concat(_cols.Select(c => "," + c.col))); }
                _csv.WriteLine(string.Join(",", F(P.Time), F(P.P.X), F(P.P.Y), F(P.P.Z), F(P.V.X), F(P.V.Y), F(P.V.Z), F(P.TiltDeg), F(P.Fd.LeanRad / ApMath.Deg),
                    F(P.Fd.AccelCmd.X), F(P.Fd.AccelCmd.Z), F(P.Move.X), F(P.Move.Y), F(P.Move.Z), ((int)P.Fd.Goals).ToString(), ((int)P.Cmd.Prot).ToString(),
                    F(P.TailSideForce), F(P.LastCycReach > 0f ? P.LastCyclicT.Length() / P.LastCycReach : 0f), F(P.LastLoacProbe), F(P.LastStallMargin), F(P.Fd.VelCmd.X), F(P.Fd.VelCmd.Z), P.Mode)
                    + string.Concat(_cols.Select(c => "," + F(c.val(this)))));
            }
        }
    }
    public void FlushTrace() => _csv?.Flush();
    static string F(double x) => x.ToString("G5", System.Globalization.CultureInfo.InvariantCulture);
    public void Close() { _csv?.Dispose(); _csv = null; }

    public float HorizErr { get { var d = P.P - Ref; return (float)Math.Sqrt(d.X * d.X + d.Z * d.Z); } }
    public float HeightErr => (float)(P.P.Y - Ref.Y);
}
