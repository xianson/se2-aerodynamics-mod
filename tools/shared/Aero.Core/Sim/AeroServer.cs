using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using Aero.Core.Aero;
using Aero.Core.Atmosphere;
using Aero.Core.Grids;

namespace Aero.Core.Sim;

/// <summary>
/// Lightweight HTTP + WebSocket server using raw TcpListener (no HttpListener,
/// no admin/URL-ACL required on Windows).
///
/// Serves the FacePressureView HTML on GET / and accepts WebSocket connections
/// for real-time aero computation using actual C# drag models.
///
/// Protocol (binary WebSocket), distinguished by message length:
///   28 bytes = Aero query:   velocity[3] + omega[3] + altitudeM[1]
///              Response: force[3] + torque[3] + dragMag + liftMag + frontalArea + mach + dynPressure + faceCp[N]
///
///   52 bytes = Sim init:     pos[3] + linVel[3] + angVel[3] + orient[4] = 13 floats
///              Response: same as sim step (initial state + aero)
///
///   24 bytes = Sim step:     dt + ctrlTorque[3] + thrustN + gravity = 6 floats
///              Response: pos[3] + linVel[3] + angVel[3] + orient[4] + simTime
///                        + force[3] + torque[3] + dragMag + liftMag + frontalArea + mach + dynPressure
///                        + faceCp[N]  =  (25 + N) * 4 bytes
///
///    4 bytes = Sim stop
/// </summary>
public class AeroServer : IDisposable
{
    private readonly TcpListener _listener;
    private readonly CancellationTokenSource _cts = new();

    private readonly Grid _grid;
    private readonly ISurfaceProvider _surface;
    private readonly MassProperties _mass;
    private readonly AtmosphereModel _atmo;
    private readonly float _blockSize;
    private readonly AeroDragModelBase _dragModel;
    private readonly ShadowMap? _shadowMap;

    private readonly float[] _cpBuffer;
    private readonly System.Diagnostics.Stopwatch _logTimer = System.Diagnostics.Stopwatch.StartNew();
    private string? _htmlContent;
    private byte[]? _htmlBytes;

    // Server-side sim (RK4 with fresh aero at each derivative evaluation)
    private FlightSim? _flightSim;

    public int Port { get; }

    public AeroServer(
        Grid grid,
        ISurfaceProvider surface,
        MassProperties mass,
        AtmosphereModel atmo,
        AeroDragModelBase dragModel,
        float blockSize,
        int port = 0)
    {
        _grid = grid;
        _surface = surface;
        _mass = mass;
        _atmo = atmo;
        _blockSize = blockSize;
        _dragModel = dragModel;

        if (dragModel is DampedShadowedDragModel dsm)
            _shadowMap = dsm.ShadowMap;
        else if (dragModel is ShadowedDragModel sdm)
            _shadowMap = sdm.ShadowMap;
        else if (dragModel is LiftingSurfaceModel lsm)
        {
            if (lsm.InnerModel is DampedShadowedDragModel di)
                _shadowMap = di.ShadowMap;
            else if (lsm.InnerModel is ShadowedDragModel inner)
                _shadowMap = inner.ShadowMap;
        }

        _cpBuffer = new float[surface.Faces.Count];

        _listener = new TcpListener(IPAddress.Loopback, port);
        _listener.Start();
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
    }

    public void SetHtml(string html)
    {
        _htmlContent = html;
        _htmlBytes = Encoding.UTF8.GetBytes(html);
    }

    public async Task RunAsync(CancellationToken externalToken = default)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token, externalToken);
        var token = linked.Token;

        while (!token.IsCancellationRequested)
        {
            TcpClient client;
            try { client = await _listener.AcceptTcpClientAsync(token); }
            catch (OperationCanceledException) { break; }
            catch { continue; }

            _ = HandleClientAsync(client, token);
        }
    }

    private async Task HandleClientAsync(TcpClient client, CancellationToken token)
    {
        try
        {
            using (client)
            {
                client.NoDelay = true;
                var stream = client.GetStream();

                // Read HTTP request
                var buf = new byte[4096];
                int read = await stream.ReadAsync(buf, 0, buf.Length, token);
                if (read == 0) return;
                string request = Encoding.ASCII.GetString(buf, 0, read);

                if (request.Contains("Upgrade: websocket", StringComparison.OrdinalIgnoreCase))
                {
                    await HandleWebSocketUpgrade(stream, request, token);
                }
                else if (request.StartsWith("GET / ") || request.StartsWith("GET / HTTP"))
                {
                    await ServeHtml(stream, token);
                }
                else
                {
                    var resp404 = Encoding.ASCII.GetBytes("HTTP/1.1 404 Not Found\r\nContent-Length: 0\r\nConnection: close\r\n\r\n");
                    await stream.WriteAsync(resp404, token);
                }
            }
        }
        catch { /* client disconnect */ }
    }

    private async Task ServeHtml(NetworkStream stream, CancellationToken token)
    {
        if (_htmlBytes == null) return;

        var header = Encoding.ASCII.GetBytes(
            $"HTTP/1.1 200 OK\r\nContent-Type: text/html; charset=utf-8\r\nContent-Length: {_htmlBytes.Length}\r\nConnection: close\r\n\r\n");
        await stream.WriteAsync(header, token);
        await stream.WriteAsync(_htmlBytes, token);
    }

    private async Task HandleWebSocketUpgrade(NetworkStream stream, string request, CancellationToken token)
    {
        // Extract Sec-WebSocket-Key
        string? key = null;
        foreach (var line in request.Split("\r\n"))
        {
            if (line.StartsWith("Sec-WebSocket-Key:", StringComparison.OrdinalIgnoreCase))
            {
                key = line.Substring(line.IndexOf(':') + 1).Trim();
                break;
            }
        }
        if (key == null) return;

        // Compute accept key
        string acceptKey = Convert.ToBase64String(
            SHA1.HashData(Encoding.ASCII.GetBytes(key + "258EAFA5-E914-47DA-95CA-C5AB0DC85B11")));

        // Send upgrade response
        var resp = Encoding.ASCII.GetBytes(
            "HTTP/1.1 101 Switching Protocols\r\n" +
            "Upgrade: websocket\r\n" +
            "Connection: Upgrade\r\n" +
            $"Sec-WebSocket-Accept: {acceptKey}\r\n\r\n");
        await stream.WriteAsync(resp, token);

        // WebSocket frame loop
        var recvBuf = new byte[256];
        var sendPayload = new byte[(11 + _cpBuffer.Length) * 4];

        while (!token.IsCancellationRequested)
        {
            // Read WebSocket frame header
            int b0, b1;
            try
            {
                b0 = stream.ReadByte();
                b1 = stream.ReadByte();
            }
            catch { break; }
            if (b0 < 0 || b1 < 0) break;

            int opcode = b0 & 0x0F;
            if (opcode == 8) break; // close frame

            bool masked = (b1 & 0x80) != 0;
            int payloadLen = b1 & 0x7F;
            if (payloadLen == 126)
            {
                int h = stream.ReadByte(), l = stream.ReadByte();
                if (h < 0 || l < 0) break;
                payloadLen = (h << 8) | l;
            }
            else if (payloadLen == 127)
            {
                // Skip 8-byte extended length (we don't expect huge frames)
                for (int i = 0; i < 8; i++) stream.ReadByte();
                break;
            }

            byte[] maskKey = new byte[4];
            if (masked)
            {
                int r = await stream.ReadAsync(maskKey, 0, 4, token);
                if (r < 4) break;
            }

            if (payloadLen > recvBuf.Length)
                recvBuf = new byte[payloadLen];

            int totalRead = 0;
            while (totalRead < payloadLen)
            {
                int r = await stream.ReadAsync(recvBuf, totalRead, payloadLen - totalRead, token);
                if (r <= 0) break;
                totalRead += r;
            }
            if (totalRead < payloadLen) break;

            // Unmask
            if (masked)
                for (int i = 0; i < payloadLen; i++)
                    recvBuf[i] ^= maskKey[i & 3];

            if (opcode == 2) // binary frame
            {
                int responseLen;
                byte[] responseBuf;

                if (payloadLen == 52)
                {
                    // Sim init
                    responseBuf = ProcessSimInit(recvBuf);
                    responseLen = responseBuf.Length;
                }
                else if (payloadLen == 24)
                {
                    // Sim step
                    responseBuf = ProcessSimStep(recvBuf);
                    responseLen = responseBuf.Length;
                }
                else if (payloadLen == 4)
                {
                    // Sim stop
                    _flightSim = null;
                    Console.WriteLine("Sim stopped");
                    continue;
                }
                else if (payloadLen >= 28)
                {
                    // Aero query (legacy)
                    responseLen = ProcessAeroRequest(recvBuf, sendPayload);
                    responseBuf = sendPayload;
                }
                else continue;

                await SendWebSocketFrame(stream, responseBuf, responseLen, token);
            }
        }
    }

    private static async Task SendWebSocketFrame(NetworkStream stream, byte[] payload, int len, CancellationToken token)
    {
        // Frame header: FIN + binary opcode
        byte[] header;
        if (len < 126)
        {
            header = new byte[] { 0x82, (byte)len };
        }
        else if (len < 65536)
        {
            header = new byte[] { 0x82, 126, (byte)(len >> 8), (byte)(len & 0xFF) };
        }
        else
        {
            header = new byte[10];
            header[0] = 0x82;
            header[1] = 127;
            // Write 8-byte big-endian length
            for (int i = 0; i < 8; i++)
                header[2 + i] = (byte)(len >> (56 - i * 8));
        }

        await stream.WriteAsync(header, token);
        await stream.WriteAsync(payload.AsMemory(0, len), token);
    }

    private byte[] ProcessSimInit(byte[] recvBuf)
    {
        var span = recvBuf.AsSpan();
        float px = BinaryPrimitives.ReadSingleLittleEndian(span);
        float py = BinaryPrimitives.ReadSingleLittleEndian(span[4..]);
        float pz = BinaryPrimitives.ReadSingleLittleEndian(span[8..]);
        float vx = BinaryPrimitives.ReadSingleLittleEndian(span[12..]);
        float vy = BinaryPrimitives.ReadSingleLittleEndian(span[16..]);
        float vz = BinaryPrimitives.ReadSingleLittleEndian(span[20..]);
        float wx = BinaryPrimitives.ReadSingleLittleEndian(span[24..]);
        float wy = BinaryPrimitives.ReadSingleLittleEndian(span[28..]);
        float wz = BinaryPrimitives.ReadSingleLittleEndian(span[32..]);
        float qx = BinaryPrimitives.ReadSingleLittleEndian(span[36..]);
        float qy = BinaryPrimitives.ReadSingleLittleEndian(span[40..]);
        float qz = BinaryPrimitives.ReadSingleLittleEndian(span[44..]);
        float qw = BinaryPrimitives.ReadSingleLittleEndian(span[48..]);

        _flightSim = new FlightSim(_grid, _surface, _dragModel, _atmo, _mass, _blockSize)
        {
            StopAtGround = false,
            StopWhenStopped = false,
        };

        _flightSim.State = new RigidBodyState
        {
            Position = new Vector3(px, py, pz),
            LinearVelocity = new Vector3(vx, vy, vz),
            AngularVelocity = new Vector3(wx, wy, wz),
            Orientation = Quaternion.Normalize(new Quaternion(qx, qy, qz, qw)),
        };

        Console.WriteLine($"Sim init: pos=({px:F0},{py:F0},{pz:F0}) vel=({vx:F1},{vy:F1},{vz:F1}) orient=({qx:F3},{qy:F3},{qz:F3},{qw:F3})");

        // Return initial state + zero aero
        return BuildSimResponse();
    }

    private byte[] ProcessSimStep(byte[] recvBuf)
    {
        if (_flightSim == null)
        {
            // Not initialized — return zeros
            return BuildSimResponse();
        }

        var span = recvBuf.AsSpan();
        float dt = BinaryPrimitives.ReadSingleLittleEndian(span);
        float ctx = BinaryPrimitives.ReadSingleLittleEndian(span[4..]);
        float cty = BinaryPrimitives.ReadSingleLittleEndian(span[8..]);
        float ctz = BinaryPrimitives.ReadSingleLittleEndian(span[12..]);
        float thrustN = BinaryPrimitives.ReadSingleLittleEndian(span[16..]);
        float gravity = BinaryPrimitives.ReadSingleLittleEndian(span[20..]);

        // Set external forces for this step
        _flightSim.ExternalBodyForce = new Vector3(-thrustN, 0, 0); // thrust along body -X (forward)
        _flightSim.ExternalBodyTorque = new Vector3(ctx, cty, ctz);
        _flightSim.Gravity = gravity;

        // RK4 step (computes fresh aero 4× internally)
        dt = System.Math.Clamp(dt, 0.0001f, 0.05f);
        var aeroResult = _flightSim.Step(dt);

        // Throttled debug log
        if (_logTimer.Elapsed.TotalMilliseconds > 500)
        {
            _logTimer.Restart();
            var s = _flightSim.State;
            float speed = s.Speed;
            var atmoState = _atmo.GetState(System.Math.Max(0f, s.Position.Y));
            float mach = atmoState.SpeedOfSound > 0 ? speed / (float)atmoState.SpeedOfSound : 0;

            // AoA from body-frame velocity
            Quaternion invQ = Quaternion.Conjugate(s.Orientation);
            Vector3 localVel = Vector3.Transform(s.LinearVelocity, invQ);
            float localSpeed = localVel.Length();
            float aoaDeg = 0;
            if (localSpeed > 0.1f)
            {
                aoaDeg = MathF.Atan2(localVel.Y, -localVel.X) * (180f / MathF.PI);
            }

            float weight = _mass.TotalMass * 9.81f;
            float lOverD = aeroResult.DragMagnitude > 1 ? aeroResult.LiftMagnitude / aeroResult.DragMagnitude : 0;
            float lOverW = weight > 1 ? aeroResult.LiftMagnitude / weight : 0;
            Console.WriteLine(
                $"t={_flightSim.Time:F1}s |v|={speed:F1} M={mach:F2} AoA={aoaDeg:F1}° " +
                $"ω=({s.AngularVelocity.X:F3},{s.AngularVelocity.Y:F3},{s.AngularVelocity.Z:F3}) " +
                $"alt={s.Position.Y:F0}m " +
                $"D={aeroResult.DragMagnitude:F0} L={aeroResult.LiftMagnitude:F0} " +
                $"L/D={lOverD:F2} L/W={lOverW:F2} W={weight:F0} " +
                $"T=({ctx:F0},{cty:F0},{ctz:F0}) thrust={thrustN:F0}");
        }

        return BuildSimResponse();
    }

    private byte[] BuildSimResponse()
    {
        // 25 header floats + faceCount Cp floats
        int headerFloats = 25;
        var buf = new byte[(headerFloats + _cpBuffer.Length) * 4];
        var outSpan = buf.AsSpan();
        int off = 0;

        if (_flightSim != null)
        {
            var s = _flightSim.State;
            var aero = _flightSim.LastAero;

            // State: pos[3] + linVel[3] + angVel[3] + orient[4] + simTime = 14 floats
            WriteFloat(outSpan, ref off, s.Position.X);
            WriteFloat(outSpan, ref off, s.Position.Y);
            WriteFloat(outSpan, ref off, s.Position.Z);
            WriteFloat(outSpan, ref off, s.LinearVelocity.X);
            WriteFloat(outSpan, ref off, s.LinearVelocity.Y);
            WriteFloat(outSpan, ref off, s.LinearVelocity.Z);
            WriteFloat(outSpan, ref off, s.AngularVelocity.X);
            WriteFloat(outSpan, ref off, s.AngularVelocity.Y);
            WriteFloat(outSpan, ref off, s.AngularVelocity.Z);
            WriteFloat(outSpan, ref off, s.Orientation.X);
            WriteFloat(outSpan, ref off, s.Orientation.Y);
            WriteFloat(outSpan, ref off, s.Orientation.Z);
            WriteFloat(outSpan, ref off, s.Orientation.W);
            WriteFloat(outSpan, ref off, (float)_flightSim.Time);

            // Aero: force[3] + torque[3] + dragMag + liftMag + frontalArea + mach + dynPressure = 11 floats
            WriteFloat(outSpan, ref off, aero.Force.X);
            WriteFloat(outSpan, ref off, aero.Force.Y);
            WriteFloat(outSpan, ref off, aero.Force.Z);
            WriteFloat(outSpan, ref off, aero.Torque.X);
            WriteFloat(outSpan, ref off, aero.Torque.Y);
            WriteFloat(outSpan, ref off, aero.Torque.Z);
            WriteFloat(outSpan, ref off, aero.DragMagnitude);
            WriteFloat(outSpan, ref off, aero.LiftMagnitude);
            WriteFloat(outSpan, ref off, aero.FrontalArea);
            WriteFloat(outSpan, ref off, (float)aero.Mach);
            WriteFloat(outSpan, ref off, (float)aero.DynamicPressure);

            // Compute per-face Cp for visualization
            Quaternion invQ = Quaternion.Conjugate(s.Orientation);
            Vector3 localVel = Vector3.Transform(s.LinearVelocity, invQ);
            var atmoState = _atmo.GetState(System.Math.Max(0f, s.Position.Y));
            var cpCtx = new AeroContext(_grid, _surface, localVel, atmoState,
                _mass.CenterOfMass, _blockSize, angularVelocity: s.AngularVelocity);
            ComputePerFaceCp(cpCtx, _cpBuffer);
        }
        else
        {
            // Zero state
            for (int i = 0; i < headerFloats; i++)
                WriteFloat(outSpan, ref off, 0f);
            Array.Fill(_cpBuffer, 0f);
        }

        for (int i = 0; i < _cpBuffer.Length; i++)
            WriteFloat(outSpan, ref off, _cpBuffer[i]);

        return buf;
    }

    private int ProcessAeroRequest(byte[] recvBuf, byte[] sendBuf)
    {
        var span = recvBuf.AsSpan();
        float vx = BinaryPrimitives.ReadSingleLittleEndian(span);
        float vy = BinaryPrimitives.ReadSingleLittleEndian(span[4..]);
        float vz = BinaryPrimitives.ReadSingleLittleEndian(span[8..]);
        float ox = BinaryPrimitives.ReadSingleLittleEndian(span[12..]);
        float oy = BinaryPrimitives.ReadSingleLittleEndian(span[16..]);
        float oz = BinaryPrimitives.ReadSingleLittleEndian(span[20..]);
        float altM = BinaryPrimitives.ReadSingleLittleEndian(span[24..]);

        var velocity = new Vector3(vx, vy, vz);
        var omega = new Vector3(ox, oy, oz);
        var atmoState = _atmo.GetState(System.Math.Max(0f, altM));

        var ctx = new AeroContext(_grid, _surface, velocity, atmoState,
            _mass.CenterOfMass, _blockSize, angularVelocity: omega);

        var aeroResult = _dragModel.Compute(ctx);
        ComputePerFaceCp(ctx, _cpBuffer);

        // Throttled debug log (~2/sec)
        if (_logTimer.Elapsed.TotalMilliseconds > 500)
        {
            _logTimer.Restart();
            float speed = velocity.Length();
            float mach = atmoState.SpeedOfSound > 0 ? speed / (float)atmoState.SpeedOfSound : 0;
            // AoA: angle between -velocity (airstream) and body -X
            // velocity is body-frame, so airstream = -velocity, forward = -X
            float aoaDeg = 0;
            if (speed > 0.1f)
            {
                var vHat = velocity / speed;
                // body forward is -X; dot(-vHat, -X) = vHat.X
                float cosAoA = -vHat.X; // dot of airstream dir with forward
                float sinAoA = vHat.Y;  // component along body up
                aoaDeg = MathF.Atan2(sinAoA, cosAoA) * (180f / MathF.PI);
            }
            float weight = _mass.TotalMass * 9.81f;
            float lOverD = aeroResult.DragMagnitude > 1 ? aeroResult.LiftMagnitude / aeroResult.DragMagnitude : 0;
            float lOverW = weight > 1 ? aeroResult.LiftMagnitude / weight : 0;
            Console.WriteLine(
                $"v=({vx:F1},{vy:F1},{vz:F1}) |v|={speed:F1} M={mach:F2} AoA={aoaDeg:F1}° " +
                $"ω=({ox:F3},{oy:F3},{oz:F3}) alt={altM:F0}m " +
                $"F=({aeroResult.Force.X:F0},{aeroResult.Force.Y:F0},{aeroResult.Force.Z:F0}) " +
                $"T=({aeroResult.Torque.X:F0},{aeroResult.Torque.Y:F0},{aeroResult.Torque.Z:F0}) " +
                $"D={aeroResult.DragMagnitude:F0} L={aeroResult.LiftMagnitude:F0} " +
                $"L/D={lOverD:F2} L/W={lOverW:F2} W={weight:F0}");
        }

        var outSpan = sendBuf.AsSpan();
        int off = 0;
        WriteFloat(outSpan, ref off, aeroResult.Force.X);
        WriteFloat(outSpan, ref off, aeroResult.Force.Y);
        WriteFloat(outSpan, ref off, aeroResult.Force.Z);
        WriteFloat(outSpan, ref off, aeroResult.Torque.X);
        WriteFloat(outSpan, ref off, aeroResult.Torque.Y);
        WriteFloat(outSpan, ref off, aeroResult.Torque.Z);
        WriteFloat(outSpan, ref off, aeroResult.DragMagnitude);
        WriteFloat(outSpan, ref off, aeroResult.LiftMagnitude);
        WriteFloat(outSpan, ref off, aeroResult.FrontalArea);
        WriteFloat(outSpan, ref off, (float)aeroResult.Mach);
        WriteFloat(outSpan, ref off, (float)aeroResult.DynamicPressure);
        for (int i = 0; i < _cpBuffer.Length; i++)
            WriteFloat(outSpan, ref off, _cpBuffer[i]);

        return off;
    }

    private void ComputePerFaceCp(in AeroContext ctx, float[] cpOut)
    {
        float speed = ctx.Speed;
        if (speed < 0.01f)
        {
            Array.Fill(cpOut, 0f);
            return;
        }

        Vector3 vHat = ctx.Velocity / speed;
        float mach = ctx.Atmosphere.SpeedOfSound > 0
            ? speed / (float)ctx.Atmosphere.SpeedOfSound : 0;

        ReadOnlySpan<float> visFactor = default;
        if (_shadowMap != null)
        {
            _shadowMap.Update(_grid, _surface, ctx.Velocity);
            visFactor = _shadowMap.VisibilityFactor;
        }

        var faces = _surface.Faces;

        float cpMax = 2.0f, cdBluff = 1.1f, cpBase = -0.15f;
        float subLim = 0.6f, supLim = 1.2f;
        if (_dragModel is NewtonianDragModel ndm)
        { cpMax = ndm.CpMax; cdBluff = ndm.CdBluff; cpBase = ndm.CpBase; subLim = ndm.SubsonicLimit; supLim = ndm.SupersonicLimit; }
        else if (_dragModel is DampedShadowedDragModel dsm)
        { cpMax = dsm.CpMax; cdBluff = dsm.CdBluff; cpBase = dsm.CpBase; subLim = dsm.SubsonicLimit; supLim = dsm.SupersonicLimit; }
        else if (_dragModel is ShadowedDragModel sdm)
        { cpMax = sdm.CpMax; cdBluff = sdm.CdBluff; cpBase = sdm.CpBase; subLim = sdm.SubsonicLimit; supLim = sdm.SupersonicLimit; }
        else if (_dragModel is LiftingSurfaceModel lsm)
        {
            if (lsm.InnerModel is NewtonianDragModel n2)
            { cpMax = n2.CpMax; cdBluff = n2.CdBluff; cpBase = n2.CpBase; subLim = n2.SubsonicLimit; supLim = n2.SupersonicLimit; }
            else if (lsm.InnerModel is DampedShadowedDragModel d2)
            { cpMax = d2.CpMax; cdBluff = d2.CdBluff; cpBase = d2.CpBase; subLim = d2.SubsonicLimit; supLim = d2.SupersonicLimit; }
            else if (lsm.InnerModel is ShadowedDragModel s2)
            { cpMax = s2.CpMax; cdBluff = s2.CdBluff; cpBase = s2.CpBase; subLim = s2.SubsonicLimit; supLim = s2.SupersonicLimit; }
        }

        for (int i = 0; i < faces.Count; i++)
        {
            var face = faces[i];
            Vector3 faceVel = ctx.VelocityAtPoint(face.Position);
            float faceSpeed = faceVel.Length();
            if (faceSpeed < 0.01f) { cpOut[i] = 0; continue; }

            Vector3 faceVHat = faceVel / faceSpeed;
            float faceMach = ctx.Atmosphere.SpeedOfSound > 0
                ? faceSpeed / (float)ctx.Atmosphere.SpeedOfSound : 0;
            float cosAlpha = Vector3.Dot(faceVHat, face.Normal);

            if (cosAlpha > 0)
            {
                float cp = ComputeCpValue(cosAlpha, faceMach, cpMax, cdBluff, subLim, supLim);
                if (!visFactor.IsEmpty && i < visFactor.Length)
                {
                    float sf = visFactor[i];
                    cp = cpBase + (cp - cpBase) * sf;
                }
                cpOut[i] = cp;
            }
            else
            {
                cpOut[i] = cpBase;
            }
        }
    }

    private static float ComputeCpValue(float cosAlpha, float mach,
        float cpMax, float cdBluff, float subLim, float supLim)
    {
        if (mach >= supLim) return cpMax * cosAlpha * cosAlpha;
        if (mach <= subLim) return cdBluff * cosAlpha;
        float t = (mach - subLim) / (supLim - subLim);
        t = t * t * (3f - 2f * t);
        return cdBluff * cosAlpha + (cpMax * cosAlpha * cosAlpha - cdBluff * cosAlpha) * t;
    }

    private static void WriteFloat(Span<byte> buf, ref int offset, float value)
    {
        BinaryPrimitives.WriteSingleLittleEndian(buf[offset..], value);
        offset += 4;
    }

    public void Dispose()
    {
        _cts.Cancel();
        try { _listener.Stop(); } catch { }
        _cts.Dispose();
    }
}
