#pragma warning disable CS0649, CS8632, CS1591, CS1573, CS1570, CS1574, CS0419, CS1734
using System;
namespace AeroMod;

/// <summary>
/// THE ELASTIC NETWORK - how a load really shares between parallel paths (ShipStructure.Solve).
///
/// Every block a rigid body (3 translations, 3 rotations); every contact patch an elastic joint at its centre, as stiff
/// as a short steel tube of the patch's outline and the blocks' wall (StructMaterial.Wall), as long as the blocks'
/// centres are apart across it: axial E A / L, shear G (A/2) / L, bending E I / L about each in-plane axis, torsion
/// G J / L (Bredt). K u = f, f each block's load (its air, its thrust, its inertia in the ship's motion - balanced:
/// each piece's root is held, and holds ~nothing). Solved by conjugate gradients PRECONDITIONED BY THE TREE: the load
/// path tree's own stiffness solves exactly in one pass in and one out (each joint takes everything outboard of it,
/// deflects by that over its stiffness, the deflections add up outward), so a structure that is a tree converges in
/// one step. A real one - wing boxes, fuselage rings, engine pods on several faces - can take hundreds (stiff joints
/// closing loops round a long path), so each solve does at most MaxIterations, warm-started from the last, and what the
/// iterate still leaves unbalanced is carried down the tree's joints: the loads are ALWAYS in equilibrium, and the way
/// they share between parallel paths converges over successive solves. Then each patch's spring force is its load: normal stress at its outline's extreme fibres
/// (N/A + Mu/Zu + Mv/Zv), shear (V over half the area, torsion by Bredt), von Mises, over the weaker block's yield x
/// StrengthAt(temperature) x Strength.
/// </summary>
public sealed partial class ShipStructure
{
    public const double E = 200e9, G = 77e9;
    /// <summary>Most conjugate-gradient iterations per solve, and the residual it stops at (of the load).</summary>
    public static int MaxIterations = 60;
    public static double Tolerance = 1e-5;

    public int Iterations;
    /// <summary>(diagnostics) each iteration's relative residual and r.z.</summary>
    public static Action<int, double, double> Trace;
    public double RelResidual;

    // per contact: blocks, lever arms from each block's centre to the patch centre, stiffness (grid axes), section
    int[] _ka, _kb;
    double[] _rax, _ray, _raz, _rbx, _rby, _rbz;
    double[] _ktx, _kty, _ktz, _krx, _kry, _krz;
    float[] _secA, _secZu, _secZv, _secT;
    int[] _secAxis;
    // the tree's joint per block: its contact, and whether the block is that contact's B
    bool[] _childIsB;
    // working vectors (6 per block)
    double[] _x, _r, _z, _p, _q, _f;
    double[] _sf;
    bool _netReady;

    internal void BuildNetwork()
    {
        int nc = _contacts.Count, n = Count;
        _ka = new int[nc]; _kb = new int[nc];
        _rax = new double[nc]; _ray = new double[nc]; _raz = new double[nc]; _rbx = new double[nc]; _rby = new double[nc]; _rbz = new double[nc];
        _ktx = new double[nc]; _kty = new double[nc]; _ktz = new double[nc]; _krx = new double[nc]; _kry = new double[nc]; _krz = new double[nc];
        _secA = new float[nc]; _secZu = new float[nc]; _secZv = new float[nc]; _secT = new float[nc]; _secAxis = new int[nc];
        for (int k = 0; k < nc; k++)
        {
            var c = _contacts[k];
            int ax = c.Axis, u = (ax + 1) % 3, v = (ax + 2) % 3;
            _ka[k] = c.A; _kb[k] = c.B; _secAxis[k] = ax;
            var p = ContactPoint(k);
            var ra = p - Center[c.A]; var rb = p - Center[c.B];
            _rax[k] = ra.X; _ray[k] = ra.Y; _raz[k] = ra.Z; _rbx[k] = rb.X; _rby[k] = rb.Y; _rbz[k] = rb.Z;
            float t = MathF.Min(Wall[c.A], Wall[c.B]) * ContactShare(k), a = c.U1 - c.U0, b = c.V1 - c.V0;   // (TouchingShare: the steel kept)
            float A = 2f * (a + b) * t;
            float iuu = t * b * b * b / 6f + a * t * b * b / 2f, ivv = t * a * a * a / 6f + b * t * a * a / 2f;
            float J = 4f * (a * b) * (a * b) * t / (2f * (a + b));
            double L = Math.Max(Math.Abs(C(Center[c.B] - Center[c.A], ax)), 0.125);
            double kn = E * A / L, ks = G * 0.5 * A / L, kt = G * J / L, ku = E * iuu / L, kv = E * ivv / L;
            // grid axes: along the patch's normal axial and torsion, across it shear and bending
            double Pick(int axis, double alongN, double alongU, double alongV) => axis == ax ? alongN : axis == u ? alongU : alongV;
            _ktx[k] = Pick(0, kn, ks, ks); _kty[k] = Pick(1, kn, ks, ks); _ktz[k] = Pick(2, kn, ks, ks);
            _krx[k] = Pick(0, kt, ku, kv); _kry[k] = Pick(1, kt, ku, kv); _krz[k] = Pick(2, kt, ku, kv);
            _secA[k] = A; _secZu[k] = iuu / MathF.Max(0.5f * b, 1e-4f); _secZv[k] = ivv / MathF.Max(0.5f * a, 1e-4f); _secT[k] = 2f * a * b * t;
        }
        _childIsB = new bool[n];
        for (int i = 0; i < n; i++) if (Parent[i] >= 0) _childIsB[i] = _contacts[_parentContact[i]].B == i;
        int m = 6 * n;
        _x = new double[m]; _r = new double[m]; _z = new double[m]; _p = new double[m]; _q = new double[m]; _f = new double[m]; _sf = new double[m];
        _netReady = true;
    }

    /// <summary>y = K x (roots held: their entries zero).</summary>
    internal void MulK(double[] x, double[] y) => MulK(x, y, false);
    internal void MulK(double[] x, double[] y, bool treeOnly)
    {
        Array.Clear(y, 0, y.Length);
        int nc = _ka.Length;
        bool[] inTree = null;
        if (treeOnly) { inTree = new bool[nc]; for (int i = 0; i < Count; i++) if (Parent[i] >= 0) inTree[_parentContact[i]] = true; }
        for (int k = 0; k < nc; k++)
        {
            if (treeOnly && !inTree[k]) continue;
            int a = 6 * _ka[k], b = 6 * _kb[k];
            double rax = _rax[k], ray = _ray[k], raz = _raz[k], rbx = _rbx[k], rby = _rby[k], rbz = _rbz[k];
            // the patch centre's motion on b relative to a: u + theta x r
            double tax = x[a + 3], tay = x[a + 4], taz = x[a + 5], tbx = x[b + 3], tby = x[b + 4], tbz = x[b + 5];
            double dx = x[b] + (tby * rbz - tbz * rby) - x[a] - (tay * raz - taz * ray);
            double dy = x[b + 1] + (tbz * rbx - tbx * rbz) - x[a + 1] - (taz * rax - tax * raz);
            double dz = x[b + 2] + (tbx * rby - tby * rbx) - x[a + 2] - (tax * ray - tay * rax);
            double Fx = _ktx[k] * dx, Fy = _kty[k] * dy, Fz = _ktz[k] * dz;
            double Mx = _krx[k] * (tbx - tax), My = _kry[k] * (tby - tay), Mz = _krz[k] * (tbz - taz);
            y[b] += Fx; y[b + 1] += Fy; y[b + 2] += Fz;
            y[b + 3] += rby * Fz - rbz * Fy + Mx; y[b + 4] += rbz * Fx - rbx * Fz + My; y[b + 5] += rbx * Fy - rby * Fx + Mz;
            y[a] -= Fx; y[a + 1] -= Fy; y[a + 2] -= Fz;
            y[a + 3] -= ray * Fz - raz * Fy + Mx; y[a + 4] -= raz * Fx - rax * Fz + My; y[a + 5] -= rax * Fy - ray * Fx + Mz;
        }
        for (int i = 0; i < Count; i++) if (Parent[i] < 0) for (int j = 0; j < 6; j++) y[6 * i + j] = 0;
    }

    /// <summary>z = K_tree^-1 r: the tree's springs alone, solved exactly (roots held). In: each joint takes all its
    /// subtree's load (force, and moment about the patch centre); out: its deflection (load / stiffness) added to its
    /// parent's motion.</summary>
    internal void TreeSolve(double[] r, double[] z)
    {
        var s = _sf;
        Array.Copy(r, s, r.Length);
        // in: s holds each block's own load plus its children's, about its centre
        for (int k = Order.Length - 1; k >= 0; k--)
        {
            int i = Order[k], p = Parent[i];
            if (p < 0) continue;
            int c = _parentContact[i], o = 6 * i, q = 6 * p;
            // the load about the patch centre: M + (centre - P) x F = M - r x F, r = P - centre
            double rx, ry, rz; if (_childIsB[i]) { rx = _rbx[c]; ry = _rby[c]; rz = _rbz[c]; } else { rx = _rax[c]; ry = _ray[c]; rz = _raz[c]; }
            double Fx = s[o], Fy = s[o + 1], Fz = s[o + 2];
            double Mx = s[o + 3] - (ry * Fz - rz * Fy), My = s[o + 4] - (rz * Fx - rx * Fz), Mz = s[o + 5] - (rx * Fy - ry * Fx);
            // to the parent: about its centre, M + rp x F (rp = P - parent centre)
            double px, py, pz; if (_childIsB[i]) { px = _rax[c]; py = _ray[c]; pz = _raz[c]; } else { px = _rbx[c]; py = _rby[c]; pz = _rbz[c]; }
            s[q] += Fx; s[q + 1] += Fy; s[q + 2] += Fz;
            s[q + 3] += Mx + (py * Fz - pz * Fy); s[q + 4] += My + (pz * Fx - px * Fz); s[q + 5] += Mz + (px * Fy - py * Fx);
            // keep the joint's load (about P) in the child's slots for the way out
            s[o + 3] = Mx; s[o + 4] = My; s[o + 5] = Mz;
        }
        // out: roots held; each child = parent's motion at P + the joint's deflection
        for (int k = 0; k < Order.Length; k++)
        {
            int i = Order[k], p = Parent[i], o = 6 * i;
            if (p < 0) { for (int j = 0; j < 6; j++) z[o + j] = 0; continue; }
            int c = _parentContact[i], q = 6 * p;
            double sg = _childIsB[i] ? 1 : -1;   // (the spring's force on B is the joint's load if the child is B)
            double dx = sg * s[o] / _ktx[c], dy = sg * s[o + 1] / _kty[c], dz = sg * s[o + 2] / _ktz[c];
            double ex = sg * s[o + 3] / _krx[c], ey = sg * s[o + 4] / _kry[c], ez = sg * s[o + 5] / _krz[c];
            double rcx, rcy, rcz, rpx, rpy, rpz;
            if (_childIsB[i]) { rcx = _rbx[c]; rcy = _rby[c]; rcz = _rbz[c]; rpx = _rax[c]; rpy = _ray[c]; rpz = _raz[c]; }
            else { rcx = _rax[c]; rcy = _ray[c]; rcz = _raz[c]; rpx = _rbx[c]; rpy = _rby[c]; rpz = _rbz[c]; }
            double tpx = z[q + 3], tpy = z[q + 4], tpz = z[q + 5];
            // child rotation, then translation so the patch centre moves by the parent's + the deflection (signed: B - A)
            double tcx = tpx + sg * ex, tcy = tpy + sg * ey, tcz = tpz + sg * ez;
            double Px = z[q] + (tpy * rpz - tpz * rpy) + sg * dx, Py = z[q + 1] + (tpz * rpx - tpx * rpz) + sg * dy, Pz = z[q + 2] + (tpx * rpy - tpy * rpx) + sg * dz;
            z[o] = Px - (tcy * rcz - tcz * rcy); z[o + 1] = Py - (tcz * rcx - tcx * rcz); z[o + 2] = Pz - (tcx * rcy - tcy * rcx);
            z[o + 3] = tcx; z[o + 4] = tcy; z[o + 5] = tcz;
        }
    }

    static double Dot(double[] a, double[] b) { double s = 0; for (int i = 0; i < a.Length; i++) s += a[i] * b[i]; return s; }

    /// <summary>K x = f by tree-preconditioned conjugate gradients, from the last solution.</summary>
    void SolveNetwork()
    {
        if (!_netReady) BuildNetwork();
        var x = _x; var r = _r; var z = _z; var p = _p; var q = _q; var f = _f;
        for (int i = 0; i < Count; i++) if (Parent[i] < 0) for (int j = 0; j < 6; j++) f[6 * i + j] = 0;
        double fn = Math.Sqrt(Dot(f, f));
        Iterations = 0; RelResidual = 0;
        if (fn == 0) { Array.Clear(x, 0, x.Length); return; }
        MulK(x, q);
        for (int i = 0; i < r.Length; i++) r[i] = f[i] - q[i];
        // (a warm start worse than none - the loads changed completely: from the tree's own solution)
        if (Math.Sqrt(Dot(r, r)) > fn) { TreeSolve(f, x); MulK(x, q); for (int i = 0; i < r.Length; i++) r[i] = f[i] - q[i]; }
        TreeSolve(r, z);
        Array.Copy(z, p, z.Length);
        double rz = Dot(r, z);
        for (int it = 0; it < MaxIterations; it++)
        {
            RelResidual = Math.Sqrt(Dot(r, r)) / fn;
            Trace?.Invoke(it, RelResidual, rz);
            if (RelResidual < Tolerance) break;
            MulK(p, q);
            double pq = Dot(p, q);
            if (pq <= 0) break;
            double alpha = rz / pq;
            for (int i = 0; i < x.Length; i++) { x[i] += alpha * p[i]; r[i] -= alpha * q[i]; }
            TreeSolve(r, z);
            double rz1 = Dot(r, z);
            double beta = rz1 / rz; rz = rz1;
            for (int i = 0; i < p.Length; i++) p[i] = z[i] + beta * p[i];
            Iterations = it + 1;
        }
    }

    /// <summary>Each patch's spring load (from the solved motion) on its own section: ContactUtil, the tree joints'
    /// loads (JForce, JMoment about the patch: what the child's side passes the parent's) and JUtil, the worst.</summary>
    void NetworkStresses()
    {
        var x = _x;
        int nc = _ka.Length;
        // the residual f - K x, carried down the tree: each tree joint's share of it (about its patch centre)
        MulK(x, _q);
        for (int i = 0; i < _r.Length; i++) _r[i] = _f[i] - _q[i];
        TreeLoads(_r);
        MaxUtil = 0f; MaxContact = -1; MaxJoint = -1;
        _springF ??= new Vector3[nc]; _springM ??= new Vector3[nc];
        for (int k = 0; k < nc; k++)
        {
            int a = 6 * _ka[k], b = 6 * _kb[k];
            double rax = _rax[k], ray = _ray[k], raz = _raz[k], rbx = _rbx[k], rby = _rby[k], rbz = _rbz[k];
            double tax = x[a + 3], tay = x[a + 4], taz = x[a + 5], tbx = x[b + 3], tby = x[b + 4], tbz = x[b + 5];
            double dx = x[b] + (tby * rbz - tbz * rby) - x[a] - (tay * raz - taz * ray);
            double dy = x[b + 1] + (tbz * rbx - tbx * rbz) - x[a + 1] - (taz * rax - tax * raz);
            double dz = x[b + 2] + (tbx * rby - tby * rbx) - x[a + 2] - (tax * ray - tay * rax);
            var F = new Vector3((float)(_ktx[k] * dx), (float)(_kty[k] * dy), (float)(_ktz[k] * dz));
            var M = new Vector3((float)(_krx[k] * (tbx - tax)), (float)(_kry[k] * (tby - tay)), (float)(_krz[k] * (tbz - taz)));
            // (what the iterate left unbalanced, down the tree: equilibrium exact whatever the iterations)
            if (_treeOf[k] >= 0) { int i = _treeOf[k]; float sg0 = _childIsB[i] ? 1f : -1f; F += _fixF[i] * sg0; M += _fixM[i] * sg0; }
            _springF[k] = F; _springM[k] = M;
            int ax = _secAxis[k], u = (ax + 1) % 3, v = (ax + 2) % 3;
            float sg = MathF.Abs(C(F, ax)) / _secA[k] + MathF.Abs(C(M, u)) / _secZu[k] + MathF.Abs(C(M, v)) / _secZv[k];
            float tau = MathF.Sqrt(C(F, u) * C(F, u) + C(F, v) * C(F, v)) / (0.5f * _secA[k]) + MathF.Abs(C(M, ax)) / MathF.Max(_secT[k], 1e-9f);
            float vm = MathF.Sqrt(sg * sg + 3f * tau * tau);
            int A = _ka[k], B = _kb[k];
            float sa = BlockStrength(A);
            float sb = BlockStrength(B);
            float util = vm / (MathF.Min(sa, sb) * Strength);
            ContactUtil[k] = util;
            if (util > MaxUtil) { MaxUtil = util; MaxContact = k; }
        }
        for (int i = 0; i < Count; i++)
        {
            if (Parent[i] < 0) { JUtil[i] = 0f; JForce[i] = Vector3.Zero; JMoment[i] = Vector3.Zero; continue; }
            int c = _parentContact[i];
            float sg = _childIsB[i] ? 1f : -1f;
            JForce[i] = _springF[c] * sg; JMoment[i] = _springM[c] * sg;
            JUtil[i] = ContactUtil[c];
        }
        if (MaxContact >= 0) MaxJoint = Outboard(MaxContact);
    }

    Vector3[] _springF, _springM, _fixF, _fixM;
    int[] _treeOf;

    /// <summary>The tree's joint loads for the loads r (each joint: its subtree's force, and moment about its patch
    /// centre) into _fixF / _fixM per child block.</summary>
    void TreeLoads(double[] r)
    {
        int n = Count;
        if (_treeOf == null)
        {
            _treeOf = new int[_ka.Length]; for (int k = 0; k < _treeOf.Length; k++) _treeOf[k] = -1;
            for (int i = 0; i < n; i++) if (Parent[i] >= 0) _treeOf[_parentContact[i]] = i;
            _fixF = new Vector3[n]; _fixM = new Vector3[n];
        }
        var s = _sf;
        Array.Copy(r, s, r.Length);
        for (int k = Order.Length - 1; k >= 0; k--)
        {
            int i = Order[k], p = Parent[i];
            if (p < 0) continue;
            int c = _parentContact[i], o = 6 * i, q = 6 * p;
            double rx, ry, rz; if (_childIsB[i]) { rx = _rbx[c]; ry = _rby[c]; rz = _rbz[c]; } else { rx = _rax[c]; ry = _ray[c]; rz = _raz[c]; }
            double Fx = s[o], Fy = s[o + 1], Fz = s[o + 2];
            double Mx = s[o + 3] - (ry * Fz - rz * Fy), My = s[o + 4] - (rz * Fx - rx * Fz), Mz = s[o + 5] - (rx * Fy - ry * Fx);
            double px, py, pz; if (_childIsB[i]) { px = _rax[c]; py = _ray[c]; pz = _raz[c]; } else { px = _rbx[c]; py = _rby[c]; pz = _rbz[c]; }
            s[q] += Fx; s[q + 1] += Fy; s[q + 2] += Fz;
            s[q + 3] += Mx + (py * Fz - pz * Fy); s[q + 4] += My + (pz * Fx - px * Fz); s[q + 5] += Mz + (px * Fy - py * Fx);
            _fixF[i] = new Vector3((float)Fx, (float)Fy, (float)Fz); _fixM[i] = new Vector3((float)Mx, (float)My, (float)Mz);
        }
    }
}
