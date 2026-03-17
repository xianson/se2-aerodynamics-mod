using System;
using Keen.VRage.Core;
using Keen.VRage.Core.Game.GameSystems.OWT;
using Keen.VRage.Core.Game.RuntimeSystems.DebugDraw;
using Keen.VRage.Core.Render;
using Keen.VRage.Core.Systems;
using Keen.VRage.DCS.Annotations;

#pragma warning disable
namespace AeroMod;

/// <summary>
/// Debug draw for aero data: per-face pressure, force vectors, wing visualization.
/// </summary>
public partial class AeroGridComponent
{
    private const float ForceScale = 1f / 50000f;
    private const float VectorScale = 0.05f; // m/s → draw length
    private const int MaxFacesToDraw = 20000; // limit for perf

[After(typeof(RenderSubmissionBegin))]
    private class OnAeroDraw : JobGroup;

    [OnAeroDraw]
    [MustHave(typeof(AeroGridComponent))]
    [OnChanged(typeof(ObservedWorldTransform))]
    private static void AeroDrawJob(
        AeroGridComponent aero,
        ObservedWorldTransform owt,
        IDebugDrawProvider ddp)
    {
        var wt = owt.Transform;
        if (!aero._initialized) return;

        // Read physics data via reflection hack
        PhysicsHack.TryGetVelocity(aero.Data, out Vector3 linVel, out Vector3 angVel);
        PhysicsHack.TryGetMassProperties(aero.Data, out float mass, out Vector3 com);
        float density = 0f;
        if (aero.Data.TryGet<AirData>(out var air))
            density = air.Density;

        // Trigger aero computation
        aero.TryCompute(wt, density, linVel, angVel, com);

        var dd = ddp.GlobalBuilder;
        float speed = linVel.Length();

        // ── Info text ──
        Vector3 up = WorldTransform.TransformDirection(Vector3.UnitY, wt);
        Vector3D textPos = wt.Position + (Vector3D)(up * 5f);

        int faces = aero._surface?.FaceCount ?? 0;
        int wings = aero._model?.Wings?.Count ?? 0;

        if (aero.HasResult)
        {
            var r = aero.LastResult;
            // Decompose in world space for display
            Vector3 fWorld = WorldTransform.TransformDirection(r.Force, wt);
            float fDotV = speed > 0.1f ? Vector3.Dot(fWorld, linVel / speed) : 0f;
            Vector3 liftW = fWorld - fDotV * (speed > 0.1f ? linVel / speed : Vector3.Zero);

            float dragN = MathF.Abs(fDotV);
            float liftN = liftW.Length();
            float ld = dragN > 1f ? liftN / dragN : 0f;
            float weight = mass * 9.81f;
            float lw = weight > 1f ? liftN / weight : 0f;

            dd.AddText(textPos,
                $"AERO faces={faces} wings={wings} mass={mass:F0}kg\n" +
                $"v={speed:F1}m/s M={r.Mach:F2} q={r.DynamicPressure:F0}Pa\n" +
                $"D={dragN:F0}N L={liftN:F0}N F={r.Force.Length():F0}N\n" +
                $"L/D={ld:F2} L/W={lw:F2} d={density:F4}",
                ColorSRGB.White, 0.5f);

            // ── Force vectors ──
            DrawForceVectors(aero, dd, wt, linVel, com);
        }
        else
        {
            dd.AddText(textPos,
                $"AERO faces={faces} wings={wings}\nv={speed:F1} d={density:F4}",
                ColorSRGB.White, 0.5f);
        }

        // ── Per-face pressure ──
        if (aero.HasResult && aero._surface != null && speed > 1f)
            DrawFacePressure(aero, dd, wt, density, speed);

        // ── Wings ──
        // ── Wings ──
        DrawWingInfo(aero, dd, wt);

        // ── Apply forces + torques ──
        if (aero.HasResult && mass > 0f)
        {
            Vector3 worldForce = WorldTransform.TransformDirection(aero.LastResult.Force, wt);
            float dt = 1f / 60f;
            Vector3 deltaV = worldForce * (dt / mass);
            PhysicsHack.ApplyDeltaVAndTorque(aero.Data, deltaV, aero.LastResult.Torque, dt);
        }
    }

    private static void DrawForceVectors(AeroGridComponent aero, MeshBuilder dd,
        in WorldTransform wt, Vector3 linVel, Vector3 com)
    {
        var r = aero.LastResult;
        // r.Force and r.Torque are in grid-LOCAL space. linVel is in WORLD space.

        // CoM in world space
        Vector3D comWorld = WorldTransform.Transform((Vector3D)com, in wt);

        // Velocity vector (cyan) — already world space, drawn at CoM
        if (linVel.LengthSquared() > 1f)
        {
            dd.AddArrow(comWorld, comWorld + (Vector3D)(linVel * VectorScale),
                ColorSRGB.Cyan, null, 0.15);
        }

        // Total force (yellow) — transform from local to world
        Vector3 forceWorld = WorldTransform.TransformDirection(r.Force, wt);
        dd.AddArrow(comWorld, comWorld + (Vector3D)(forceWorld * ForceScale),
            ColorSRGB.Yellow, null, 0.15);

        // Decompose force into drag and lift in WORLD space
        float speed = linVel.Length();
        if (speed > 0.1f)
        {
            Vector3 vHatWorld = linVel / speed;

            // Drag (red)
            float forceDotV = Vector3.Dot(forceWorld, vHatWorld);
            Vector3 dragWorld = forceDotV * vHatWorld;
            dd.AddArrow(comWorld, comWorld + (Vector3D)(dragWorld * ForceScale),
                ColorSRGB.Red, null, 0.12);

            // Lift (green)
            Vector3 liftWorld = forceWorld - dragWorld;
            if (liftWorld.LengthSquared() > 0.01f)
            {
                dd.AddArrow(comWorld, comWorld + (Vector3D)(liftWorld * ForceScale),
                    ColorSRGB.LimeGreen, null, 0.12);
            }
        }

        // ── Center of Pressure ──
        // CoP = CoM + (Torque × Force) / |Force|²  (local space)
        float forceSq = r.Force.LengthSquared();
        if (forceSq > 1f)
        {
            Vector3 copOffset = Vector3.Cross(r.Torque, r.Force) / forceSq;
            Vector3D copWorld = WorldTransform.Transform((Vector3D)(com + copOffset), in wt);

            // CoP marker — magenta sphere-like cross
            float markerSize = 0.3f;
            dd.AddLine(copWorld - (Vector3D)(WorldTransform.TransformDirection(Vector3.UnitX, wt) * markerSize),
                       copWorld + (Vector3D)(WorldTransform.TransformDirection(Vector3.UnitX, wt) * markerSize),
                       ColorSRGB.Magenta, 3f);
            dd.AddLine(copWorld - (Vector3D)(WorldTransform.TransformDirection(Vector3.UnitY, wt) * markerSize),
                       copWorld + (Vector3D)(WorldTransform.TransformDirection(Vector3.UnitY, wt) * markerSize),
                       ColorSRGB.Magenta, 3f);
            dd.AddLine(copWorld - (Vector3D)(WorldTransform.TransformDirection(Vector3.UnitZ, wt) * markerSize),
                       copWorld + (Vector3D)(WorldTransform.TransformDirection(Vector3.UnitZ, wt) * markerSize),
                       ColorSRGB.Magenta, 3f);

            // Line from CoM to CoP
            dd.AddLine(comWorld, copWorld, ColorSRGB.Magenta, 1.5f);

            dd.AddText(copWorld, "CoP", ColorSRGB.Magenta, 0.3f);
        }

        // CoM marker
        dd.AddText(comWorld, "CoM", ColorSRGB.White, 0.3f);
    }

    /// <summary>
    /// Front-face color scale: t ∈ [0,1] → cyan → green → yellow → red.
    /// Used for positive Cp faces only.
    /// </summary>
    private static ColorSRGB CpToColorFront(float t)
    {
        t = MathF.Max(0f, MathF.Min(1f, t));
        float r, g, b;
        if (t < 0.333f)
        {
            // cyan → green
            float s = t / 0.333f;
            r = 0f;
            g = 0.8f + s * 0.2f;
            b = 1f - s;
        }
        else if (t < 0.667f)
        {
            // green → yellow
            float s = (t - 0.333f) / 0.334f;
            r = s;
            g = 1f;
            b = 0f;
        }
        else
        {
            // yellow → red
            float s = (t - 0.667f) / 0.333f;
            r = 1f;
            g = 1f - s;
            b = 0f;
        }
        return new ColorSRGB(r, g, b);
    }

    /// <summary>
    /// Compute tangent vectors for a face normal (axis-aligned normals only).
    /// Returns two perpendicular unit vectors spanning the face plane.
    /// </summary>
    /// <summary>
    /// Get tangent vectors for quad placement. Snaps to nearest axis for consistent
    /// quad orientation even when normals are smoothed off-axis.
    /// Winding order ensures cross(t1, t2) aligns with the dominant normal direction.
    /// </summary>
    private static void GetFaceTangents(Vector3 normal, out Vector3 t1, out Vector3 t2)
    {
        float ax = MathF.Abs(normal.X);
        float ay = MathF.Abs(normal.Y);
        float az = MathF.Abs(normal.Z);

        // Tangent pairs chosen so cross(t1, t2) aligns with the face normal direction.
        // X-dominant: cross(±Y, Z) = ±X  ✓
        // Y-dominant: cross(±Z, X) = ±Y  ✓
        // Z-dominant: cross(±X, Y) = ±Z  ✓
        if (ax >= ay && ax >= az)
        {
            float sign = normal.X >= 0 ? 1f : -1f;
            t1 = Vector3.UnitY * sign;
            t2 = Vector3.UnitZ;
        }
        else if (ay >= ax && ay >= az)
        {
            // Was t1=X, t2=±Z which gives cross(X,Z)=-Y — inverted winding!
            float sign = normal.Y >= 0 ? 1f : -1f;
            t1 = Vector3.UnitZ * sign;
            t2 = Vector3.UnitX;
        }
        else
        {
            float sign = normal.Z >= 0 ? 1f : -1f;
            t1 = Vector3.UnitX * sign;
            t2 = Vector3.UnitY;
        }
    }

    private static void DrawFacePressure(AeroGridComponent aero, MeshBuilder dd,
        in WorldTransform wt, float density, float speed)
    {
        var faces = aero._surface.Faces;
        if (faces == null || faces.Count == 0) return;

        // Get real per-face Cp from the drag model
        var dragModel = (aero._model?.InnerModel as DampedShadowedDragModel);
        if (dragModel == null) return;

        var faceCp = dragModel.FaceCp;
        if (faceCp == null || faceCp.Count < faces.Count) return;

        int step = Math.Max(1, faces.Count / MaxFacesToDraw);

        // Cell size for quad extents (SE2 cells are 0.25m)
        const float HalfCell = 0.125f;
        const float NormalOffset = 0.03f;

        // Find max front Cp for normalization
        float frontMax = 0f;
        for (int i = 0; i < faceCp.Count; i += step)
        {
            float cp = faceCp[i];
            if (cp > frontMax) frontMax = cp;
        }

        float invFrontMax = frontMax > 0.001f ? 1f / frontMax : 1f;

        // Draw colored quads for front-facing faces only
        for (int i = 0; i < faces.Count; i += step)
        {
            var face = faces[i];
            float cp = faceCp[i];

            // Only draw faces that produce force (Cp > 0)
            if (cp <= 0f) continue;

            float t = MathF.Sqrt(cp * invFrontMax);
            ColorSRGB color = CpToColorFront(t);

            // Compute quad corners in local space
            GetFaceTangents(face.Normal, out Vector3 tan1, out Vector3 tan2);

            // Offset position slightly along normal to sit above surface
            Vector3 pos = face.Position + face.Normal * NormalOffset;

            // 4 corners of the face quad (local space, full cell size)
            Vector3 p0Local = pos + (-tan1 - tan2) * HalfCell;
            Vector3 p1Local = pos + ( tan1 - tan2) * HalfCell;
            Vector3 p2Local = pos + ( tan1 + tan2) * HalfCell;
            Vector3 p3Local = pos + (-tan1 + tan2) * HalfCell;

            // Transform to world
            Vector3D p0 = WorldTransform.Transform((Vector3D)p0Local, in wt);
            Vector3D p1 = WorldTransform.Transform((Vector3D)p1Local, in wt);
            Vector3D p2 = WorldTransform.Transform((Vector3D)p2Local, in wt);
            Vector3D p3 = WorldTransform.Transform((Vector3D)p3Local, in wt);

            // Engine convention: quad internal normal must point inward (away from viewer).
            // Swapping p1↔p3 reverses winding so the visible side faces outward.
            dd.AddQuadClockWise(p0, p3, p2, p1, color, false);
        }
    }


    private static void DrawWingInfo(AeroGridComponent aero, MeshBuilder dd, in WorldTransform wt)
    {
        var wings = aero._model?.Wings;
        if (wings == null || wings.Count == 0) return;

        var wingForces = aero._model?.LastWingForces;

        for (int i = 0; i < wings.Count; i++)
        {
            var wing = wings[i];
            Vector3D centroidWorld = WorldTransform.Transform((Vector3D)wing.Centroid, in wt);
            Vector3 normalWorld = WorldTransform.TransformDirection(wing.Normal, wt);

            string forceInfo = "";
            if (wingForces != null && i < wingForces.Count)
            {
                var wf = wingForces[i];
                float liftMag = wf.LiftForce.Length();
                float dragMag = wf.InducedDrag.Length();
                float wLD = dragMag > 1f ? liftMag / dragMag : 0f;
                forceInfo = $"\nCL={wf.CL:F3} CDi={wf.CDi:F4} a={wf.AlphaEffective * 180f / MathF.PI:F1}°" +
                            $"\nL={liftMag:F0}N Di={dragMag:F0}N L/D={wLD:F1}";

                // Lift force vector (green)
                Vector3 liftWorld = WorldTransform.TransformDirection(wf.LiftForce, wt);
                if (liftWorld.LengthSquared() > 1f)
                {
                    dd.AddArrow(centroidWorld,
                        centroidWorld + (Vector3D)(liftWorld * ForceScale),
                        ColorSRGB.LimeGreen, null, 0.1);
                }

                // Induced drag vector (red)
                Vector3 dragWorld = WorldTransform.TransformDirection(wf.InducedDrag, wt);
                if (dragWorld.LengthSquared() > 1f)
                {
                    dd.AddArrow(centroidWorld,
                        centroidWorld + (Vector3D)(dragWorld * ForceScale),
                        ColorSRGB.Red, null, 0.08);
                }
            }

            dd.AddText(centroidWorld + (Vector3D)(normalWorld * 0.5f),
                $"W{i} AR={wing.AspectRatio:F1} S={wing.PlanformArea:F1}m²\n" +
                $"span={wing.Span:F1}m t/c={wing.ThicknessRatio:F2}" +
                forceInfo,
                ColorSRGB.DodgerBlue, 0.4f);
        }
    }

    // ── Cached values for debug ──
    private Vector3 _lastVelocityLocal;
    private Vector3 _lastComLocal;
    private float _lastDensity;
}
