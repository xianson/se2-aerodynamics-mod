#pragma warning disable
using System;
using System.Collections.Generic;
using Keen.Game2.Simulation.WorldObjects.CubeBlocks;
using Keen.Game2.Simulation.WorldObjects.CubeGrids;
using Keen.VRage.Core;
using Keen.VRage.Core.Game.Components;
using Keen.VRage.DCS.Annotations;

namespace AeroMod;

/// <summary>
/// A 3x3x3 clamshell AIRBRAKE (server; injected into its blocks' server prefabs - 2026-10-14, the user: "an airbrake block
/// that deploys with the parking gear signal ... max angle as a slider"): its two leaves open together to MaxOpening (the
/// terminal's slider, 0..MaxOpeningLimit - within the 3x3x3 box) while the grid's parking brake is on (P) and the block is
/// on, powered and whole, and close again when it is not - at OpenRate. The grid's air reads the opening (IAirbrakeState:
/// the Airbrake component, a flat plate of both leaves x sin of it); the clients turn the leaves by it (published with the
/// gears' records: ThrustTorque.PublishGear, the opening in the progress field). Saved with the world (AeroSurfaceSaved).
/// </summary>
public partial class AeroAirbrakeComponent : Component, IInSceneListener, IAirbrakeState
{
    /// <summary>The most a leaf opens inside the 3x3x3 box (the kitbash's leaves: airbrake_from_kitbash.py), and the default.</summary>
    public const float MaxOpeningLimit = 35f, DefaultMaxOpening = 35f;
    /// <summary>How fast the leaves move (deg/s): fully open in about a second.</summary>
    public static float OpenRate = 35f;
    public static bool Enabled = true;
    /// <summary>Harness: open every airbrake without the parking signal (1), close them (2), follow it (0).</summary>
    public static int DevCommand;

    float _max = DefaultMaxOpening;
    public float MaxOpening { get => _max; set => _max = float.IsFinite(value) ? Math.Clamp(value, 0f, MaxOpeningLimit) : _max; }
    public bool Changed => _max != DefaultMaxOpening;

    /// <summary>Each leaf's angle from closed now (deg).</summary>
    public float OpeningDeg { get; private set; }

    internal static readonly List<AeroAirbrakeComponent> All = new();
    bool _server; long _tick; Vector3I _key; bool _hasKey;
    PowerableBlockComponent _power; bool _powerLooked;
    public static bool DevIgnorePower;

    void IInSceneListener.OnAddedToScene()
    {
        try { _server = !string.Equals(Data.Scene?.DebugName, "Client", StringComparison.Ordinal); } catch { _server = true; }
        lock (All) All.Add(this);
    }
    void IInSceneListener.OnBeforeRemovedFromScene() { lock (All) All.Remove(this); }

    public bool Working
    {
        get
        {
            if (!_powerLooked) { _powerLooked = true; _power = Entity?.TryGet<PowerableBlockComponent>(); }
            var p = _power;
            if (p == null) return true;
            return p.Functional && p.Enabled && (p.Supplied || DevIgnorePower);
        }
    }

    [Before(typeof(Keen.VRage.Core.Systems.Simulation))]
    private class OnAirbrake : JobGroup;

    [OnAirbrake]
    [MustHave(typeof(AeroAirbrakeComponent))]
    private static void AirbrakeJob(AeroAirbrakeComponent a)
    {
        if (!a._server || !Enabled || a.Entity == null) return;
        long now = System.Diagnostics.Stopwatch.GetTimestamp();
        float dt = a._tick == 0 ? 0f : (float)Math.Min(0.1, (now - a._tick) / (double)System.Diagnostics.Stopwatch.Frequency);
        a._tick = now;
        if (!a._hasKey && a.Entity.TryGet<CubeBlockComponent>() is CubeBlockComponent cb) { var bb = cb.AABB; a._key = bb.Min + bb.Max; a._hasKey = true; }
        var grid = a.Entity.GetTopLevelParent();
        bool parked = grid?.TryGet<CubeGridComponent>()?.HasActiveParkingBrake ?? false;
        bool open = DevCommand == 1 || (DevCommand == 0 && parked && a.Working);
        float target = open ? a._max : 0f;
        float step = OpenRate * dt;
        a.OpeningDeg = Math.Clamp(target, a.OpeningDeg - step, a.OpeningDeg + step);
        // (to the clients: the opening in the gear record's progress)
        var aero = grid?.TryGet<AeroGridComponent>();
        if (aero != null && a._hasKey) ThrustTorque.PublishGear(aero, grid.Data.GetWorldTransform(), a._key, a.OpeningDeg, 0f);
    }

    /// <summary>One line of the save: "x y z max" (world position of the block, its max opening).</summary>
    internal string SaveLine()
    {
        var p = Entity.Data.GetWorldTransform().Position; var c = System.Globalization.CultureInfo.InvariantCulture;
        return string.Join(" ", p.X.ToString("R", c), p.Y.ToString("R", c), p.Z.ToString("R", c), _max.ToString("R", c));
    }

    /// <summary>Harness (aeroget AeroAirbrakeComponent.Summary).</summary>
    public static string Summary
    {
        get
        {
            var sb = new System.Text.StringBuilder();
            lock (All)
            {
                sb.Append(All.Count).Append(" airbrakes");
                foreach (var a in All)
                {
                    bool parked = a.Entity?.GetTopLevelParent()?.TryGet<CubeGridComponent>()?.HasActiveParkingBrake ?? false;
                    sb.Append($" | open {a.OpeningDeg:F1} of {a._max:F0} deg, parked {parked}, {(a.Working ? "working" : "OFF")}");
                }
            }
            return sb.ToString();
        }
    }
}
