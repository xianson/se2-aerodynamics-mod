#pragma warning disable
using System;
using Keen.VRage.Core;
namespace AeroMod;

// THE PUBLIC API'S TICK RECORD (AeroApi version 7: Api/AeroApi.Flight.cs): this grid's flight state, control surfaces and
// thruster forces, copied at the end of its simulation tick (AeroSimJob, after the structure's step) - only while someone
// reads them (ApiGridState.Watch.cs: RecFlight; the hull's faces RecHull). Simulation thread; no allocation once the arrays
// have grown.
public partial class AeroGridComponent
{
    /// <summary>The API's copies of this grid (Api/ApiGridState.cs); null until the API first asks about it.</summary>
    internal volatile ApiGridState ApiState;

    /// <summary>The API's copies, made on the first ask (any thread).</summary>
    internal ApiGridState ApiStateFor()
    {
        var s = ApiState;
        if (s != null) return s;
        System.Threading.Interlocked.CompareExchange(ref ApiState, new ApiGridState(), null);
        return ApiState;
    }

    /// <summary>Someone reads this grid's tick record (the simulation tick's test: one null test when nobody does).</summary>
    internal bool ApiWatched { get { var s = ApiState; return s != null && s.TickWatched(System.Diagnostics.Stopwatch.GetTimestamp()); } }

    /// <summary>The tick record (AeroSimJob, simulation thread): airVel / linVel the air past the grid and its motion (world
    /// m/s), appliedF / appliedT the air's force and torque applied this tick (local, the world's multipliers in; zero:
    /// none), gravity (world m/s2).</summary>
    internal void PublishApiTick(Vector3 airVel, Vector3 linVel, Vector3 appliedF, Vector3 appliedT, Vector3 gravity, in WorldTransform wt)
    {
        var s = ApiState;
        if (s == null) return;
        long now = System.Diagnostics.Stopwatch.GetTimestamp();
        // (each record while its own reader reads it: GetSurface alone no longer copies the flight record, 2026-10-15 review)
        if (s.On(ApiGridState.RecFlight, now)) PublishApiFlight(s, airVel, linVel, appliedF, appliedT, gravity, wt);
        if (s.On(ApiGridState.RecHull, now)) PublishApiHull(s, now);
    }

    void PublishApiFlight(ApiGridState s, Vector3 airVel, Vector3 linVel, Vector3 appliedF, Vector3 appliedT, Vector3 gravity, in WorldTransform wt)
    {
        var f = s.FlightBack;
        float airspeed = airVel.Length();
        float mach = HasResult ? (float)LastResult.Mach : 0f;
        f[AeroApi.FsAge] = 0f;
        f[AeroApi.FsAirspeed] = airspeed; f[AeroApi.FsGroundSpeed] = linVel.Length();
        f[AeroApi.FsMach] = mach; f[AeroApi.FsDensity] = LastDensity;
        f[AeroApi.FsDynamicPressure] = HasResult ? (float)LastResult.DynamicPressure : 0f;
        Put(f, AeroApi.FsTravel, airspeed > 0.1f ? WorldTransform.TransformDirectionInv(airVel / airspeed, wt) : Vector3.Zero);
        Put(f, AeroApi.FsWind, LastWind);
        Put(f, AeroApi.FsAngVel, WorldTransform.TransformDirectionInv(LastAngVel, wt));
        f[AeroApi.FsMass] = LastMass; Put(f, AeroApi.FsCom, LastCoM);
        f[AeroApi.FsGroundHeight] = GroundHeight; Put(f, AeroApi.FsUp, GroundUpLocal); f[AeroApi.FsGravity] = gravity.Length();
        Put(f, AeroApi.FsForce, appliedF); Put(f, AeroApi.FsTorque, appliedT);
        Put(f, AeroApi.FsAirframeTorque, LastAeroTorque); Put(f, AeroApi.FsSurfaceTorque, LastSurfaceTorque);
        f[AeroApi.FsDrag] = HasResult ? LastResult.DragMagnitude : 0f; f[AeroApi.FsLift] = HasResult ? LastResult.LiftMagnitude : 0f;
        f[AeroApi.FsFrontalArea] = HasResult ? LastResult.FrontalArea : 0f;
        f[AeroApi.FsLoadFactor] = LastMass > 0f ? appliedF.Length() / (LastMass * 9.81f) : 0f;
        f[AeroApi.FsStallState] = StallState; f[AeroApi.FsStallMargin] = StallMarginDeg; f[AeroApi.FsStallLiftShare] = StallLiftInUse; f[AeroApi.FsStallG] = StallGAvailable;
        f[AeroApi.FsLoacState] = LoacState; f[AeroApi.FsLoacRatio] = LoacRatio;
        f[AeroApi.FsMaxUtil] = StructMaxUtil; f[AeroApi.FsMaxSkinK] = StructMaxSkinK; f[AeroApi.FsHeatShare] = StructHeatShare;
        f[AeroApi.FsMaxWingStrain] = MaxWingStrain; f[AeroApi.FsSteerCap] = StructSteerCap;
        f[AeroApi.FsControlValid] = LastControlTorqueValid ? 1f : 0f; Put(f, AeroApi.FsControlDemand, LastControlTorque);
        var r = LastThrust;
        f[AeroApi.FsAware] = r.Aware ? 1f : 0f;
        Put(f, AeroApi.FsGyroTorque, r.GyroTorque); Put(f, AeroApi.FsThrusterTorque, r.RcsTorque); Put(f, AeroApi.FsReach, r.Reach);
        Put(f, AeroApi.FsNeed, r.Need); Put(f, AeroApi.FsDelivered, r.Delivered);
        f[AeroApi.FsSurfacesCommanded] = _allocN; f[AeroApi.FsSurfacesSaturated] = _allocSat;
        f[AeroApi.FsApplied] = AeroForcesApplied ? 1f : 0f;
        f[AeroApi.FsEntryStrength] = AeroEntryFx.EntryStrength(mach, LastDensity);

        // the control surfaces (the registry is this thread's)
        int ns = 0;
        var comps = _components?.Components;
        if (comps != null)
        {
            int nc = comps.Count;
            s.SurfCellB = ApiGridState.Fit(s.SurfCellB, nc); s.SurfCmdB = ApiGridState.Fit(s.SurfCmdB, nc); s.SurfDegB = ApiGridState.Fit(s.SurfDegB, nc);
            s.SurfLiftB = ApiGridState.Fit(s.SurfLiftB, nc); s.SurfDragB = ApiGridState.Fit(s.SurfDragB, nc);
            for (int i = 0; i < nc; i++)
            {
                if (comps[i] is not ControlSurface c) continue;
                s.SurfCellB[ns] = c.BlockPosition; s.SurfCmdB[ns] = c.DeflectionInput; s.SurfDegB[ns] = c.DeflectionAngle * (180f / MathF.PI);
                s.SurfLiftB[ns] = c.CurrentLift; s.SurfDragB[ns] = c.CurrentDrag; ns++;
            }
        }
        // the thrusters' forces (the attitude law's, last tick: ThrustTorque.Apply), _thrusterCache order
        var shares = LastThrustShares;
        // (this list's shares only: right after a rebuild they are the old list's - LastThrustGen, 2026-10-15)
        int nt = shares != null && LastThrustGen == _thrusterGen ? Math.Min(Math.Min(LastThrustCount, shares.Length), _thrusterCache.Count) : 0;
        s.ThrForceB = ApiGridState.Fit(s.ThrForceB, nt);
        if (nt > 0) Array.Copy(shares, s.ThrForceB, nt);

        long now = System.Diagnostics.Stopwatch.GetTimestamp();
        lock (s.Lock)
        {
            ApiGridState.Swap(ref s.Flight, ref s.FlightBack); s.FlightStamp = now;
            ApiGridState.Swap(ref s.SurfCell, ref s.SurfCellB); ApiGridState.Swap(ref s.SurfCmd, ref s.SurfCmdB); ApiGridState.Swap(ref s.SurfDeg, ref s.SurfDegB);
            ApiGridState.Swap(ref s.SurfLift, ref s.SurfLiftB); ApiGridState.Swap(ref s.SurfDrag, ref s.SurfDragB); s.SurfCount = ns;
            ApiGridState.Swap(ref s.ThrForce, ref s.ThrForceB); s.ThrCount = nt; s.ThrGen = _thrusterGen;
        }
    }

    /// <summary>The hull's faces for AeroApi.GetSurface (simulation thread, while it is asked for): when the surface changed,
    /// else at most every HullEvery s (the face model's Cp moves each tick). No allocation once the arrays have grown.</summary>
    void PublishApiHull(ApiGridState s, long now)
    {
        var surf = _surface;
        if (surf != null && !_staggeredBuildActive && ReferenceEquals(surf, s.HullOf) && surf.Version == s.HullVersion
            && now - s.HullAt < (long)(HullEvery * System.Diagnostics.Stopwatch.Frequency)) return;
        int n = 0;
        if (surf != null && !_staggeredBuildActive)
        {
            var faces = surf.Faces;
            var cps = (_model?.InnerModel as DampedShadowedDragModel)?.FaceCp;
            n = faces.Count;
            s.HullPosB = ApiGridState.Fit(s.HullPosB, n); s.HullNrmB = ApiGridState.Fit(s.HullNrmB, n);
            s.HullAreaB = ApiGridState.Fit(s.HullAreaB, n); s.HullCpB = ApiGridState.Fit(s.HullCpB, n);
            for (int i = 0; i < n; i++)
            {
                var f = faces[i];
                s.HullPosB[i] = f.Position; s.HullNrmB[i] = f.Normal; s.HullAreaB[i] = f.Area;
                s.HullCpB[i] = cps != null && i < cps.Count ? cps[i] : 0f;
            }
        }
        lock (s.Lock)
        {
            ApiGridState.Swap(ref s.HullPos, ref s.HullPosB); ApiGridState.Swap(ref s.HullNrm, ref s.HullNrmB);
            ApiGridState.Swap(ref s.HullArea, ref s.HullAreaB); ApiGridState.Swap(ref s.HullCp, ref s.HullCpB);
            s.HullCount = n;
        }
        s.HullOf = _staggeredBuildActive ? null : surf; s.HullVersion = surf?.Version ?? -1; s.HullAt = now;
    }
    /// <summary>How often (s) the published faces' Cp is refreshed while GetSurface is read.</summary>
    internal static float HullEvery = 0.25f;

    static void Put(float[] f, int i, Vector3 v) { f[i] = v.X; f[i + 1] = v.Y; f[i + 2] = v.Z; }
}
