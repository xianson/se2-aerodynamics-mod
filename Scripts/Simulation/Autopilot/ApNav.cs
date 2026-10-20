#pragma warning disable CS0649, CS8632, CS1591, CS1573, CS1570, CS1574, CS0419, CS1734
using System;
using System.Globalization;
namespace AeroMod;

/// <summary>
/// THE ALTIMETER (engine-free): the height over a datum - the ground under the ship where the mode was engaged - measured along
/// the gravity. Over a planet the "up" turns as the ship flies; the planet's centre is found where the gravity's lines through the
/// datum and through the ship meet (they meet at the centre of a round planet), so the height is the distance from that centre
/// less the datum's. Close to the datum (the lines nearly parallel) the flat height along the datum's up is exact enough and is used.
/// </summary>
public sealed class ApAltimeter
{
    Vector3D _p0; Vector3 _up0 = Vector3.UnitY; float _h0; bool _set;
    /// <summary>The planet's centre as last found (HasCenter), for the diagnostics.</summary>
    public Vector3D Center; public bool HasCenter;
    /// <summary>The up turned this little (rad) still flies the flat height; past twice as much the radial one.</summary>
    public const float FlatAngle = 0.004f;

    public bool Set => _set;
    /// <summary>The datum: a point (world), its up and its own height (m: the ship's height over the ground there, so the
    /// altimeter reads the height over that ground).</summary>
    public void Reset(Vector3D p, Vector3 up, float h0) { _p0 = p; _up0 = up.LengthSquared() > 0.5f ? Vector3.Normalize(up) : Vector3.UnitY; _h0 = h0; _set = true; HasCenter = false; }

    /// <summary>The height (m) at p with the gravity's up there.</summary>
    public float Height(Vector3D p, Vector3 up)
    {
        if (!_set) Reset(p, up, 0f);
        double flat = Vector3D.Dot(p - _p0, (Vector3D)_up0) + _h0;
        if (up.LengthSquared() < 0.5f) return (float)flat;
        up = Vector3.Normalize(up);
        float ang = MathF.Acos(Math.Clamp(Vector3.Dot(up, _up0), -1f, 1f));
        if (ang < FlatAngle) return (float)flat;
        // the two lines p0 - t up0 and p - u up: their nearest points; the centre between them
        Vector3D d0 = _up0, d1 = up, w = _p0 - p;
        double a = 1.0, b = Vector3D.Dot(d0, d1), c = 1.0, d = Vector3D.Dot(d0, w), e = Vector3D.Dot(d1, w), den = a * c - b * b;
        if (den < 1e-9) return (float)flat;
        double t = (b * e - c * d) / den, u = (a * e - b * d) / den;
        Vector3D c0 = _p0 + d0 * t, c1 = p + d1 * u;
        Center = (c0 + c1) * 0.5; HasCenter = true;
        double radial = (p - Center).Length() - (_p0 - Center).Length() + _h0;
        double k = Math.Clamp((ang - FlatAngle) / FlatAngle, 0.0, 1.0);
        return (float)(flat + (radial - flat) * k);
    }
}

/// <summary>Headings (engine-free): north is the world's -Z laid flat on the local horizon (+X where -Z is straight up), east to
/// its right; degrees clockwise from north, as a compass.</summary>
public static class ApCompass
{
    public static void Frame(Vector3 up, out Vector3 north, out Vector3 east)
    {
        north = ApMath.Flat(-Vector3.UnitZ, up);
        if (north.LengthSquared() < 1e-4f) north = ApMath.Flat(Vector3.UnitX, up);
        north = Vector3.Normalize(north); east = Vector3.Cross(north, up);
    }
    /// <summary>The heading (deg 0..360) of a world direction.</summary>
    public static float Heading(Vector3 v, Vector3 up)
    {
        Frame(up, out var n, out var e);
        float h = MathF.Atan2(Vector3.Dot(v, e), Vector3.Dot(v, n)) / ApMath.Deg;
        return h < 0f ? h + 360f : h;
    }
    /// <summary>The world direction (horizontal unit) of a heading (deg).</summary>
    public static Vector3 Dir(float deg, Vector3 up)
    {
        Frame(up, out var n, out var e);
        float r = deg * ApMath.Deg;
        return n * MathF.Cos(r) + e * MathF.Sin(r);
    }
    /// <summary>a - b wrapped into -180..180 (deg).</summary>
    public static float Diff(float a, float b) { float d = (a - b) % 360f; if (d > 180f) d -= 360f; if (d < -180f) d += 360f; return d; }
}

/// <summary>
/// L1 LATERAL GUIDANCE (engine-free; ArduPilot's L1, the guidance PX4's NPFG grew from): steer toward a point L1 ahead on the path,
/// the lateral acceleration 2 V^2 / L1 sin(eta) - the ground velocity in it (the wind's drift is the ground track's error, so a
/// crosswind is flown with the crab it needs). L1 itself from the ship's own turn: the full-scale ask (eta 90 deg) a share of the
/// acceleration it can turn with (FlightPerf / the bank limit) - never a period set by hand. A carrot on a circle a chord L1 away
/// gives exactly V^2 / R on it, so a loiter needs no feed-forward. Horizontal, world vectors, allocation-free.
/// </summary>
public static class ApL1
{
    /// <summary>The L1 distance (m) for ground speed v and the lateral acceleration it can fly (m/s2).</summary>
    public static float Distance(float v, float aMax) => MathF.Max(30f, 2.2f * v * v / MathF.Max(aMax, 0.3f));

    /// <summary>The lateral acceleration (world, horizontal) toward the carrot c from pos at ground velocity vg.</summary>
    public static Vector3 Toward(Vector3D pos, Vector3 vg, Vector3 up, Vector3D c, float l1, float aMax)
    {
        Vector3 v = ApMath.Flat(vg, up); float sp = v.Length();
        Vector3 to = ApMath.Flat((Vector3)(c - pos), up);
        if (to.LengthSquared() < 1e-4f) return Vector3.Zero;
        if (sp < 1f) return Vector3.Zero;
        Vector3 vh = v / sp, th = Vector3.Normalize(to), right = Vector3.Cross(vh, up);
        float sinE = Vector3.Dot(th, right), cosE = Vector3.Dot(th, vh);
        float a = cosE < 0f ? MathF.Sign(sinE == 0f ? 1f : sinE) * aMax : Math.Clamp(2f * sp * sp / l1 * sinE, -aMax, aMax);
        return right * a;
    }

    /// <summary>Along the line a -> b (crossTrack out: + right of it, m).</summary>
    public static Vector3 Line(Vector3D pos, Vector3 vg, Vector3 up, Vector3D a, Vector3D b, float l1, float aMax, out float crossTrack)
    {
        Vector3 ab = ApMath.Flat((Vector3)(b - a), up); float len = ab.Length();
        crossTrack = 0f;
        if (len < 1f) return Toward(pos, vg, up, b, l1, aMax);
        Vector3 t = ab / len, rel = ApMath.Flat((Vector3)(pos - a), up);
        Vector3 right = Vector3.Cross(t, up);
        float s = Vector3.Dot(rel, t); crossTrack = Vector3.Dot(rel, right);
        float ax = MathF.Abs(crossTrack);
        float ahead = ax < l1 ? MathF.Sqrt(l1 * l1 - ax * ax) : 0f;
        Vector3D carrot = a + (Vector3D)(t * (s + MathF.Max(ahead, 1f)));
        return Toward(pos, vg, up, carrot, l1, aMax);
    }

    /// <summary>Round a circle (centre c, radius r; dir +1 clockwise seen from above - right turns, -1 left); radErr out: the
    /// distance from the centre less r (m).</summary>
    public static Vector3 Circle(Vector3D pos, Vector3 vg, Vector3 up, Vector3D c, float r, int dir, float l1, float aMax, out float radErr)
    {
        Vector3 rel = ApMath.Flat((Vector3)(pos - c), up); float d = rel.Length();
        radErr = d - r;
        Vector3 rh = d > 1f ? rel / d : ApMath.Flat(Vector3.UnitX, up);
        if (rh.LengthSquared() < 1e-4f) rh = ApMath.Flat(Vector3.UnitZ, up);
        rh = Vector3.Normalize(rh);
        Vector3 t = Vector3.Cross(rh, up) * (dir >= 0 ? 1f : -1f);
        float phi;
        if (d > r + l1)
        {
            // (far outside: to the tangent point, then on round)
            float beta = MathF.Acos(Math.Clamp(r / d, -1f, 1f));
            phi = beta;
        }
        else phi = 2f * MathF.Asin(Math.Clamp(l1 / (2f * MathF.Max(r, 1f)), 0f, 1f));
        Vector3D carrot = c + (Vector3D)((rh * MathF.Cos(phi) + t * MathF.Sin(phi)) * r);
        return Toward(pos, vg, up, carrot, l1, aMax);
    }

    /// <summary>Onto a course (world direction, horizontal): the carrot far along it.</summary>
    public static Vector3 Course(Vector3 vg, Vector3 up, Vector3 course, float l1, float aMax)
    {
        Vector3 v = ApMath.Flat(vg, up); float sp = v.Length();
        Vector3 cw = ApMath.Flat(course, up);
        if (sp < 1f || cw.LengthSquared() < 1e-6f) return Vector3.Zero;
        Vector3 vh = v / sp, th = Vector3.Normalize(cw), right = Vector3.Cross(vh, up);
        float sinE = Vector3.Dot(th, right), cosE = Vector3.Dot(th, vh);
        float a = cosE < 0f ? MathF.Sign(sinE == 0f ? 1f : sinE) * aMax : Math.Clamp(2f * sp * sp / l1 * sinE, -aMax, aMax);
        return right * a;
    }
}

/// <summary>
/// TECS - total energy control (engine-free; ArduPilot / PX4's): the throttle flies the total energy (height and speed together),
/// the flight path its split, so a plane short of thrust trades height for speed instead of stalling. Every limit is the ship's
/// own: the thrust along the path and the drag now (the stall sweep's), so the most climb is the excess power's (V (T - D) / W);
/// the most sink the idle's (the drag alone holds the speed). Outputs a vertical acceleration (world up, m/s2) for the lift and a
/// throttle (0..1; NaN: speed not held - the flight director's own, the drag matched).
/// </summary>
public sealed class ApTecs
{
    float _vdot, _vPrev, _thrI; bool _has;
    /// <summary>Diagnostics: the climb rate asked (m/s), the throttle (0..1), climb limited by the thrust / sink by the idle.</summary>
    public float HdotDem, Throttle; public bool ThrustLimited, IdleLimited;
    /// <summary>The flight path's time constant (s: the vertical speed error's), the height's (s), the speed's (s).</summary>
    public const float TGamma = 1.5f, THeight = 6f, TSpeed = 6f;

    public void Reset() { _has = false; _thrI = 0f; _vdot = 0f; }

    /// <summary>h, hdot: height (m) and climb (m/s); hDem: the height wanted (NaN: hold the path - no vertical ask); vs: the most
    /// climb / sink asked (m/s); v: airspeed, vDem: wanted (NaN: speed not held); thrustMax (N, along the path), drag (N), mass,
    /// g; aVertMax: the most vertical acceleration (m/s2, the lift's).</summary>
    public void Step(float h, float hdot, float hDem, float vs, float v, float vDem, float thrustMax, float drag, float mass, float g, float aVertMax, float dt, out float aVert, out float throttle)
    {
        v = MathF.Max(v, 1f);
        if (!_has) { _vPrev = v; _has = true; }
        _vdot += ((v - _vPrev) / MathF.Max(dt, 1e-3f) - _vdot) * MathF.Min(1f, dt / 0.5f); _vPrev = v;
        bool hold = float.IsFinite(hDem), speed = float.IsFinite(vDem);
        float hdotDem = hold ? Math.Clamp((hDem - h) / THeight, -vs, vs) : hdot;
        float accMax = MathF.Max(0.2f, (thrustMax - drag) / mass), decMax = MathF.Max(0.2f, drag / mass);
        float vdotDem = speed ? Math.Clamp((vDem - v) / TSpeed, -decMax, accMax) : 0f;
        ThrustLimited = IdleLimited = false;
        if (speed && thrustMax > 1f)
        {
            float t = mass * (vdotDem + g * hdotDem / v) + drag;
            if (t > thrustMax)
            {
                // (short of thrust: the speed first - the climb is what the excess power gives; under the speed asked, none)
                ThrustLimited = true;
                float hdMax = (thrustMax - drag - mass * MathF.Max(vdotDem, 0f)) * v / (mass * g);
                if (v < 0.9f * vDem) hdMax = MathF.Min(hdMax, 0f);
                hdotDem = MathF.Max(MathF.Min(hdotDem, hdMax), -vs);
                t = thrustMax;
            }
            else if (t < 0f)
            {
                // (the idle cannot hold the speed down this steep: a shallower descent)
                IdleLimited = true;
                float hdMin = (-drag - mass * MathF.Min(vdotDem, 0f)) * v / (mass * g);
                hdotDem = MathF.Min(MathF.Max(hdotDem, hdMin), vs);
                t = 0f;
            }
            float u = t / thrustMax;
            bool sat = (u >= 0.999f && vDem > v) || (u <= 0.001f && vDem < v);
            if (!sat) _thrI = Math.Clamp(_thrI + 0.02f * (vDem - v) * dt, -0.3f, 0.3f);
            throttle = Math.Clamp(u + _thrI, 0f, 1f);
        }
        else throttle = float.NaN;
        HdotDem = hdotDem; Throttle = throttle;
        aVert = hold || speed ? Math.Clamp((hdotDem - hdot) / TGamma, -aVertMax, aVertMax) : 0f;
    }
}

/// <summary>
/// THE ROUTE's text (engine-free): entries split by ';' or new lines; each one coordinates - "x y z", "x, y, z", "{X:1 Y:2 Z:3}",
/// SE1's "GPS:name:x:y:z:", SE2's clipboard "name|{X:.. Y:.. Z:..}|colour|icon" - or a GPS marker's name, found by the caller's
/// resolver (the grid owner's GPS list in game). Allocates: parsed when the text changes, never per tick.
/// </summary>
public static class ApRoute
{
    public struct Point { public string Name; public Vector3D Pos; public bool Found; }

    public static List<Point> Parse(string text, Func<string, (bool ok, Vector3D pos)> resolve)
    {
        var list = new List<Point>();
        if (string.IsNullOrWhiteSpace(text)) return list;
        foreach (var raw in text.Split(new[] { ';', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries))
        {
            string e = raw.Trim();
            if (e.Length == 0) continue;
            if (TryCoords(e, out var name, out var pos)) { list.Add(new Point { Name = name, Pos = pos, Found = true }); continue; }
            var r = resolve != null ? resolve(e) : (false, default);
            list.Add(new Point { Name = e, Pos = r.pos, Found = r.ok });
        }
        return list;
    }

    static readonly CultureInfo C = CultureInfo.InvariantCulture;

    /// <summary>One entry as coordinates (its name: the GPS one, else ""). False: not coordinates (a marker's name).</summary>
    public static bool TryCoords(string e, out string name, out Vector3D pos)
    {
        name = ""; pos = default;
        e = e.Trim();
        if (e.StartsWith("GPS:", StringComparison.OrdinalIgnoreCase))
        {
            var w = e.Split(':');
            if (w.Length >= 5 && D(w[2], out double x) && D(w[3], out double y) && D(w[4], out double z)) { name = w[1]; pos = new Vector3D(x, y, z); return true; }
            return false;
        }
        int bar = e.IndexOf('|');
        if (bar >= 0)
        {
            var w = e.Split('|');
            if (w.Length >= 2 && Braced(w[1], out pos)) { name = w[0].Replace("<SEGMENT_BREAK>", "|"); return true; }
            return false;
        }
        if (Braced(e, out pos)) return true;
        var n = e.Split(new[] { ' ', ',', '\t' }, StringSplitOptions.RemoveEmptyEntries);
        if (n.Length == 3 && D(n[0], out double a) && D(n[1], out double b) && D(n[2], out double c)) { pos = new Vector3D(a, b, c); return true; }
        return false;
    }

    /// <summary>"{X:1 Y:2 Z:3}" (Vector3D.ToString's form; the braces optional).</summary>
    static bool Braced(string s, out Vector3D pos)
    {
        pos = default;
        s = s.Trim().Trim('{', '}').Trim();
        int ix = s.IndexOf("X:", StringComparison.OrdinalIgnoreCase), iy = s.IndexOf("Y:", StringComparison.OrdinalIgnoreCase), iz = s.IndexOf("Z:", StringComparison.OrdinalIgnoreCase);
        if (ix < 0 || iy < ix || iz < iy) return false;
        if (D(s.Substring(ix + 2, iy - ix - 2), out double x) && D(s.Substring(iy + 2, iz - iy - 2), out double y) && D(s.Substring(iz + 2), out double z)) { pos = new Vector3D(x, y, z); return true; }
        return false;
    }

    static bool D(string s, out double v) => double.TryParse(s.Trim().TrimEnd(','), NumberStyles.Float, C, out v) && double.IsFinite(v);
}
