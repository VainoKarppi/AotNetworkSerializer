using System;
using System.Net;
using System.Net.Sockets;

namespace DynTypeNetwork;

public static partial class Server
{
    public const int SERVER_ID = 1;
    private static int _clientIdCounter = 1;

    public class Connection : TcpClient
    {
        public int Id { get; set; } = Interlocked.Increment(ref _clientIdCounter);
        public bool HandshakeDone { get; set; } = false;
        public Dictionary<string, object?> CustomData { get; set; } = [];
    }

    public readonly static Dictionary<int, Connection> Clients = [];

    private static TcpListener? _tcpListener;
    private static UdpClient? _udpListener;
    private static CancellationTokenSource? _cts;

    public static event Action<NetworkMessage>? MessageReceived;

    // ── Start TCP server ──────────────────────
    public static void StartTcp(int port)
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
            var client = new Connection {
                Client = (await _tcpListener!.AcceptTcpClientAsync(token)).Client
            };
            
            _ = HandleTcpClientAsync(client, token);
        }
    }

    private static async Task HandleTcpClientAsync(Connection client, CancellationToken token)
    {
        try
        {
            while (!token.IsCancellationRequested && client.Connected)
            {
                NetworkMessage? msg = MessageBuilder.ReadStreamMessage(client.GetStream());
                if (msg == null) break;

                if (msg.MessageType == MessageType.Handshake) {
                    await HandleClientHandshake(client, msg);
                    continue;
                }

                if (msg.MessageType == MessageType.Response) {
                    Responses[msg.MessageId] = msg;
                    continue;
                }

                if (msg.MessageType == MessageType.Custom) {
                    if (msg.TargetId == SERVER_ID) {
                       await MessageBuilder.HandleCustomMessage(client.GetStream(), msg, token); 
                    } else {
                        _ = Task.Run(() => ForwardMessageToTarget(client.GetStream(), msg), token);
                    }
                    
                    continue;
                }

                MessageReceived?.Invoke(msg);
            }
        }
        catch (OperationCanceledException) {
            Console.WriteLine($"[SERVER] Client disconnected (success)");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[SERVER] Client error: {ex}");
        }

        Clients.Remove(client.Id);
    }

    
    
    // ── Start UDP server ──────────────────────
    public static void StartUdp(int port)
    {
        _udpListener = new UdpClient(port);
        _cts = new CancellationTokenSource();
        _ = ReceiveUdpAsync(_cts.Token);
    }

    private static async Task ReceiveUdpAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            var result = await _udpListener!.ReceiveAsync(token);
            var msg = MessageBuilder.ReadMessage(result.Buffer, includeData: true);
            MessageReceived?.Invoke(msg);
        }
    }

    // ── Stop server ───────────────────────────
    public static void Stop()
    {
        Clients.Clear();

        _cts?.Cancel();
        _tcpListener?.Stop();
        _udpListener?.Close();
        _tcpListener = null;
        _udpListener = null;
    }


    // TODO MOVE TO SHARED
    private static async Task SendResponseMessage<T>(Connection client, ushort messageId, T? data)
    {
        NetworkMessage message = new()
        {
            SenderId = 1,
            TargetId = client.Id,
            MessageId = messageId,
            MessageType = MessageType.Response
        };
        var packet = MessageBuilder.CreateMessage(message, data);

        await client.GetStream().WriteAsync(packet);
    }


    private static async Task HandleClientHandshake(Connection client, NetworkMessage message)
    {
        // TODO valida hash etc, and calulate real client id.
        HandshakeMessage handshake = new() {
            Success = true,
            Message = "SUCCESS",
            ClientId = client.Id
        };

        Clients.Add(client.Id, client);

        await SendResponseMessage(client, message.MessageId, handshake);
    }


    /// <summary>
    /// Placeholder method to forward a message to the correct target.
    /// Implementation should locate the target client by ID and send the message.
    /// </summary>
    private static void ForwardMessageToTarget(NetworkStream stream, NetworkMessage msg)
    {
        Console.WriteLine($"[NETWORK] Forwarding message {msg.MessageId} from {msg.SenderId} to {msg.TargetId}");

        // TODO set as a setting
        bool maskSender = false;
        if (maskSender) msg.SenderId = SERVER_ID;


        

        // Example logic:
        // 1. Find the client connection by msg.TargetId
        // 2. Serialize the message
        // 3. Send over TCP stream
        // TODO: implement actual forwarding
    }
}



