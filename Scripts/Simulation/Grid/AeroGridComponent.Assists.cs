#pragma warning disable CS0649, CS8632, CS1591, CS1573, CS1570, CS1574, CS0419, CS1734
using System;
using System.Threading;
using Keen.Game2.Simulation.WorldObjects.Movement;
using Keen.VRage.Core;

namespace AeroMod;

/// <summary>
/// THE FLIGHT ASSISTS ON A GRID (Lift/FlightAssists.cs). Stall protection in manual flight (Assists.StallProtection, off by
/// default), every tick in the attitude job after the autopilot's step and before ThrustTorque.Apply:
///  SERVER: a seated pilot's target attitude (TargetControlData) - the autopilot not flying - governed by PilotProtection (the
///   autopilot's EnvelopeGuard, its instruments ApSenseNow, the world's autopilot caps): PilotGov is what AttitudeTarget hands
///   the aero-aware law. The pilot's own target is the loss-of-control evaluation's probe (PilotProbe: StructureServer, Loac.cs
///   EvaluateApProbe) and keeps the stall sweep running (StallWarning.cs). Published in the autopilot record (ThrustTorque.ApPub).
///  CLIENT: the copy flies the server's governed target too (its prediction the same), and the readout says what clips it
///   (PilotProtText: "STALL LIMIT" / "G LIMIT" / "CONTROL LIMIT" - Hud/MachHud.cs).
/// The reticle itself is never moved: held past the limit, the ship holds the limit (as a fly-by-wire airliner's full stick).
/// Off: a bool check and nothing else.
/// </summary>
public partial class AeroGridComponent
{
    PilotProtection _pilotProt;
    /// <summary>This tick's governed pilot target (world) - AttitudeTarget flies it while PilotGovOn.</summary>
    internal bool PilotGovOn; internal Quaternion PilotGov = Quaternion.Identity;
    /// <summary>Server: the pilot's own target as the loss-of-control evaluation's probe (while protection runs).</summary>
    internal volatile bool PilotProbeOn; internal Quaternion PilotProbe = Quaternion.Identity;
    /// <summary>Client: what the protection clips now ("" nothing) - the speed readout's.</summary>
    internal volatile string PilotProtText = "";
    ThrustTorque.Published _asRec; long _asRecheck;

    /// <summary>The attitude job's assist step (AeroSimJob.AttitudeJob, after AutopilotStep, before ThrustTorque.Apply).</summary>
    internal void AssistStep(in WorldTransform wt)
    {
        bool want = FlightAssists.StallProtection && !ApFlying;
        if (IsServerScene) ServerAssist(wt, want); else ClientAssist(want);
    }

    void ServerAssist(in WorldTransform wt, bool want)
    {
        if (!want || !Data.TryGet<TargetControlData>(out var tc) || !tc.TargetOrientation.IsValidAndRotationIsNormalized())
        {
            if (PilotGovOn || PilotProbeOn) { PilotGovOn = false; PilotProbeOn = false; _pilotProt?.Reset(); PublishPilotGov(wt, false, ApProt.None); }
            return;
        }
        _pilotProt ??= new PilotProtection();   // (once per grid, at its first protected tick)
        var s = ApSenseNow(wt, null);
        s.Lim = ApLimits.Defaults;
        PilotProbe = tc.TargetOrientation; PilotProbeOn = true;
        PilotGov = _pilotProt.Step(in s, tc.TargetOrientation); PilotGovOn = true;
        PublishPilotGov(wt, true, _pilotProt.Shown);
    }

    void PublishPilotGov(in WorldTransform wt, bool on, ApProt shown)
    {
        if (!on && _flamePub?.Ap == null) return;   // (nothing published yet: nothing to take back)
        var p = ThrustTorque.ApRecord(this, wt);
        lock (p) { p.PilotGov = on; p.PilotTarget = PilotGov; p.PilotProt = (int)shown; p.PilotGovStamp = System.Diagnostics.Stopwatch.GetTimestamp(); }
    }

    void ClientAssist(bool want)
    {
        if (!want || !Data.Has<TargetControlData>()) { PilotGovOn = false; PilotProtText = ""; return; }
        long now = System.Diagnostics.Stopwatch.GetTimestamp(), f = System.Diagnostics.Stopwatch.Frequency;
        var rec = _asRec;
        if (rec == null || rec.Gone || now >= _asRecheck)
        {
            _asRecheck = now + f / 4;
            if (!ThrustTorque.FindAp(Entity, rec, now, out var m)) m = null;
            _asRec = rec = m;
        }
        var ap = rec?.Ap;
        if (ap == null) { PilotGovOn = false; PilotProtText = ""; return; }
        bool on; Quaternion target; int prot;
        lock (ap) { on = ap.PilotGov && now - ap.PilotGovStamp < f / 2; target = ap.PilotTarget; prot = ap.PilotProt; }
        PilotGovOn = on && target.IsValidAndRotationIsNormalized(); PilotGov = target;
        PilotProtText = PilotGovOn ? ApText.Prot((ApProt)prot) : "";
    }
}
