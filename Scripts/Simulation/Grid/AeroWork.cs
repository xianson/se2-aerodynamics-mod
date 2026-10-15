#pragma warning disable
using System;

namespace AeroMod;

/// <summary>
/// All of aero's background work - grids' full builds and their local updates after damage - in one queue, done by a
/// fixed number of workers (Workers), highest priority first (each job's priority read when a worker takes it, so a
/// grid that became important since is served first). A hundred damaged grids queue; they do not start a hundred
/// threads. The workers also bound each job's own parallelism (ThreadsForJob): together aero never keeps more than
/// Workers cores busy.
/// </summary>
public static class AeroWork
{
    /// <summary>A queued job: what the simulation thread polls (as it polled a Task).</summary>
    public sealed class Item
    {
        internal Func<float> Priority;
        internal Action Run;
        internal long Seq;
        volatile bool _done;
        public Exception Error { get; internal set; }
        public bool IsCompleted => _done;
        public bool IsFaulted => _done && Error != null;
        internal void Finish(Exception e) { Error = e; _done = true; }
    }

    /// <summary>Workers (cores aero may keep busy in the background).</summary>
    public static int Workers = 2;
    static readonly List<Item> _queue = new();
    static readonly object _lock = new();
    static int _running, _active;
    static long _seq;

    /// <summary>Jobs running now.</summary>
    public static int Active => _active;
    /// <summary>Jobs waiting.</summary>
    public static int Waiting { get { lock (_lock) return _queue.Count; } }

    /// <summary>Threads one job may use (a table build splits its directions): the spare workers' share.</summary>
    public static int ThreadsForJob() => Math.Max(1, Workers - Math.Max(0, _active - 1));

    public static Item Enqueue(Func<float> priority, Action run)
    {
        DampedShadowedDragModel.ThreadBudget ??= ThreadsForJob;
        var it = new Item { Priority = priority, Run = run, Seq = System.Threading.Interlocked.Increment(ref _seq) };
        lock (_lock)
        {
            _queue.Add(it);
            if (_running < Workers)
            {
                _running++;
                System.Threading.Tasks.Task.Factory.StartNew(Worker, System.Threading.CancellationToken.None, System.Threading.Tasks.TaskCreationOptions.LongRunning, System.Threading.Tasks.TaskScheduler.Default);
            }
            else System.Threading.Monitor.Pulse(_lock);
        }
        return it;
    }

    static void Worker()
    {
        try { System.Threading.Thread.CurrentThread.Priority = System.Threading.ThreadPriority.BelowNormal; } catch { }
        while (true)
        {
            Item job;
            lock (_lock)
            {
                if (_queue.Count == 0 && !System.Threading.Monitor.Wait(_lock, 10000)) { }
                if (_queue.Count == 0) { _running--; return; }   // (idle 10 s: the thread goes)
                // the most important now (ties: the oldest)
                int best = 0; float bp = float.MinValue;
                for (int i = 0; i < _queue.Count; i++)
                {
                    float p;
                    try { p = _queue[i].Priority?.Invoke() ?? 0f; } catch { p = 0f; }
                    if (p > bp || (p == bp && _queue[i].Seq < _queue[best].Seq)) { bp = p; best = i; }
                }
                job = _queue[best];
                _queue.RemoveAt(best);
                _active++;
            }
            Exception err = null;
            try { job.Run(); } catch (Exception e) { err = e; }
            lock (_lock) _active--;
            job.Finish(err);
        }
    }
}
