#pragma warning disable CS0649, CS8632, CS1591, CS1573, CS1570, CS1574, CS0419, CS1734
using System;
using System.Threading;
using Keen.VRage.Core.Systems;
using Keen.VRage.Physics;

namespace AeroMod;

/// <summary>WorldGravity's engine side (WorldGravity.cs: the setting and the law, game-free): the setting onto each scene's
/// IPhysics.GravityMultiplier.</summary>
public static partial class WorldGravity
{
    sealed class Slot { public object Session; public int Version = int.MinValue; public bool Warned; }
    static readonly Slot _server = new Slot(), _client = new Slot();

    /// <summary>The session's physics gets the setting when it changed (or the session did). Cheap when nothing did.</summary>
    public static void Tick(Session session, bool client)
    {
        if (session == null) return;
        var s = client ? _client : _server;
        int v = Volatile.Read(ref _version);
        if (!Due(s.Session, s.Version, session, v)) return;
        lock (s)
        {
            if (!Due(s.Session, s.Version, session, v)) return;
            bool fresh = !ReferenceEquals(s.Session, session);
            if (fresh) { s.Warned = false; if (!client) AppliedServer = float.NaN; }
            string scene = client ? "client" : "server";
            IPhysics phys = null;
            try { phys = session.TryGet<IPhysics>(); } catch { }
            if (phys == null)
            {
                // (not there yet while a world comes up: tried again next tick; said once)
                if (!s.Warned) { s.Warned = true; Status = $"{scene}: no IPhysics in the session yet"; Log.Default?.Warning("[AERO] world gravity: " + Status); }
                return;
            }
            float m = _mult, before = phys.GravityMultiplier;
            phys.GravityMultiplier = m;
            s.Session = session; s.Version = v;
            if (!client) AppliedServer = m;
            Status = $"{scene}: x{m:0.##} applied (was x{before:0.##})";
            Log.Default?.Info("[AERO] world gravity: " + Status);
        }
    }
}
