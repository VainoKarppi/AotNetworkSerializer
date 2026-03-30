using System;
using System.Net;
using System.Net.Sockets;
using DynTypeSerializer;

namespace DynTypeNetwork;

public static partial class Server
{
    public const int SERVER_ID = 1;
    private static int _clientIdCounter = 1;

    public class Connection : TcpClient
    {
        public int Id { get; set; } = Interlocked.Increment(ref _clientIdCounter);
        public bool HandshakeDone { get; set; } = false;
    }

    public readonly static Dictionary<int, Connection> Clients = [];

    private static TcpListener? _tcpListener;
    private static UdpClient? _udpListener;
    private static CancellationTokenSource? _cts;

    public static event Action<NetworkMessage>? MessageReceived;

    public static bool IsTcpServerRunning() =>
        _tcpListener != null && _tcpListener.Server.IsBound;

    public static bool IsUdpServerRunning() =>
        _udpListener != null && _udpListener.Client.IsBound;
    
    public static bool IsRunning() => IsTcpServerRunning() || IsUdpServerRunning();

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
        Console.WriteLine($"[SERVER] Client connected: {client.Id}");
        bool clientDisconnectSuccess = true;
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
        catch (Exception)
        {
            Console.WriteLine($"[SERVER] Client disconnected (failed)");
            clientDisconnectSuccess = false;
        }

        Clients.Remove(client.Id);

        await OnClientDisconnected(client, clientDisconnectSuccess);
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

    public static async Task OnClientDisconnected(Connection client, bool success) {
        Console.WriteLine($"[SERVER] Client {client.Id} disconnected.");
        Clients.Remove(client.Id);

        foreach (var otherClient in Clients.Values) {
            await SendMessageAsync(otherClient, otherClient.Id, MessageType.ClientDisconnected, new object[] { client.Id, success });
        }
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

    public static async Task SendMessageAsync(Connection client, int targetId, MessageType type, object? data)
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


    private static async Task HandleClientHandshake(Connection client, NetworkMessage message)
    {
        Console.WriteLine($"[SERVER] Handling handshake from client {client.Id}");
        HandshakeMessage? payload = MessageBuilder.UnpackPayload<HandshakeMessage>(message.Payload);
        if (payload == null) {
            Console.WriteLine($"[SERVER] Invalid handshake from client {client.Id}");
            client.Close();
            return;
        }

        // TODO validate hash etc

        // Register client methods from handshake, if not already registered (eg. from previous client handshakes)
        if (MethodBuilder.GetAvailableClientMethods().Length == 0) {
            MethodBuilder.RegisterFromHandshake(payload.AvailableMethods, isServer: true);
        }

        Console.WriteLine("\n=== Client Methods ===");
        foreach (var method in MethodBuilder.GetAvailableClientMethods())
        {
            string parameters = string.Join(", ", method.Parameters.Select(p => $"{p.Type.Name} {p.Name}"));
            string returnType = method.ReturnType?.Name ?? "void";
            Console.WriteLine($"{method.Name}({parameters}) : {returnType}");
        }
        Console.WriteLine();


        Clients.Add(client.Id, client);

        HandshakeMessage handshake = new() {
            Success = true,
            Message = "SUCCESS",
            ClientId = client.Id,
            OtherConnectedClients = Clients.Keys.Where(id => id != client.Id).ToList(),
            AvailableMethods = MethodBuilder.GetAvailableServerMethods()
        };

        await SendResponseMessage(client, message.MessageId, handshake);

        // TODO notify other clients that client was connected
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



