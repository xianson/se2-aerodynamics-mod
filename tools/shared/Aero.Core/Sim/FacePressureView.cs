using System.Numerics;
using System.Text;
using Aero.Core.Aero;
using Aero.Core.Grids;

namespace Aero.Core.Sim;

/// <summary>
/// Generates an interactive 3D face-pressure visualization.
/// Self-contained HTML with Three.js — each surface face is a colored quad.
///
/// Color encodes pressure coefficient (Cp):
///   Deep blue  = strong suction (leeward base pressure)
///   Cyan/white = neutral / low pressure
///   Yellow     = moderate windward
///   Red        = high pressure (stagnation)
///
/// Interactive controls:
///   - AoA slider (0-90°)
///   - Speed / Mach slider
///   - Angular velocity (optional)
///   - Orbit camera
///   - Real-time Cp recomputation in JavaScript
/// </summary>
public static class FacePressureView
{
    public static string Generate(
        Grid grid,
        ISurfaceProvider surface,
        string outputPath,
        float blockSize = 2.5f,
        float initialSpeed = 200f,
        float initialAoaDeg = 5f,
        float massPerBlock = 500f,
        string title = "Face Pressure Map",
        int? serverPort = null,
        MassProperties? massOverride = null)
    {
        var sb = new StringBuilder();

        // Compute mass properties (or use caller-supplied override)
        var mass = massOverride ?? MassProperties.FromGrid(grid, blockSize, massPerBlock);
        Vector3 com = mass.CenterOfMass;

        var faces = surface.Faces;
        int faceCount = faces.Count;

        // Build a blocky surface for geometric (axis-aligned) normals used for quad orientation.
        // The smooth surface normals are used only for Cp computation.
        var cubeSurf = new CubeSurfaceProvider();
        cubeSurf.Build(grid, blockSize);
        var cubeFaces = cubeSurf.Faces;

        // Detect wings (build-time)
        var wingDetector = new ConnectedComponentWingDetector();
        var wings = wingDetector.Detect(grid, surface, blockSize);

        // ── HTML ──
        sb.AppendLine("<!DOCTYPE html><html><head>");
        sb.AppendLine($"<title>{title}</title>");
        sb.AppendLine(@"<style>
* { margin:0; padding:0; box-sizing:border-box; }
body { background:#0a0a1a; overflow:hidden; font-family:'Segoe UI',sans-serif; }
canvas { display:block; }
#hud { position:absolute; top:12px; left:12px; color:#0cf; font-size:13px;
       pointer-events:none; line-height:1.7; text-shadow:0 0 6px #000; }
#hud .val { color:#fff; font-weight:bold; }
#controls { position:absolute; bottom:12px; left:12px; right:12px;
            background:rgba(0,0,0,0.7); border-radius:8px; padding:12px 16px; }
#controls label { color:#888; font-size:12px; display:inline-block; min-width:80px; }
#controls input[type=range] { width:200px; accent-color:#0cf; vertical-align:middle; }
#controls .row { margin:4px 0; display:flex; align-items:center; gap:8px; }
#controls .sval { color:#0cf; font-size:13px; font-weight:bold; min-width:70px; }
#legend { position:absolute; top:12px; right:12px; width:30px; height:200px;
          border:1px solid #333; border-radius:4px; overflow:hidden; }
#legendLabels { position:absolute; top:12px; right:48px; color:#888; font-size:11px;
                height:200px; display:flex; flex-direction:column; justify-content:space-between; pointer-events:none; }
#info { color:#555; font-size:11px; position:absolute; top:220px; right:12px; pointer-events:none; text-align:right; }
</style>");
        sb.AppendLine("</head><body>");

        // HUD
        sb.AppendLine("<div id=\"hud\">");
        sb.AppendLine($"<div style=\"color:#7b68ee;font-size:16px;font-weight:bold;margin-bottom:4px\">{title}</div>");
        sb.AppendLine($"<div style=\"color:#555;font-size:11px;margin-bottom:6px\">{grid.BlockCount:N0} blocks, {faceCount:N0} faces</div>");
        sb.AppendLine("Drag: <span class=\"val\" id=\"hDrag\">-</span> N<br>");
        sb.AppendLine("Lift: <span class=\"val\" id=\"hLift\">-</span> N<br>");
        sb.AppendLine("Frontal: <span class=\"val\" id=\"hArea\">-</span> m&sup2;<br>");
        sb.AppendLine("Cd (eff): <span class=\"val\" id=\"hCd\">-</span><br>");
        if (wings.Length > 0)
            sb.AppendLine($"Wings: <span class=\"val\">{wings.Length}</span> (circulation lift)<br>");
        sb.AppendLine("<span style=\"color:#ff2222\">&#9679; CoM</span>&nbsp;&nbsp;<span style=\"color:#22ff44\">&#9679; CoP</span><br>");
        sb.AppendLine("<span style=\"color:#ff4444\">&#x2192; Vel</span>&nbsp;&nbsp;<span style=\"color:#ffaa22\">&#x2192; Drag</span>&nbsp;&nbsp;<span style=\"color:#00ccff\">&#x2192; Lift</span>&nbsp;&nbsp;<span style=\"color:#44ff44\">&#x2192; Thrust</span><br>");
        sb.AppendLine("<span style=\"color:#ff44ff\">&#x2758; Smooth N</span>&nbsp;&nbsp;<span style=\"color:#666\">&#x2758; Geo N</span><br>");
        sb.AppendLine("</div>");

        // Legend (gradient bar)
        sb.AppendLine("<canvas id=\"legend\" width=\"30\" height=\"200\"></canvas>");
        sb.AppendLine("<div id=\"legendLabels\"><span>Cp 2.0</span><span>1.0</span><span>0</span><span>-0.15</span></div>");
        sb.AppendLine($"<div id=\"info\">{faceCount} faces</div>");

        // Controls
        sb.AppendLine(@"<div id=""controls"">
<div class=""row""><label>AoA</label><input type=""range"" id=""slAoA"" min=""-20"" max=""90"" step=""1"" value=""INITIAL_AOA""><span class=""sval"" id=""vAoA"">-</span></div>
<div class=""row""><label>Speed m/s</label><input type=""range"" id=""slSpeed"" min=""1"" max=""1000"" step=""1"" value=""INITIAL_SPEED""><span class=""sval"" id=""vSpeed"">-</span></div>
<div class=""row""><label>Alt km</label><input type=""range"" id=""slAlt"" min=""0"" max=""50"" step=""0.5"" value=""5""><span class=""sval"" id=""vAlt"">-</span></div>
<div class=""row""><label>Spin rad/s</label><input type=""range"" id=""slOmega"" min=""0"" max=""5"" step=""0.1"" value=""0""><span class=""sval"" id=""vOmega"">-</span></div>
<div class=""row""><label>Thrust kN</label><input type=""range"" id=""slThrust"" min=""0"" max=""5000"" step=""10"" value=""200""><span class=""sval"" id=""vThrust"">200</span></div>
<div class=""row""><label>Target FPA</label><input type=""range"" id=""slTargetFPA"" min=""-45"" max=""45"" step=""1"" value=""0""><span class=""sval"" id=""vTargetFPA"">0°</span>
<label style=""margin-left:8px"">Gain</label><input type=""range"" id=""slCtrlGain"" min=""0.1"" max=""10"" step=""0.1"" value=""1""><span class=""sval"" id=""vCtrlGain"" style=""min-width:40px"">1.0</span></div>
<div class=""row""><label>Sim</label>
<button id=""btnSim"" style=""background:#222;color:#0cf;border:1px solid #0cf;border-radius:4px;padding:4px 12px;cursor:pointer;font-size:12px"">Start</button>
<button id=""btnPause"" style=""background:#222;color:#888;border:1px solid #444;border-radius:4px;padding:4px 8px;cursor:pointer;font-size:12px"">Pause</button>
<button id=""btnSlower"" style=""background:#222;color:#888;border:1px solid #444;border-radius:4px;padding:4px 8px;cursor:pointer;font-size:12px"">&frac12;x</button>
<button id=""btnFaster"" style=""background:#222;color:#888;border:1px solid #444;border-radius:4px;padding:4px 8px;cursor:pointer;font-size:12px"">2x</button>
<span class=""sval"" id=""vSimSpeed"" style=""color:#666"">1x</span>
<label style=""margin-left:12px""><input type=""checkbox"" id=""chkGrav"" checked> Gravity</label>
<label style=""margin-left:8px""><input type=""checkbox"" id=""chkAP"" checked> SAS</label>
<label style=""margin-left:8px""><input type=""checkbox"" id=""chkStream"" checked> Streamlined</label>
<label style=""margin-left:8px""><input type=""checkbox"" id=""chkShadow"" checked> Shadow</label>
<label style=""margin-left:8px"">Decay</label><input type=""range"" id=""slDecay"" min=""0"" max=""20"" step=""0.5"" value=""5"" style=""width:80px""><span class=""sval"" id=""vDecay"" style=""min-width:40px"">5</span>
<label style=""margin-left:8px""><input type=""checkbox"" id=""chkNormals""> Normals</label>
<span class=""sval"" id=""vSim"" style=""color:#888"">idle</span></div>
</div>".Replace("INITIAL_AOA", ((int)initialAoaDeg).ToString())
        .Replace("INITIAL_SPEED", ((int)initialSpeed).ToString()));

        // Three.js
        sb.AppendLine("<script src=\"https://cdn.jsdelivr.net/npm/three@0.137.0/build/three.min.js\"></script>");
        sb.AppendLine("<script src=\"https://cdn.jsdelivr.net/npm/three@0.137.0/examples/js/controls/OrbitControls.js\"></script>");
        sb.AppendLine("<script>");

        // Emit face data as flat Float32Arrays (position, normal, area — all grid-local meters)
        sb.AppendLine($"const faceCount = {faceCount};");
        sb.AppendLine($"const blockSize = {blockSize:G5};");
        sb.AppendLine($"const comX = {com.X:G6}, comY = {com.Y:G6}, comZ = {com.Z:G6};");

        EmitFaceArray(sb, "facePx", faces, f => f.Position.X);
        EmitFaceArray(sb, "facePy", faces, f => f.Position.Y);
        EmitFaceArray(sb, "facePz", faces, f => f.Position.Z);
        // Smoothed normals for Cp computation
        EmitFaceArray(sb, "faceNx", faces, f => f.Normal.X);
        EmitFaceArray(sb, "faceNy", faces, f => f.Normal.Y);
        EmitFaceArray(sb, "faceNz", faces, f => f.Normal.Z);
        EmitFaceArray(sb, "faceArea", faces, f => f.Area);
        // Geometric (axis-aligned) normals for quad orientation
        EmitFaceArray(sb, "faceGeoNx", cubeFaces, f => f.Normal.X);
        EmitFaceArray(sb, "faceGeoNy", cubeFaces, f => f.Normal.Y);
        EmitFaceArray(sb, "faceGeoNz", cubeFaces, f => f.Normal.Z);

        // Emit grid cell occupancy for shadow ray marching
        // Pack cell coords as "x,y,z" strings in a JS Set
        sb.Append("const gridCells = new Set([");
        bool firstCell = true;
        foreach (var block in grid.Blocks.Values)
        {
            foreach (var cell in block.EnumerateCells())
            {
                if (!firstCell) sb.Append(',');
                firstCell = false;
                sb.Append($"\"{cell.X},{cell.Y},{cell.Z}\"");
            }
        }
        sb.AppendLine("]);");
        sb.AppendLine("function cellKey(x,y,z){ return x+','+y+','+z; }");

        // Emit wing data to JS
        sb.AppendLine($"const wingCount = {wings.Length};");
        if (wings.Length > 0)
        {
            // Emit wing arrays: normal, spanAxis, chordAxis, centroid, aeroCenter, planformArea, span, CLAlpha, CLMax, alphaStall, oswaldE, AR
            var wFields = new (string name, Func<LiftingSurface, float> sel)[]
            {
                ("wingNx", w => w.Normal.X), ("wingNy", w => w.Normal.Y), ("wingNz", w => w.Normal.Z),
                ("wingCentX", w => w.Centroid.X - com.X), ("wingCentY", w => w.Centroid.Y - com.Y), ("wingCentZ", w => w.Centroid.Z - com.Z),
                ("wingAcX", w => w.AeroCenter.X - com.X), ("wingAcY", w => w.AeroCenter.Y - com.Y), ("wingAcZ", w => w.AeroCenter.Z - com.Z),
                ("wingSx", w => w.SpanAxis.X), ("wingSy", w => w.SpanAxis.Y), ("wingSz", w => w.SpanAxis.Z),
                ("wingCx", w => w.ChordAxis.X), ("wingCy", w => w.ChordAxis.Y), ("wingCz", w => w.ChordAxis.Z),
                ("wingArea", w => w.PlanformArea), ("wingSpan", w => w.Span),
                ("wingMeanChord", w => w.MeanChord),
                ("wingCLa", w => w.CLAlpha), ("wingCLmax", w => w.CLMax), ("wingAlphaStall", w => w.AlphaStall),
                ("wingOswaldE", w => w.OswaldE), ("wingAR", w => w.AspectRatio),
            };
            foreach (var (name, sel) in wFields)
            {
                sb.Append($"const {name} = new Float32Array([");
                for (int i = 0; i < wings.Length; i++)
                {
                    if (i > 0) sb.Append(',');
                    sb.Append(sel(wings[i]).ToString("G6"));
                }
                sb.AppendLine("]);");
            }
        }

        // Mass properties for sim
        sb.AppendLine($"const totalMass = {mass.TotalMass:G6};");
        sb.AppendLine($"const Ixx={mass.Ixx:G6},Iyy={mass.Iyy:G6},Izz={mass.Izz:G6};");
        sb.AppendLine($"const Ixy={mass.Ixy:G6},Ixz={mass.Ixz:G6},Iyz={mass.Iyz:G6};");
        sb.AppendLine($"const invIxx={mass.InvIxx:G8},invIyy={mass.InvIyy:G8},invIzz={mass.InvIzz:G8};");
        sb.AppendLine($"const invIxy={mass.InvIxy:G8},invIxz={mass.InvIxz:G8},invIyz={mass.InvIyz:G8};");
        sb.AppendLine($"const gravity = 9.80665;");
        sb.AppendLine($"const INITIAL_AOA_NUM = {initialAoaDeg:G4};");
        sb.AppendLine($"const SERVER_PORT = {(serverPort.HasValue ? serverPort.Value.ToString() : "null")};");

        // Main scene JavaScript
        sb.Append(@"
// ── WebSocket connection to C# aero server ──
let aeroWs = null;
let aeroWsPending = null; // resolve function for pending request
let serverConnected = false;

function connectAeroServer() {
    if (SERVER_PORT === null) return;
    try {
        aeroWs = new WebSocket('ws://localhost:' + SERVER_PORT + '/');
        aeroWs.binaryType = 'arraybuffer';
        aeroWs.onopen = () => { serverConnected = true; console.log('Aero server connected'); updatePressure();
            // Auto-start sim after a short delay for initial aero to render
            setTimeout(() => { if (!simRunning) document.getElementById('btnSim').click(); }, 500);
        };
        aeroWs.onclose = () => { serverConnected = false; aeroWs = null; console.log('Aero server disconnected'); };
        aeroWs.onerror = () => { serverConnected = false; };
        aeroWs.onmessage = (e) => {
            const byteLen = e.data.byteLength;
            const floatCount = byteLen / 4;

            if (simServerPending && floatCount > 20) {
                // Sim response (25 + faceCount floats)
                simServerPending = false;
                const resp = parseSimResponse(e.data);
                applySimResponse(resp);
                // Kick off next step if time accumulated
                if (simRunning && !simPaused && sim && simAccumDt > 0) {
                    const step = Math.min(simAccumDt, 0.02);
                    simAccumDt -= step;
                    sendSimStepWithControls(step);
                }
            } else {
                // Aero query response (11 + faceCount floats)
                const resp = parseAeroResponse(e.data);
                lastServerAero = resp;
                applyServerAero(resp);
            }
        };
    } catch(e) { serverConnected = false; }
}
connectAeroServer();

function sendAeroRequest(vel, omega, altM) {
    if (!aeroWs || aeroWs.readyState !== 1) return null;
    const buf = new ArrayBuffer(28);
    const f = new Float32Array(buf);
    f[0]=vel[0]; f[1]=vel[1]; f[2]=vel[2];
    f[3]=omega[0]; f[4]=omega[1]; f[5]=omega[2];
    f[6]=altM;
    aeroWs.send(buf);
    return new Promise(resolve => { aeroWsPending = resolve; });
}

function sendSimInit(pos, linVel, angVel, orient) {
    if (!aeroWs || aeroWs.readyState !== 1) return;
    const buf = new ArrayBuffer(52); // 13 floats
    const f = new Float32Array(buf);
    f[0]=pos[0]; f[1]=pos[1]; f[2]=pos[2];
    f[3]=linVel[0]; f[4]=linVel[1]; f[5]=linVel[2];
    f[6]=angVel[0]; f[7]=angVel[1]; f[8]=angVel[2];
    f[9]=orient[0]; f[10]=orient[1]; f[11]=orient[2]; f[12]=orient[3];
    aeroWs.send(buf);
    simServerPending = true;
}

function sendSimStep(dt, ctrlTorque, thrustN, gravity) {
    if (!aeroWs || aeroWs.readyState !== 1) return;
    const buf = new ArrayBuffer(24); // 6 floats
    const f = new Float32Array(buf);
    f[0]=dt; f[1]=ctrlTorque[0]; f[2]=ctrlTorque[1]; f[3]=ctrlTorque[2];
    f[4]=thrustN; f[5]=gravity;
    aeroWs.send(buf);
    simServerPending = true;
}

function sendSimStop() {
    if (!aeroWs || aeroWs.readyState !== 1) return;
    const buf = new ArrayBuffer(4);
    new Float32Array(buf)[0] = 0;
    aeroWs.send(buf);
}

function parseSimResponse(data) {
    const f = new Float32Array(data);
    return {
        pos: [f[0], f[1], f[2]],
        linVel: [f[3], f[4], f[5]],
        angVel: [f[6], f[7], f[8]],
        orient: [f[9], f[10], f[11], f[12]],
        simTime: f[13],
        force: [f[14], f[15], f[16]],
        torque: [f[17], f[18], f[19]],
        dragMag: f[20],
        liftMag: f[21],
        frontalArea: f[22],
        mach: f[23],
        dynamicPressure: f[24],
        faceCp: f.subarray(25)
    };
}

function parseAeroResponse(data) {
    const f = new Float32Array(data);
    return {
        force: [f[0], f[1], f[2]],
        torque: [f[3], f[4], f[5]],
        dragMag: f[6],
        liftMag: f[7],
        frontalArea: f[8],
        mach: f[9],
        dynamicPressure: f[10],
        faceCp: f.subarray(11)  // per-face Cp values
    };
}

// Last server aero result (used synchronously by sim loop and updatePressure)
let lastServerAero = null;

function applyServerAero(resp) {
    // Apply per-face Cp to colors
    let cpMin = 1e9, cpMax2 = -1e9;
    for (let i = 0; i < faceCount && i < resp.faceCp.length; i++) {
        const cp = resp.faceCp[i];
        faceCpArr[i] = cp;
        if (cp < cpMin) cpMin = cp;
        if (cp > cpMax2) cpMax2 = cp;
    }
    const cpRange = cpMax2 - cpMin;
    const invCpRange = cpRange > 0.001 ? 1.0 / cpRange : 1.0;
    const colArr = geo.attributes.color.array;
    for (let i = 0; i < faceCount; i++) {
        const t = (faceCpArr[i] - cpMin) * invCpRange;
        const [cr,cg,cb] = tToColor(t);
        const vi = i * 4;
        for (let c = 0; c < 4; c++) {
            const idx = (vi + c) * 3;
            colArr[idx] = cr; colArr[idx+1] = cg; colArr[idx+2] = cb;
        }
    }
    geo.attributes.color.needsUpdate = true;
    drawLegend(cpMin, cpMax2);

    // Velocity arrow (body-frame velocity, rotated to world by arrowGroup quaternion)
    let velX, velY, velZ;
    if (lastRequestVel) { velX=lastRequestVel[0]; velY=lastRequestVel[1]; velZ=lastRequestVel[2]; }
    else { const speed=parseFloat(document.getElementById('slSpeed').value); const ar=parseFloat(document.getElementById('slAoA').value)*Math.PI/180; velX=-speed*Math.cos(ar); velY=-speed*Math.sin(ar); velZ=0; }
    const vLen = Math.sqrt(velX*velX+velY*velY+velZ*velZ);
    if (vLen > 0.1) {
        velArrow.visible = true;
        velArrow.setDirection(new THREE.Vector3(velX/vLen, velY/vLen, velZ/vLen));
        const aLen = shipSize * 0.6;
        velArrow.setLength(aLen, aLen*0.12, aLen*0.06);
    } else { velArrow.visible = false; }

    // Force arrows from server
    const totalFx=resp.force[0], totalFy=resp.force[1], totalFz=resp.force[2];
    const totalTx=resp.torque[0], totalTy=resp.torque[1], totalTz=resp.torque[2];
    const dragMag=resp.dragMag, liftMag=resp.liftMag;

    // Drag direction (opposing velocity)
    if (vLen > 0.1 && dragMag > 1) {
        const vHatX=velX/vLen, vHatY=velY/vLen, vHatZ=velZ/vLen;
        forceArrow.visible = true;
        forceArrow.setDirection(new THREE.Vector3(-vHatX, -vHatY, -vHatZ));
        const maxF = Math.max(dragMag, liftMag, 1);
        const fLen = Math.max(dragMag * shipSize * 0.8 / maxF, shipSize * 0.05);
        forceArrow.setLength(fLen, fLen*0.12, fLen*0.06);

        // Lift direction (perpendicular to velocity)
        const fDotV = totalFx*vHatX + totalFy*vHatY + totalFz*vHatZ;
        const lVx=totalFx-fDotV*vHatX, lVy=totalFy-fDotV*vHatY, lVz=totalFz-fDotV*vHatZ;
        const lM = Math.sqrt(lVx*lVx+lVy*lVy+lVz*lVz);
        if (lM > 1) {
            liftArrow.visible = true;
            liftArrow.setDirection(new THREE.Vector3(lVx/lM, lVy/lM, lVz/lM));
            const lLen = Math.max(liftMag * shipSize * 0.8 / maxF, shipSize * 0.05);
            liftArrow.setLength(lLen, lLen*0.12, lLen*0.06);
        } else { liftArrow.visible = false; }
    } else { forceArrow.visible = false; liftArrow.visible = false; }

    // Thrust arrow
    const thrustKN = parseFloat(document.getElementById('slThrust').value);
    if (thrustKN > 0) {
        thrustArrow.visible = true;
        thrustArrow.setDirection(new THREE.Vector3(-1, 0, 0));
        const tLen = shipSize * 0.5 * (thrustKN / Math.max(thrustKN, dragMag/1000, 1));
        thrustArrow.setLength(Math.max(tLen, shipSize*0.1), tLen*0.12, tLen*0.06);
    } else { thrustArrow.visible = false; }

    // CoP
    const f2 = totalFx*totalFx + totalFy*totalFy + totalFz*totalFz;
    if (f2 > 100) {
        const cpx=(totalFy*totalTz-totalFz*totalTy)/f2;
        const cpy=(totalFz*totalTx-totalFx*totalTz)/f2;
        const cpz=(totalFx*totalTy-totalFy*totalTx)/f2;
        copMesh.position.set(cpx, cpy, cpz);
        copMesh.visible = true;
    } else { copMesh.visible = false; }

    // HUD
    document.getElementById('hDrag').textContent = dragMag.toFixed(0);
    document.getElementById('hLift').textContent = liftMag.toFixed(0);
    document.getElementById('hArea').textContent = resp.frontalArea.toFixed(1);
    const q0 = resp.dynamicPressure;
    const cd = resp.frontalArea > 0.01 && q0 > 0.01 ? dragMag / (q0 * resp.frontalArea) : 0;
    document.getElementById('hCd').textContent = cd.toFixed(3);
    document.getElementById('vAoA').textContent = parseFloat(document.getElementById('slAoA').value) + '\u00b0 (M' + resp.mach.toFixed(2) + ')';
}

// ── Atmosphere model (ISA) ──
function getAtmo(altKm) {
    const alt = altKm * 1000;
    let T, p;
    if (alt < 11000) {
        T = 288.15 - 0.0065 * alt;
        p = 101325 * Math.pow(T / 288.15, 5.2559);
    } else if (alt < 25000) {
        T = 216.65;
        p = 22632 * Math.exp(-0.000157 * (alt - 11000));
    } else {
        T = 216.65 + 0.003 * (alt - 25000);
        p = 2488 * Math.pow(216.65 / T, 11.201);
    }
    const rho = p / (287.05 * T);
    const sos = Math.sqrt(1.4 * 287.05 * T);
    return { rho, sos, T };
}

// ── Cp model (Newtonian, matches C#) ──
const cpMax = 2.0, cdBluff = 0.4, cpBase = -0.05, cfSkin = 0.003;
const subLim = 0.6, supLim = 1.2, invMachRange = 1.0 / (supLim - subLim);

function computeCp(cosA, mach) {
    if (cosA <= 0) return cpBase;
    const cpSub = cdBluff * cosA;
    const cpSup = cpMax * cosA * cosA;
    let t = (mach - subLim) * invMachRange;
    t = Math.max(0, Math.min(1, t));
    t = t * t * (3 - 2 * t);
    return cpSub + (cpSup - cpSub) * t;
}

// Streamlined Cp: for bodies with smooth taper, faces at shallow angles
// to the flow experience pressure recovery — only the stagnation region
// (cosA > ~0.5) retains full Newtonian Cp. The rest is drastically reduced.
// Physics: d'Alembert's paradox — a streamlined body in inviscid flow has
// zero pressure drag because aft pressure recovery cancels forward pressure.
// Real drag comes from skin friction + small separation losses.
function computeCpStreamlined(cosA, mach) {
    if (cosA <= 0) return cpBase * 0.3; // less base suction on streamlined aft
    const cpFull = computeCp(cosA, mach);
    // Smooth reduction: faces at low incidence (sides of fuselage) get ~10% of
    // Newtonian Cp. Stagnation region (cosA > 0.7) gets full Cp.
    // This models pressure recovery on tapered aft sections.
    const recoveryFactor = cosA * cosA; // 0 at shallow, 1 at stagnation
    return cpFull * (0.08 + 0.92 * recoveryFactor);
}

// ── Shadow: DDA ray march upstream through grid cells ──
// Returns shadow factor: 1.0 = fully visible, 0.0 = fully shadowed.
// With wake decay > 0, shadow effect decreases exponentially with distance
// from the occluding block (models turbulent wake dissipation).
const invBS = 1.0 / blockSize;
function shadowFactor(fpx, fpy, fpz, vhx, vhy, vhz, decayLen) {
    const rx = vhx, ry = vhy, rz = vhz;
    let gx = fpx * invBS, gy = fpy * invBS, gz = fpz * invBS;
    let cx = Math.floor(gx), cy = Math.floor(gy), cz = Math.floor(gz);
    const startKey = cellKey(cx, cy, cz);
    const stepX = rx > 0 ? 1 : (rx < 0 ? -1 : 0);
    const stepY = ry > 0 ? 1 : (ry < 0 ? -1 : 0);
    const stepZ = rz > 0 ? 1 : (rz < 0 ? -1 : 0);
    const tdx = stepX !== 0 ? Math.abs(1.0 / rx) : 1e30;
    const tdy = stepY !== 0 ? Math.abs(1.0 / ry) : 1e30;
    const tdz = stepZ !== 0 ? Math.abs(1.0 / rz) : 1e30;
    let tmx = stepX > 0 ? (cx+1-gx)*tdx : stepX < 0 ? (gx-cx)*tdx : 1e30;
    let tmy = stepY > 0 ? (cy+1-gy)*tdy : stepY < 0 ? (gy-cy)*tdy : 1e30;
    let tmz = stepZ > 0 ? (cz+1-gz)*tdz : stepZ < 0 ? (gz-cz)*tdz : 1e30;
    for (let i = 0; i < 200; i++) {
        let t;
        if (tmx < tmy) {
            if (tmx < tmz) { t = tmx; cx += stepX; tmx += tdx; }
            else { t = tmz; cz += stepZ; tmz += tdz; }
        } else {
            if (tmy < tmz) { t = tmy; cy += stepY; tmy += tdy; }
            else { t = tmz; cz += stepZ; tmz += tdz; }
        }
        const k = cellKey(cx, cy, cz);
        if (k === startKey) continue;
        if (gridCells.has(k)) {
            // Found occluder at distance t (in grid cells)
            if (decayLen <= 0) return 0.0; // binary shadow
            const dist = t; // cells from face to occluder
            return 1.0 - Math.exp(-dist / decayLen);
        }
    }
    return 1.0; // no occluder found
}

// Per-face shadow cache (recomputed when direction changes)
// Now stores float shadow factor [0,1] instead of binary
const faceShadowF = new Float32Array(faceCount);

// ── Color map: normalized t ∈ [0,1] → RGB ──
// 0 = low (blue) → 0.25 = cyan → 0.5 = green → 0.75 = yellow → 1.0 = red
function tToColor(t) {
    t = Math.max(0, Math.min(1, t));
    let r, g, b;
    if (t < 0.25) {
        const s = t / 0.25;
        r = 0; g = s * 0.8; b = 0.5 + s * 0.5;
    } else if (t < 0.5) {
        const s = (t - 0.25) / 0.25;
        r = s * 0.4; g = 0.8 + s * 0.2; b = 1.0 - s * 0.7;
    } else if (t < 0.75) {
        const s = (t - 0.5) / 0.25;
        r = 0.4 + s * 0.6; g = 1.0; b = 0.3 - s * 0.3;
    } else {
        const s = (t - 0.75) / 0.25;
        r = 1.0; g = 1.0 - s; b = 0;
    }
    return [r, g, b];
}

// ── Scene setup ──
const scene = new THREE.Scene();
scene.background = new THREE.Color(0x0a0a1a);

const camera = new THREE.PerspectiveCamera(60, innerWidth/innerHeight, 0.1, 50000);
const renderer = new THREE.WebGLRenderer({antialias:true});
renderer.setSize(innerWidth, innerHeight);
renderer.setPixelRatio(Math.min(devicePixelRatio, 2));
document.body.appendChild(renderer.domElement);

const orbitCtrl = new THREE.OrbitControls(camera, renderer.domElement);
orbitCtrl.enableDamping = true;
orbitCtrl.dampingFactor = 0.1;

// ── Mouse joystick (controls pitch/roll when sim is running) ──
let mouseX = 0, mouseY = 0; // normalized [-1, 1] from screen center
let mouseActive = false;
renderer.domElement.addEventListener('mousemove', (e) => {
    if (!simRunning || simPaused) return;
    const rect = renderer.domElement.getBoundingClientRect();
    mouseX = ((e.clientX - rect.left) / rect.width - 0.5) * 2;
    mouseY = ((e.clientY - rect.top) / rect.height - 0.5) * 2;
    mouseActive = true;
});
renderer.domElement.addEventListener('mouseleave', () => { mouseActive = false; mouseX = 0; mouseY = 0; });

// ── Keyboard controls: arrows = pitch/yaw, Q/E = roll ──
const keysDown = {};
window.addEventListener('keydown', (e) => {
    const k = e.key.toLowerCase();
    keysDown[k] = true;
    if (['arrowup','arrowdown','arrowleft','arrowright','q','e'].includes(k)) e.preventDefault();
});
window.addEventListener('keyup', (e) => { keysDown[e.key.toLowerCase()] = false; });

// Lights — brighter ambient so face colors are clearly visible
scene.add(new THREE.AmbientLight(0xffffff, 0.7));
const dl = new THREE.DirectionalLight(0xffffff, 0.8);
dl.position.set(1, 2, 1);
scene.add(dl);
const dl2 = new THREE.DirectionalLight(0x8888bb, 0.3);
dl2.position.set(-1, -0.5, -1);
scene.add(dl2);

// ── Build face quads using individual meshes in a merged BufferGeometry ──
// Each face = 2 triangles (quad), with per-vertex color for Cp visualization.
// This avoids InstancedMesh color bugs and gives us direct vertex color control.

const faceSize = blockSize * 0.96;
const halfSize = faceSize * 0.5;

// Pre-allocate: 4 verts per face, 6 indices per face (2 triangles)
const positions = new Float32Array(faceCount * 4 * 3);
const normals = new Float32Array(faceCount * 4 * 3);
const faceColors = new Float32Array(faceCount * 4 * 3); // per-vertex color
const indices = new Uint32Array(faceCount * 6);

// Quad corners in local plane space (before rotation)
const corners = [
    [-halfSize, -halfSize, 0],
    [ halfSize, -halfSize, 0],
    [ halfSize,  halfSize, 0],
    [-halfSize,  halfSize, 0],
];

const upZ = new THREE.Vector3(0, 0, 1);
const tempQ = new THREE.Quaternion();
const tempV = new THREE.Vector3();

for (let i = 0; i < faceCount; i++) {
    const px = facePx[i] - comX;
    const py = facePy[i] - comY;
    const pz = facePz[i] - comZ;
    const nx = faceNx[i], ny = faceNy[i], nz = faceNz[i];
    // Geometric (axis-aligned) normal for quad placement
    const gnx = faceGeoNx[i], gny = faceGeoNy[i], gnz = faceGeoNz[i];

    // Rotation from +Z to geometric normal
    const gVec = new THREE.Vector3(gnx, gny, gnz);
    tempQ.setFromUnitVectors(upZ, gVec);

    const vi = i * 4; // vertex index offset
    for (let c = 0; c < 4; c++) {
        tempV.set(corners[c][0], corners[c][1], corners[c][2]);
        tempV.applyQuaternion(tempQ);

        const idx = (vi + c) * 3;
        // Position: face center + rotated corner + small outward offset along geometric normal
        positions[idx]     = px + tempV.x + gnx * 0.08;
        positions[idx + 1] = py + tempV.y + gny * 0.08;
        positions[idx + 2] = pz + tempV.z + gnz * 0.08;

        // Use smoothed normal for lighting/shading
        normals[idx]     = nx;
        normals[idx + 1] = ny;
        normals[idx + 2] = nz;

        // Initial color (will be overwritten by updatePressure)
        faceColors[idx] = 0.5;
        faceColors[idx + 1] = 0.5;
        faceColors[idx + 2] = 0.5;
    }

    // Two triangles per quad
    const ii = i * 6;
    indices[ii]     = vi;
    indices[ii + 1] = vi + 1;
    indices[ii + 2] = vi + 2;
    indices[ii + 3] = vi;
    indices[ii + 4] = vi + 2;
    indices[ii + 5] = vi + 3;
}

const geo = new THREE.BufferGeometry();
geo.setAttribute('position', new THREE.BufferAttribute(positions, 3));
geo.setAttribute('normal', new THREE.BufferAttribute(normals, 3));
geo.setAttribute('color', new THREE.BufferAttribute(faceColors, 3));
geo.setIndex(new THREE.BufferAttribute(indices, 1));

const faceMat = new THREE.MeshPhongMaterial({
    vertexColors: true,
    side: THREE.DoubleSide,
    flatShading: true,
    shininess: 15,
});

const faceMesh = new THREE.Mesh(geo, faceMat);
scene.add(faceMesh);

// ── Normal visualization (smooth = magenta, geometric = gray) ──
const normLen = blockSize * 1.2;
const smoothNormPos = new Float32Array(faceCount * 2 * 3);
const geoNormPos = new Float32Array(faceCount * 2 * 3);
for (let i = 0; i < faceCount; i++) {
    const px = facePx[i] - comX, py = facePy[i] - comY, pz = facePz[i] - comZ;
    const base = i * 6;
    // Smooth normal line
    smoothNormPos[base]   = px; smoothNormPos[base+1] = py; smoothNormPos[base+2] = pz;
    smoothNormPos[base+3] = px + faceNx[i]*normLen;
    smoothNormPos[base+4] = py + faceNy[i]*normLen;
    smoothNormPos[base+5] = pz + faceNz[i]*normLen;
    // Geo normal line
    geoNormPos[base]   = px; geoNormPos[base+1] = py; geoNormPos[base+2] = pz;
    geoNormPos[base+3] = px + faceGeoNx[i]*normLen;
    geoNormPos[base+4] = py + faceGeoNy[i]*normLen;
    geoNormPos[base+5] = pz + faceGeoNz[i]*normLen;
}
const smoothNormGeo = new THREE.BufferGeometry();
smoothNormGeo.setAttribute('position', new THREE.BufferAttribute(smoothNormPos, 3));
const smoothNormLines = new THREE.LineSegments(smoothNormGeo,
    new THREE.LineBasicMaterial({ color: 0xff44ff, transparent: true, opacity: 0.8, depthTest: false }));
smoothNormLines.visible = false;
smoothNormLines.renderOrder = 997;
faceMesh.add(smoothNormLines);

const geoNormGeo = new THREE.BufferGeometry();
geoNormGeo.setAttribute('position', new THREE.BufferAttribute(geoNormPos, 3));
const geoNormLines = new THREE.LineSegments(geoNormGeo,
    new THREE.LineBasicMaterial({ color: 0x666666, transparent: true, opacity: 0.5, depthTest: false }));
geoNormLines.visible = false;
geoNormLines.renderOrder = 996;
faceMesh.add(geoNormLines);

document.getElementById('chkNormals').addEventListener('change', function() {
    smoothNormLines.visible = this.checked;
    geoNormLines.visible = this.checked;
});

// ── CoM marker (red sphere) ──
const comGeo = new THREE.SphereGeometry(blockSize * 1.2, 16, 12);
const comMat = new THREE.MeshPhongMaterial({color: 0xff2222, emissive: 0x990000, transparent: true, opacity: 0.85, depthTest: false});
const comMesh = new THREE.Mesh(comGeo, comMat);
comMesh.renderOrder = 999;
comMesh.position.set(0, 0, 0); // faces are already centered relative to CoM
scene.add(comMesh);

// ── Wing planform outlines (transparent yellow planes) ──
const wingMeshes = [];
if (wingCount > 0) {
    const wingMat = new THREE.MeshPhongMaterial({
        color: 0xffcc00, emissive: 0x443300, transparent: true, opacity: 0.25,
        side: THREE.DoubleSide, depthTest: false, depthWrite: false
    });
    const wingEdgeMat = new THREE.LineBasicMaterial({ color: 0xffcc00, transparent: true, opacity: 0.8, depthTest: false });
    for (let wi = 0; wi < wingCount; wi++) {
        const span = wingSpan[wi];
        const chord = wingMeanChord[wi];
        const planeGeo = new THREE.PlaneGeometry(chord, span);

        // Build rotation: align plane so X=chord, Y=span, Z=normal
        const m = new THREE.Matrix4();
        m.makeBasis(
            new THREE.Vector3(wingCx[wi], wingCy[wi], wingCz[wi]),
            new THREE.Vector3(wingSx[wi], wingSy[wi], wingSz[wi]),
            new THREE.Vector3(wingNx[wi], wingNy[wi], wingNz[wi])
        );
        const wingMesh = new THREE.Mesh(planeGeo, wingMat);
        wingMesh.applyMatrix4(m);
        wingMesh.position.set(wingCentX[wi], wingCentY[wi], wingCentZ[wi]);
        wingMesh.renderOrder = 998;
        faceMesh.add(wingMesh); // child of faceMesh so it rotates with body
        wingMeshes.push(wingMesh);

        // Edge outline
        const edgeGeo = new THREE.EdgesGeometry(planeGeo);
        const edgeLine = new THREE.LineSegments(edgeGeo, wingEdgeMat);
        edgeLine.applyMatrix4(m);
        edgeLine.position.set(wingCentX[wi], wingCentY[wi], wingCentZ[wi]);
        edgeLine.renderOrder = 998;
        faceMesh.add(edgeLine);

        // Small sphere at aero center (quarter-chord)
        const acGeo = new THREE.SphereGeometry(blockSize * 0.6, 8, 6);
        const acMat = new THREE.MeshPhongMaterial({color: 0xffcc00, emissive: 0x665500, transparent: true, opacity: 0.8, depthTest: false});
        const acMesh = new THREE.Mesh(acGeo, acMat);
        acMesh.position.set(wingAcX[wi], wingAcY[wi], wingAcZ[wi]);
        acMesh.renderOrder = 998;
        faceMesh.add(acMesh);
    }
}

// ── CoP marker (green sphere) ──
const copGeo = new THREE.SphereGeometry(blockSize * 1.2, 16, 12);
const copMat = new THREE.MeshPhongMaterial({color: 0x22ff44, emissive: 0x009900, transparent: true, opacity: 0.85, depthTest: false});
const copMesh = new THREE.Mesh(copGeo, copMat);
copMesh.renderOrder = 999;
scene.add(copMesh);

// ── Arrow group (rotates with body during sim) ──
const arrowGroup = new THREE.Group();
scene.add(arrowGroup);

const arrowDir = new THREE.Vector3(1,0,0);
// Velocity arrow (red) = flight direction
let velArrow = new THREE.ArrowHelper(arrowDir, new THREE.Vector3(0,0,0), 30, 0xff4444, 6, 3);
arrowGroup.add(velArrow);
// Drag arrow (orange) = opposes flight
let forceArrow = new THREE.ArrowHelper(arrowDir, new THREE.Vector3(0,0,0), 30, 0xffaa22, 6, 3);
arrowGroup.add(forceArrow);
// Lift arrow (cyan) = perpendicular to flight
let liftArrow = new THREE.ArrowHelper(arrowDir, new THREE.Vector3(0,0,0), 30, 0x00ccff, 6, 3);
arrowGroup.add(liftArrow);
// Thrust arrow (green) = along flight direction
let thrustArrow = new THREE.ArrowHelper(arrowDir, new THREE.Vector3(0,0,0), 30, 0x44ff44, 6, 3);
thrustArrow.visible = false;
arrowGroup.add(thrustArrow);

// ── Trajectory trail ──
const TRAIL_MAX = 2000;
const trailPositions = new Float32Array(TRAIL_MAX * 3);
let trailLen = 0;
const trailGeo = new THREE.BufferGeometry();
trailGeo.setAttribute('position', new THREE.BufferAttribute(trailPositions, 3));
trailGeo.setDrawRange(0, 0);
const trailMat = new THREE.LineBasicMaterial({ color: 0xff8844, transparent: true, opacity: 0.6, depthTest: false });
const trailLine = new THREE.Line(trailGeo, trailMat);
trailLine.renderOrder = 998;
trailLine.frustumCulled = false;
scene.add(trailLine);

let trailTimer = 0;
function addTrailPoint(x, y, z) {
    if (trailLen >= TRAIL_MAX) {
        // Shift arrays left by half
        const half = TRAIL_MAX / 2 | 0;
        trailWorldPositions.splice(0, half * 3);
        trailPositions.copyWithin(0, half * 3, trailLen * 3);
        trailLen -= half;
    }
    trailWorldPositions.push(x, y, z);
    trailLen++;
    trailGeo.setDrawRange(0, trailLen);
}

function clearTrail() {
    trailLen = 0;
    trailWorldPositions = [];
    trailGeo.setDrawRange(0, 0);
}

// Store world positions separately; display positions are offset so aircraft is at origin
let trailWorldPositions = [];
function updateTrailOffset(cx, cy, cz) {
    for (let i = 0; i < trailLen; i++) {
        trailPositions[i*3]   = trailWorldPositions[i*3]   - cx;
        trailPositions[i*3+1] = trailWorldPositions[i*3+1] - cy;
        trailPositions[i*3+2] = trailWorldPositions[i*3+2] - cz;
    }
    trailGeo.attributes.position.needsUpdate = true;
}

// ── Ship extent ──
let mnX=Infinity,mxX=-Infinity,mnY=Infinity,mxY=-Infinity,mnZ=Infinity,mxZ=-Infinity;
for (let i = 0; i < faceCount; i++) {
    const x=facePx[i]-comX, y=facePy[i]-comY, z=facePz[i]-comZ;
    if(x<mnX)mnX=x; if(x>mxX)mxX=x;
    if(y<mnY)mnY=y; if(y>mxY)mxY=y;
    if(z<mnZ)mnZ=z; if(z>mxZ)mxZ=z;
}
const shipSize = Math.max(mxX-mnX, mxY-mnY, mxZ-mnZ, 10);

// ── Camera ──
camera.position.set(-shipSize*1.5, shipSize*0.8, shipSize*1.5);
orbitCtrl.target.set(0, 0, 0);

// ── Legend ──
const legendCanvas = document.getElementById('legend');
const lctx = legendCanvas.getContext('2d');
const legendLabels = document.getElementById('legendLabels');

function drawLegend(minCp, maxCp) {
    for (let y = 0; y < 200; y++) {
        const t = 1 - y / 199; // top = high, bottom = low
        const [r,g,b] = tToColor(t);
        lctx.fillStyle = `rgb(${r*255|0},${g*255|0},${b*255|0})`;
        lctx.fillRect(0, y, 30, 1);
    }
    const mid = (minCp + maxCp) * 0.5;
    legendLabels.innerHTML = `<span>Cp ${maxCp.toFixed(2)}</span><span>${mid.toFixed(2)}</span><span>${minCp.toFixed(2)}</span>`;
}

// ── Per-face Cp storage for two-pass coloring ──
const faceCpArr = new Float32Array(faceCount);

// Body-frame velocity/omega overrides set by sim loop (null when not simulating)
let simBodyVel = null;
let simAngVel = null;
// Stashed velocity from last server request (survives simBodyVel being cleared)
let lastRequestVel = null;

function updatePressure() {
    const aoa = parseFloat(document.getElementById('slAoA').value);
    const speed = parseFloat(document.getElementById('slSpeed').value);
    const altKm = parseFloat(document.getElementById('slAlt').value);
    const omega = parseFloat(document.getElementById('slOmega').value);

    const thrustKN = parseFloat(document.getElementById('slThrust').value);

    document.getElementById('vSpeed').textContent = speed.toFixed(0) + ' m/s';
    document.getElementById('vAlt').textContent = altKm.toFixed(1) + ' km';
    document.getElementById('vOmega').textContent = omega.toFixed(1);
    document.getElementById('vThrust').textContent = thrustKN.toFixed(0) + ' kN';
    document.getElementById('vTargetFPA').textContent = document.getElementById('slTargetFPA').value + '\u00b0';
    document.getElementById('vCtrlGain').textContent = parseFloat(document.getElementById('slCtrlGain').value).toFixed(1);

    // Use actual body-frame velocity from sim if available, otherwise reconstruct from AoA slider
    let velX, velY, velZ;
    if (simBodyVel) {
        velX = simBodyVel[0];
        velY = simBodyVel[1];
        velZ = simBodyVel[2];
    } else {
        const aoaRad = aoa * Math.PI / 180;
        velX = -speed * Math.cos(aoaRad);
        velY = -speed * Math.sin(aoaRad);
        velZ = 0;
    }

    // ── Server mode: use C# aero models via WebSocket ──
    if (serverConnected && aeroWs && aeroWs.readyState === 1) {
        // Stash velocity for applyServerAero (simBodyVel is cleared before onmessage fires)
        lastRequestVel = [velX, velY, velZ];
        if (!simBodyVel) {
            // Not simulating: send slider-driven request for visualization
            const omegaVec = [0, 0, omega];
            sendAeroRequest([velX, velY, velZ], omegaVec, altKm * 1000);
        }
        // During sim, simStep sends its own requests with current state
        // Response handled asynchronously via onmessage → applyServerAero
        return;
    }

    const atmo = getAtmo(altKm);
    const mach = speed / atmo.sos;
    const halfRho = 0.5 * atmo.rho;
    const streamlined = document.getElementById('chkStream').checked;
    const useShadow = document.getElementById('chkShadow').checked;
    const decayLen = parseFloat(document.getElementById('slDecay').value);
    document.getElementById('vDecay').textContent = decayLen.toFixed(1);

    // Pre-compute shadow factor for main flow direction (no spin)
    if (useShadow && speed > 0.1) {
        const svhx = velX/speed, svhy = velY/speed, svhz = 0;
        for (let i = 0; i < faceCount; i++) {
            const nx = faceNx[i], ny = faceNy[i], nz = faceNz[i];
            const cosA = svhx*nx + svhy*ny + svhz*nz;
            if (cosA > 0) {
                faceShadowF[i] = shadowFactor(facePx[i], facePy[i], facePz[i], svhx, svhy, svhz, decayLen);
            } else {
                faceShadowF[i] = 1.0;
            }
        }
    } else {
        faceShadowF.fill(1.0);
    }

    const wz = omega;

    let totalFx = 0, totalFy = 0, totalFz = 0;
    let totalTx = 0, totalTy = 0, totalTz = 0;
    let frontalArea = 0;
    let cpMin = 1e9, cpMax2 = -1e9;

    // Pass 1: compute forces and Cp per face
    for (let i = 0; i < faceCount; i++) {
        const rx = facePx[i] - comX;
        const ry = facePy[i] - comY;
        const rz = facePz[i] - comZ;

        let vx = velX + (-wz * ry);
        let vy = velY + (wz * rx);
        let vz = velZ;

        const spSq = vx*vx + vy*vy + vz*vz;
        let cp = streamlined ? cpBase * 0.3 : cpBase;

        if (spSq >= 0.01) {
            const invSp = 1.0 / Math.sqrt(spSq);
            const sp = spSq * invSp;
            const vhx = vx*invSp, vhy = vy*invSp, vhz = vz*invSp;

            const nx = faceNx[i], ny = faceNy[i], nz = faceNz[i];
            const cosA = vhx*nx + vhy*ny + vhz*nz;

            const localMach = sp / atmo.sos;
            const q = halfRho * spSq;
            const area = faceArea[i];

            if (cosA > 0) {
                const sf = faceShadowF[i];
                if (sf < 0.01) {
                    // Fully shadowed: base suction only
                    cp = streamlined ? cpBase * 0.3 : cpBase;
                } else {
                    const cpWind = streamlined ? computeCpStreamlined(cosA, localMach) : computeCp(cosA, localMach);
                    const cpShadow = streamlined ? cpBase * 0.3 : cpBase;
                    cp = cpShadow + (cpWind - cpShadow) * sf;
                    frontalArea += area * cosA * sf;
                }
            }

            const pf = -cp * q * area;
            let fx = nx * pf, fy = ny * pf, fz = nz * pf;

            if (cosA > 0 && cfSkin > 0) {
                let tx = vhx - cosA*nx, ty = vhy - cosA*ny, tz = vhz - cosA*nz;
                const tl = Math.sqrt(tx*tx+ty*ty+tz*tz);
                if (tl > 1e-6) {
                    const fric = cfSkin * q * area / tl;
                    fx += tx*fric; fy += ty*fric; fz += tz*fric;
                }
            }

            totalFx += fx; totalFy += fy; totalFz += fz;
            totalTx += ry*fz - rz*fy;
            totalTy += rz*fx - rx*fz;
            totalTz += rx*fy - ry*fx;
        }

        faceCpArr[i] = cp;
        if (cp < cpMin) cpMin = cp;
        if (cp > cpMax2) cpMax2 = cp;
    }

    // Wing circulation lift correction for HUD forces
    if (wingCount > 0 && speed > 1) {
        const wVhx=velX/speed, wVhy=velY/speed, wVhz=0;
        for (let wi = 0; wi < wingCount; wi++) {
            const wnx=wingNx[wi], wny=wingNy[wi], wnz=wingNz[wi];
            const vDotN = velX*wnx + velY*wny;
            const vipx=velX-vDotN*wnx, vipy=velY-vDotN*wny, vipz=-vDotN*wnz;
            const vipSp=Math.sqrt(vipx*vipx+vipy*vipy+vipz*vipz);
            const alpha = Math.atan2(-vDotN, vipSp+1e-6);
            const absA=Math.abs(alpha), signA=alpha>=0?1:-1;
            const clA=wingCLa[wi], aStall=wingAlphaStall[wi];
            let cl;
            if(absA<=aStall){cl=clA*alpha;}
            else{const se=(absA-aStall)/(aStall+0.01);const f0=Math.exp(-2*se);const sf=Math.sqrt(f0);
                const kf=0.25*(1+sf)*(1+sf);const cla=clA*aStall*signA;const clf=Math.sin(2*alpha);
                const bl=Math.min(1,se*0.5);cl=cla*kf*(1-bl)+clf*bl;}
            const wMach=speed/atmo.sos;
            if(wMach>0.01&&wMach<0.85)cl/=Math.sqrt(1-wMach*wMach);
            else if(wMach>=1.05)cl/=Math.sqrt(wMach*wMach-1);
            else if(wMach>=0.85){const mt=(wMach-0.85)/0.2;const ms=mt*mt*(3-2*mt);
                cl*=1/Math.sqrt(Math.max(0.01,1-0.85*0.85))+(1/Math.sqrt(1.05*1.05-1)-1/Math.sqrt(Math.max(0.01,1-0.85*0.85)))*ms;}
            const wq=halfRho*speed*speed, sA=wingArea[wi];
            const nDV=wnx*wVhx+wny*wVhy;
            let ldx=wnx-nDV*wVhx, ldy=wny-nDV*wVhy, ldz=wnz;
            const ldL=Math.sqrt(ldx*ldx+ldy*ldy+ldz*ldz);
            if(ldL>1e-6){ldx/=ldL;ldy/=ldL;ldz/=ldL;}
            const lM=wq*sA*cl;
            const ar=wingAR[wi], eO=wingOswaldE[wi];
            const cdi=ar>0.1?cl*cl/(Math.PI*eO*ar):0;
            const diM=wq*sA*cdi;
            const cd0=0.010, profM=wq*sA*cd0;
            // Subtract Newtonian pressure on wing faces (match whichever Cp model is active)
            const cosAW=Math.abs(vDotN)/(speed+1e-6);
            if(cosAW>0.01){const cpW=streamlined?computeCpStreamlined(cosAW,mach):computeCp(cosAW,mach);
                totalFx-=wnx*(-cpW*wq*sA);totalFy-=wny*(-cpW*wq*sA);totalFz-=wnz*(-cpW*wq*sA);}
            // Add wing forces: lift + induced drag + profile drag
            totalFx+=ldx*lM+wVhx*(diM+profM);
            totalFy+=ldy*lM+wVhy*(diM+profM);
            totalFz+=ldz*lM;
        }
    }

    // Pass 2: auto-scale colors to actual Cp range
    const cpRange = cpMax2 - cpMin;
    const invCpRange = cpRange > 0.001 ? 1.0 / cpRange : 1.0;
    const colArr = geo.attributes.color.array;

    for (let i = 0; i < faceCount; i++) {
        const t = (faceCpArr[i] - cpMin) * invCpRange;
        const [cr,cg,cb] = tToColor(t);
        const vi = i * 4;
        for (let c = 0; c < 4; c++) {
            const idx = (vi + c) * 3;
            colArr[idx] = cr; colArr[idx+1] = cg; colArr[idx+2] = cb;
        }
    }

    geo.attributes.color.needsUpdate = true;
    drawLegend(cpMin, cpMax2);

    // ── Velocity arrow (red, shows flight direction) ──
    if (speed > 0.1) {
        velArrow.visible = true;
        // Flight direction = velocity direction (toward nose at AoA=0)
        velArrow.setDirection(new THREE.Vector3(velX/speed, velY/speed, 0));
        const aLen = shipSize * 0.6;
        velArrow.setLength(aLen, aLen*0.12, aLen*0.06);
        velArrow.position.set(0, 0, 0);
    }

    // ── Decompose force into drag + lift ──
    const vHatX = velX/speed, vHatY = velY/speed, vHatZ = velZ/speed;
    const fDotV = totalFx*vHatX + totalFy*vHatY + totalFz*vHatZ;
    const dragMag = Math.abs(fDotV);
    // Lift vector (perpendicular to velocity)
    const liftVx = totalFx - fDotV*vHatX;
    const liftVy = totalFy - fDotV*vHatY;
    const liftVz = totalFz - fDotV*vHatZ;
    const liftMag = Math.sqrt(liftVx*liftVx + liftVy*liftVy + liftVz*liftVz);

    // Scale arrows proportional to force, with a reasonable max length
    const maxForce = Math.max(dragMag, liftMag, 1);
    const arrowScale = shipSize * 0.8 / maxForce;

    // ── Drag force arrow (orange, opposing velocity) ──
    if (dragMag > 1) {
        forceArrow.visible = true;
        // Drag opposes velocity
        forceArrow.setDirection(new THREE.Vector3(-vHatX, -vHatY, -vHatZ));
        const fLen = Math.max(dragMag * arrowScale, shipSize * 0.05);
        forceArrow.setLength(fLen, fLen*0.12, fLen*0.06);
    } else {
        forceArrow.visible = false;
    }

    // ── Lift force arrow (cyan, perpendicular to velocity) ──
    if (liftMag > 1) {
        liftArrow.visible = true;
        liftArrow.setDirection(new THREE.Vector3(liftVx/liftMag, liftVy/liftMag, liftVz/liftMag));
        const lLen = Math.max(liftMag * arrowScale, shipSize * 0.05);
        liftArrow.setLength(lLen, lLen*0.12, lLen*0.06);
    } else {
        liftArrow.visible = false;
    }

    // ── Thrust arrow (green, always along body forward axis = -X toward nose) ──
    if (thrustKN > 0) {
        thrustArrow.visible = true;
        thrustArrow.setDirection(new THREE.Vector3(-1, 0, 0));
        const tLen = shipSize * 0.5 * (thrustKN / Math.max(thrustKN, dragMag/1000, 1));
        thrustArrow.setLength(Math.max(tLen, shipSize*0.1), tLen*0.12, tLen*0.06);
    } else {
        thrustArrow.visible = false;
    }

    // ── CoP: r_cp = (F x tau) / |F|^2 ──
    const f2 = totalFx*totalFx + totalFy*totalFy + totalFz*totalFz;
    if (f2 > 100) {
        const cpx = (totalFy*totalTz - totalFz*totalTy) / f2;
        const cpy = (totalFz*totalTx - totalFx*totalTz) / f2;
        const cpz = (totalFx*totalTy - totalFy*totalTx) / f2;
        copMesh.position.set(cpx, cpy, cpz);
        copMesh.visible = true;
    } else {
        copMesh.visible = false;
    }

    // ── HUD ──
    document.getElementById('hDrag').textContent = dragMag.toFixed(0);
    document.getElementById('hLift').textContent = liftMag.toFixed(0);
    document.getElementById('hArea').textContent = frontalArea.toFixed(1);
    const q0 = halfRho * speed * speed;
    const cd = frontalArea > 0.01 && q0 > 0.01 ? dragMag / (q0 * frontalArea) : 0;
    document.getElementById('hCd').textContent = cd.toFixed(3);
    document.getElementById('vAoA').textContent = aoa + '\u00b0 (M' + mach.toFixed(2) + ')';
}

// ── Event listeners ──
['slAoA','slSpeed','slAlt','slOmega','slThrust','slTargetFPA','slCtrlGain','slDecay'].forEach(id => {
    document.getElementById(id).addEventListener('input', updatePressure);
});

document.getElementById('chkStream').addEventListener('change', updatePressure);
document.getElementById('chkShadow').addEventListener('change', updatePressure);

// Initial compute
updatePressure();

// (animation handled by mainLoop at bottom)

window.addEventListener('resize', () => {
    camera.aspect = innerWidth / innerHeight;
    camera.updateProjectionMatrix();
    renderer.setSize(innerWidth, innerHeight);
});

document.addEventListener('keydown', e => {
    if (e.code === 'KeyR') {
        camera.position.set(-shipSize*1.5, shipSize*0.8, shipSize*1.5);
        orbitCtrl.target.set(0, 0, 0);
    }
});

// ── 6DOF Real-time Simulation ────────────────────────────────────
// Integrates physics each render frame. Speed multiplier controls
// how many sim-seconds pass per real-second.

// Quaternion helpers
function qmul(a, b) {
    return [
        a[3]*b[0]+a[0]*b[3]+a[1]*b[2]-a[2]*b[1],
        a[3]*b[1]-a[0]*b[2]+a[1]*b[3]+a[2]*b[0],
        a[3]*b[2]+a[0]*b[1]-a[1]*b[0]+a[2]*b[3],
        a[3]*b[3]-a[0]*b[0]-a[1]*b[1]-a[2]*b[2]
    ];
}
function qrot(q, v) {
    const qv=[q[0],q[1],q[2]];
    const uv=[qv[1]*v[2]-qv[2]*v[1], qv[2]*v[0]-qv[0]*v[2], qv[0]*v[1]-qv[1]*v[0]];
    const uuv=[qv[1]*uv[2]-qv[2]*uv[1], qv[2]*uv[0]-qv[0]*uv[2], qv[0]*uv[1]-qv[1]*uv[0]];
    return [v[0]+2*(q[3]*uv[0]+uuv[0]), v[1]+2*(q[3]*uv[1]+uuv[1]), v[2]+2*(q[3]*uv[2]+uuv[2])];
}
function qconj(q) { return [-q[0],-q[1],-q[2],q[3]]; }
function qnorm(q) {
    const l=Math.sqrt(q[0]*q[0]+q[1]*q[1]+q[2]*q[2]+q[3]*q[3]);
    return l>0?[q[0]/l,q[1]/l,q[2]/l,q[3]/l]:[0,0,0,1];
}

function computeAeroForces(vel, omega, altKm) {
    const atmo = getAtmo(altKm);
    const halfRho = 0.5 * atmo.rho;
    const streamlined = document.getElementById('chkStream').checked;
    const useShadow = document.getElementById('chkShadow').checked;
    let fx=0,fy=0,fz=0, tx=0,ty=0,tz=0, fArea=0;
    for (let i = 0; i < faceCount; i++) {
        const rx=facePx[i]-comX, ry=facePy[i]-comY, rz=facePz[i]-comZ;
        let vx=vel[0]+(omega[1]*rz-omega[2]*ry);
        let vy=vel[1]+(omega[2]*rx-omega[0]*rz);
        let vz=vel[2]+(omega[0]*ry-omega[1]*rx);
        const spSq=vx*vx+vy*vy+vz*vz;
        if(spSq<0.01) continue;
        const invSp=1/Math.sqrt(spSq), sp=spSq*invSp;
        const vhx=vx*invSp,vhy=vy*invSp,vhz=vz*invSp;
        const nx=faceNx[i],ny=faceNy[i],nz=faceNz[i];
        const cosA=vhx*nx+vhy*ny+vhz*nz;
        const q=halfRho*spSq, area=faceArea[i];
        let cp=streamlined ? cpBase*0.3 : cpBase;
        if(cosA>0){
            const sf = useShadow ? faceShadowF[i] : 1.0;
            if(sf<0.01){cp=streamlined?cpBase*0.3:cpBase;}
            else{
                const cpW=streamlined ? computeCpStreamlined(cosA,sp/atmo.sos) : computeCp(cosA,sp/atmo.sos);
                const cpS=streamlined?cpBase*0.3:cpBase;
                cp=cpS+(cpW-cpS)*sf;
                fArea+=area*cosA*sf;
            }
        }
        const pf=-cp*q*area;
        let ffx=nx*pf,ffy=ny*pf,ffz=nz*pf;
        if(cosA>0&&cfSkin>0){
            let ttx=vhx-cosA*nx,tty=vhy-cosA*ny,ttz=vhz-cosA*nz;
            const tl=Math.sqrt(ttx*ttx+tty*tty+ttz*ttz);
            if(tl>1e-6){const fr=cfSkin*q*area/tl; ffx+=ttx*fr;ffy+=tty*fr;ffz+=ttz*fr;}
        }
        fx+=ffx;fy+=ffy;fz+=ffz;
        tx+=ry*ffz-rz*ffy; ty+=rz*ffx-rx*ffz; tz+=rx*ffy-ry*ffx;
    }
    // ── Wing circulation lift (thin airfoil + Kirchhoff stall) ──
    if (wingCount > 0) {
        const speed = Math.sqrt(vel[0]*vel[0]+vel[1]*vel[1]+vel[2]*vel[2]);
        if (speed > 1) {
            const vhx2=vel[0]/speed, vhy2=vel[1]/speed, vhz2=vel[2]/speed;
            const q0 = halfRho * speed * speed;
            const mach0 = speed / atmo.sos;

            for (let wi = 0; wi < wingCount; wi++) {
                // Velocity at wing aero center (body frame)
                const acx=wingAcX[wi], acy=wingAcY[wi], acz=wingAcZ[wi];
                let wvx=vel[0]+(omega[1]*acz-omega[2]*acy);
                let wvy=vel[1]+(omega[2]*acx-omega[0]*acz);
                let wvz=vel[2]+(omega[0]*acy-omega[1]*acx);
                const wsp=Math.sqrt(wvx*wvx+wvy*wvy+wvz*wvz);
                if(wsp<1) continue;
                const wvhx=wvx/wsp, wvhy=wvy/wsp, wvhz=wvz/wsp;

                // AoA from relative wind (-velocity). vDotN of velocity is negative
                // at positive AoA for +Y wing, so negate for correct sign.
                const wnx=wingNx[wi], wny=wingNy[wi], wnz=wingNz[wi];
                const vDotN = wvx*wnx + wvy*wny + wvz*wnz;
                const vipx=wvx-vDotN*wnx, vipy=wvy-vDotN*wny, vipz=wvz-vDotN*wnz;
                const vipSp=Math.sqrt(vipx*vipx+vipy*vipy+vipz*vipz);
                const alpha = Math.atan2(-vDotN, vipSp + 1e-6);
                const absAlpha = Math.abs(alpha);
                const signA = alpha >= 0 ? 1 : -1;

                // CL via Kirchhoff stall model
                const clAlpha=wingCLa[wi], alphaStall=wingAlphaStall[wi];
                let cl;
                if (absAlpha <= alphaStall) {
                    cl = clAlpha * alpha;
                } else {
                    const stallExcess = (absAlpha - alphaStall) / (alphaStall + 0.01);
                    const f0 = Math.exp(-2 * stallExcess);
                    const sqrtF0 = Math.sqrt(f0);
                    const kirchhoff = 0.25 * (1+sqrtF0) * (1+sqrtF0);
                    const clAttached = clAlpha * alphaStall * signA;
                    const clFlat = Math.sin(2*alpha);
                    const blendToFlat = Math.min(1, stallExcess * 0.5);
                    cl = clAttached * kirchhoff * (1-blendToFlat) + clFlat * blendToFlat;
                }

                // Prandtl-Glauert compressibility
                const wMach = wsp / atmo.sos;
                if (wMach > 0.01 && wMach < 0.85) {
                    cl /= Math.sqrt(1 - wMach*wMach);
                } else if (wMach >= 1.05) {
                    cl /= Math.sqrt(wMach*wMach - 1);
                } else if (wMach >= 0.85) {
                    const mt = (wMach-0.85)/0.2;
                    const ms = mt*mt*(3-2*mt);
                    const subB = Math.sqrt(Math.max(0.01, 1-0.85*0.85));
                    const supB = Math.sqrt(1.05*1.05-1);
                    cl *= 1/subB + (1/supB - 1/subB) * ms;
                }

                const wq = 0.5 * atmo.rho * wsp * wsp;
                const sArea = wingArea[wi];

                // Lift direction: component of normal perpendicular to flow
                const nDotV = wnx*wvhx + wny*wvhy + wnz*wvhz;
                let ldx=wnx-nDotV*wvhx, ldy=wny-nDotV*wvhy, ldz=wnz-nDotV*wvhz;
                const ldLen = Math.sqrt(ldx*ldx+ldy*ldy+ldz*ldz);
                if(ldLen>1e-6){ldx/=ldLen;ldy/=ldLen;ldz/=ldLen;}

                const liftMag = wq * sArea * cl;
                const lfx=ldx*liftMag, lfy=ldy*liftMag, lfz=ldz*liftMag;

                // Induced drag: CDi = CL²/(π·e·AR)
                const ar=wingAR[wi], eOswald=wingOswaldE[wi];
                const cdi = ar > 0.1 ? cl*cl / (Math.PI * eOswald * ar) : 0;
                const diMag = wq * sArea * cdi;
                const dix=wvhx*diMag, diy=wvhy*diMag, diz=wvhz*diMag;

                // Subtract ENTIRE Newtonian windward pressure force on wing faces.
                // Real wings at low AoA have nearly zero pressure drag (suction on
                // upper surface cancels pressure on lower). The Newtonian model
                // treats them as bluff panels, producing huge spurious drag.
                // We replace with: circulation lift + induced drag + small profile drag.
                const cosAlphaW = Math.abs(vDotN) / (wsp + 1e-6);
                if (cosAlphaW > 0.01) {
                    const localMach = wsp / atmo.sos;
                    const cpW = streamlined ? computeCpStreamlined(cosAlphaW, localMach) : computeCp(cosAlphaW, localMach);
                    const pressFx = wnx * (-cpW * wq * sArea);
                    const pressFy = wny * (-cpW * wq * sArea);
                    const pressFz = wnz * (-cpW * wq * sArea);
                    // Subtract entire pressure force (both drag and lift components)
                    fx -= pressFx; fy -= pressFy; fz -= pressFz;
                    const cex=wingCentX[wi], cey=wingCentY[wi], cez=wingCentZ[wi];
                    tx -= (cey*pressFz - cez*pressFy);
                    ty -= (cez*pressFx - cex*pressFz);
                    tz -= (cex*pressFy - cey*pressFx);
                }

                // Add profile drag (Cd0 ≈ 0.008-0.012 for clean wing)
                const cd0 = 0.010;
                const profDragMag = wq * sArea * cd0;
                const pdx=wvhx*profDragMag, pdy=wvhy*profDragMag, pdz=wvhz*profDragMag;
                fx += pdx; fy += pdy; fz += pdz;
                tx += acy*pdz - acz*pdy;
                ty += acz*pdx - acx*pdz;
                tz += acx*pdy - acy*pdx;

                // Add wing forces
                fx += lfx + dix; fy += lfy + diy; fz += lfz + diz;
                tx += acy*(lfz+diz) - acz*(lfy+diy);
                ty += acz*(lfx+dix) - acx*(lfz+diz);
                tz += acx*(lfy+diy) - acy*(lfx+dix);
            }
        }
    }

    return {force:[fx,fy,fz], torque:[tx,ty,tz], frontalArea:fArea};
}

// ── Sim state ──
let sim = null; // {pos, linVel, angVel, orient, simTime}
let simRunning = false;
let simPaused = false;
let simSpeed = 1;
let simLastWall = 0;
let apHoldQuat = null; // target quaternion for attitude hold

let simAeroPending = false;
let simServerPending = false; // waiting for sim step response from server
let simAccumDt = 0; // accumulated sim time waiting for server response

// Send a server request for current sim state (legacy aero-only mode)
function simRequestAero() {
    if (!sim || !serverConnected || !aeroWs || aeroWs.readyState !== 1 || simAeroPending) return;
    const invQ = qconj(sim.orient);
    const localVel = qrot(invQ, sim.linVel);
    simAeroPending = true;
    lastRequestVel = [localVel[0], localVel[1], localVel[2]];
    sendAeroRequest(localVel, sim.angVel, Math.max(0, sim.pos[1]));
}

// Apply sim response from server (updates local state from server-side RK4)
function applySimResponse(resp) {
    if (!sim) return;
    sim.pos = resp.pos;
    sim.linVel = resp.linVel;
    sim.angVel = resp.angVel;
    sim.orient = resp.orient;
    sim.simTime = resp.simTime;

    // Store aero for HUD/arrows
    lastServerAero = {
        force: resp.force,
        torque: resp.torque,
        dragMag: resp.dragMag,
        liftMag: resp.liftMag,
        frontalArea: resp.frontalArea,
        mach: resp.mach,
        dynamicPressure: resp.dynamicPressure,
        faceCp: resp.faceCp
    };
    // Body-frame velocity for visualization
    const invQ = qconj(sim.orient);
    const localVel = qrot(invQ, sim.linVel);
    lastRequestVel = [localVel[0], localVel[1], localVel[2]];
    applyServerAero(lastServerAero);

    // Ground check
    if (sim.pos[1] <= 0 && sim.linVel[1] < 0) {
        sim.pos[1] = 0; sim.linVel = [0,0,0]; sim.angVel = [0,0,0];
        simRunning = false; simPaused = false;
        sendSimStop();
    }
}

// Compute control torque and send sim step to server
function sendSimStepWithControls(dt) {
    if (!sim || !serverConnected || simServerPending) return;

    let ctrlTorque = [0, 0, 0];
    const apOn = document.getElementById('chkAP').checked;
    const worldSpd = Math.sqrt(sim.linVel[0]*sim.linVel[0]+sim.linVel[1]*sim.linVel[1]+sim.linVel[2]*sim.linVel[2]);

    if (worldSpd > 5) {
        const gain = parseFloat(document.getElementById('slCtrlGain').value);
        const invQ = qconj(sim.orient);
        const localVel = qrot(invQ, sim.linVel);
        const localSpd = Math.sqrt(localVel[0]*localVel[0]+localVel[1]*localVel[1]+localVel[2]*localVel[2]);
        const altKm = Math.max(0, sim.pos[1] / 1000);
        const atmoCtrl = getAtmo(altKm);
        const qDyn = 0.5 * atmoCtrl.rho * localSpd * localSpd;

        // Control authority based on wing area
        let totalWingArea = 0, maxWingArm = 1;
        for (let wi = 0; wi < wingCount; wi++) {
            totalWingArea += wingArea[wi];
            const arm = Math.sqrt(wingAcX[wi]*wingAcX[wi]+wingAcY[wi]*wingAcY[wi]+wingAcZ[wi]*wingAcZ[wi]);
            if (arm > maxWingArm) maxWingArm = arm;
        }
        const ctrlFraction = 0.2;
        const ctrlClAlpha = 4.0;
        const maxTorque = totalWingArea > 0
            ? qDyn * totalWingArea * ctrlFraction * ctrlClAlpha * maxWingArm
            : qDyn * shipSize * shipSize * 0.05 * shipSize * 0.5;

        // Manual input
        let pitchIn = 0, rollIn = 0, yawIn = 0;
        if (keysDown['arrowup'])    pitchIn += 1;
        if (keysDown['arrowdown'])  pitchIn -= 1;
        if (keysDown['arrowleft'])  yawIn += 1;
        if (keysDown['arrowright']) yawIn -= 1;
        if (keysDown['q'])          rollIn += 1;
        if (keysDown['e'])          rollIn -= 1;

        const altHeld = keysDown['alt'] || keysDown['meta'];
        orbitCtrl.enabled = !simRunning || !mouseActive || altHeld;
        if (mouseActive && !altHeld) {
            pitchIn += -mouseY;
            rollIn += mouseX;
        }
        pitchIn = Math.max(-1, Math.min(1, pitchIn));
        rollIn = Math.max(-1, Math.min(1, rollIn));
        yawIn = Math.max(-1, Math.min(1, yawIn));

        const hasInput = Math.abs(pitchIn) > 0.01 || Math.abs(rollIn) > 0.01 || Math.abs(yawIn) > 0.01;

        if (hasInput) {
            // Manual control: apply angular acceleration proportional to input
            const manualAlpha = 0.3 * gain;
            ctrlTorque[2] = -pitchIn * manualAlpha * Izz;
            ctrlTorque[0] = rollIn * manualAlpha * Ixx;
            ctrlTorque[1] = yawIn * manualAlpha * Iyy;
        }

        if (apOn) {
            const kd = 2.0 * gain;

            // ── Roll leveler: always roll toward wings-level ──
            // Body up in world frame — cross with world-up gives roll error axis
            const bodyUpW = qrot(sim.orient, [0, 1, 0]);
            // Roll error = cross(bodyUpW, worldUp).x projected onto body forward (-X)
            // cross([bx,by,bz],[0,1,0]) = [bz, 0, -bx]
            // Transform back to body frame for torque
            const rollErr = qrot(qconj(sim.orient), [bodyUpW[2], 0, -bodyUpW[0]]);
            const kpRoll = 3.0 * gain;
            ctrlTorque[0] -= kpRoll * Ixx * rollErr[0] + kd * Ixx * sim.angVel[0];

            // ── FPA controller: pitch to hold target flight path angle ──
            const targetFPA = parseFloat(document.getElementById('slTargetFPA').value) * Math.PI / 180;
            const horizSpd = Math.sqrt(sim.linVel[0]*sim.linVel[0] + sim.linVel[2]*sim.linVel[2]);
            const currentFPA = Math.atan2(sim.linVel[1], horizSpd);
            const fpaErr = targetFPA - currentFPA;
            const kpFPA = 0.5 * gain;
            ctrlTorque[2] -= kpFPA * Izz * fpaErr + kd * Izz * sim.angVel[2];

            // Yaw: rate damp only
            ctrlTorque[1] -= kd * Iyy * sim.angVel[1];
        }
    }

    const thrustN = parseFloat(document.getElementById('slThrust').value) * 1000;
    const useGrav = document.getElementById('chkGrav').checked;
    const grav = useGrav ? gravity : 0;

    sendSimStep(dt, ctrlTorque, thrustN, grav);
}

// simStep is now server-side (RK4 with fresh aero at each derivative evaluation).
// JS only sends control inputs and receives state back.

document.getElementById('btnSim').onclick = function() {
    if (simRunning) {
        // Stop
        simRunning = false; simPaused = false; sim = null;
        sendSimStop();
        clearTrail();
        faceMesh.quaternion.set(0,0,0,1);
        comMesh.quaternion.set(0,0,0,1);
        arrowGroup.quaternion.set(0,0,0,1);
        document.getElementById('btnSim').textContent = 'Start';
        document.getElementById('vSim').textContent = 'idle';
        updatePressure();
        return;
    }

    const initAoA = INITIAL_AOA_NUM;
    document.getElementById('slAoA').value = initAoA;
    let speed0 = parseFloat(document.getElementById('slSpeed').value);
    if (speed0 < 10) { speed0 = 300; document.getElementById('slSpeed').value = 300; }
    const altKm0 = parseFloat(document.getElementById('slAlt').value);
    const aoaRad = initAoA * Math.PI / 180;

    const halfA = aoaRad * 0.5;
    const sinH = Math.sin(halfA), cosH = Math.cos(halfA);

    sim = {
        pos: [0, altKm0*1000, 0],
        linVel: [-speed0, 0, 0],
        angVel: [0, 0, 0],
        orient: [0, 0, -sinH, cosH],
        simTime: 0
    };
    simRunning = true; simPaused = false;
    simLastWall = performance.now();
    simServerPending = false;
    simAccumDt = 0;
    apHoldQuat = null; // don't engage autopilot immediately — let aircraft find natural trim

    // Send sim init to server (server creates FlightSim with RK4)
    sendSimInit(sim.pos, sim.linVel, sim.angVel, sim.orient);

    document.getElementById('btnSim').textContent = 'Stop';
    document.getElementById('vSim').textContent = 'running';
};

document.getElementById('btnPause').onclick = function() {
    if (!simRunning) return;
    simPaused = !simPaused;
    if (!simPaused) simLastWall = performance.now();
    document.getElementById('btnPause').textContent = simPaused ? 'Resume' : 'Pause';
};

document.getElementById('btnSlower').onclick = function() {
    simSpeed = Math.max(0.125, simSpeed * 0.5);
    document.getElementById('vSimSpeed').textContent = simSpeed + 'x';
};
document.getElementById('btnFaster').onclick = function() {
    simSpeed = Math.min(64, simSpeed * 2);
    document.getElementById('vSimSpeed').textContent = simSpeed + 'x';
};

// ── Main render loop ──
function mainLoop() {
    requestAnimationFrame(mainLoop);

    if (simRunning && !simPaused && sim) {
        const now = performance.now();
        let wallDt = Math.min((now - simLastWall) / 1000, 0.05);
        simLastWall = now;

        // Accumulate sim time; physics advances on server via onmessage callback
        simAccumDt += wallDt * simSpeed;
        simAccumDt = Math.min(simAccumDt, 0.1);
        // Kick off server sim step if none pending
        if (!simServerPending && simAccumDt > 0) {
            const step = Math.min(simAccumDt, 0.02);
            simAccumDt -= step;
            sendSimStepWithControls(step);
        }

        // Record trail point every 0.2s of sim time (world coords)
        trailTimer += wallDt * simSpeed;
        if (trailTimer >= 0.2) {
            trailTimer = 0;
            addTrailPoint(sim.pos[0], sim.pos[1], sim.pos[2]);
        }
        // Shift trail so aircraft is always at origin
        updateTrailOffset(sim.pos[0], sim.pos[1], sim.pos[2]);

        // Update visuals from sim state
        const invQ = qconj(sim.orient);
        const localVel = qrot(invQ, sim.linVel);
        const sp = Math.sqrt(sim.linVel[0]*sim.linVel[0]+sim.linVel[1]*sim.linVel[1]+sim.linVel[2]*sim.linVel[2]);

        // Update sliders to reflect sim state
        document.getElementById('slSpeed').value = Math.min(1000, Math.round(sp));
        document.getElementById('slAlt').value = Math.max(0, sim.pos[1]/1000).toFixed(1);
        const omegaMag = Math.sqrt(sim.angVel[0]*sim.angVel[0]+sim.angVel[1]*sim.angVel[1]+sim.angVel[2]*sim.angVel[2]);
        document.getElementById('slOmega').value = Math.min(5, omegaMag).toFixed(1);
        // AoA = angle between body -X axis and body-frame velocity
        if (sp > 0.1) {
            const aoaDeg = Math.atan2(-localVel[1], -localVel[0]) * 180 / Math.PI;
            document.getElementById('slAoA').value = Math.max(-20, Math.min(90, Math.round(aoaDeg)));
        }

        // Pass actual body-frame velocity to updatePressure (avoids clamped AoA reconstruction)
        simBodyVel = localVel;
        simAngVel = sim.angVel;
        updatePressure();
        simBodyVel = null;
        simAngVel = null;

        // Rotate mesh and arrows (aircraft stays at origin, camera orbits around it)
        faceMesh.quaternion.set(sim.orient[0], sim.orient[1], sim.orient[2], sim.orient[3]);
        comMesh.quaternion.copy(faceMesh.quaternion);
        copMesh.quaternion.copy(faceMesh.quaternion);
        arrowGroup.quaternion.copy(faceMesh.quaternion);

        const status = 't=' + sim.simTime.toFixed(1) + 's  alt=' + (sim.pos[1]).toFixed(0) + 'm  v=' + sp.toFixed(0) + 'm/s  \u03c9=' + omegaMag.toFixed(2);
        document.getElementById('vSim').textContent = status;

        if (!simRunning) {
            document.getElementById('vSim').textContent += ' (stopped)';
            document.getElementById('btnSim').textContent = 'Start';
            document.getElementById('btnPause').textContent = 'Pause';
        }
    }

    orbitCtrl.update();
    renderer.render(scene, camera);
}
mainLoop();
");
        sb.AppendLine("</script></body></html>");

        File.WriteAllText(outputPath, sb.ToString());
        return Path.GetFullPath(outputPath);
    }

    private static void EmitFaceArray(StringBuilder sb, string name, IReadOnlyList<SurfaceFace> faces, Func<SurfaceFace, float> sel)
    {
        sb.Append($"const {name} = new Float32Array([");
        for (int i = 0; i < faces.Count; i++)
        {
            if (i > 0) sb.Append(',');
            float v = sel(faces[i]);
            if (float.IsNaN(v) || float.IsInfinity(v)) v = 0;
            sb.Append(v.ToString("G6"));
        }
        sb.AppendLine("]);");
    }
}
