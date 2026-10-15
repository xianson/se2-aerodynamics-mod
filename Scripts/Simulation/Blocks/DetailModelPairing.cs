#pragma warning disable
using System;
using System.Reflection;

namespace AeroMod;

/// <summary>
/// Lets a mod's [BlockDetailProvider] model open in the control panel without crashing the game.
///
/// Why it crashed: StreamedControlPanelSessionComponent.AttachBlockDetailModels registers every detail model for
/// replication, then translates it to its client copy with IReplicationWorkarounds.TranslateLocalToRemote, which is
/// ReplayContext._localToRemoteObjects[model]. Replication needs [Replicate] plus the generated IAccessible code, which a
/// mod's model can't have ([Replicate] is not whitelisted; the generators don't run on mod scripts). So
/// RegisterObservable only asserts (non-fatal) and pairs nothing, and the lookup throws KeyNotFoundException.
///
/// The fix: pair the model with ITSELF (ReplayContext.AddObjectPairing(model, model)) when it is built - its ctor runs
/// inside Allocate, before RegisterObservable. RegisterObservable then sees an existing pairing and asserts (non-fatal)
/// instead of replicating, and the translation hands the UI the server object itself - what the game does when observable
/// replication is off (ObservableObjectReplicationSessionComponent.TranslateLocalToRemote). In process only: single player
/// and the host; a remote client gets nothing (as with every mod setting).
///
/// The game never unpairs it (its handle is Disposable.Empty), so the model a block had before is unpaired when that block
/// opens a new one: at most one stale pairing per block.
/// </summary>
public static class DetailModelPairing
{
    static readonly object _lock = new();
    static readonly System.Runtime.CompilerServices.ConditionalWeakTable<Entity, object> _lastByBlock = new();
    static PropertyInfo _replayContextProp;
    static MethodInfo _add, _remove, _hasLocal;
    static bool _resolved, _failed;

    /// <summary>Last outcome, for logs and the harness.</summary>
    public static string Status = "not used";

    /// <summary>Call from the detail model's default ctor; <paramref name="anchor"/> is one of its [Component] fields.</summary>
    public static void Pair(object model, Component anchor)
    {
        try
        {
            var block = anchor?.Entity;
            var replay = ReplayContextOf(block);
            if (replay == null) return;
            lock (_lock)
            {
                if (_lastByBlock.TryGetValue(block, out var old))
                {
                    _lastByBlock.Remove(block);
                    if ((bool)_hasLocal.Invoke(replay, new[] { old })) _remove.Invoke(replay, new[] { old });
                }
                if (!(bool)_hasLocal.Invoke(replay, new[] { model })) _add.Invoke(replay, new[] { model, model });
                _lastByBlock.Add(block, model);
            }
            Status = $"paired {model.GetType().Name}";
        }
        catch (Exception e)
        {
            Status = $"pairing failed: {e.GetBaseException().Message}";
            Log.Default?.Info($"[AERO] DetailModelPairing: {Status}");
        }
    }

    /// <summary>The replay context of the block's session's observable replication (the server's, where the model is built).</summary>
    static object ReplayContextOf(Entity block)
    {
        if (_failed) return null;
        var holder = block?.GetSession()?.SessionComponents;
        if (holder == null) return null;
        foreach (var c in holder.Components)
        {
            for (var t = c?.GetType(); t != null; t = t.BaseType)
            {
                if (t.FullName != "Keen.VRage.Multiplayer.Replications.ObservableObjectReplicationSessionComponent") continue;
                if (!_resolved && !Resolve(t)) return null;
                return _replayContextProp.GetValue(c);
            }
        }
        Status = "no observable replication session component";
        return null;
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
}
