using System;
using System.Collections.Concurrent;
using System.Linq.Expressions;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using DynTypeSerializer;

namespace DynTypeNetwork;






public static partial class Client
{
    private static UdpClient? _udpClient;
    private static IPEndPoint? _udpEndpoint;
    public static bool IsUdpConnected() => _udpClient != null && _udpEndpoint != null;




    // ── Connect UDP ──────────────────────────
    private static async Task ConnectUdp(string host, int port)
    {
        _cts = new CancellationTokenSource();
        _udpEndpoint = new IPEndPoint(IPAddress.Parse(host), port);

        // Bind to local port 0 to let OS choose, or specify a fixed local port if needed
        _udpClient = new UdpClient(0);

        _ = Task.Run(() => StartUdpReceiveLoop(_udpClient, _cts.Token, port), _cts.Token);

        NetworkMessage registerMsg = new()
        {
            SenderId = ClientID,
            TargetId = Server.SERVER_ID,
            MessageType = MessageType.UdpRegister
        };

        var packet = MessageBuilder.CreateUdpMessage(registerMsg);
        await _udpClient.SendAsync(packet.AsMemory(), _udpEndpoint, _cts.Token);

        Console.WriteLine("[CLIENT] UDP connection established");
    }

    private static async Task StartUdpReceiveLoop(UdpClient client, CancellationToken token, int port)
    {
        while (!token.IsCancellationRequested)
        {
            try
            {
                var result = await client.ReceiveAsync(token);

                NetworkMessage msg;
                try
                {
                    msg = MessageBuilder.ReadUdpMessage(result.Buffer, includeData: true);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[CLIENT UDP] Invalid packet: {ex.Message}");
                    continue; // ignore bad packets
                }

                if (msg.MessageType != MessageType.Custom || msg.TargetId != ClientID)
                    continue;

                _ = Task.Run(() => OnUdpMessageReceived?.Invoke(msg), token);

                _ = Task.Run(() =>
                {
                    try
                    {
                        var request = MessageBuilder.UnpackPayload<MethodRequest>(msg.Payload);
                        if (request == null) throw new Exception("Unable to unpack payload");

                        MethodBuilder.CallClientMethod<object>(request.MethodName!, msg, request.Args!);
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"[CLIENT UDP] Method execution failed: {ex}");
                    }
                }, token);
            }
            catch (OperationCanceledException)
            {
                // Expected on shutdown → DO NOT restart
                break;
            }
            catch (SocketException ex) when (ex.SocketErrorCode == SocketError.InvalidArgument)
            {
                Console.WriteLine("[CLIENT UDP] Socket invalid, recreating socket in 1s...");
                client.Dispose();

                try
                {
                    await Task.Delay(1000, token);
                    client = new UdpClient(port); // rebind to the same local port
                    _udpClient = client; // update global reference
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[CLIENT UDP] Receive loop crashed: {ex}");
                await Task.Delay(1000, token);
            }
        }

        client?.Dispose();
        Console.WriteLine("[CLIENT UDP] Receive loop stopped.");
    }
    
}