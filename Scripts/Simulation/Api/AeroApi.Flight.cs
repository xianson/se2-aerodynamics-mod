#pragma warning disable
using System;
using System.Collections.Generic;
namespace AeroMod;

// THE PUBLIC API, VERSION 7: the per-tick flight state, the control surfaces, the thrusters' forces, watching a grid.
// Published by the grid's simulation tick (AeroGridComponent.PublishApiTick, Grid/AeroGridComponent.Api.cs) while someone
// reads it; copied out under the grid's API lock - any thread, no allocation, no exception handling on the way.
public static partial class AeroApi
{
    // ── watching ──

    /// <summary>(version 7) How long (s) a read keeps a grid's version-7 record published: every read of it pushes it on.</summary>
    internal const float WatchSeconds = 5f;

    /// <summary>The grid's API copies, made on the first ask (any thread), with the record <paramref name="rec"/>
    /// (ApiGridState.RecFlight ...) kept published for WatchSeconds more - that record only (2026-10-15 review: one window
    /// for all kept a flight reader's grid publishing its whole structure every evaluation). Null: no aero for this grid here.</summary>
    static ApiGridState Watch(Keen.VRage.DCS.Components.Entity grid, int rec)
    {
        var aero = AeroOf(grid);
        if (aero == null) return null;
        var s = aero.ApiStateFor();
        s.Push(rec, System.Diagnostics.Stopwatch.GetTimestamp() + (long)(WatchSeconds * System.Diagnostics.Stopwatch.Frequency));
        return s;
    }

    /// <summary>(version 7) Start publishing a grid's version-7 records now, for <paramref name="seconds"/> (at most 600;
    /// every read also keeps them on for 5 s). The records fill on the grid's next simulation tick (flight state, surfaces,
    /// thrusters) and its next structure evaluation (blocks, faces): call it a tick ahead of your first read to have an
    /// answer then. faces: the hull's heat faces too (big ships: 10^5 faces, copied each evaluation). False: no aero for
    /// this grid here. Any thread.</summary>
    public static bool WatchGrid(Keen.VRage.DCS.Components.Entity grid, float seconds, bool faces)
    {
        var s = Watch(grid, ApiGridState.RecFlight);
        if (s == null) return false;
        long now = System.Diagnostics.Stopwatch.GetTimestamp();
        long until = now + (long)(Math.Max(Math.Clamp(seconds, 0f, 600f), WatchSeconds) * System.Diagnostics.Stopwatch.Frequency);
        s.Push(ApiGridState.RecFlight, until); s.Push(ApiGridState.RecStruct, until);
        if (faces) s.Push(ApiGridState.RecFaces, until);
        return true;
    }

    /// <summary>(version 7) The API contract as a call (the same number as <see cref="Version"/>) - for a caller binding
    /// typed delegates only.</summary>
    public static int GetApiVersion() => Version;

    // ── the flight state ──

    // the layout: an index's meaning never changes; new values are only appended (a later version - its length grows)
    internal const int FsAge = 0, FsAirspeed = 1, FsGroundSpeed = 2, FsMach = 3, FsDensity = 4, FsDynamicPressure = 5,
        FsTravel = 6, FsWind = 9, FsAngVel = 12, FsMass = 15, FsCom = 16, FsGroundHeight = 19, FsUp = 20, FsGravity = 23,
        FsForce = 24, FsTorque = 27, FsAirframeTorque = 30, FsSurfaceTorque = 33, FsDrag = 36, FsLift = 37, FsFrontalArea = 38,
        FsLoadFactor = 39, FsStallState = 40, FsStallMargin = 41, FsStallLiftShare = 42, FsStallG = 43, FsLoacState = 44,
        FsLoacRatio = 45, FsMaxUtil = 46, FsMaxSkinK = 47, FsHeatShare = 48, FsMaxWingStrain = 49, FsSteerCap = 50,
        FsControlValid = 51, FsControlDemand = 52, FsAware = 55, FsGyroTorque = 56, FsThrusterTorque = 59, FsReach = 62,
        FsNeed = 65, FsDelivered = 68, FsSurfacesCommanded = 71, FsSurfacesSaturated = 72, FsApplied = 73, FsEntryStrength = 74;
    internal const int FlightStateLength = 75;

    static readonly string[] FlightNames =
    {
        "Age", "Airspeed", "GroundSpeed", "Mach", "Density", "DynamicPressure",
        "TravelX", "TravelY", "TravelZ", "WindX", "WindY", "WindZ", "AngularVelocityX", "AngularVelocityY", "AngularVelocityZ",
        "Mass", "CenterOfMassX", "CenterOfMassY", "CenterOfMassZ", "GroundHeight", "UpX", "UpY", "UpZ", "Gravity",
        "ForceX", "ForceY", "ForceZ", "TorqueX", "TorqueY", "TorqueZ", "AirframeTorqueX", "AirframeTorqueY", "AirframeTorqueZ",
        "SurfaceTorqueX", "SurfaceTorqueY", "SurfaceTorqueZ", "Drag", "Lift", "FrontalArea", "LoadFactor",
        "StallState", "StallMarginDeg", "StallLiftShare", "StallGAvailable", "LoacState", "LoacRatio",
        "MaxUtilisation", "MaxSkinK", "HeatShare", "MaxWingStrain", "SteerCap",
        "ControlValid", "ControlDemandX", "ControlDemandY", "ControlDemandZ", "AttitudeAware",
        "GyroTorqueX", "GyroTorqueY", "GyroTorqueZ", "ThrusterTorqueX", "ThrusterTorqueY", "ThrusterTorqueZ",
        "ReachX", "ReachY", "ReachZ", "NeedX", "NeedY", "NeedZ", "DeliveredX", "DeliveredY", "DeliveredZ",
        "SurfacesCommanded", "SurfacesSaturated", "AeroApplied", "EntryStrength",
    };

    /// <summary>(version 7) The flight state's value names, in index order (a new array each call: call it once). The
    /// length is the state's length in this version; later versions only append.</summary>
    public static string[] GetFlightStateNames() => (string[])FlightNames.Clone();

    /// <summary>(version 7) A flight-state value's index by name, -1 for none (look your indices up once).</summary>
    public static int GetFlightStateIndex(string name) => name == null ? -1 : Array.IndexOf(FlightNames, name);

    /// <summary>(version 7) THE GRID'S FLIGHT STATE, the last simulation tick's, as one packed float array (the layout:
    /// docs/API.md, GetFlightStateNames): the air, the motion, mass and gravity, the applied aerodynamic force and its torque
    /// split (airframe / control surfaces), the warnings (stall, loss of control), the structure's gauges, the attitude
    /// law's torques and authority. Fills min(into.Length, the state's length) values; returns how many (0: nothing
    /// published - not watched until now (the first read starts it: an answer from the next tick), no aero here, or a
    /// remote client). Any thread; no allocation.</summary>
    public static int GetFlightState(Keen.VRage.DCS.Components.Entity grid, float[] into)
    {
        if (into == null) return 0;
        var s = Watch(grid, ApiGridState.RecFlight);
        if (s == null) return 0;
        lock (s.Lock)
        {
            if (s.FlightStamp == 0) return 0;
            int n = Math.Min(into.Length, FlightStateLength);
            Array.Copy(s.Flight, into, n);
            if (n > FsAge) into[FsAge] = (float)((System.Diagnostics.Stopwatch.GetTimestamp() - s.FlightStamp) / (double)System.Diagnostics.Stopwatch.Frequency);
            return n;
        }
    }

    /// <summary>(version 7) The grid's control surfaces on the last tick: block cell, command (-1..1: the pilot's and the
    /// allocator's together), deflection (deg: -command x its maximum, ControlSurface.DeflectionAngle), lift and drag (N). Arrays may be null; fills min(length, count) of each. Returns the count (0: none, or not published
    /// yet - see GetFlightState). Any thread; no allocation.</summary>
    public static int GetControlSurfaces(Keen.VRage.DCS.Components.Entity grid, Vector3I[] cells, float[] commands, float[] deflectionsDeg, float[] liftN, float[] dragN)
    {
        var s = Watch(grid, ApiGridState.RecFlight);
        if (s == null) return 0;
        lock (s.Lock)
        {
            int n = s.SurfCount;
            if (cells != null) Array.Copy(s.SurfCell, cells, Math.Min(n, cells.Length));
            if (commands != null) Array.Copy(s.SurfCmd, commands, Math.Min(n, commands.Length));
            if (deflectionsDeg != null) Array.Copy(s.SurfDeg, deflectionsDeg, Math.Min(n, deflectionsDeg.Length));
            if (liftN != null) Array.Copy(s.SurfLift, liftN, Math.Min(n, liftN.Length));
            if (dragN != null) Array.Copy(s.SurfDrag, dragN, Math.Min(n, dragN.Length));
            return n;
        }
    }

    /// <summary>(version 7) Each thruster's force on the last tick (N, along its thrust direction), in the order of
    /// <see cref="GetThrusters"/>; thrusterList: the thruster list's generation (it moves when the thrusters are listed
    /// again - read GetThrusters again then). Fills min(length, count); returns the count (0: none, or not published yet).
    /// The attitude law's forces when it flies the ship (ThrustTorque), else the game's. Any thread; no allocation.</summary>
    public static int GetThrusterForces(Keen.VRage.DCS.Components.Entity grid, float[] forceN, out int thrusterList)
    {
        thrusterList = 0;
        var s = Watch(grid, ApiGridState.RecFlight);
        if (s == null) return 0;
        lock (s.Lock)
        {
            thrusterList = s.ThrGen;
            if (forceN != null) Array.Copy(s.ThrForce, forceN, Math.Min(s.ThrCount, forceN.Length));
            return s.ThrCount;
        }
    }
}
