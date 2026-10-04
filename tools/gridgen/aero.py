"""aero - static-stability estimate from the planform (the textbook approximation).

Each horizontal-ish lifting surface i contributes lift slope a_i (Helmbold/DATCOM: 2 pi A / (2 + sqrt(4 + A^2
(1 + tan^2 L_c2)))), times its area S_i, the horizontal-projection factor cos^2(dihedral) (V-tails), a dynamic-
pressure ratio eta (0.9 behind the wing) and the downwash factor (1 - de/da), de/da = 2 a_w / (pi A_w), for surfaces
aft of the main wing.  The neutral point is the slope-weighted mean of the surfaces' aerodynamic centres (quarter
MAC), shifted forward by the fuselage's Munk moment, Cm_a,f = K_f W_f^2 L_f / (S c) per degree (Perkins & Hage /
Raymer chart for K_f vs the wing's quarter-chord station).

For a conventional layout this is exactly h_n = h_ac,w + eta V_H (a_t / a_w)(1 - de/da) - dh_fus.
"""
from __future__ import annotations

import math

import numpy as np

KF_TABLE = np.array([[0.0, 0.0015], [0.1, 0.0025], [0.2, 0.0045], [0.3, 0.0075], [0.4, 0.011], [0.5, 0.016],
                     [0.6, 0.025], [0.7, 0.04], [1.0, 0.06]])


def lift_slope(AR, sweep_c2=0.0):
    AR = max(AR, 0.3)
    return 2 * math.pi * AR / (2 + math.sqrt(4 + AR ** 2 * (1 + math.tan(sweep_c2) ** 2)))


def planform(surf):
    """Planform figures of one surface (both halves if mirrored): area S (true), projected horizontal span b,
    MAC, x of the MAC leading edge, x of the aerodynamic centre, aspect ratio, half-chord sweep."""
    s, ds, c, xle = surf.strips()
    w = c * ds
    S_half = float(w.sum())
    mac = float((c ** 2 * ds).sum() / S_half)
    xac = float((w * (xle + 0.25 * c)).sum() / S_half)
    k = 2 if surf.mirror else 1
    S = k * S_half
    cosG = abs(math.cos(surf.G))
    if surf.vertical:
        h = float(s.max() + ds / 2)
        AR = 2 * h * h / S_half          # fin on a body: end-plate effect doubles the effective aspect ratio
        b = h
    else:
        b = k * (getattr(surf, "y0", 0.0) * (1 if surf.mirror else 0) + float(s.max() + ds / 2) * cosG)
        AR = b * b / max(S * cosG, 1e-6)
    # half-chord sweep from the root and tip half-chord points
    x_r, x_t = xle[0] + c[0] / 2, xle[-1] + c[-1] / 2
    span_len = float(s[-1] - s[0]) or 1.0
    sweep_c2 = math.atan2(x_t - x_r, span_len)
    return dict(S=S, b=b, mac=mac, xac=xac, x_mac_le=xac - 0.25 * mac, AR=AR, sweep_c2=sweep_c2,
                proj=cosG ** 2 if not surf.vertical else 0.0)


def neutral_point(surfaces, fus=None):
    """surfaces: objects with role, id, mirror, vertical, G, strips().  fus: object with L, W (or None)."""
    pf = {s.id: planform(s) for s in surfaces}
    wings = [s for s in surfaces if s.role == "wing"]
    if not wings:
        wings = [max(surfaces, key=lambda s: pf[s.id]["S"] * pf[s.id]["proj"])]
    Sw = sum(pf[s.id]["S"] for s in wings)
    mac = sum(pf[s.id]["S"] * pf[s.id]["mac"] for s in wings) / Sw
    x_mac_le = sum(pf[s.id]["S"] * pf[s.id]["x_mac_le"] for s in wings) / Sw
    xac_w = x_mac_le + 0.25 * mac
    ARw = sum(pf[s.id]["S"] * pf[s.id]["AR"] for s in wings) / Sw
    aw = lift_slope(ARw, sum(pf[s.id]["S"] * pf[s.id]["sweep_c2"] for s in wings) / Sw)
    deps = min(max(2 * aw / (math.pi * ARw), 0.0), 0.6)
    num = den = 0.0
    contrib = []
    for s in surfaces:
        p = pf[s.id]
        if p["proj"] <= 1e-3:
            continue
        a = lift_slope(p["AR"], p["sweep_c2"])
        if s in wings:
            eta, eps = 1.0, 1.0
        elif p["xac"] > xac_w + 0.25 * mac:
            eta, eps = 0.9, 1.0 - deps
        else:
            eta, eps = 1.0, 1.0                 # canard / foreplane: no downwash
        k = a * eta * eps * p["S"] * p["proj"]
        num += k * p["xac"]
        den += k
        contrib.append(dict(id=s.id, role=s.role, S=round(p["S"], 2), xac=round(p["xac"], 3), a=round(a, 3),
                            eta=eta, eps=round(eps, 3), weight=round(k / max(Sw, 1e-9), 4)))
    x_np = num / den
    dx_fus = 0.0
    if fus is not None and getattr(fus, "L", 0) > 0:
        xq = (wings[0].x_le + 0.25 * wings[0].cr) / fus.L if hasattr(wings[0], "x_le") else xac_w / fus.L
        Kf = float(np.interp(xq, KF_TABLE[:, 0], KF_TABLE[:, 1]))
        cma_f = Kf * fus.W ** 2 * fus.L / (Sw * mac)            # per degree
        cla = den / Sw / 57.2958                                 # per degree, on the wing reference area
        dx_fus = min(cma_f / cla * mac, 0.6 * mac)            # sanity cap: the chart is for slender bodies
        x_np -= dx_fus
    return dict(x_np=x_np, mac=mac, x_mac_le=x_mac_le, xac_wing=xac_w, S_ref=Sw, AR_wing=ARw, a_wing=aw,
                downwash=deps, dx_fuselage=dx_fus, surfaces=contrib)
