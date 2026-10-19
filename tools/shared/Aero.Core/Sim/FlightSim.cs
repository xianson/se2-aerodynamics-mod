using System.Numerics;
using Aero.Core.Aero;
using Aero.Core.Atmosphere;
using Aero.Core.Components;
using Aero.Core.Grids;

namespace Aero.Core.Sim;

/// <summary>
/// Snapshot of simulation state at one time step, for recording/analysis.
/// </summary>
public readonly struct FlightRecord
{
    public readonly double Time;
    public readonly RigidBodyState State;
    public readonly AeroResult Aero;
    public readonly AtmosphereState Atmosphere;

    /// <summary>Altitude above surface (m). Assumes Y-up.</summary>
    public readonly float Altitude;

    /// <summary>Mach number.</summary>
    public readonly double Mach;

    /// <summary>Dynamic pressure (Pa).</summary>
    public readonly double DynamicPressure;

    /// <summary>Angle of attack (degrees) — angle between velocity and body X-axis.</summary>
    public readonly float AngleOfAttack;

    public FlightRecord(double time, RigidBodyState state, AeroResult aero,
        AtmosphereState atmo, float altitude, double mach, double q, float aoa)
    {
        Time = time;
        State = state;
        Aero = aero;
        Atmosphere = atmo;
        Altitude = altitude;
        Mach = mach;
        DynamicPressure = q;
        AngleOfAttack = aoa;
    }

    public override string ToString()
        => $"t={Time:F2}s alt={Altitude:F0}m M={Mach:F2} v={State.Speed:F1}m/s " +
           $"AoA={AngleOfAttack:F1}° D={Aero.DragMagnitude:F0}N L={Aero.LiftMagnitude:F0}N";
}

/// <summary>
/// 6DOF flight simulator.
///
/// Integrates rigid body dynamics with aerodynamic forces from a drag model
/// and gravitational force. Uses RK4 for the 13-variable state vector.
///
/// Coordinate convention: Y-up, gravity = -Y.
/// Altitude = Position.Y (above Y=0 surface).
/// </summary>
public class FlightSim
{
    // ─── Configuration ──────────────────────────────────────────────

    /// <summary>The grid being simulated.</summary>
    public Grid Grid { get; }

    /// <summary>Pre-built surface cache for the grid.</summary>
    public ISurfaceProvider SurfaceCache { get; }

    /// <summary>Drag model to use for force computation.</summary>
    public AeroDragModelBase DragModel { get; }

    /// <summary>Atmosphere model for density/temperature/speed-of-sound at altitude.</summary>
    public AtmosphereModel AtmosphereModel { get; }

    /// <summary>Mass properties (total mass, CoM, inertia tensor).</summary>
    public MassProperties Mass { get; }

    /// <summary>Block size in meters (default 2.5 for SE2 large grid).</summary>
    public float BlockSize { get; }

    /// <summary>Surface gravity in m/s² (default 9.80665 for Earth).</summary>
    public float Gravity { get; set; } = 9.80665f;

    /// <summary>If true, stop when altitude drops below 0.</summary>
    public bool StopAtGround { get; set; } = true;

    /// <summary>If true, stop when speed drops below 0.1 m/s.</summary>
    public bool StopWhenStopped { get; set; } = false;

    /// <summary>External body-frame force (e.g. thrust). Applied each derivative evaluation.</summary>
    public Vector3 ExternalBodyForce { get; set; }

    /// <summary>External body-frame torque (e.g. control surfaces). Applied each derivative evaluation.</summary>
    public Vector3 ExternalBodyTorque { get; set; }

    /// <summary>
    /// Block-level aero component registry (control surfaces, airbrakes, rotors, etc.).
    /// When set, components are evaluated each derivative step and their forces are added.
    /// </summary>
    public AeroComponentRegistry? Components { get; set; }

    /// <summary>
    /// Callback invoked at the start of each derivative evaluation.
    /// Use to update component inputs (deflections, collective, etc.) dynamically.
    /// Parameters: (FlightSim sim, double currentTime).
    /// </summary>
    public Action<FlightSim, double>? OnBeforeStep { get; set; }

    // ─── State ──────────────────────────────────────────────────────

    /// <summary>Current simulation time in seconds.</summary>
    public double Time { get; private set; }

    /// <summary>Current rigid body state.</summary>
    public RigidBodyState State { get; set; }

    /// <summary>Last computed aero result (from most recent step).</summary>
    public AeroResult LastAero { get; private set; }

    /// <summary>Recorded flight history.</summary>
    public List<FlightRecord> History { get; } = new();

    /// <summary>Record interval — save a FlightRecord every N steps. 0 = every step.</summary>
    public int RecordInterval { get; set; } = 1;

    // ─── Constructor ────────────────────────────────────────────────

    public FlightSim(Grid grid, ISurfaceProvider surfaceCache, AeroDragModelBase dragModel,
        AtmosphereModel atmosphereModel, MassProperties mass, float blockSize = 2.5f)
    {
        Grid = grid;
        SurfaceCache = surfaceCache;
        DragModel = dragModel;
        AtmosphereModel = atmosphereModel;
        Mass = mass;
        BlockSize = blockSize;

        State = new RigidBodyState
        {
            Orientation = Quaternion.Identity,
        };
    }

    // ─── Run ────────────────────────────────────────────────────────

    /// <summary>
    /// Run the simulation for the given duration at the given time step.
    /// Returns the recorded flight history.
    /// </summary>
    public List<FlightRecord> Run(double duration, double dt = 0.01)
    {
        History.Clear();
        Time = 0;
        int step = 0;

        while (Time < duration)
        {
            // Record
            if (RecordInterval <= 0 || step % RecordInterval == 0)
                Record();

            // Integrate
            StepRK4(dt);
            Time += dt;
            step++;

            // Ground collision
            if (StopAtGround && State.Position.Y <= 0 && State.LinearVelocity.Y < 0)
            {
                var s = State;
                s.Position = new Vector3(s.Position.X, 0, s.Position.Z);
                s.LinearVelocity = Vector3.Zero;
                s.AngularVelocity = Vector3.Zero;
                State = s;
                Record();
                break;
            }

            // Stopped
            if (StopWhenStopped && State.Speed < 0.1f)
            {
                Record();
                break;
            }
        }

        // Final record
        if (History.Count == 0 || History[^1].Time < Time)
            Record();

        return History;
    }

    /// <summary>
    /// Advance simulation by one time step using RK4 integration.
    /// ExternalBodyForce and ExternalBodyTorque are applied at each derivative evaluation.
    /// Returns the last computed aero result.
    /// </summary>
    public AeroResult Step(double dt)
    {
        StepRK4(dt);
        Time += dt;
        return LastAero;
    }

    // ─── RK4 Integration ────────────────────────────────────────────

    private void StepRK4(double dt)
    {
        var s0 = State;
        float dtf = (float)dt;

        var k1 = ComputeDerivatives(s0);
        var k2 = ComputeDerivatives(Advance(s0, k1, dtf * 0.5f));
        var k3 = ComputeDerivatives(Advance(s0, k2, dtf * 0.5f));
        var k4 = ComputeDerivatives(Advance(s0, k3, dtf));

        var state = State;

        state.Position += (dtf / 6f) * (k1.dPos + 2f * k2.dPos + 2f * k3.dPos + k4.dPos);
        state.LinearVelocity += (dtf / 6f) * (k1.dVel + 2f * k2.dVel + 2f * k3.dVel + k4.dVel);
        state.AngularVelocity += (dtf / 6f) * (k1.dOmega + 2f * k2.dOmega + 2f * k3.dOmega + k4.dOmega);

        // Quaternion integration: q' = q + (dt/6)(k1+2k2+2k3+k4)
        var dq = ScaleQuat(k1.dQuat, 1f) + ScaleQuat(k2.dQuat, 2f) +
                 ScaleQuat(k3.dQuat, 2f) + ScaleQuat(k4.dQuat, 1f);
        state.Orientation = Quaternion.Normalize(new Quaternion(
            state.Orientation.X + dtf / 6f * dq.X,
            state.Orientation.Y + dtf / 6f * dq.Y,
            state.Orientation.Z + dtf / 6f * dq.Z,
            state.Orientation.W + dtf / 6f * dq.W));

        State = state;
    }

    private readonly struct Derivatives
    {
        public readonly Vector3 dPos;    // = velocity
        public readonly Quaternion dQuat; // = 0.5 * ω * q
        public readonly Vector3 dVel;    // = acceleration (F/m + g)
        public readonly Vector3 dOmega;  // = angular accel (I⁻¹ · τ)

        public Derivatives(Vector3 dp, Quaternion dq, Vector3 dv, Vector3 dw)
        {
            dPos = dp;
            dQuat = dq;
            dVel = dv;
            dOmega = dw;
        }
    }

    private Derivatives ComputeDerivatives(RigidBodyState s)
    {
        // Allow caller to update component inputs before force evaluation
        OnBeforeStep?.Invoke(this, Time);

        float altitude = s.Position.Y;
        var atmo = AtmosphereModel.GetState(altitude < 0 ? 0 : altitude);

        // Rotate world-frame velocity into body (grid-local) frame.
        // Face normals/positions are grid-local, so velocity must match.
        // AngularVelocity is body-frame (Euler equations convention), matching the aero context.
        Quaternion invQ = Quaternion.Conjugate(s.Orientation);
        Vector3 localVel = Vector3.Transform(s.LinearVelocity, invQ);

        var ctx = new AeroContext(Grid, SurfaceCache, localVel, atmo, Mass.CenterOfMass, BlockSize,
            angularVelocity: s.AngularVelocity, groundHeight: altitude);

        // Compute aero in body frame
        var aeroLocal = DragModel.Compute(ctx);

        // Evaluate block-level components (control surfaces, airbrakes, rotors, etc.)
        Vector3 componentForce = Vector3.Zero;
        Vector3 componentTorque = Vector3.Zero;
        if (Components != null && Components.Count > 0)
        {
            float dt = (float)(Time > 0 ? 1.0 / 60.0 : 1.0 / 60.0); // default 60Hz for component dt
            (componentForce, componentTorque) = Components.EvaluateAll(ctx, dt);
        }

        // Total body-frame force: aero + components + external (thrust, etc.)
        Vector3 bodyForce = aeroLocal.Force + componentForce + ExternalBodyForce;

        // Rotate force back to world frame for linear dynamics
        Vector3 worldForce = Vector3.Transform(bodyForce, s.Orientation);

        // Store world-frame result for recording/display (aero only, not external)
        LastAero = new AeroResult(Vector3.Transform(aeroLocal.Force, s.Orientation),
            Vector3.Transform(aeroLocal.Torque, s.Orientation),
            aeroLocal.DragMagnitude, aeroLocal.LiftMagnitude,
            aeroLocal.FrontalArea, aeroLocal.Mach, aeroLocal.DynamicPressure);

        // Gravity (world Y-down)
        Vector3 gravityForce = new Vector3(0, -Gravity * Mass.TotalMass, 0);

        // Linear acceleration (world frame)
        Vector3 totalForce = worldForce + gravityForce;
        Vector3 linearAccel = Mass.TotalMass > 0 ? totalForce / Mass.TotalMass : Vector3.Zero;

        // Angular acceleration: α = I⁻¹ · (τ - ω × (I · ω))
        // Gyroscopic term: ω × (I·ω)
        Vector3 Iw = new Vector3(
            Mass.Ixx * s.AngularVelocity.X + Mass.Ixy * s.AngularVelocity.Y + Mass.Ixz * s.AngularVelocity.Z,
            Mass.Ixy * s.AngularVelocity.X + Mass.Iyy * s.AngularVelocity.Y + Mass.Iyz * s.AngularVelocity.Z,
            Mass.Ixz * s.AngularVelocity.X + Mass.Iyz * s.AngularVelocity.Y + Mass.Izz * s.AngularVelocity.Z);
        Vector3 gyroscopic = Vector3.Cross(s.AngularVelocity, Iw);
        // Torque must be body-frame for Euler equations (inertia tensor is body-frame)
        // Total body torque: aero + components + external
        Vector3 angAccel = Mass.AngularAcceleration(aeroLocal.Torque + componentTorque + ExternalBodyTorque - gyroscopic);

        // Quaternion derivative: dq/dt = 0.5 * q * ω_body
        // AngularVelocity is body-frame (from Euler equations), so use right-multiply.
        var wq = new Quaternion(s.AngularVelocity.X, s.AngularVelocity.Y, s.AngularVelocity.Z, 0);
        var dq = MultiplyQuat(s.Orientation, wq);
        dq = ScaleQuat(dq, 0.5f);

        return new Derivatives(s.LinearVelocity, dq, linearAccel, angAccel);
    }

    private static RigidBodyState Advance(RigidBodyState s, Derivatives d, float dt)
    {
        s.Position += d.dPos * dt;
        s.LinearVelocity += d.dVel * dt;
        s.AngularVelocity += d.dOmega * dt;
        s.Orientation = Quaternion.Normalize(new Quaternion(
            s.Orientation.X + d.dQuat.X * dt,
            s.Orientation.Y + d.dQuat.Y * dt,
            s.Orientation.Z + d.dQuat.Z * dt,
            s.Orientation.W + d.dQuat.W * dt));
        return s;
    }

    // ─── Recording ──────────────────────────────────────────────────

    private void Record()
    {
        float alt = State.Position.Y;
        var atmo = AtmosphereModel.GetState(alt < 0 ? 0 : alt);
        double mach = atmo.SpeedOfSound > 0 ? State.Speed / atmo.SpeedOfSound : 0;
        double q = atmo.GetDynamicPressure(State.Speed);

        // Angle of attack: angle between velocity and body forward (-X axis = nose)
        float aoa = 0;
        if (State.Speed > 0.1f)
        {
            Vector3 bodyForward = State.BodyToWorld(-Vector3.UnitX);
            Vector3 vHat = State.LinearVelocity / State.Speed;
            // Signed AoA: positive when nose is above velocity vector
            Vector3 bodyUp = State.BodyToWorld(Vector3.UnitY);
            float dot = Vector3.Dot(vHat, bodyForward);
            dot = System.Math.Clamp(dot, -1f, 1f);
            float magnitude = MathF.Acos(dot) * (180f / MathF.PI);
            float sign = Vector3.Dot(vHat, bodyUp) < 0 ? 1f : -1f; // nose above vel = positive
            aoa = sign * magnitude;
        }

        History.Add(new FlightRecord(Time, State, LastAero, atmo, alt, mach, q, aoa));
    }

    // ─── Quaternion helpers ─────────────────────────────────────────

    private static Quaternion MultiplyQuat(Quaternion a, Quaternion b)
    {
        return new Quaternion(
            a.W * b.X + a.X * b.W + a.Y * b.Z - a.Z * b.Y,
            a.W * b.Y - a.X * b.Z + a.Y * b.W + a.Z * b.X,
            a.W * b.Z + a.X * b.Y - a.Y * b.X + a.Z * b.W,
            a.W * b.W - a.X * b.X - a.Y * b.Y - a.Z * b.Z);
    }

    private static Quaternion ScaleQuat(Quaternion q, float s)
        => new(q.X * s, q.Y * s, q.Z * s, q.W * s);

    private static Quaternion AddQuat(Quaternion a, Quaternion b)
        => new(a.X + b.X, a.Y + b.Y, a.Z + b.Z, a.W + b.W);

    // Operator + for Quaternion used in Derivatives sum
    // (System.Numerics.Quaternion doesn't have operator+)
}

// ─── Extension for Quaternion arithmetic in RK4 ─────────────────

internal static class QuaternionExtensions
{
    public static Quaternion Add(this Quaternion a, Quaternion b)
        => new(a.X + b.X, a.Y + b.Y, a.Z + b.Z, a.W + b.W);
}
