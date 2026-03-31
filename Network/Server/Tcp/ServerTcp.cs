using System;
using System.Net;
using System.Net.Sockets;
using DynTypeSerializer;

namespace DynTypeNetwork;

public static partial class Server
{

  


    private static TcpListener? _tcpListener;
    


    public static bool IsTcpServerRunning() =>
        _tcpListener != null && _tcpListener.Server.IsBound;

    
    

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

                if (msg.MessageType == MessageType.Handshake) {
                    await HandleClientHandshake(client, msg);
                    continue;
                }

                if (msg.MessageType == MessageType.Response) {
                    Responses[msg.MessageId] = msg;
                    continue;
                }

                if (msg.MessageType == MessageType.ClientDisconnected) {
                    clientDisconnectSuccess = true;
                    continue;
                }

                if (msg.MessageType == MessageType.Custom) {
                    if (msg.TargetId == SERVER_ID) {
                       await MessageBuilder.HandleCustomMessage(client.GetStream(), msg, token); 
                    } else {
                        await ForwardMessageToTarget(client, msg);
                    }
                    
                    continue;
                }

                OnTcpMessageReceived?.Invoke(msg);
            }
        }
        catch (Exception)
        {
        }

        Clients.Remove(client.Id);

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
        var packet = MessageBuilder.CreateMessage(message, data);

        await client.GetStream().WriteAsync(packet);
    }



    /// <summary>
    /// Placeholder method to forward a message to the correct target.
    /// Implementation should locate the target client by ID and send the message.
    /// </summary>
    private static async Task ForwardMessageToTarget(Connection sender, NetworkMessage message)
    {
        Console.WriteLine($"[NETWORK] Forwarding message {message.MessageId} from {message.SenderId} to {message.TargetId}");

        // TODO set as a setting
        bool maskSender = false;
        if (maskSender) message.SenderId = SERVER_ID;

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

        NetworkMessage response = new()
        {
            SenderId = SERVER_ID,
            TargetId = message.SenderId,
            MessageId = message.MessageId,
            MessageType = MessageType.Handshake
        };
        var packet = MessageBuilder.CreateMessage(response, result);

        await sender.GetStream().WriteAsync(packet);
    }
}



