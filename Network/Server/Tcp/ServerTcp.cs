using System;
using System.Net;
using System.Net.Sockets;
using DynTypeSerializer;

namespace DynTypeNetwork;

public static partial class Server
{
    private static TcpListener? _tcpListener;
    
    public static bool IsTcpServerRunning() => _tcpListener != null && _tcpListener.Server.IsBound;

    private static void StartTcp(int port)
    {
        _tcpListener = new TcpListener(IPAddress.Any, port);
        _tcpListener.Start();
        _cts = new CancellationTokenSource();
        _ = AcceptTcpClientsAsync(_cts.Token);
    }

    private static async Task AcceptTcpClientsAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            var tcpClient = await _tcpListener!.AcceptTcpClientAsync(token);
            var client = new Connection { Client = tcpClient.Client };

            ThreadPool.QueueUserWorkItem(async _ =>
            {
                try
                {
                    await HandleTcpClientAsync(client, token);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[SERVER] Client {client.Id} thread exception: {ex}");
                }
            }, null);
        }
    }

    private static async Task HandleTcpClientAsync(Connection client, CancellationToken token)
    {
        bool clientDisconnectSuccess = false;
        try
        {
            while (!token.IsCancellationRequested && client.Connected)
            {
                NetworkMessage? msg = MessageBuilder.ReadTcpMessage(client.GetStream());
                if (msg == null) break;

                switch (msg.MessageType)
                {
                    case MessageType.Handshake:
                        await HandleClientHandshake(client, msg);
                        continue;

                    case MessageType.Response:
                        Responses[msg.MessageId] = msg;
                        continue;

                    case MessageType.ClientDisconnected:
                        clientDisconnectSuccess = true;
                        continue;

                    case MessageType.Custom:
                        if (msg.TargetId == SERVER_ID) _ = Task.Run(() => OnTcpMessageReceived?.Invoke(msg));
                        if (msg.TargetId == SERVER_ID) {
                            await MessageBuilder.HandleCustomMessage(client.GetStream(), msg, token);
                        } else {
                            _ = msg.TargetId == 0 ? BroadcastTcp(client, msg) : ForwardTcpMessageToTarget(client, msg);
                        }
                        continue;

                    default:
                        Console.WriteLine($"[SERVER] Unknown message type from client {client.Id}: {msg.MessageType}");
                        continue;
                }


            }
        } catch (Exception) {}

        await ClientDisconnected(client, clientDisconnectSuccess);
    }

    
    


    private static async Task SendMessageAsync(Connection client, int targetId, MessageType type, object? data)
    {
        NetworkMessage message = new()
        {
            SenderId = 1,
            TargetId = targetId,
            MessageType = type
        };
        var packet = MessageBuilder.CreatePacket(message, data);

        await client.GetStream().WriteAsync(packet);
    }


    private static async Task BroadcastTcp(Connection sender, NetworkMessage message)
    {
        var tasks = new List<Task<object?>>();

        // TODO FIX: This still sends the message back to sender?
        foreach (var client in Clients.Values.Where(c => c.Connected && c.Id != sender.Id))
        {
            // If MessageId == 0, we treat it as a fire-and-forget broadcast, where we don't expect any response from the clients. We just send the message to all clients and return immediately.
            if (message.MessageId == 0) {
                _ = Task.Run(async () => {
                    var requestMessage = new NetworkMessage {
                        SenderId = sender.Id,
                        TargetId = client.Id,
                        MessageType = message.MessageType,
                        MessageId = message.MessageId,
                        Payload = message.Payload
                    };

                    var data = MessageBuilder.CreateTcpMessage(requestMessage);
                    await client.GetStream().WriteAsync(data);
                });
            
                continue;
            }
            
            // If MessageId > 0, we expect a response from each client, which we will aggregate and send back to the sender once all responses are received or timeout occurs.

            Console.WriteLine($"[NETWORK] Broadcasting message {message.MessageId} from {message.SenderId} to client {client.Id} and waiting for response");

            tasks.Add(Task.Run(async () =>
            {
                ushort requestId = MessageBuilder.GenerateRequestId(ref _requestId);
                Requests.Add(requestId);

                var requestMessage = new NetworkMessage
                {
                    SenderId = message.SenderId,
                    TargetId = client.Id,
                    MessageType = message.MessageType,
                    MessageId = requestId,
                    Payload = message.Payload
                };

                var data = MessageBuilder.CreateTcpMessage(requestMessage);
                await client.GetStream().WriteAsync(data);

                NetworkMessage? returnMessage = await WaitWithTimeout(requestId);

                Console.WriteLine($"[NETWORK] Received response for broadcast message {message.MessageId} from client {client.Id}");
                if (returnMessage == null || returnMessage.Payload == null) return null;

                return MessageBuilder.UnpackPayload<object>(returnMessage.Payload);
            }));
        }

        object?[] broadcastResponses = [];

        if (tasks.Count > 0)
        {
            try
            {
                using var cts = new CancellationTokenSource(TIMEOUT_MS);

                var results = await Task.WhenAll(tasks).WaitAsync(cts.Token);

                broadcastResponses = results.Where(r => r != null).ToArray();
            }
            catch (OperationCanceledException)
            {
                throw new TimeoutException("Broadcast timed out.");
            }
        }

        NetworkMessage broadcastResponse = new()
        {
            SenderId = SERVER_ID,
            TargetId = message.SenderId,
            MessageId = message.MessageId,
            MessageType = MessageType.ResponseBroadcast
        };

        var broadcastResponseData = MessageBuilder.CreatePacket(broadcastResponse, broadcastResponses);

        await sender.GetStream().WriteAsync(broadcastResponseData);
    }


    /// <summary>
    /// Placeholder method to forward a message to the correct target.
    /// Implementation should locate the target client by ID and send the message.
    /// </summary>
    private static async Task ForwardTcpMessageToTarget(Connection sender, NetworkMessage message)
    {
        Console.WriteLine($"[NETWORK] Forwarding message {message.MessageId} from {message.SenderId} to {message.TargetId}");


        Connection? target = Clients[message.TargetId];
        if (target == null) {
            // TODO send error response back to sender, if needed
            return;
        }
        
        // Request data from target, and send response back to sender (if MessageId > 0)
        MethodRequest? request = MessageBuilder.UnpackPayload<MethodRequest>(message.Payload);
        if (request == null || string.IsNullOrEmpty(request.MethodName)) {
            // TODO send error response back to sender, if needed
            return;
        }

        object? result = await RequestDataAsync<object>(target.Id, request.MethodName!, request.Args);

        bool maskSender = false; // TODO set as a setting
        NetworkMessage response = new()
        {
            SenderId = maskSender ? SERVER_ID : message.SenderId,
            TargetId = message.SenderId,
            MessageId = message.MessageId,
            MessageType = MessageType.Handshake
        };
        var packet = MessageBuilder.CreatePacket(response, result);

        await sender.GetStream().WriteAsync(packet);
    }
}



