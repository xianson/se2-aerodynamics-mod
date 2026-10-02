using System.Diagnostics.Tracing;

/// <summary>Bench-only: what is allocated, by type (the runtime's ~100 KB allocation samples; they arrive late, so a
/// window is cut by the events' own time stamps).</summary>
sealed class AllocSampler : EventListener
{
    public static AllocSampler Instance;
    readonly List<(DateTime t, string type, long bytes)> _log = new();
    protected override void OnEventSourceCreated(EventSource s)
    {
        if (s.Name == "Microsoft-Windows-DotNETRuntime") EnableEvents(s, EventLevel.Verbose, (EventKeywords)0x1);
    }
    protected override void OnEventWritten(EventWrittenEventArgs e)
    {
        if (e.EventName == null || !e.EventName.StartsWith("GCAllocationTick")) return;
        int ti = e.PayloadNames.IndexOf("TypeName"), ai = e.PayloadNames.IndexOf("AllocationAmount64");
        var t = ti >= 0 ? e.Payload[ti]?.ToString() ?? "?" : "?"; long a = ai >= 0 ? Convert.ToInt64(e.Payload[ai]) : 100000;
        lock (_log) _log.Add((e.TimeStamp.ToUniversalTime(), t, a));
    }
    public void Dump(string label, DateTime from, DateTime to)
    {
        System.Threading.Thread.Sleep(2000);
        var by = new Dictionary<string, long>();
        lock (_log)
        {
            foreach (var (t, type, b) in _log) if (t >= from && t <= to) { by.TryGetValue(type, out long x); by[type] = x + b; }
            _log.RemoveAll(x => x.t <= to);
        }
        Console.WriteLine($"        allocations ({label}):");
        foreach (var kv in by.OrderByDescending(k => k.Value).Take(10)) Console.WriteLine($"          {kv.Value / 1048576.0,7:F1} MB  {kv.Key}");
    }
}
