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
/// Visualization only — physics computation and force application are in AeroSimJob.cs.
/// </summary>
public partial class AeroGridComponent
{
    private const float ForceScale = 1f / 50000f;
    private const float VectorScale = 0.05f; // m/s → draw length
    private const int MaxFacesToDraw = 20000; // limit for perf
    private const float DebugDrawMaxDistance = 200f;

    /// <summary>Master toggle for all debug drawing. DiagActive grids always draw.</summary>
    internal static bool EnableDebugDraw = false;

    /// <summary>Set by the active player grid each frame so other grids can cull debug draw.</summary>
    internal static Vector3D DebugFocusPosition;

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

        // Read cached physics state (written by AeroSimJob at 60Hz)
        Vector3 linVel = aero.LastLinVel;
        Vector3 com = aero.LastCoM;
        float mass = aero.LastMass;
        float speed = aero.LastSpeed;
        float density = aero.LastDensity;

        // Skip debug drawing for grids far from focus
        double distSq = (wt.Position - DebugFocusPosition).LengthSquared();
        bool drawDebug = distSq < DebugDrawMaxDistance * DebugDrawMaxDistance;

        if (drawDebug && (EnableDebugDraw || aero.DiagActive))
        {
            var dd = ddp.GlobalBuilder;

            // ── Info text ──
            Vector3 up = WorldTransform.TransformDirection(Vector3.UnitY, wt);
            Vector3D textPos = wt.Position + (Vector3D)(up * 5f);

            int faces = aero._surface?.FaceCount ?? 0;
            int wings = aero._model?.Wings?.Count ?? 0;

            if (aero.HasResult)
            {
                var r = aero.LastResult;
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

                DrawForceVectors(aero, dd, wt, linVel, com);
            }
            else
            {
                dd.AddText(textPos,
                    $"AERO faces={faces} wings={wings}\nv={speed:F1} d={density:F4}",
                    ColorSRGB.White, 0.5f);
            }

            DrawWingInfo(aero, dd, wt);
            DrawAeroComponents(aero, dd, wt, linVel);
        }

        // Stats are committed by AeroSimJob at 60Hz — draw job is visualization only.
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

        // ── Center of Pressure (smoothed) ──
        // CoP = CoM + (Force × Torque) / |Force|²  (local space)
        // Derivation: T = r × F → r_perp = (F × T) / |F|²
        float forceSq = r.Force.LengthSquared();
        if (forceSq > 1f)
        {
            Vector3 copOffset = Vector3.Cross(r.Force, r.Torque) / forceSq;
            Vector3 copLocal = com + copOffset;

            // Lerp in local space for smooth motion
            const float LerpRate = 0.1f;
            if (!aero._copInitialized)
            {
                aero._smoothedCopLocal = copLocal;
                aero._copInitialized = true;
            }
            else
            {
                aero._smoothedCopLocal = Vector3.Lerp(aero._smoothedCopLocal, copLocal, LerpRate);
            }

            Vector3D copWorld = WorldTransform.Transform((Vector3D)aero._smoothedCopLocal, in wt);

            // CoP marker — solid sphere, always on top
            dd.AddSphere(new WorldTransform(copWorld), 0.35, cFill: ColorSRGB.Magenta, depth: false);

            dd.AddText(copWorld, "CoP", ColorSRGB.Magenta, 0.3f);
        }

        // CoM marker — solid sphere, always on top
        dd.AddSphere(new WorldTransform(comWorld), 0.4, cFill: ColorSRGB.Yellow, depth: false);
        dd.AddText(comWorld, "CoM", ColorSRGB.Yellow, 0.3f);
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

        var dragModel = (aero._model?.InnerModel as DampedShadowedDragModel);
        if (dragModel == null) return;

        var faceCp = dragModel.FaceCp;
        if (faceCp == null || faceCp.Count < faces.Count) return;

        int step = Math.Max(1, faces.Count / MaxFacesToDraw);

        const float HalfCell = 0.125f;
        const float NormalOffset = 0.03f;

        // ── Pre-compute rotation matrix from quaternion (once) ──
        Quaternion q = wt.Orientation;
        float x2 = q.X + q.X, y2 = q.Y + q.Y, z2 = q.Z + q.Z;
        float xx = q.X * x2, xy = q.X * y2, xz = q.X * z2;
        float yy = q.Y * y2, yz = q.Y * z2, zz = q.Z * z2;
        float wx = q.W * x2, wy = q.W * y2, wz = q.W * z2;
        float m00 = 1f - (yy + zz), m01 = xy - wz,        m02 = xz + wy;
        float m10 = xy + wz,        m11 = 1f - (xx + zz),  m12 = yz - wx;
        float m20 = xz - wy,        m21 = yz + wx,         m22 = 1f - (xx + yy);
        double wpx = wt.Position.X, wpy = wt.Position.Y, wpz = wt.Position.Z;

        // ── Pre-compute 4 corner offsets per axis direction (6 dirs × 4 corners) ──
        // Tangent pairs per axis-aligned normal (same logic as GetFaceTangents)
        Span<Vector3> normals = stackalloc Vector3[6];
        normals[0] = Vector3.UnitX;  normals[1] = -Vector3.UnitX;
        normals[2] = Vector3.UnitY;  normals[3] = -Vector3.UnitY;
        normals[4] = Vector3.UnitZ;  normals[5] = -Vector3.UnitZ;

        // For each direction: 4 local corner offsets (already rotated to world)
        Span<Vector3D> wOff0 = stackalloc Vector3D[6];
        Span<Vector3D> wOff1 = stackalloc Vector3D[6];
        Span<Vector3D> wOff2 = stackalloc Vector3D[6];
        Span<Vector3D> wOff3 = stackalloc Vector3D[6];

        for (int d = 0; d < 6; d++)
        {
            GetFaceTangents(normals[d], out Vector3 t1, out Vector3 t2);
            Vector3 nOff = normals[d] * NormalOffset;

            Vector3 c0 = nOff + (-t1 - t2) * HalfCell;
            Vector3 c1 = nOff + ( t1 - t2) * HalfCell;
            Vector3 c2 = nOff + ( t1 + t2) * HalfCell;
            Vector3 c3 = nOff + (-t1 + t2) * HalfCell;

            // Rotate offsets to world (no translation — these are offsets)
            wOff0[d] = RotateByMatrix(c0, m00, m01, m02, m10, m11, m12, m20, m21, m22);
            wOff1[d] = RotateByMatrix(c1, m00, m01, m02, m10, m11, m12, m20, m21, m22);
            wOff2[d] = RotateByMatrix(c2, m00, m01, m02, m10, m11, m12, m20, m21, m22);
            wOff3[d] = RotateByMatrix(c3, m00, m01, m02, m10, m11, m12, m20, m21, m22);
        }

        // ── Find max Cp for normalization (strided scan) ──
        float frontMax = 0f;
        for (int i = 0; i < faceCp.Count; i += step)
        {
            float cp = faceCp[i];
            if (cp > frontMax) frontMax = cp;
        }
        float invFrontMax = frontMax > 0.001f ? 1f / frontMax : 1f;

        // ── Draw quads ──
        for (int i = 0; i < faces.Count; i += step)
        {
            float cp = faceCp[i];
            if (cp <= 0f) continue;

            var face = faces[i];
            float t = MathF.Sqrt(cp * invFrontMax);
            ColorSRGB color = CpToColorFront(t);

            // Classify face to nearest axis direction
            int dir = FaceDirIndex(face.Normal);

            // Rotate face position to world, add world origin
            float px = face.Position.X, py = face.Position.Y, pz = face.Position.Z;
            double wx0 = wpx + (m00 * px + m01 * py + m02 * pz);
            double wy0 = wpy + (m10 * px + m11 * py + m12 * pz);
            double wz0 = wpz + (m20 * px + m21 * py + m22 * pz);

            Vector3D p0 = new(wx0 + wOff0[dir].X, wy0 + wOff0[dir].Y, wz0 + wOff0[dir].Z);
            Vector3D p1 = new(wx0 + wOff1[dir].X, wy0 + wOff1[dir].Y, wz0 + wOff1[dir].Z);
            Vector3D p2 = new(wx0 + wOff2[dir].X, wy0 + wOff2[dir].Y, wz0 + wOff2[dir].Z);
            Vector3D p3 = new(wx0 + wOff3[dir].X, wy0 + wOff3[dir].Y, wz0 + wOff3[dir].Z);

            dd.AddQuadClockWise(p0, p3, p2, p1, color, true);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector3D RotateByMatrix(Vector3 v,
        float m00, float m01, float m02,
        float m10, float m11, float m12,
        float m20, float m21, float m22)
    {
        return new Vector3D(
            m00 * v.X + m01 * v.Y + m02 * v.Z,
            m10 * v.X + m11 * v.Y + m12 * v.Z,
            m20 * v.X + m21 * v.Y + m22 * v.Z);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int FaceDirIndex(Vector3 n)
    {
        float ax = MathF.Abs(n.X), ay = MathF.Abs(n.Y), az = MathF.Abs(n.Z);
        if (ax >= ay && ax >= az) return n.X >= 0 ? 0 : 1;
        if (ay >= az) return n.Y >= 0 ? 2 : 3;
        return n.Z >= 0 ? 4 : 5;
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

    private static void DrawAeroComponents(AeroGridComponent aero, MeshBuilder dd,
        in WorldTransform wt, Vector3 linVel)
    {
        var comps = aero._components;
        if (comps == null || comps.Count == 0) return;

        float speed = linVel.Length();

        for (int i = 0; i < comps.Components.Count; i++)
        {
            var comp = comps.Components[i];
            Vector3D posWorld = WorldTransform.Transform((Vector3D)comp.Position, in wt);

            if (comp is ControlSurface cs)
            {
                // Hinge axis (blue arrow)
                Vector3 hingeWorld = WorldTransform.TransformDirection(cs.HingeAxis, wt);
                dd.AddArrow(posWorld, posWorld + (Vector3D)(hingeWorld * 1.5f),
                    ColorSRGB.DodgerBlue, null, 0.08);

                // Undeflected chord direction (cyan arrow, thin)
                Vector3 chordWorld = WorldTransform.TransformDirection(cs.ChordDirection, wt);
                dd.AddArrow(posWorld, posWorld + (Vector3D)(chordWorld * 1.5f),
                    ColorSRGB.Cyan, null, 0.05);

                // Deflected chord direction (yellow arrow) — Rodrigues rotation
                float deflRad = -cs.DeflectionInput * cs.MaxDeflection * MathF.PI / 180f;
                float cosD = MathF.Cos(deflRad);
                float sinD = MathF.Sin(deflRad);
                Vector3 deflChord = cs.ChordDirection * cosD +
                    Vector3.Cross(cs.HingeAxis, cs.ChordDirection) * sinD +
                    cs.HingeAxis * Vector3.Dot(cs.HingeAxis, cs.ChordDirection) * (1f - cosD);
                Vector3 deflChordWorld = WorldTransform.TransformDirection(deflChord, wt);
                dd.AddArrow(posWorld, posWorld + (Vector3D)(deflChordWorld * 2.0f),
                    ColorSRGB.Yellow, null, 0.10);

                // Surface normal of deflected surface (green arrow)
                Vector3 surfNorm = Vector3.Cross(cs.HingeAxis, deflChord);
                float snLen = surfNorm.Length();
                if (snLen > 1e-6f) surfNorm /= snLen;
                Vector3 surfNormWorld = WorldTransform.TransformDirection(surfNorm, wt);
                dd.AddArrow(posWorld, posWorld + (Vector3D)(surfNormWorld * 1.5f),
                    ColorSRGB.LimeGreen, null, 0.10);

                // Resultant force vector (red arrow, scaled)
                // Recompute force direction from last Compute results
                if (speed > 1f)
                {
                    Vector3 localVel = aero._lastVelocityLocal;
                    float localSpeed = localVel.Length();
                    if (localSpeed > 0.1f)
                    {
                        Vector3 vHat = localVel / localSpeed;
                        // Lift direction: surfNormal component perpendicular to velocity
                        Vector3 liftDir = surfNorm - Vector3.Dot(surfNorm, vHat) * vHat;
                        float ldLen = liftDir.Length();
                        if (ldLen > 1e-6f) liftDir /= ldLen;

                        // Total force = lift + drag
                        Vector3 forceLocal = liftDir * cs.CurrentLift - vHat * cs.CurrentDrag;
                        float forceMag = forceLocal.Length();
                        if (forceMag > 1f)
                        {
                            Vector3 forceWorld = WorldTransform.TransformDirection(forceLocal, wt);
                            float fScale = MathF.Min(5f, forceMag * 0.0005f); // scale for visibility
                            dd.AddArrow(posWorld, posWorld + (Vector3D)(forceWorld * (fScale / forceMag)),
                                ColorSRGB.Red, null, 0.12);
                        }
                    }
                }

                // Show gridAngVel on each CS so we know the input at this exact frame
                Vector3 gAV = aero._lastGridAngVel;
                string info = $"CS#{i} defl={cs.DeflectionInput:F2}\nAoA={cs.EffectiveAoA:F1}°" +
                    $"\nL={cs.CurrentLift:F0}N D={cs.CurrentDrag:F0}N" +
                    $"\ngAV=({gAV.X:F2},{gAV.Y:F2},{gAV.Z:F2})";

                dd.AddText(posWorld + (Vector3D)(hingeWorld * 0.3f), info, ColorSRGB.Cyan, 0.35f);
            }
            else if (comp is Airbrake ab)
            {
                // Facing direction (orange)
                Vector3 facingWorld = WorldTransform.TransformDirection(ab.FacingDirection, wt);
                dd.AddArrow(posWorld, posWorld + (Vector3D)(facingWorld * 1.5f),
                    ColorSRGB.Orange, null, 0.08);

                string info = $"AB deploy={ab.DeployFraction:F2}\neff={ab.Effectiveness:F2}" +
                    $"\nD={ab.DragForce:F0}N Cp={ab.EffectiveCp:F3}";
                dd.AddText(posWorld + (Vector3D)(facingWorld * 0.3f), info, ColorSRGB.Orange, 0.35f);
            }
            else if (comp is AtmosphericScoop sc)
            {
                // Facing direction (green)
                Vector3 facingWorld = WorldTransform.TransformDirection(sc.FacingDirection, wt);
                dd.AddArrow(posWorld, posWorld + (Vector3D)(facingWorld * 1.5f),
                    ColorSRGB.LimeGreen, null, 0.08);

                string info = $"SCOOP mdot={sc.MassFlowRate:F2}kg/s" +
                    $"\ncoll={sc.CollectionRate:F2}kg/s" +
                    $"\nD={sc.DragForce:F0}N";
                dd.AddText(posWorld + (Vector3D)(facingWorld * 0.3f), info, ColorSRGB.LimeGreen, 0.35f);
            }
            else if (comp is AirIntake ai)
            {
                // Facing direction (white)
                Vector3 facingWorld = WorldTransform.TransformDirection(ai.FacingDirection, wt);
                dd.AddArrow(posWorld, posWorld + (Vector3D)(facingWorld * 1.5f),
                    ColorSRGB.White, null, 0.08);

                string info = $"INTAKE mdot={ai.MassFlowRate:F2}kg/s" +
                    $"\nPtot={ai.TotalPressure:F0}Pa ram={ai.RamPressureRatio:F2}";
                dd.AddText(posWorld + (Vector3D)(facingWorld * 0.3f), info, ColorSRGB.White, 0.35f);
            }
            else if (comp is HelicopterRotor rotor)
            {
                // Disc axis (magenta arrow)
                Vector3 discWorld = WorldTransform.TransformDirection(rotor.DiscAxis, wt);
                dd.AddArrow(posWorld, posWorld + (Vector3D)(discWorld * 2.5f),
                    ColorSRGB.Magenta, null, 0.12);

                // Thrust vector (green, scaled)
                if (rotor.CurrentThrust > 1f)
                {
                    Vector3 thrustWorld = WorldTransform.TransformDirection(rotor.DiscAxis * rotor.CurrentThrust, wt);
                    dd.AddArrow(posWorld, posWorld + (Vector3D)(thrustWorld * ForceScale),
                        ColorSRGB.LimeGreen, null, 0.10);
                }

                // Cyclic torque visualization (yellow arrows on pitch/roll axes)
                if (rotor.CyclicTorque.LengthSquared() > 100f)
                {
                    Vector3 cyclicWorld = WorldTransform.TransformDirection(rotor.CyclicTorque, wt);
                    float tScale = 1f / 2_000_000f; // torque → draw scale
                    dd.AddArrow(posWorld, posWorld + (Vector3D)(cyclicWorld * tScale),
                        ColorSRGB.Yellow, null, 0.08);
                }

                string spinDir = rotor.SpinSign > 0 ? "CCW" : "CW";
                string autoStr = rotor.AutorotationThrust > 1f ? $"\nAUTO={rotor.AutorotationThrust:F0}N" : "";
                string info = $"ROTOR({spinDir}) T={rotor.CurrentThrust:F0}N" +
                    $"\nrho={rotor.DensityScale:F3} ETL={rotor.TranslationalLiftFactor:F3}" +
                    $"\nGE={rotor.GroundEffectFactor:F3}" +
                    $"\ncoll={rotor.CollectivePitch:F2} hold={rotor.OrientationHoldActive}" +
                    autoStr +
                    $"\nreaction={rotor.ReactionTorque:F0}N·m" +
                    $"\ncyclic=({rotor.CyclicTorque.X:F0},{rotor.CyclicTorque.Y:F0},{rotor.CyclicTorque.Z:F0})" +
                    $"\nyaw={rotor.YawTorqueApplied:F0}N·m";
                dd.AddText(posWorld + (Vector3D)(discWorld * 0.5f), info, ColorSRGB.Magenta, 0.35f);
            }
        }
    }

    // ── Cached values for debug ──
    private Vector3 _lastVelocityLocal;
    private Vector3 _lastComLocal;
    internal Vector3 _lastGridAngVel;
    private float _lastDensity;
    private Vector3 _smoothedCopLocal = Vector3.Zero;
    private bool _copInitialized;
}
