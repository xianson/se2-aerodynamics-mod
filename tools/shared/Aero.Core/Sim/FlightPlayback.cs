using System.Numerics;
using System.Text;
using Aero.Core.Aero;
using Aero.Core.Grids;
using Aero.Core.Math;

namespace Aero.Core.Sim;

/// <summary>
/// Generates an interactive 3D playback of a flight simulation.
/// Self-contained HTML with Three.js from CDN.
/// Uses InstancedMesh for efficient rendering of large grids (67K+ blocks).
/// Only renders surface cells to keep draw calls manageable.
/// </summary>
public static class FlightPlayback
{
    public static string Generate(
        Grid grid,
        ISurfaceProvider surface,
        List<FlightRecord> history,
        string outputPath,
        float blockSize = 2.5f,
        string title = "Flight Playback")
    {
        var sb = new StringBuilder();

        // ── Collect ALL cell positions to compute CoM ──
        Vector3 com = Vector3.Zero;
        int totalCells = 0;
        foreach (var block in grid.Blocks.Values)
            foreach (var cell in block.EnumerateCells())
            {
                com += new Vector3(cell.X + 0.5f, cell.Y + 0.5f, cell.Z + 0.5f);
                totalCells++;
            }
        if (totalCells > 0) com /= totalCells;

        // ── Determine surface cells (cells with at least one missing neighbor) ──
        var cellSet = new HashSet<Vector3I>();
        foreach (var block in grid.Blocks.Values)
            foreach (var cell in block.EnumerateCells())
                cellSet.Add(cell);

        var surfaceCells = new List<Vector3I>();
        Vector3I[] dirs = {
            new(1,0,0), new(-1,0,0),
            new(0,1,0), new(0,-1,0),
            new(0,0,1), new(0,0,-1),
        };
        foreach (var cell in cellSet)
        {
            bool isSurface = false;
            foreach (var d in dirs)
            {
                if (!cellSet.Contains(new Vector3I(cell.X + d.X, cell.Y + d.Y, cell.Z + d.Z)))
                {
                    isSurface = true;
                    break;
                }
            }
            if (isSurface) surfaceCells.Add(cell);
        }

        // ── HTML ──
        sb.AppendLine("<!DOCTYPE html><html><head>");
        sb.AppendLine($"<title>{title}</title>");
        sb.AppendLine("<style>");
        sb.AppendLine("* { margin:0; padding:0; box-sizing:border-box; }");
        sb.AppendLine("body { background:#111; overflow:hidden; font-family:'Segoe UI',sans-serif; }");
        sb.AppendLine("canvas { display:block; }");
        sb.AppendLine("#hud { position:absolute; top:12px; left:12px; color:#0cf; font-size:13px; pointer-events:none; line-height:1.6; text-shadow:0 0 6px #000; }");
        sb.AppendLine("#hud .val { color:#fff; font-weight:bold; }");
        sb.AppendLine("#controls { position:absolute; bottom:12px; left:12px; right:12px; display:flex; align-items:center; gap:10px; }");
        sb.AppendLine("#controls button { background:#222; color:#0cf; border:1px solid #0cf; border-radius:4px; padding:6px 14px; cursor:pointer; font-size:13px; }");
        sb.AppendLine("#controls button:hover { background:#0cf; color:#111; }");
        sb.AppendLine("#slider { flex:1; accent-color:#0cf; }");
        sb.AppendLine("#timeLabel { color:#888; font-size:13px; min-width:80px; text-align:right; }");
        sb.AppendLine("#speedLabel { color:#666; font-size:12px; min-width:40px; }");
        sb.AppendLine("#cellCount { color:#555; font-size:11px; position:absolute; top:12px; right:12px; pointer-events:none; }");
        sb.AppendLine("</style>");
        sb.AppendLine("</head><body>");

        // HUD
        sb.AppendLine("<div id=\"hud\">");
        sb.AppendLine($"<div style=\"color:#7b68ee;font-size:16px;font-weight:bold;margin-bottom:4px\">{title}</div>");
        sb.AppendLine($"<div style=\"color:#555;font-size:11px;margin-bottom:6px\">{grid.BlockCount:N0} blocks, {surfaceCells.Count:N0} surface cells</div>");
        sb.AppendLine("Alt: <span class=\"val\" id=\"hAlt\">-</span> m<br>");
        sb.AppendLine("Speed: <span class=\"val\" id=\"hSpd\">-</span> m/s<br>");
        sb.AppendLine("Mach: <span class=\"val\" id=\"hMach\">-</span><br>");
        sb.AppendLine("AoA: <span class=\"val\" id=\"hAoA\">-</span>°<br>");
        sb.AppendLine("q: <span class=\"val\" id=\"hQ\">-</span> Pa<br>");
        sb.AppendLine("Drag: <span class=\"val\" id=\"hDrag\">-</span> N<br>");
        sb.AppendLine("Lift: <span class=\"val\" id=\"hLift\">-</span> N<br>");
        sb.AppendLine("ω: <span class=\"val\" id=\"hOmega\">-</span> rad/s<br>");
        sb.AppendLine("τ<sub>Z</sub>: <span class=\"val\" id=\"hTorque\">-</span> N·m<br>");
        sb.AppendLine("<span style=\"color:#ff2222\">● CoM</span>&nbsp;&nbsp;<span style=\"color:#22ff44\">● CoP</span><br>");
        sb.AppendLine("</div>");

        // Controls
        sb.AppendLine("<div id=\"controls\">");
        sb.AppendLine("<button id=\"btnPlay\">▶</button>");
        sb.AppendLine("<button id=\"btnSlower\">½×</button>");
        sb.AppendLine("<button id=\"btnFaster\">2×</button>");
        sb.AppendLine("<span id=\"speedLabel\">1×</span>");
        sb.AppendLine("<input type=\"range\" id=\"slider\" min=\"0\" max=\"1000\" value=\"0\">");
        sb.AppendLine("<span id=\"timeLabel\">0.0 / 0.0 s</span>");
        sb.AppendLine("</div>");

        // Three.js r137
        sb.AppendLine("<script src=\"https://cdn.jsdelivr.net/npm/three@0.137.0/build/three.min.js\"></script>");
        sb.AppendLine("<script src=\"https://cdn.jsdelivr.net/npm/three@0.137.0/examples/js/controls/OrbitControls.js\"></script>");

        sb.AppendLine("<script>");

        // Emit surface cell positions as flat Float32Array (relative to CoM, in meters)
        sb.AppendLine($"const cellCount = {surfaceCells.Count};");
        sb.Append("const cellPos = new Float32Array([");
        for (int i = 0; i < surfaceCells.Count; i++)
        {
            if (i > 0) sb.Append(',');
            float x = (surfaceCells[i].X + 0.5f - com.X) * blockSize;
            float y = (surfaceCells[i].Y + 0.5f - com.Y) * blockSize;
            float z = (surfaceCells[i].Z + 0.5f - com.Z) * blockSize;
            sb.Append($"{x:G5},{y:G5},{z:G5}");
        }
        sb.AppendLine("]);");

        // Emit flight data (downsampled if needed)
        int maxFrames = 2000;
        int step = System.Math.Max(1, history.Count / maxFrames);
        var sampled = new List<FlightRecord>();
        for (int i = 0; i < history.Count; i += step)
            sampled.Add(history[i]);
        if (sampled[^1].Time < history[^1].Time)
            sampled.Add(history[^1]);

        sb.AppendLine($"const frameCount = {sampled.Count};");
        EmitFloatArray(sb, "fTime", sampled, r => (float)r.Time);
        EmitFloatArray(sb, "fPosX", sampled, r => r.State.Position.X);
        EmitFloatArray(sb, "fPosY", sampled, r => r.State.Position.Y);
        EmitFloatArray(sb, "fPosZ", sampled, r => r.State.Position.Z);
        EmitFloatArray(sb, "fQuatX", sampled, r => r.State.Orientation.X);
        EmitFloatArray(sb, "fQuatY", sampled, r => r.State.Orientation.Y);
        EmitFloatArray(sb, "fQuatZ", sampled, r => r.State.Orientation.Z);
        EmitFloatArray(sb, "fQuatW", sampled, r => r.State.Orientation.W);
        EmitFloatArray(sb, "fAlt", sampled, r => r.Altitude);
        EmitFloatArray(sb, "fSpd", sampled, r => r.State.Speed);
        EmitFloatArray(sb, "fMach", sampled, r => (float)r.Mach);
        EmitFloatArray(sb, "fAoA", sampled, r => r.AngleOfAttack);
        EmitFloatArray(sb, "fQ", sampled, r => (float)r.DynamicPressure);
        EmitFloatArray(sb, "fDrag", sampled, r => r.Aero.DragMagnitude);
        EmitFloatArray(sb, "fLift", sampled, r => r.Aero.LiftMagnitude);
        EmitFloatArray(sb, "fOmega", sampled, r => r.State.AngularSpeed);
        EmitFloatArray(sb, "fVelX", sampled, r => r.State.LinearVelocity.X);
        EmitFloatArray(sb, "fVelY", sampled, r => r.State.LinearVelocity.Y);
        EmitFloatArray(sb, "fVelZ", sampled, r => r.State.LinearVelocity.Z);
        // Drag force direction for visualization
        EmitFloatArray(sb, "fDragX", sampled, r => r.Aero.Force.X);
        EmitFloatArray(sb, "fDragY", sampled, r => r.Aero.Force.Y);
        EmitFloatArray(sb, "fDragZ", sampled, r => r.Aero.Force.Z);
        // Torque for CoP computation
        EmitFloatArray(sb, "fTorqueX", sampled, r => r.Aero.Torque.X);
        EmitFloatArray(sb, "fTorqueY", sampled, r => r.Aero.Torque.Y);
        EmitFloatArray(sb, "fTorqueZ", sampled, r => r.Aero.Torque.Z);

        sb.AppendLine($"const blockSize = {blockSize:G5};");
        sb.AppendLine($"const totalTime = {sampled[^1].Time:G6};");
        // CoM in grid-local coords (meters, relative to grid origin)
        sb.AppendLine($"const comX = {(com.X * blockSize):G5};");
        sb.AppendLine($"const comY = {(com.Y * blockSize):G5};");
        sb.AppendLine($"const comZ = {(com.Z * blockSize):G5};");

        // Main Three.js scene — uses InstancedMesh for performance
        sb.Append(@"
// ── Scene setup ──
const scene = new THREE.Scene();
scene.background = new THREE.Color(0x0a0a1a);
scene.fog = new THREE.FogExp2(0x0a0a1a, 0.000015);

const camera = new THREE.PerspectiveCamera(60, window.innerWidth/window.innerHeight, 0.1, 500000);
const renderer = new THREE.WebGLRenderer({antialias:true});
renderer.setSize(window.innerWidth, window.innerHeight);
renderer.setPixelRatio(Math.min(window.devicePixelRatio, 2));
document.body.appendChild(renderer.domElement);

const controls = new THREE.OrbitControls(camera, renderer.domElement);
controls.enableDamping = true;
controls.dampingFactor = 0.1;

// Lights
scene.add(new THREE.AmbientLight(0x404060, 0.8));
const dirLight = new THREE.DirectionalLight(0xffffff, 1.5);
dirLight.position.set(1, 2, 1);
scene.add(dirLight);
const backLight = new THREE.DirectionalLight(0x4466aa, 0.4);
backLight.position.set(-1, -0.5, -1);
scene.add(backLight);

// ── Ground plane with horizon ──
const groundGeo = new THREE.PlaneGeometry(500000, 500000);
const groundMat = new THREE.MeshLambertMaterial({color:0x0a1a0a, transparent:true, opacity:0.7});
const ground = new THREE.Mesh(groundGeo, groundMat);
ground.rotation.x = -Math.PI/2;
ground.position.y = 0;
scene.add(ground);

const gridHelper = new THREE.GridHelper(20000, 200, 0x222233, 0x181828);
scene.add(gridHelper);

// ── Build grid mesh using InstancedMesh (single draw call!) ──
const gridGroup = new THREE.Group();
const blockGeo = new THREE.BoxGeometry(blockSize*0.92, blockSize*0.92, blockSize*0.92);
const mat = new THREE.MeshPhongMaterial({
    color: 0x4488cc,
    specular: 0x223344,
    shininess: 30,
    flatShading: true,
});
const instancedMesh = new THREE.InstancedMesh(blockGeo, mat, cellCount);
const dummy = new THREE.Object3D();

for (let i = 0; i < cellCount; i++) {
    dummy.position.set(cellPos[i*3], cellPos[i*3+1], cellPos[i*3+2]);
    dummy.updateMatrix();
    instancedMesh.setMatrixAt(i, dummy.matrix);
}
instancedMesh.instanceMatrix.needsUpdate = true;
gridGroup.add(instancedMesh);
scene.add(gridGroup);

// ── Force arrows (velocity=red, drag=yellow) ──
const arrowDir = new THREE.Vector3(1,0,0);
const arrowOrigin = new THREE.Vector3(0,0,0);
let velArrow = new THREE.ArrowHelper(arrowDir, arrowOrigin, 20, 0xff4444, 5, 3);
let dragArrow = new THREE.ArrowHelper(arrowDir, arrowOrigin, 20, 0xffaa22, 5, 3);
scene.add(velArrow);
scene.add(dragArrow);

// ── CoM marker (red sphere, always rendered in front) ──
const comGeo = new THREE.SphereGeometry(blockSize * 1.5, 16, 12);
const comMat = new THREE.MeshPhongMaterial({color: 0xff2222, emissive: 0x990000, transparent: true, opacity: 0.9, depthTest: false});
const comMesh = new THREE.Mesh(comGeo, comMat);
comMesh.renderOrder = 999;
comMesh.position.set(0, 0, 0); // CoM is at origin since cells are relative to CoM
gridGroup.add(comMesh);

// ── CoP marker (green sphere, always rendered in front) ──
const copGeo = new THREE.SphereGeometry(blockSize * 1.5, 16, 12);
const copMat = new THREE.MeshPhongMaterial({color: 0x22ff44, emissive: 0x009900, transparent: true, opacity: 0.9, depthTest: false});
const copMesh = new THREE.Mesh(copGeo, copMat);
copMesh.renderOrder = 999;
gridGroup.add(copMesh);

// ── Trajectory trail ──
const trailMax = frameCount;
const trailGeo = new THREE.BufferGeometry();
const trailPositions = new Float32Array(trailMax * 3);
const trailColors = new Float32Array(trailMax * 3);
for (let i = 0; i < trailMax; i++) {
    trailPositions[i*3] = fPosX[i];
    trailPositions[i*3+1] = fPosY[i];
    trailPositions[i*3+2] = fPosZ[i];
    const t = i / trailMax;
    // Color by Mach: blue → cyan → yellow → red
    const m = fMach[i];
    if (m < 0.5) {
        trailColors[i*3] = 0.0;
        trailColors[i*3+1] = m * 1.6 + 0.2;
        trailColors[i*3+2] = 1.0;
    } else if (m < 1.0) {
        trailColors[i*3] = (m - 0.5) * 2;
        trailColors[i*3+1] = 1.0;
        trailColors[i*3+2] = 1.0 - (m - 0.5) * 2;
    } else {
        trailColors[i*3] = 1.0;
        trailColors[i*3+1] = Math.max(0, 1.0 - (m - 1.0) * 0.5);
        trailColors[i*3+2] = 0.0;
    }
}
trailGeo.setAttribute('position', new THREE.BufferAttribute(trailPositions, 3));
trailGeo.setAttribute('color', new THREE.BufferAttribute(trailColors, 3));
const trailMat = new THREE.LineBasicMaterial({vertexColors:true, transparent:true, opacity:0.8, linewidth:2});
const trail = new THREE.Line(trailGeo, trailMat);
scene.add(trail);

// ── Playback state ──
let playing = true;
let playSpeed = 5.0;  // start at 5× for long sims
let currentFrame = 0;
let playTime = 0;
const slider = document.getElementById('slider');
const timeLabel = document.getElementById('timeLabel');
const btnPlay = document.getElementById('btnPlay');
const speedLabel = document.getElementById('speedLabel');
speedLabel.textContent = playSpeed + '×';

btnPlay.onclick = () => { playing = !playing; btnPlay.textContent = playing ? '⏸' : '▶'; };
document.getElementById('btnSlower').onclick = () => { playSpeed = Math.max(0.125, playSpeed * 0.5); speedLabel.textContent = playSpeed + '×'; };
document.getElementById('btnFaster').onclick = () => { playSpeed = Math.min(64, playSpeed * 2); speedLabel.textContent = playSpeed + '×'; };
slider.oninput = () => {
    const t = (slider.value / 1000) * totalTime;
    playTime = t;
    currentFrame = findFrame(t);
};

function findFrame(t) {
    // Binary search for performance
    let lo = 0, hi = frameCount - 1;
    while (lo < hi) {
        const mid = (lo + hi + 1) >> 1;
        if (fTime[mid] <= t) lo = mid; else hi = mid - 1;
    }
    return lo;
}

function lerp(a, b, t) { return a + (b - a) * t; }

function updateFrame(f, frac) {
    const i = Math.min(f, frameCount - 1);
    const j = Math.min(f + 1, frameCount - 1);

    // Position
    const px = lerp(fPosX[i], fPosX[j], frac);
    const py = lerp(fPosY[i], fPosY[j], frac);
    const pz = lerp(fPosZ[i], fPosZ[j], frac);
    gridGroup.position.set(px, py, pz);

    // Orientation (slerp)
    const q1 = new THREE.Quaternion(fQuatX[i], fQuatY[i], fQuatZ[i], fQuatW[i]);
    const q2 = new THREE.Quaternion(fQuatX[j], fQuatY[j], fQuatZ[j], fQuatW[j]);
    q1.slerp(q2, frac);
    gridGroup.quaternion.copy(q1);

    // Velocity arrow (red)
    const vx = lerp(fVelX[i], fVelX[j], frac);
    const vy = lerp(fVelY[i], fVelY[j], frac);
    const vz = lerp(fVelZ[i], fVelZ[j], frac);
    const vLen = Math.sqrt(vx*vx + vy*vy + vz*vz);
    if (vLen > 0.1) {
        velArrow.visible = true;
        velArrow.position.set(px, py, pz);
        velArrow.setDirection(new THREE.Vector3(vx/vLen, vy/vLen, vz/vLen));
        const arrowLen = Math.min(vLen * 0.3, 500);
        velArrow.setLength(arrowLen, arrowLen*0.15, arrowLen*0.08);
    } else {
        velArrow.visible = false;
    }

    // Drag force arrow (yellow/orange)
    const dx = lerp(fDragX[i], fDragX[j], frac);
    const dy = lerp(fDragY[i], fDragY[j], frac);
    const dz = lerp(fDragZ[i], fDragZ[j], frac);
    const dLen = Math.sqrt(dx*dx + dy*dy + dz*dz);
    if (dLen > 1) {
        dragArrow.visible = true;
        dragArrow.position.set(px, py, pz);
        dragArrow.setDirection(new THREE.Vector3(dx/dLen, dy/dLen, dz/dLen));
        const dragScale = Math.min(Math.log10(dLen + 1) * 30, 400);
        dragArrow.setLength(dragScale, dragScale*0.15, dragScale*0.08);
    } else {
        dragArrow.visible = false;
    }

    // CoP: r_cp = (F × τ) / |F|² — displacement from CoM to center of pressure
    // This gives the point where a single force F would produce torque τ = r × F
    const tx = lerp(fTorqueX[i], fTorqueX[j], frac);
    const ty = lerp(fTorqueY[i], fTorqueY[j], frac);
    const tz = lerp(fTorqueZ[i], fTorqueZ[j], frac);
    const f2 = dx*dx + dy*dy + dz*dz;
    if (f2 > 100) { // need meaningful force
        // r = (F × τ) / |F|²
        const rx = (dy*tz - dz*ty) / f2;
        const ry = (dz*tx - dx*tz) / f2;
        const rz = (dx*ty - dy*tx) / f2;
        // Transform to grid-local space using inverse of grid quaternion
        const q = gridGroup.quaternion.clone().invert();
        const rLocal = new THREE.Vector3(rx, ry, rz).applyQuaternion(q);
        copMesh.position.copy(rLocal);
        copMesh.visible = true;
    } else {
        copMesh.visible = false;
    }

    // Trail visibility
    trail.geometry.setDrawRange(0, Math.min(i + 1, trailMax));

    // HUD
    document.getElementById('hAlt').textContent = lerp(fAlt[i], fAlt[j], frac).toFixed(0);
    document.getElementById('hSpd').textContent = lerp(fSpd[i], fSpd[j], frac).toFixed(1);
    document.getElementById('hMach').textContent = lerp(fMach[i], fMach[j], frac).toFixed(3);
    document.getElementById('hAoA').textContent = lerp(fAoA[i], fAoA[j], frac).toFixed(1);
    document.getElementById('hQ').textContent = lerp(fQ[i], fQ[j], frac).toFixed(0);
    document.getElementById('hDrag').textContent = lerp(fDrag[i], fDrag[j], frac).toFixed(0);
    document.getElementById('hLift').textContent = lerp(fLift[i], fLift[j], frac).toFixed(0);
    document.getElementById('hOmega').textContent = lerp(fOmega[i], fOmega[j], frac).toFixed(3);
    document.getElementById('hTorque').textContent = lerp(fTorqueZ[i], fTorqueZ[j], frac).toFixed(0);

    const t = lerp(fTime[i], fTime[j], frac);
    timeLabel.textContent = t.toFixed(1) + ' / ' + totalTime.toFixed(1) + ' s';
    slider.value = (t / totalTime) * 1000;
}

// ── Camera setup ──
// Estimate ship extent from cell positions
let minX=Infinity,maxX=-Infinity,minY=Infinity,maxY=-Infinity,minZ=Infinity,maxZ=-Infinity;
for (let i = 0; i < cellCount; i++) {
    const x=cellPos[i*3], y=cellPos[i*3+1], z=cellPos[i*3+2];
    if(x<minX)minX=x; if(x>maxX)maxX=x;
    if(y<minY)minY=y; if(y>maxY)maxY=y;
    if(z<minZ)minZ=z; if(z>maxZ)maxZ=z;
}
const shipSize = Math.max(maxX-minX, maxY-minY, maxZ-minZ, 30);

const startPos = new THREE.Vector3(fPosX[0], fPosY[0], fPosZ[0]);
camera.position.set(startPos.x - shipSize*2, startPos.y + shipSize*1.2, startPos.z + shipSize*2);
controls.target.copy(startPos);

// ── Animation loop ──
let lastTime = performance.now();

function animate() {
    requestAnimationFrame(animate);

    const now = performance.now();
    const dt = (now - lastTime) / 1000;
    lastTime = now;

    if (playing && currentFrame < frameCount - 1) {
        playTime += dt * playSpeed;
        if (playTime > totalTime) { playTime = totalTime; playing = false; btnPlay.textContent = '▶'; }
        currentFrame = findFrame(playTime);
    }

    const i = currentFrame;
    const j = Math.min(i + 1, frameCount - 1);
    let frac = 0;
    if (fTime[j] > fTime[i]) frac = Math.min(1, (playTime - fTime[i]) / (fTime[j] - fTime[i]));

    updateFrame(i, frac);

    // Camera follow (smooth)
    const targetPos = gridGroup.position.clone();
    controls.target.lerp(targetPos, 0.05);
    controls.update();

    // Move ground grid to follow horizontally
    gridHelper.position.x = Math.round(targetPos.x / 1000) * 1000;
    gridHelper.position.z = Math.round(targetPos.z / 1000) * 1000;

    renderer.render(scene, camera);
}
animate();

window.addEventListener('resize', () => {
    camera.aspect = window.innerWidth / window.innerHeight;
    camera.updateProjectionMatrix();
    renderer.setSize(window.innerWidth, window.innerHeight);
});

// Keyboard shortcuts
document.addEventListener('keydown', e => {
    if (e.code === 'Space') { playing = !playing; btnPlay.textContent = playing ? '⏸' : '▶'; e.preventDefault(); }
    if (e.code === 'ArrowRight') { playTime = Math.min(totalTime, playTime + totalTime*0.02); currentFrame = findFrame(playTime); }
    if (e.code === 'ArrowLeft') { playTime = Math.max(0, playTime - totalTime*0.02); currentFrame = findFrame(playTime); }
    if (e.code === 'KeyR') { playTime = 0; currentFrame = 0; playing = true; btnPlay.textContent = '⏸'; }
});
");

        sb.AppendLine("</script></body></html>");

        File.WriteAllText(outputPath, sb.ToString());
        return System.IO.Path.GetFullPath(outputPath);
    }

    private static void EmitFloatArray(StringBuilder sb, string name, List<FlightRecord> data, Func<FlightRecord, float> sel)
    {
        sb.Append($"const {name} = new Float32Array([");
        for (int i = 0; i < data.Count; i++)
        {
            if (i > 0) sb.Append(',');
            float v = sel(data[i]);
            if (float.IsNaN(v) || float.IsInfinity(v)) v = 0;
            sb.Append(v.ToString("G6"));
        }
        sb.AppendLine("]);");
    }
}
