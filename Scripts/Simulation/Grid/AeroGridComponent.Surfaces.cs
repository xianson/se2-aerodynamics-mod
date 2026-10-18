using System;
using Keen.Game2.Simulation.WorldObjects.CubeGrids;
using Keen.Game2.Simulation.WorldObjects.CubeGrids.BlockOctrees;
using Keen.Game2.Simulation.WorldObjects.Movement;
using Keen.VRage.Core;

#pragma warning disable CS0649, CS8632, CS1591, CS1573, CS1570, CS1574, CS0419, CS1734
namespace AeroMod;

// The control surfaces: the pilot's input mixer, the allocation for the aero-aware attitude law, trim / axis masks, flap angles.
public partial class AeroGridComponent
{
    private int _csLogCooldown;
    private int _csLogOnceCountdown = 300; // log once after 5s regardless of input

    private void UpdateControlSurfaceInputs(Vector3 centerOfMass, WorldTransform wt,
        Vector3 localAngVel, float dynamicPressure, Vector3 bodyTorque)
    {
        if (_components.Count == 0 && !HingeSurfaces.Wanted(this)) return;   // (hinge surfaces: native subgrid flaps)

        // ══════════════════════════════════════════════════════════════════════
        // STEP 0: Determine logging trigger
        // ══════════════════════════════════════════════════════════════════════
        // Log on input, or once after startup to dump all vectors
        bool hasInput = false;

        // Peek at input state for log decision
        if (Data.Has<TargetControlData>() || Data.Has<AngularControlData>())
        {
            if (Data.TryGet<AngularControlData>(out var peekAng))
                hasInput = peekAng.TargetAngularVelocity.LengthSquared() > 0.001f;
            if (Data.Has<TargetControlData>())
                hasInput = true; // reticle always counts as input
        }

        bool inputLog = _csLogCooldown <= 0 && hasInput;
        bool startupLog = _csLogOnceCountdown > 0 && --_csLogOnceCountdown == 0;
        bool shouldLog = AeroLog.Verbose && (inputLog || startupLog);   // (~20 + 13 per surface lines every 2 s per piloted grid: verbose only)
        if (inputLog) _csLogCooldown = 120;
        _csLogCooldown--;

        // ══════════════════════════════════════════════════════════════════════
        // STEP 1: World transform decomposition
        // ══════════════════════════════════════════════════════════════════════
        Quaternion gridOrientation = wt.Orientation;
        Vector3 gridPosition = (Vector3)wt.Position;
        _cockpit = Data.TryGet<TargetControlData>(out var cockpitTd) && cockpitTd.RelativeCockpitOrientation.IsValidAndRotationIsNormalized() ? cockpitTd.RelativeCockpitOrientation : Quaternion.Identity;

        if (shouldLog)
        {
            Log.Default?.Info($"[AERO-CS] ═══ FULL TRACE START ═══");
            Log.Default?.Info($"[AERO-CS] STEP1 wt.Position=({gridPosition.X:F2},{gridPosition.Y:F2},{gridPosition.Z:F2})");
            Log.Default?.Info($"[AERO-CS] STEP1 wt.Orientation=({gridOrientation.X:F5},{gridOrientation.Y:F5},{gridOrientation.Z:F5},{gridOrientation.W:F5}) |q|={MathF.Sqrt(gridOrientation.X*gridOrientation.X + gridOrientation.Y*gridOrientation.Y + gridOrientation.Z*gridOrientation.Z + gridOrientation.W*gridOrientation.W):F6}");
        }

        // ══════════════════════════════════════════════════════════════════════
        // STEP 2: Read raw input data from ECS
        // ══════════════════════════════════════════════════════════════════════
        Vector3 targetAngVel = Vector3.Zero;
        string inputMode = "NONE";

        bool hasTargetData = Data.TryGet<TargetControlData>(out var targetData);
        bool hasAngularData = Data.TryGet<AngularControlData>(out var angularData);

        if (shouldLog)
        {
            // Log gyro capability
            float gyroMaxTorque = GridPhysics.TryGetGyroMaxTorque(Data);
            if (gyroMaxTorque > 0)
                Log.Default?.Info($"[AERO-CS] STEP2 gyro MaxTorque={gyroMaxTorque:F0} N·m");

            Log.Default?.Info($"[AERO-CS] STEP2 hasTargetControlData={hasTargetData} hasAngularControlData={hasAngularData}");

            if (hasTargetData)
            {
                var tq = targetData.TargetOrientation;
                Log.Default?.Info($"[AERO-CS] STEP2 TargetOrientation=({tq.X:F5},{tq.Y:F5},{tq.Z:F5},{tq.W:F5}) |q|={MathF.Sqrt(tq.X*tq.X + tq.Y*tq.Y + tq.Z*tq.Z + tq.W*tq.W):F6}");
                Log.Default?.Info($"[AERO-CS] STEP2 RelativeCockpitOrientation=({targetData.RelativeCockpitOrientation.X:F5},{targetData.RelativeCockpitOrientation.Y:F5},{targetData.RelativeCockpitOrientation.Z:F5},{targetData.RelativeCockpitOrientation.W:F5})");
            }

            if (hasAngularData)
            {
                var raw = angularData.TargetAngularVelocity;
                Log.Default?.Info($"[AERO-CS] STEP2 AngularControlData.TargetAngularVelocity=({raw.X:F5},{raw.Y:F5},{raw.Z:F5}) |v|={raw.Length():F5}");
            }
        }

        // ══════════════════════════════════════════════════════════════════════
        // STEP 3: Compute target angular velocity from input
        // ══════════════════════════════════════════════════════════════════════
        if (hasTargetData)
        {
            inputMode = "RETICLE";
            // ── OUTER LOOP: Attitude → Desired angular rate ──
            // Orientation error in local frame (same as GridGyroscopesComponent.ComputeTorqueTarget)
            Quaternion errorQuat = Quaternion.Inverse(gridOrientation) * targetData.TargetOrientation;
            Vector3 eulerError = errorQuat.ConvertToEuler(); // radians, local frame (matches game convention)

            // Proportional on the attitude error (the rate damping is ControlSurface.RateCommand's, per surface).
            // ConvertToEuler returns (Pitch, Yaw, Roll) = (X, Y, Z) — same frame as
            // effectiveness vectors and localAngVel. No axis remapping needed.
            const float Kp = 5.0f;    // proportional gain on attitude error (rad)

            // (no keyboard roll override: with the reticle on, Q/E rolls the TARGET orientation - CockpitInputHandlerComponent
            //  multiplies TargetOrientation by the roll - and the cockpit stops updating AngularControlData (UpdateRotationData
            //  is [DoesNotHave(ReticleTargettingEnabler)]); its stale roll here overrode Q/E: the surfaces ignored the keys and
            //  fought the gyros' roll - "Q/E inverted", the user on the Tu-134, 2026-10-12. The error's roll carries Q/E.)
            Vector3 attitudeCmd = eulerError * Kp;

            // Clamp per axis
            const float maxCmd = 5.0f;
            attitudeCmd = new Vector3(
                MathF.Max(-maxCmd, MathF.Min(maxCmd, attitudeCmd.X)),
                MathF.Max(-maxCmd, MathF.Min(maxCmd, attitudeCmd.Y * targetData.PerAxisDampeningMultiplier.Y)),
                MathF.Max(-maxCmd, MathF.Min(maxCmd, attitudeCmd.Z)));

            // the rotation wanted: the attitude command as a rate; the surfaces take it through ControlSurface.RateCommand
            // (damping on the rate ERROR - the old "- w Kd" here plus 3 w per surface damped the turn the gyros were making)
            targetAngVel = attitudeCmd;

            if (shouldLog)
            {
                Log.Default?.Info($"[AERO-CS] STEP3 RETICLE eulerError=({eulerError.X:F5},{eulerError.Y:F5},{eulerError.Z:F5}) rad");
                Log.Default?.Info($"[AERO-CS] STEP3 RETICLE attitudeCmd=({attitudeCmd.X:F5},{attitudeCmd.Y:F5},{attitudeCmd.Z:F5})");
                Log.Default?.Info($"[AERO-CS] STEP3 RETICLE localAngVel=({localAngVel.X:F5},{localAngVel.Y:F5},{localAngVel.Z:F5})");
                Log.Default?.Info($"[AERO-CS] STEP3 RETICLE bodyTorque=({bodyTorque.X:F1},{bodyTorque.Y:F1},{bodyTorque.Z:F1})");
                Log.Default?.Info($"[AERO-CS] STEP3 RETICLE PD_cmd=({targetAngVel.X:F5},{targetAngVel.Y:F5},{targetAngVel.Z:F5})");
            }
        }
        else if (hasAngularData && angularData.TargetAngularVelocity.LengthSquared() > 0.0001f)
        {
            inputMode = "KEYBOARD";

            Vector3 raw = angularData.TargetAngularVelocity;
            targetAngVel = new Vector3(
                MathF.Max(-1f, MathF.Min(1f, raw.X)),
                MathF.Max(-1f, MathF.Min(1f, raw.Y)),
                MathF.Max(-1f, MathF.Min(1f, raw.Z)));

            if (shouldLog)
            {
                Log.Default?.Info($"[AERO-CS] STEP3 KEYBOARD raw=({raw.X:F5},{raw.Y:F5},{raw.Z:F5})");
                Log.Default?.Info($"[AERO-CS] STEP3 targetAngVel_clamped=({targetAngVel.X:F5},{targetAngVel.Y:F5},{targetAngVel.Z:F5})");
            }
        }
        else
        {
            inputMode = "HOLD";
            // _lastGridAngVel already set by UpdateAttitudeHold()
            // which runs before early returns in TryCompute
            targetAngVel = _lastGridAngVel;
        }

        _lastGridAngVel = targetAngVel;

        // ══════════════════════════════════════════════════════════════════════
        // STEP 4: Speed-dependent gain
        // ══════════════════════════════════════════════════════════════════════
        const float QRef = 5000f;
        float gainScale = QRef / MathF.Max(QRef, dynamicPressure);
        HingeSurfaces.Drive(this, centerOfMass, targetAngVel, localAngVel, gainScale);

        if (shouldLog)
        {
            var localVel = _lastVelocityLocal;
            float speed = localVel.Length();
            Vector3 flowDir = speed > 0.1f ? localVel / speed : Vector3.Zero;

            Log.Default?.Info($"[AERO-CS] STEP4 inputMode={inputMode} targetAngVel=({targetAngVel.X:F5},{targetAngVel.Y:F5},{targetAngVel.Z:F5})");
            Log.Default?.Info($"[AERO-CS] STEP4 localAngVel(actual)=({localAngVel.X:F5},{localAngVel.Y:F5},{localAngVel.Z:F5})");
            Log.Default?.Info($"[AERO-CS] STEP4 localVelocity=({localVel.X:F2},{localVel.Y:F2},{localVel.Z:F2}) speed={speed:F1}");
            Log.Default?.Info($"[AERO-CS] STEP4 flowDir=({flowDir.X:F4},{flowDir.Y:F4},{flowDir.Z:F4})");
            Log.Default?.Info($"[AERO-CS] STEP4 dynPressure={dynamicPressure:F1} QRef={QRef:F0} gainScale={gainScale:F5}");
            Log.Default?.Info($"[AERO-CS] STEP4 CoM=({centerOfMass.X:F3},{centerOfMass.Y:F3},{centerOfMass.Z:F3}) blockSize={_blockSize} components={_components.Count}");
        }

        // No authority normalization — let the dot product in STEP 5 naturally
        // distribute commands to surfaces based on their effectiveness alignment.
        if (inputMode == "RETICLE" && shouldLog)
        {
            Log.Default?.Info($"[AERO-CS] STEP4b PD cmd=({targetAngVel.X:F5},{targetAngVel.Y:F5},{targetAngVel.Z:F5})");
        }

        // ══════════════════════════════════════════════════════════════════════
        // STEP 5: Per-surface loop
        // ══════════════════════════════════════════════════════════════════════
        for (int i = 0; i < _components.Components.Count; i++)
        {
            if (_components.Components[i] is Airbrake ab)
            {
                // (the clamshell: both leaves seen from ahead - sin of their opening; the old 1x1: always out)
                ab.DeployFraction = ab.State != null ? MathF.Sin(Math.Clamp(ab.State.OpeningDeg, 0f, 90f) * MathF.PI / 180f) : 1f;
            }
            else if (_components.Components[i] is ControlSurface cs)
            {
                // its terminal: off / unpowered - trails at neutral; the axes it may help with; its flap / trim
                if (!SurfaceSetup(cs, _lastVelocityLocal, out float baseInput, out bool usable)) { cs.DeflectionInput = 0f; continue; }
                if (!usable) { cs.DeflectionInput = baseInput; continue; }

                // 5a. Moment arm from CoM to surface position
                Vector3 r = cs.Position - centerOfMass;

                // 5b. Lift direction = cross(hinge, chord)
                //     This is the direction of force when the surface deflects
                Vector3 liftDir = Vector3.Cross(cs.HingeAxis, cs.ChordDirection);
                // (the force a deflection adds runs along (air . chord) x normal: with the air coming over the trailing
                //  edge - a surface mounted backwards - the same deflection pushes the other way)
                if (Vector3.Dot(-_lastVelocityLocal, cs.ChordDirection) < 0f) liftDir = -liftDir;
                float liftDirLen = liftDir.Length();

                // 5c. Torque axis from positive deflection.
                //     Torque = Cross(r, F). Positive DeflectionInput → deflRad = -input*maxDefl
                //     → positive AoA → positive lift → torque = Cross(r, liftDir*lift).
                //     effectiveness = Cross(r, liftDir): torque direction from positive lift.
                //     But positive input → positive lift, so effectiveness IS the torque direction of +input.
                Vector3 effectiveness = MaskAxes(cs, Vector3.Cross(r, liftDir));
                float effLen = effectiveness.Length();

                if (effLen < 0.01f)
                {
                    cs.DeflectionInput = baseInput;
                    if (shouldLog)
                    {
                        Log.Default?.Info($"[AERO-CS] CS#{i} SKIPPED effLen={effLen:F4} pos=({cs.Position.X:F3},{cs.Position.Y:F3},{cs.Position.Z:F3}) blockPos=({cs.BlockPosition.X},{cs.BlockPosition.Y},{cs.BlockPosition.Z})");
                        Log.Default?.Info($"[AERO-CS] CS#{i} SKIPPED hinge=({cs.HingeAxis.X:F3},{cs.HingeAxis.Y:F3},{cs.HingeAxis.Z:F3}) chord=({cs.ChordDirection.X:F3},{cs.ChordDirection.Y:F3},{cs.ChordDirection.Z:F3})");
                        Log.Default?.Info($"[AERO-CS] CS#{i} SKIPPED liftDir=({liftDir.X:F3},{liftDir.Y:F3},{liftDir.Z:F3}) r=({r.X:F3},{r.Y:F3},{r.Z:F3}) eff=({effectiveness.X:F5},{effectiveness.Y:F5},{effectiveness.Z:F5})");
                    }
                    continue;
                }

                // 5d. Normalize effectiveness to unit vector
                Vector3 effNorm = effectiveness / effLen;

                // 5e. Control law — mode-dependent
                float scaledInput;
                float command;
                float damping;

                // every mode: ControlSurface.RateCommand - the rotation wanted (reticle: the attitude command; keyboard: the
                // pilot's rates; hold: the hold's) plus damping of the rate error
                command = Vector3.Dot(effNorm, targetAngVel) * ControlSurface.RateKp;
                damping = Vector3.Dot(effNorm, localAngVel - targetAngVel) * ControlSurface.RateKd;
                scaledInput = ControlSurface.RateCommand(effNorm, targetAngVel, localAngVel, gainScale);
                float clampedInput = MathF.Max(-1f, MathF.Min(1f, baseInput + scaledInput));
                cs.DeflectionInput = float.IsNaN(ControlSurface.TestInput) ? clampedInput : Math.Clamp(ControlSurface.TestInput, -1f, 1f);

                if (shouldLog)
                {
                    // 5f. Compute derived values for logging (Rodrigues deflection, negated to match Compute)
                    float deflRad = -cs.DeflectionInput * cs.MaxDeflection * MathF.PI / 180f;
                    Vector3 deflChord = DeflectedChord(cs);
                    Vector3 deflSurfNormal = Vector3.Cross(cs.HingeAxis, deflChord);
                    float deflSurfNormLen = deflSurfNormal.Length();
                    if (deflSurfNormLen > 1e-6f) deflSurfNormal /= deflSurfNormLen;

                    // Flow at this point
                    float localSpeed = _lastVelocityLocal.Length();
                    Vector3 localFlowDir = localSpeed > 0.1f ? _lastVelocityLocal / localSpeed : Vector3.Zero;

                    // AoA decomposition
                    float dotChordFlow = Vector3.Dot(-localFlowDir, deflChord);
                    float dotNormalFlow = Vector3.Dot(-localFlowDir, deflSurfNormal);
                    float aoaRad = MathF.Atan2(dotNormalFlow, dotChordFlow);

                    // Individual dot products for debugging effectiveness
                    float dotEffTarget = Vector3.Dot(effNorm, targetAngVel);
                    float dotEffAngVel = Vector3.Dot(effNorm, localAngVel);

                    // Cross product verification (matches code: cross(liftDir, r))
                    Vector3 torqueFromLift = Vector3.Cross(liftDir, r);

                    Log.Default?.Info($"[AERO-CS] CS#{i} GEOM blockPos=({cs.BlockPosition.X},{cs.BlockPosition.Y},{cs.BlockPosition.Z}) pos=({cs.Position.X:F3},{cs.Position.Y:F3},{cs.Position.Z:F3})");
                    Log.Default?.Info($"[AERO-CS] CS#{i} GEOM hinge=({cs.HingeAxis.X:F3},{cs.HingeAxis.Y:F3},{cs.HingeAxis.Z:F3}) chord=({cs.ChordDirection.X:F3},{cs.ChordDirection.Y:F3},{cs.ChordDirection.Z:F3}) dot(h,c)={Vector3.Dot(cs.HingeAxis, cs.ChordDirection):F5}");
                    Log.Default?.Info($"[AERO-CS] CS#{i} ARM CoM=({centerOfMass.X:F3},{centerOfMass.Y:F3},{centerOfMass.Z:F3}) r=({r.X:F3},{r.Y:F3},{r.Z:F3}) |r|={r.Length():F3}");
                    Log.Default?.Info($"[AERO-CS] CS#{i} EFF liftDir=({liftDir.X:F3},{liftDir.Y:F3},{liftDir.Z:F3}) eff_raw=cross(liftDir,r)=({torqueFromLift.X:F5},{torqueFromLift.Y:F5},{torqueFromLift.Z:F5}) |eff|={effLen:F5}");
                    Log.Default?.Info($"[AERO-CS] CS#{i} EFF effNorm=({effNorm.X:F5},{effNorm.Y:F5},{effNorm.Z:F5})");
                    Log.Default?.Info($"[AERO-CS] CS#{i} PD dot(eff,target)={dotEffTarget:F5} cmd={command:F5} dot(eff,angVel)={dotEffAngVel:F5} damp={damping:F5} mode={inputMode}");
                    Log.Default?.Info($"[AERO-CS] CS#{i} PD scaled={scaledInput:F5}->clamped={clampedInput:F5}");
                    Log.Default?.Info($"[AERO-CS] CS#{i} DEFL input={cs.DeflectionInput:F5} deg={cs.DeflectionInput * cs.MaxDeflection:F2} maxDefl={cs.MaxDeflection:F1} deflRad={deflRad:F5}");
                    Log.Default?.Info($"[AERO-CS] CS#{i} DEFL deflChord=({deflChord.X:F5},{deflChord.Y:F5},{deflChord.Z:F5}) surfNorm=({deflSurfNormal.X:F5},{deflSurfNormal.Y:F5},{deflSurfNormal.Z:F5})");
                    Log.Default?.Info($"[AERO-CS] CS#{i} FLOW vel=({_lastVelocityLocal.X:F2},{_lastVelocityLocal.Y:F2},{_lastVelocityLocal.Z:F2}) spd={localSpeed:F1} flowDir=({localFlowDir.X:F4},{localFlowDir.Y:F4},{localFlowDir.Z:F4})");
                    Log.Default?.Info($"[AERO-CS] CS#{i} AOA dot(-flow,chord)={dotChordFlow:F5} dot(-flow,norm)={dotNormalFlow:F5} atan2={aoaRad:F5}rad ({aoaRad * 180f / MathF.PI:F2}°)");
                    Log.Default?.Info($"[AERO-CS] CS#{i} FORCE AoA={cs.EffectiveAoA:F2}° CL={cs.EffectiveCp:F5} L={cs.CurrentLift:F1}N D={cs.CurrentDrag:F1}N area={cs.Area:F3}m²");
                }
            }
        }

        if (shouldLog)
        {
            Log.Default?.Info($"[AERO-CS] ═══ FULL TRACE END ═══");
        }
    }

    /// <summary>The flap models' deflections, for the client copies (AeroFlapComponent turns each flap subpart by its angle).</summary>
    private void PublishFlapAngles(in WorldTransform wt)
    {
        var t = _flapTable; int n = 0; bool same = t != null;
        for (int i = 0; i < _components.Components.Count; i++)
            if (_components.Components[i] is ControlSurface fs && fs.FlapKey is Vector3I fk)
            {
                n++;
                if (same && !(t.Index.TryGetValue(fk, out int slot) && slot < t.Angle.Length)) same = false;
            }
        if (n == 0) return;
        if (!same || t.Index.Count != n)
        {
            // (a new set of flaps: a new table - a client holding the old one finds it replaced and matches again)
            t = new ThrustTorque.FlapTable { Angle = new float[n] }; int k = 0;
            for (int i = 0; i < _components.Components.Count; i++)
                if (_components.Components[i] is ControlSurface fs && fs.FlapKey is Vector3I fk && !t.Index.ContainsKey(fk)) t.Index[fk] = k++;
            _flapTable = t;
        }
        for (int i = 0; i < _components.Components.Count; i++)
            if (_components.Components[i] is ControlSurface fs && fs.FlapKey is Vector3I fk && t.Index.TryGetValue(fk, out int slot))
                t.Angle[slot] = fs.DeflectionAngle;
        ThrustTorque.PublishFlaps(this, wt, t);
    }
    private ThrustTorque.FlapTable _flapTable;

    /// <summary>
    /// The control surfaces as actuators of the aero-aware attitude law (the user, 2026-10-12: "the gyro / thrust controller
    /// is correct - the surfaces follow it"): its torque demand (LastControlTorque, grid-local N m, a tick old) less what the
    /// surfaces give undeflected, shared out by least squares over each surface's torque per unit input - measured now, by
    /// computing it at +-1/2 input - and clamped to +-1. So the surfaces give about the demand and never more (an excess the
    /// gyros and thrusters would counter); the law asks them for the rest (demand - LastSurfaceTorque).
    /// </summary>
    private void AllocateSurfaces(in AeroContext ctx, Vector3 com)
    {
        var list = _allocScratch; list.Clear(); _allocBase.Clear(); _allocA.Clear(); _allocN = 0;   // (read by Loac: never stale or mismatched)
        _cockpit = Data.TryGet<TargetControlData>(out var tcd) && tcd.RelativeCockpitOrientation.IsValidAndRotationIsNormalized() ? tcd.RelativeCockpitOrientation : Quaternion.Identity;
        Vector3 t0 = Vector3.Zero;
        for (int i = 0; i < _components.Components.Count; i++)
        {
            if (_components.Components[i] is not ControlSurface cs) continue;
            var cond = LocalAeroConditions.FromContext(ctx, cs.Position);
            // its terminal: off - neutral, left out; no axes - its flap / trim held; else the control works about the trim
            if (!SurfaceSetup(cs, _lastVelocityLocal, out float b, out bool usable)) b = 0f;
            else if (usable)
            {
                cs.DeflectionInput = b + 0.5f; var tp = cs.Compute(cond).TorqueAbout(com);
                cs.DeflectionInput = b - 0.5f; var tm = cs.Compute(cond).TorqueAbout(com);
                // (torque per unit input, about the centre of mass - only its share on the axes it may help with: what it
                //  gives on the others the gyros answer, the law asking them for demand less what the surfaces gave)
                var a = MaskAxes(cs, tp - tm);
                if (a.LengthSquared() > 1e-6f) { list.Add((cs, a)); _allocBase.Add(b); }
            }
            cs.DeflectionInput = b; t0 += cs.Compute(cond).TorqueAbout(com);
        }
        if (list.Count == 0) { SurfacePush = SurfacePull = Vector3.Zero; SurfaceNeutral = t0; _surfReachFrame = _simFrameCount; return; }
        // the world's tuning scale (applied torque / computed), as the sim job took it last tick
        float lc = LastComponentTorque.Length();
        float scale = lc > 1e-3f ? LastSurfaceTorque.Length() / lc : 1f;
        if (!(scale > 1e-3f) || !float.IsFinite(scale)) scale = 1f;
        Vector3 want = LastControlTorque / scale - t0;
        // (the loss-of-control warning's: the surfaces' torque at full throw per axis, and at their trim - Loac.cs)
        SurfaceTravel(list, scale, t0);
        _allocA.Clear(); foreach (var (_, a) in list) _allocA.Add(a);
        if (_allocU.Length < list.Count) _allocU = new float[list.Count * 2];
        if (!ControlSurface.Allocate(_allocA, want, _allocU)) return;
        _allocN = list.Count; _allocSat = 0;
        for (int k = 0; k < list.Count; k++)
        {
            float u = Math.Clamp(_allocBase[k] + _allocU[k], -1f, 1f);
            list[k].cs.DeflectionInput = u; if (MathF.Abs(u) > 0.999f) _allocSat++;
        }
    }
    /// <summary>The surfaces as the attitude law's actuators on/off (harness: aeroset AeroGridComponent.AllocEnabled true).
    /// (Its off switch is for diagnosis: the 2026-10-12 freeze blamed on it was EffectCull's exceptions.) Server only: the
    /// client copies show the server's deflections.</summary>
    public static bool AllocEnabled = true;
    private readonly List<(ControlSurface cs, Vector3 a)> _allocScratch = new();
    private readonly List<Vector3> _allocA = new(); private readonly List<float> _allocBase = new(); private float[] _allocU = new float[16];

    /// <summary>After a rebuild: each control surface's section from the grid's cells (its host ahead, span, end plates -
    /// ControlSurfaceGeometry, no wing detection), logged once per rebuild per surface.</summary>
    private void MeasureSurfaces()
    {
        ControlSurfaceGeometry.Measure(_components.Components, _gridAccessor);
        for (int i = 0; i < _components.Components.Count; i++)
            if (_components.Components[i] is ControlSurface cs && cs.Surroundings is SurfaceSurroundings g)
                if (AeroLog.Detail) Log.Default?.Info($"[AERO] surface at {cs.BlockPosition}: +chord edge {g.Describe(0)}; -chord edge {g.Describe(1)}");
    }

    /// <summary>The cockpit's orientation in the grid (TargetControlData), this step: the frame of the surfaces' axis toggles.</summary>
    private Quaternion _cockpit = Quaternion.Identity;

    /// <summary>
    /// A surface's terminal settings this step (none: works on every axis): its max deflection applied; false when it is
    /// off, unpowered or broken (it trails at neutral). baseInput: its flap / trim as an input (+ trim: the force toward the
    /// cockpit's up - for a fin, its right); usable: it may help on some axis.
    /// </summary>
    /// <summary>A surface's chord turned about its hinge by its current deflection (Rodrigues; negated to match its
    /// Compute) - the surface log and the debug draw.</summary>
    private static Vector3 DeflectedChord(ControlSurface cs)
    {
        float deflRad = -cs.DeflectionInput * cs.MaxDeflection * MathF.PI / 180f;
        float cosD = MathF.Cos(deflRad);
        float sinD = MathF.Sin(deflRad);
        return cs.ChordDirection * cosD +
            Vector3.Cross(cs.HingeAxis, cs.ChordDirection) * sinD +
            cs.HingeAxis * Vector3.Dot(cs.HingeAxis, cs.ChordDirection) * (1f - cosD);
    }

    private bool SurfaceSetup(ControlSurface cs, Vector3 velLocal, out float baseInput, out bool usable)
    {
        baseInput = 0f; usable = true;
        var s = cs.Settings;
        if (s == null) return true;
        if (!s.Working) { usable = false; return false; }
        cs.MaxDeflection = s.MaxDeflection;
        usable = (s.Pitch || s.Yaw || s.Roll) && cs.MaxDeflection > 0.01f;
        if (s.Trim != 0f && cs.MaxDeflection > 0.01f)
        {
            // (+ input pushes along hinge x chord - the other way with the air over the trailing edge, as the mixer)
            Vector3 push = Vector3.Cross(cs.HingeAxis, cs.ChordDirection);
            if (Vector3.Dot(-velLocal, cs.ChordDirection) < 0f) push = -push;
            Vector3 up = Vector3.Transform(Vector3.UnitY, _cockpit), right = Vector3.Transform(Vector3.UnitX, _cockpit);
            float pu = Vector3.Dot(push, up), pr = Vector3.Dot(push, right);
            float sign = MathF.Abs(pu) >= MathF.Abs(pr) ? MathF.Sign(pu) : MathF.Sign(pr);
            baseInput = Math.Clamp(sign * s.Trim / cs.MaxDeflection, -1f, 1f);
        }
        return true;
    }

    /// <summary>A torque (grid frame) with the parts on the cockpit's axes the surface may not help with taken out.</summary>
    private Vector3 MaskAxes(ControlSurface cs, Vector3 torque)
    {
        var s = cs.Settings;
        if (s == null || (s.Pitch && s.Yaw && s.Roll)) return torque;
        var c = Vector3.Transform(torque, Quaternion.Inverse(_cockpit));
        c = new Vector3(s.Pitch ? c.X : 0f, s.Yaw ? c.Y : 0f, s.Roll ? c.Z : 0f);
        return Vector3.Transform(c, _cockpit);
    }
}
