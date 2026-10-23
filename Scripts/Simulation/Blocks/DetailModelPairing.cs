#pragma warning disable CS0649, CS8632, CS1591, CS1573, CS1570, CS1574, CS0419, CS1734
using System;
using System.Collections.Generic;
using System.Reflection;

namespace AeroMod;

/// <summary>
/// Lets a mod's [BlockDetailProvider] model open in the control panel without crashing the game.
///
/// Why it crashed: StreamedControlPanelSessionComponent.AttachBlockDetailModels registers every detail model for
/// replication, then hands the models to the client. Replication needs [Replicate] plus the generated IAccessible code
/// (a TypeInfoHolder), which a mod's model can't have (SE2 4.0 runs the generators on mod scripts, but their serializer
/// does not compile there: see the models' [NoSerialize]). So RegisterObservable only asserts (non-fatal) and pairs
/// nothing, and handing the model over fails:
///  - 2.4: IReplicationWorkarounds.TranslateLocalToRemote = ReplayContext._localToRemoteObjects[model] threw
///    KeyNotFoundException (fatal);
///  - 4.0: the network story's result is translated through the server transport's InProcessReplicationTracker
///    (InProcessServerPeerTransportSessionComponent.Tracker, TryTranslateLocalToRemoteWithStrategy): no pairing, the
///    story is skipped ("Skipping story ... due to completion failed patching") and the block's panel never comes -
///    the game's own models of that block with it.
///
/// The fix: pair the model with ITSELF when it is built - its ctor runs inside Allocate, before RegisterObservable - in the
/// tracker (AddManualPair(model, model): both directions, so a detach request coming back from the client resolves too)
/// and, as on 2.4, in the replay context (ReplayContext.AddObjectPairing(model, model)). The client is then handed the
/// server object itself - what the game does when observable replication is off. In process only: single player and the
/// host; a remote client gets nothing (as with every mod setting).
///
/// The game never unpairs it (its handle is Disposable.Empty), so the model a block had before is unpaired when that block
/// opens a new one OF THE SAME TYPE: at most one stale pairing per block and model type. (2026-10-23: keyed by the block alone,
/// a block with two of our models - the solid nozzle, which carries its own grain since 3fcf26d, builds the nozzle's and the
/// segment's - had the first one unpaired by the second: the story failed "completion failed patching" and the whole panel was
/// lost.) HEALTH: every outcome, and every reason none was paired, is logged once per model type and reason.
/// </summary>
public static class DetailModelPairing
{
    static readonly object _lock = new();
    static readonly System.Runtime.CompilerServices.ConditionalWeakTable<Entity, Dictionary<Type, object>> _lastByBlock = new();
    static readonly HashSet<string> _said = new();
    static void Say(string what) { lock (_said) if (!_said.Add(what)) return; Log.Default?.Info($"[AERO] DetailModelPairing: {what}"); }
    static PropertyInfo _replayContextProp, _trackerProp;
    static MethodInfo _add, _remove, _hasLocal, _tAdd, _tRemove, _tHas;
    static bool _resolved, _failed, _tResolved, _tFailed;

    /// <summary>Last outcome, for logs and the harness.</summary>
    public static string Status = "not used";

    /// <summary>Call from the detail model's default ctor; <paramref name="anchor"/> is one of its [Component] fields.</summary>
    public static void Pair(object model, Component anchor)
    {
        try
        {
            string name = model?.GetType().Name ?? "null";
            var block = anchor?.Entity;
            if (block == null) { Status = $"{name}: not paired - its [Component] anchor is not on the block (the panel will fail)"; Say(Status); return; }
            var holder = block.GetSession()?.SessionComponents;
            if (holder == null) { Status = $"{name}: not paired - no session components"; Say(Status); return; }
            var replay = ReplayContextOf(holder);
            var trackers = TrackersOf(holder);
            if (replay == null && trackers.Count == 0) { Status = $"{name}: not paired - no replay context and no replication tracker"; Say(Status); return; }
            lock (_lock)
            {
                if (!_lastByBlock.TryGetValue(block, out var byType)) { byType = new Dictionary<Type, object>(); _lastByBlock.Add(block, byType); }
                var type = model.GetType();
                if (byType.TryGetValue(type, out var old) && !ReferenceEquals(old, model))
                {
                    if (replay != null && (bool)_hasLocal.Invoke(replay, new[] { old })) _remove.Invoke(replay, new[] { old });
                    foreach (var t in trackers) if (Has(t, old)) _tRemove.Invoke(t, new[] { old });
                }
                if (replay != null && !(bool)_hasLocal.Invoke(replay, new[] { model })) _add.Invoke(replay, new[] { model, model });
                foreach (var t in trackers) if (!Has(t, model)) _tAdd.Invoke(t, new[] { model, model });
                byType[type] = model;
            }
            Status = $"paired {name} ({trackers.Count} tracker(s){(replay != null ? ", replay context" : "")})";
            Say($"{name} paired ({trackers.Count} tracker(s){(replay != null ? ", replay context" : "")})");
        }
        catch (Exception e)
        {
            Status = $"{model?.GetType().Name}: pairing failed: {e.GetBaseException().Message}";
            Say(Status);
        }
    }

    static bool Has(object tracker, object model)
    {
        var args = new object[] { model, null };
        return (bool)_tHas.Invoke(tracker, args);
    }

    /// <summary>The replay context of the block's session's observable replication (the server's, where the model is built).</summary>
    static object ReplayContextOf(Entity holder)
    {
        if (_failed) return null;
        foreach (var c in holder.Components)
        {
            for (var t = c?.GetType(); t != null; t = t.BaseType)
            {
                if (t.FullName != "Keen.VRage.Multiplayer.Replications.ObservableObjectReplicationSessionComponent") continue;
                if (!_resolved && !Resolve(t)) return null;
                return _replayContextProp.GetValue(c);
            }
        }
        return null;
    }

    /// <summary>SE2 4.0: the server transports' trackers (one per in-process client: InProcessServerPeerTransportSessionComponent.Tracker).</summary>
    static List<object> TrackersOf(Entity holder)
    {
        var list = new List<object>(1);
        if (_tFailed) return list;
        foreach (var c in holder.Components)
        {
            var t = c?.GetType();
            if (t == null || t.FullName != "Keen.VRage.Multiplayer.Transport.InProcess.InProcessServerPeerTransportSessionComponent") continue;
            if (!_tResolved && !ResolveTracker(t)) return list;
            var tr = _trackerProp.GetValue(c);
            if (tr != null) list.Add(tr);
        }
        return list;
    }

    static bool Resolve(Type replicationBase)
    {
        _resolved = true;
        const BindingFlags inst = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        _replayContextProp = replicationBase.GetProperty("ReplayContext", inst);
        var rc = _replayContextProp?.PropertyType;
        _add = rc?.GetMethod("AddObjectPairing", inst, null, new[] { typeof(object), typeof(object) }, null);
        _remove = rc?.GetMethod("RemoveObjectPairing", inst, null, new[] { typeof(object) }, null);
        _hasLocal = rc?.GetMethod("HasPairingLocal", inst, null, new[] { typeof(object) }, null);
        _failed = _add == null || _remove == null || _hasLocal == null;
        if (_failed)
        {
            Status = "ReplayContext API not found";
            Log.Default?.Info($"[AERO] DetailModelPairing: {Status}");
        }
        return !_failed;
    }

    static bool ResolveTracker(Type transport)
    {
        _tResolved = true;
        const BindingFlags inst = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        _trackerProp = transport.GetProperty("Tracker", inst);
        var tt = _trackerProp?.PropertyType;
        MethodInfo add = null, remove = null;
        if (tt != null)
            foreach (var m in tt.GetMethods(inst))
            {
                if (m.Name == "AddManualPair" && m.IsGenericMethodDefinition && m.GetParameters().Length == 2) add = m;
                else if (m.Name == "RemoveManualPair" && m.IsGenericMethodDefinition && m.GetParameters().Length == 1) remove = m;
            }
        _tAdd = add?.MakeGenericMethod(typeof(object));
        _tRemove = remove?.MakeGenericMethod(typeof(object));
        _tHas = tt?.GetMethod("TryGetObjectPairing", inst);
        _tFailed = _trackerProp == null || _tAdd == null || _tRemove == null || _tHas == null || _tHas.GetParameters().Length != 2;
        if (_tFailed)
        {
            Status = "replication tracker API not found";
            Log.Default?.Info($"[AERO] DetailModelPairing: {Status}");
        }
        return !_tFailed;
    }
}
