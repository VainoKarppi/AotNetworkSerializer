using System;
using System.Net;
using System.Net.Sockets;
using DynTypeSerializer;

namespace DynTypeNetwork;

public static partial class Server
{
    private static UdpClient? _udpListener;

    public static bool IsUdpServerRunning() =>
        _udpListener != null && _udpListener.Client.IsBound;
    

    // ── Start UDP server ──────────────────────
    private static void StartUdp(int port)
    {
        _udpListener = new UdpClient(port);
        _cts = new CancellationTokenSource();
        StartUdpServerReceiveLoop(port, _cts.Token);
        Console.WriteLine("[SERVER] UDP Server started");
    }

    private static void StartUdpServerReceiveLoop(int port, CancellationToken token)
    {
        _cts ??= new CancellationTokenSource();

        _ = Task.Run(async () =>
        {
            while (!token.IsCancellationRequested)
            {
                try
                {
                    // --- Ensure listener exists ---
                    if (_udpListener == null || !_udpListener.Client.IsBound)
                    {
                        Console.WriteLine("[SERVER UDP] Listener missing or not bound. Recreating...");

                        try
                        {
                            _udpListener?.Dispose();
                        }
                        catch { /* ignore */ }

                        _udpListener = new UdpClient(port); // <-- use your port

                        Console.WriteLine("[SERVER UDP] Listener recreated.");
                    }

                    // --- Receive ---
                    var result = await _udpListener.ReceiveAsync(token);

                    NetworkMessage msg = MessageBuilder.ReadUdpMessage(result.Buffer, includeData: true);

                    // --- Resolve sender ---
                    if (!Clients.TryGetValue(msg.SenderId, out var senderClient)) continue;

                    // --- Handle registration ---
                    if (msg.MessageType == MessageType.UdpRegister)
                    {
                        if (senderClient.UdpEndpoint == null || !senderClient.UdpEndpoint.Equals(result.RemoteEndPoint))
                        {
                            senderClient.UdpEndpoint = result.RemoteEndPoint;
                            Console.WriteLine($"[SERVER UDP] Registered UDP endpoint for client {senderClient.Id}: {senderClient.UdpEndpoint}");
                        }
                        continue;
                    }

                    // --- Validate message type ---
                    if (msg.MessageType != MessageType.Custom) continue;

                    // --- Forward to target ---
                    if (msg.TargetId == SERVER_ID)
                    {
                        // --- Handle server-bound message ---
                        Console.WriteLine($"[SERVER UDP] Received message from {result.RemoteEndPoint}: Type={msg.MessageType}, SenderId={msg.SenderId}, TargetId={msg.TargetId}, Payload={msg.Payload}");

                        _ = Task.Run(() => OnUdpMessageReceived?.Invoke(msg));

                        _ = Task.Run(() =>
                        {
                            try
                            {
                                MethodRequest? request = MessageBuilder.UnpackPayload<MethodRequest>(msg.Payload);
                                if (request == null) throw new Exception("Unable to unpack payload");

                                MethodBuilder.CallServerMethod<object>(request.MethodName!, msg, request.Args!);
                            }
                            catch (Exception ex)
                            {
                                Console.WriteLine($"[SERVER UDP] Method execution failed: {ex}");
                            }
                        }, token);
                    }

                    if (msg.TargetId > 0) {
                        if (!Clients.TryGetValue(msg.TargetId, out var targetClient) || targetClient.UdpEndpoint == null) {
                            Console.WriteLine($"[SERVER UDP] Cannot forward, target {msg.TargetId} not available.");
                            continue;
                        }

                        // TODO set maskSender as option
                        bool maskSender = false;
                        if (maskSender) msg.SenderId = SERVER_ID;

                        // TODO recalculate crc32 if payload is modified (maskSender)

                        Console.WriteLine($"[SERVER UDP] Forwarding message from {msg.SenderId} to {msg.TargetId} via UDP.");

                        await _udpListener.SendAsync(result.Buffer.AsMemory(), targetClient.UdpEndpoint, token);
                        continue;
                    }

                    // --- Broadcast to all clients ---
                    Console.WriteLine($"[SERVER UDP] Broadcasting message from {msg.SenderId} to all clients via UDP.");
                    await BroadcastUdp(senderClient, msg);
                    
                }
                catch (OperationCanceledException)
                {
                    // ✅ ONLY exit condition
                    break;
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[SERVER UDP] Receive loop error: {ex.Message}");
                }

                // --- Always retry ---
                try
                {
                    await Task.Delay(1000, token);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }

            Console.WriteLine("[SERVER UDP] Receive loop stopped.");
        }, token);
    }



    private static async Task BroadcastUdp(Connection sender, NetworkMessage message)
    {
        foreach (var client in Clients.Values)
        {
            if (!client.Connected || client.Id == sender.Id || client.UdpEndpoint == null)
                continue;

            Console.WriteLine($"[NETWORK] Broadcasting UDP message from {message.SenderId} to client {client.Id}");

            NetworkMessage udpMsg = new()
            {
                SenderId = message.SenderId,
                TargetId = client.Id,
                MessageType = MessageType.Custom,
                Payload = message.Payload
            };

            var packet = MessageBuilder.CreateUdpMessage(udpMsg);

            // Fire-and-forget is fine for UDP
            _ = _udpListener!.SendAsync(packet.AsMemory(), client.UdpEndpoint);
        }
    }
}