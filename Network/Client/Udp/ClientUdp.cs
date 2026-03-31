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
        _udpClient = new UdpClient();
        _udpEndpoint = new IPEndPoint(IPAddress.Parse(host), port);
        _cts = new CancellationTokenSource();

        _ = Task.Run(() => StartUdpReceiveLoop(_udpClient, _cts.Token), _cts.Token);

        NetworkMessage registerMsg = new() {
            SenderId = ClientID,
            TargetId = Server.SERVER_ID,
            MessageType = MessageType.UdpRegister
        };

        var packet = MessageBuilder.CreateUdpMessage(registerMsg);
        
        await _udpClient.SendAsync(packet.AsMemory(), _udpEndpoint, _cts.Token);
    }



    private static async Task StartUdpReceiveLoop(UdpClient client, CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try
            {
                while (!token.IsCancellationRequested)
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
            }
            catch (OperationCanceledException)
            {
                // Expected on shutdown → DO NOT restart
                break;
            }
            catch (Exception ex)
            {
                if (token.IsCancellationRequested)
                    break;

                Console.WriteLine($"[CLIENT UDP] Receive loop crashed: {ex.Message}");
                Console.WriteLine("[CLIENT UDP] Recreating socket in 1s...");

                try
                {
                    await Task.Delay(1000, token);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }

        client?.Dispose();
        Console.WriteLine("[CLIENT UDP] Receive loop stopped.");
    }
    
}