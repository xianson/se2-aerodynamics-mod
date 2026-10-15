#pragma warning disable CS0649, CS8632, CS1591, CS1573, CS1570, CS1574, CS0419, CS1734
using System;
using System.Reflection;
using Keen.Game2.Client.GameSystems.CameraSystems;
using Keen.Game2.Client.GameSystems.PlayerControl;
using Keen.VRage.Core;
using Keen.VRage.Core.Game.GameSystems.Audio;
using Keen.VRage.Library.Utils;
namespace AeroMod;

/// <summary>
/// THE AIRFRAME YOU CAN FEEL (2026-10-14): the pilot hears and feels the structure's load - not the g, the structure's
/// own worst utilisation (StructureServer: the worst joint's load over its strength, heat and damage in; wear from 1,
/// a break at StructureLoads.BreakUtil). Client, the grid the local player is seated in only (first or third person).
///  - CREAKS: quiet occasional creaks from CreakFrom (0.6), more often and louder toward FullAt (1), heavier groans from
///    GroanFrom; a metal damage sound when a block wears, a sharp break when one goes (StructureLoads' wear and break
///    counters). Random gaps, random variants and volumes: no audible loop. The game's own sound events (FMOD paths,
///    GameData: a block's OnDamagedAudio, the hinges' limit stops, the destroyed-block sounds), played on the cockpit
///    block's own SpatialAudioComponent (the seat's: SeatEffectsComponent) - attached to the cockpit, at your ear.
///  - SHAKE: a cockpit buzz from ShakeFrom (0.5), gentle, noticeable near 1, a short jolt when a block wears or breaks -
///    through the render camera's CameraShakeComponent.Shake (its Enabled and ShakePowerMultiplier apply: the game's
///    own camera-shake switches; SE2 2.4 has no player option for it).
/// Read from the server copy the client grid pairs with (DesignProbe.ServerAero: single player and a host - a remote
/// client hears and feels nothing: nothing is published to it). Every threshold and volume a public static.
/// Costs: idle (not seated, or the features off) a timestamp compare a frame; seated, the seat looked up 4x a second
/// and the server copy paired every 2 s - no per-frame allocation. By reflection: the seat's SpatialAudioComponent and
/// the engine's audio player (VRage.Audio is not a mod reference) - docs/REFLECTION_AND_WHITELIST.md.
/// </summary>
public static class StructureFeel
{
    // ── creaks ──
    public static bool Creaks = true;
    /// <summary>Utilisation where the creaks start, where the heavier groans take over, and where they are at their most
    /// frequent (wear begins at 1).</summary>
    public static float CreakFrom = 0.6f, GroanFrom = 0.85f, FullAt = 1f;
    /// <summary>The mean gap between creaks (s) at CreakFrom and at FullAt; each gap x (1 +- GapJitter), at random.
    /// Wearing (over FullAt): the gap shrinks to WearGapScale x the short one at the break.</summary>
    public static float CreakGapLong = 7f, CreakGapShort = 1.1f, GapJitter = 0.6f, WearGapScale = 0.5f;
    /// <summary>Volumes (0..1, x Volume): a creak at CreakFrom / at GroanFrom; a groan at GroanFrom / at the break; a
    /// block wearing; a block breaking. Each creak's x (1 +- VolumeJitter).</summary>
    public static float CreakVolumeLow = 0.12f, CreakVolumeHigh = 0.35f, GroanVolumeLow = 0.35f, GroanVolumeHigh = 0.8f;
    public static float WearVolume = 0.6f, BreakVolume = 1f, VolumeJitter = 0.2f;
    /// <summary>All of them x this (the setting Feel.CreakVolume).</summary>
    public static float Volume = 1f;
    /// <summary>At most one wear sound per WearGap s and one break per BreakGap s (a burst of wear is one sound).</summary>
    public static float WearGap = 0.6f, BreakGap = 0.3f;
    /// <summary>Every sound is stopped (faded) this long after it started: a one-shot ends by itself, a looping event
    /// (a wrong path set here) never runs on.</summary>
    public static float SoundMaxSeconds = 4f;
    /// <summary>The game's FMOD events, a variant at random. (GameData: the block template's OnDamagedAudio, the hinges'
    /// MaxDistance / MoveEnd stops, the pistons' link hit, the destroyed-block and metal impact sounds. SE2 2.4 has no
    /// creak or stress event.) A path the audio catalog does not list is skipped (Status says which).</summary>
    public static string[] CreakSounds = { "event:/SFX/Block/Hinge/Hinge50MoveEnd", "event:/SFX/Block/Hinge/Hinge50MaxDistance" };
    public static string[] GroanSounds = { "event:/SFX/Block/Hinge/Hinge250MaxDistance", "event:/SFX/Block/Hinge/Hinge250MoveEnd", "event:/SFX/Block/Piston/PistonLargeLinkHit" };
    public static string[] WearSounds = { "event:/SFX/Explosions/DamageIncrementalMetal" };
    public static string[] BreakSounds = { "event:/SFX/Explosions/DestroyMetalBlockSmall", "event:/SFX/Impacts/ImpactMetalMetal" };
    /// <summary>No seat audio component: the engine's 3D one-shot at the cockpit instead - it plays at full volume (the
    /// engine ignores the volume there), so only sounds at least this loud.</summary>
    public static float FallbackMinVolume = 0.5f;

    // ── shake ──
    public static bool Shake = true;
    /// <summary>Utilisation where the buzz starts; its power (the game's 0..15 scale - its own acceleration shake is capped
    /// at 1) at FullAt, and at the break (StructureLoads.BreakUtil); grows as t^ShakeCurve from ShakeFrom to FullAt.</summary>
    public static float ShakeFrom = 0.5f, ShakeAtFull = 0.6f, ShakeAtBreak = 1.5f, ShakeCurve = 1.5f;
    /// <summary>All of it x this (the setting Feel.ShakeScale).</summary>
    public static float ShakeScale = 1f;
    /// <summary>A jolt (power) when a block breaks / wears, fading with this time constant (s).</summary>
    public static float JoltBreak = 5f, JoltWear = 0.8f, JoltSeconds = 0.2f;
    /// <summary>The buzz follows the utilisation (a step a few times a second) at this rate (1/s): no steps in it.</summary>
    public static float ShakeEase = 4f;

    // ── diagnostics (harness aeroget) ──
    public static string Status = "not started";
    /// <summary>The audio catalog's events whose path names metal, creak, stress, strain, groan, bend or crack (once).</summary>
    public static string Found = "-";
    public static float LastUtil, LastShake;
    public static int Played, Jolts, Seated;

    // state (one claimant a frame: the effects jobs run in parallel, one of them does it)
    static long _nextFrame, _lastFrame, _lookAt, _pairAt, _nextCreakAt, _wearAt, _breakAt;
    static Entity _clientGrid, _seat;
    static AeroGridComponent _server;
    static int _wearsSeen, _breaksSeen;
    static bool _armed;
    static float _util, _shakeU, _jolt;
    static readonly Random _rng = new Random();

    /// <summary>Client, every frame, from every grid's effects job (AeroEntryFx.EntryJob; after the frame's camera update,
    /// so the shake lands on the next frame's): one job a frame does the work.</summary>
    internal static void Tick(Entity anyGrid)
    {
        long now = System.Diagnostics.Stopwatch.GetTimestamp(), f = System.Diagnostics.Stopwatch.Frequency;
        long due = System.Threading.Interlocked.Read(ref _nextFrame);
        if (now < due || System.Threading.Interlocked.CompareExchange(ref _nextFrame, now + f / 250, due) != due) return;
        if (!Creaks && !Shake) { if (_clientGrid != null) Drop(); return; }
        float dt = _lastFrame == 0 ? 0f : MathF.Min((now - _lastFrame) / (float)f, 0.1f);
        _lastFrame = now;
        if (now >= _lookAt) { _lookAt = now + f / 4; Look(anyGrid, now, f); }
        StopDue(now);
        if (_clientGrid == null) return;
        if (Shake) ShakeFrame(dt, anyGrid, now, f); else _jolt = 0f;
        if (Creaks && _armed && now >= _nextCreakAt) Creak(now, f);
    }

    /// <summary>4x a second: the seat, the server copy, the utilisation, the wear and break counters.</summary>
    static void Look(Entity anyGrid, long now, long f)
    {
        Entity seat = null, grid = null;
        try
        {
            var players = anyGrid?.GetSession()?.SessionComponents.TryGet<ClientPlayersSessionComponent>();
            seat = players?.LocalPlayerController?.GetTopObservableEntity();
            // (seated, that is the cockpit block: its grid is up the hierarchy - MachHud's walk)
            grid = seat;
            for (int up = 0; grid != null && up < 4 && grid.TryGet<AeroGridComponent>() == null; up++)
                grid = grid.TryGet<Keen.VRage.Core.Game.Components.HierarchyComponent>()?.Parent;
            if (grid != null && grid.TryGet<AeroGridComponent>() == null) grid = null;
        }
        catch { grid = null; }
        if (grid == null || ReferenceEquals(grid, seat)) { if (_clientGrid != null) Drop(); return; }   // (on foot, or a grid's own camera)
        if (!ReferenceEquals(grid, _clientGrid) || !ReferenceEquals(seat, _seat))
        {
            Drop();
            _clientGrid = grid; _seat = seat; _pairAt = 0;
            _wearsSeen = StructureLoads.Wears; _breaksSeen = StructureLoads.Breaks;   // (counted from now: nothing old replayed)
            Seated++;
        }
        if (now >= _pairAt || _server?.Entity == null || !PhysicsHack.Alive(_server.Entity.Data))
        {
            _pairAt = now + 2 * f;
            try { _server = DesignProbe.ServerAero(_clientGrid); } catch { _server = null; }
        }
        // the structure's worst utilisation (the last evaluation's; none while it is off or not evaluated yet)
        var s = _server;
        float u = StructureLoads.Enabled && s != null && s.StructMaxSkinK > 0f ? s.StructMaxUtil : 0f;
        if (!(u >= 0f) || float.IsInfinity(u)) u = 0f;
        _util = u; LastUtil = u;
        // wear and break: the counters (global - only player-flown grids are evaluated; in single player, yours)
        int wears = StructureLoads.Wears, breaks = StructureLoads.Breaks;
        bool broke = breaks != _breaksSeen, wore = wears != _wearsSeen;
        _breaksSeen = breaks; _wearsSeen = wears;
        if (broke && s != null)
        {
            if (Shake) { _jolt = MathF.Max(_jolt, JoltBreak); Jolts++; }
            if (Creaks && now >= _breakAt) { _breakAt = now + (long)(BreakGap * f); PlayOne(BreakSounds, BreakVolume, now, f); }
        }
        else if (wore && s != null)
        {
            if (Shake) { _jolt = MathF.Max(_jolt, JoltWear); Jolts++; }
            if (Creaks && now >= _wearAt) { _wearAt = now + (long)(WearGap * f); PlayOne(WearSounds, WearVolume, now, f); }
        }
        // the creaks: armed above CreakFrom - the first one after a short random wait, not the instant it crosses
        if (u >= CreakFrom) { if (!_armed) { _armed = true; _nextCreakAt = now + (long)((0.3 + 1.2 * _rng.NextDouble()) * f); } }
        else _armed = false;
    }

    static void Creak(long now, long f)
    {
        float u = _util;
        float t = Math.Clamp((u - CreakFrom) / MathF.Max(FullAt - CreakFrom, 1e-3f), 0f, 1f);
        float w = Math.Clamp((u - FullAt) / MathF.Max(StructureLoads.BreakUtil - FullAt, 1e-3f), 0f, 1f);   // (wearing: toward the break)
        float gap = CreakGapLong + (CreakGapShort - CreakGapLong) * t;
        gap *= 1f + (WearGapScale - 1f) * w;
        gap *= 1f + GapJitter * (float)(2.0 * _rng.NextDouble() - 1.0);
        _nextCreakAt = now + (long)(MathF.Max(gap, 0.15f) * f);
        if (u >= GroanFrom)
        {
            float g = Math.Clamp((u - GroanFrom) / MathF.Max(StructureLoads.BreakUtil - GroanFrom, 1e-3f), 0f, 1f);
            PlayOne(GroanSounds, GroanVolumeLow + (GroanVolumeHigh - GroanVolumeLow) * g, now, f);
        }
        else
        {
            float c = Math.Clamp((u - CreakFrom) / MathF.Max(GroanFrom - CreakFrom, 1e-3f), 0f, 1f);
            PlayOne(CreakSounds, CreakVolumeLow + (CreakVolumeHigh - CreakVolumeLow) * c, now, f);
        }
    }

    static void ShakeFrame(float dt, Entity anyGrid, long now, long f)
    {
        float u = _util;
        _shakeU += (u - _shakeU) * MathF.Min(1f, dt * ShakeEase);
        float x = _shakeU, p = 0f;
        if (x > ShakeFrom)
        {
            float t = Math.Clamp((x - ShakeFrom) / MathF.Max(FullAt - ShakeFrom, 1e-3f), 0f, 1f);
            p = ShakeAtFull * MathF.Pow(t, ShakeCurve);
            if (x > FullAt) p += (ShakeAtBreak - ShakeAtFull) * Math.Clamp((x - FullAt) / MathF.Max(StructureLoads.BreakUtil - FullAt, 1e-3f), 0f, 1f);
        }
        if (_jolt > 0f) { p += _jolt; _jolt *= MathF.Exp(-dt / MathF.Max(JoltSeconds, 0.01f)); if (_jolt < 0.01f) _jolt = 0f; }
        p *= ShakeScale;
        LastShake = p;
        if (p < 0.005f) return;
        var cam = Shaker(anyGrid, now, f);
        if (cam == null) return;
        try { cam.Shake(new CameraShakeArgs { Power = MathF.Min(p, CameraShakeComponent.MAX_SHAKE) }); }
        catch (Exception e) { _shaker = null; Status = "shake failed: " + e.Message; }
    }

    // ── the camera ──
    static CameraShakeComponent _shaker;
    static long _shakerAt;
    /// <summary>The render camera's shake component (the one the camera reads - CameraComponent's UpdateShake), looked for
    /// every 2 s while there is none.</summary>
    static CameraShakeComponent Shaker(Entity anyGrid, long now, long f)
    {
        var s = _shaker;
        if (s != null && s.Entity != null && PhysicsHack.Alive(s.Entity.Data)) return s;
        _shaker = null;
        if (now < _shakerAt) return null;
        _shakerAt = now + 2 * f;
        try
        {
            var session = anyGrid?.GetSession();
            if (session == null) return null;
            foreach (var e in session.GetEntitiesOfType<CameraShakeComponent>())
            {
                var c = e?.TryGet<CameraShakeComponent>();
                if (c != null) { _shaker = c; break; }
            }
            if (_shaker == null) Status = "no camera shake component";
        }
        catch (Exception e) { Status = "camera lookup failed: " + e.Message; }
        return _shaker;
    }

    // ── the sounds ──
    // the seat's SpatialAudioComponent: PlayOneShot(AudioEvent, float volume, bool force), Stop(AudioEvent, bool fadeOut)
    static Action<AudioEvent, float, bool> _seatPlay;
    static Action<AudioEvent, bool> _seatStop;
    static Entity _seatBound;
    static MethodInfo _miPlay, _miStop;
    // the engine's 3D one-shot (AudioEngineComponent.Play(AudioEvent, WorldTransform, float) - the volume ignored)
    static Action<AudioEvent, WorldTransform, float> _enginePlay;
    static bool _engineLooked;
    static HashSet<string> _catalog;
    // the events, made once per array (a settings change: a new array)
    static readonly Dictionary<string[], AudioEvent[]> _events = new();
    // sounds to stop (SoundMaxSeconds after they started)
    static readonly AudioEvent[] _stopEv = new AudioEvent[8];
    static readonly Action<AudioEvent, bool>[] _stopBy = new Action<AudioEvent, bool>[8];
    static readonly long[] _stopAt = new long[8];

    static void PlayOne(string[] paths, float volume, long now, long f)
    {
        var evs = Events(paths);
        if (evs == null || evs.Length == 0) return;
        var ev = evs[_rng.Next(evs.Length)];
        float v = volume * Volume * (1f + VolumeJitter * (float)(2.0 * _rng.NextDouble() - 1.0));
        v = Math.Clamp(v, 0f, 1f);
        if (v < 0.01f) return;
        try
        {
            BindSeat();
            if (_seatPlay != null)
            {
                _seatPlay(ev, v, true);
                Played++;
                if (_seatStop != null) QueueStop(ev, _seatStop, now + (long)(SoundMaxSeconds * f));
                return;
            }
            if (v >= FallbackMinVolume && _enginePlay != null && _seat != null && PhysicsHack.Alive(_seat.Data))
            {
                _enginePlay(ev, _seat.Data.GetWorldTransform(), v);
                Played++;
            }
        }
        catch (Exception e) { _seatPlay = null; _seatStop = null; Status = "sound failed: " + e.Message; }
    }

    static void QueueStop(AudioEvent ev, Action<AudioEvent, bool> stop, long at)
    {
        int slot = 0; long oldest = long.MaxValue;
        for (int i = 0; i < _stopAt.Length; i++)
        {
            if (_stopBy[i] == null) { slot = i; break; }
            if (_stopAt[i] < oldest) { oldest = _stopAt[i]; slot = i; }
        }
        _stopEv[slot] = ev; _stopBy[slot] = stop; _stopAt[slot] = at;
    }

    static void StopDue(long now)
    {
        for (int i = 0; i < _stopAt.Length; i++)
        {
            var by = _stopBy[i];
            if (by == null || now < _stopAt[i]) continue;
            _stopBy[i] = null;
            try { by(_stopEv[i], true); } catch { }
        }
    }

    /// <summary>The seat's audio component's PlayOneShot and Stop, bound once per seat (reflection: SpatialAudioComponent is
    /// in VRage.Audio, not a mod reference). The engine's player and the catalog check, once.</summary>
    static void BindSeat()
    {
        if (!_engineLooked) LookEngine();
        var seat = _seat;
        if (ReferenceEquals(seat, _seatBound) && (_seatPlay != null || seat == null)) return;
        _seatBound = seat; _seatPlay = null; _seatStop = null;
        if (seat == null) return;
        object comp = null;
        foreach (var c in seat.Components)
        {
            for (var t = c?.GetType(); t != null; t = t.BaseType)
                if (t.FullName == "Keen.VRage.Audio.Components.SpatialAudioComponent") { comp = c; break; }
            if (comp != null) break;
        }
        if (comp == null) { Status = $"seat {seat.DebugName}: no audio component - the engine's 3D one-shot (loud sounds only)"; return; }
        var ct = comp.GetType();
        _miPlay = ct.GetMethod("PlayOneShot", BindingFlags.Public | BindingFlags.Instance, null, new[] { typeof(AudioEvent), typeof(float), typeof(bool) }, null);
        _miStop = ct.GetMethod("Stop", BindingFlags.Public | BindingFlags.Instance, null, new[] { typeof(AudioEvent), typeof(bool) }, null);
        if (_miPlay == null) { Status = "SpatialAudioComponent.PlayOneShot not found (the game changed)"; return; }
        _seatPlay = (Action<AudioEvent, float, bool>)Delegate.CreateDelegate(typeof(Action<AudioEvent, float, bool>), comp, _miPlay);
        if (_miStop != null) _seatStop = (Action<AudioEvent, bool>)Delegate.CreateDelegate(typeof(Action<AudioEvent, bool>), comp, _miStop);
        Status = $"seat {seat.DebugName}: its audio component{(_catalog == null ? " (no catalog: paths unchecked)" : "")}";
    }

    /// <summary>The engine's audio (AudioEngineComponent, as the IAudioEventCatalog the game's mission actions read it):
    /// the loaded events (our paths checked against them) and its 3D one-shot player.</summary>
    static void LookEngine()
    {
        _engineLooked = true;
        try
        {
            var cat = Singleton<VRageCore>.Instance?.Engine?.AsInterface<IAudioEventCatalog>();
            if (cat == null) { Status = "no audio engine"; return; }
            var set = new HashSet<string>(StringComparer.Ordinal);
            var found = new System.Text.StringBuilder();
            foreach (var ev in cat.AudioEvents)
            {
                if (ev.Path == null) continue;
                set.Add(ev.Path);
                string p = ev.Path.ToLowerInvariant();
                if (p.Contains("creak") || p.Contains("stress") || p.Contains("strain") || p.Contains("groan") || p.Contains("bend") || p.Contains("crack") || (p.Contains("metal") && !p.Contains("music")))
                    found.Append(found.Length > 0 ? ", " : "").Append(ev.Path);
            }
            _catalog = set.Count > 0 ? set : null;
            Found = found.Length > 0 ? found.ToString() : "-";
            var mi = cat.GetType().GetMethod("Play", BindingFlags.Public | BindingFlags.Instance, null, new[] { typeof(AudioEvent), typeof(WorldTransform), typeof(float) }, null);
            if (mi != null) _enginePlay = (Action<AudioEvent, WorldTransform, float>)Delegate.CreateDelegate(typeof(Action<AudioEvent, WorldTransform, float>), cat, mi);
        }
        catch (Exception e) { Status = "audio engine lookup failed: " + e.Message; }
    }

    static AudioEvent[] Events(string[] paths)
    {
        if (paths == null) return null;
        if (_events.TryGetValue(paths, out var evs)) return evs;
        var list = new List<AudioEvent>(paths.Length);
        string missing = null;
        foreach (var p in paths)
        {
            if (string.IsNullOrEmpty(p)) continue;
            if (_catalog != null && !_catalog.Contains(p)) { missing = missing == null ? p : missing + ", " + p; continue; }
            list.Add(new AudioEvent(p));
        }
        if (missing != null) Status = "not in the audio catalog (skipped): " + missing;
        evs = list.ToArray();
        _events[paths] = evs;
        return evs;
    }

    /// <summary>Left the seat (or the features off): quiet at once.</summary>
    static void Drop()
    {
        _clientGrid = null; _seat = null; _server = null; _armed = false;
        _util = _shakeU = _jolt = 0f; LastUtil = LastShake = 0f;
    }
}
