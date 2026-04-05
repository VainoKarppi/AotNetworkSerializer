using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using DynTypeSerializer;

namespace DynTypeNetwork;

public static partial class Server
{
    public const int SERVER_ID = 1;
    private static int _clientIdCounter = 1;

    private class Connection : TcpClient
    {
        internal int Id { get; set; } = Interlocked.Increment(ref _clientIdCounter);
        internal bool HandshakeDone { get; set; } = false;
        internal IPEndPoint? UdpEndpoint { get; set; }
    }

    private readonly static Dictionary<int, Connection> Clients = [];

    public static List<int> GetClients() {
        if (!IsTcpServerRunning()) throw new Exception("Server is not running");
        return Clients.Keys.ToList();
    }

    
    private static CancellationTokenSource? _cts;


    public static bool IsRunning() => IsTcpServerRunning() || IsUdpServerRunning();

    // ── Start TCP server ──────────────────────
    public static void Start(int port, bool startUdp = false)
    {
        StartTcp(port);
        if (startUdp) StartUdp(port);
    }

    
    
    

    // ── Stop server ───────────────────────────
    public static async Task StopAsync()
    {
        OnServerShutdown?.Invoke();

        // Send disconnect message to clients, before clearing list and closing connections
        foreach (Connection? client in Clients.Values) {
            if (client == null || !client.Connected) continue;
            
            await SendMessageAsync(client, client.Id, MessageType.ServerShutdown, null);
        }

        _cts?.Cancel();

        Clients.Clear();

        _tcpListener?.Stop();
        _udpListener?.Close();
        _tcpListener = null;
        _udpListener = null;
    }

    private static async Task ClientDisconnected(Connection client, bool success) {
        Clients.Remove(client.Id);
        if (!client.HandshakeDone) return;

        OnClientDisconnected?.Invoke(client.Id, success);

        Console.WriteLine($"[SERVER] Client {client.Id} disconnected. Success: {success}");

        foreach (var otherClient in Clients.Values) {
            await SendMessageAsync(otherClient, otherClient.Id, MessageType.ClientDisconnected, new object[] { client.Id, success });
        }
    }




    private static async Task HandleClientHandshake(Connection client, NetworkMessage message)
    {
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

        HandshakeMessage handshake = new() {
            Success = true,
            Message = "SUCCESS",
            ClientId = client.Id,
            OtherConnectedClients = Clients.Keys.Where(id => id != client.Id).ToList(),
            AvailableMethods = MethodBuilder.GetAvailableServerMethods()
        };

        Clients.Add(client.Id, client);
        client.HandshakeDone = true;

        OnClientConnected?.Invoke(client.Id);

        // Notify other clients that client was connected
        foreach (var otherClient in Clients.Values) {
            if (!otherClient.Connected || otherClient.Id == client.Id || !otherClient.HandshakeDone) continue;
            await SendMessageAsync(otherClient, otherClient.Id, MessageType.ClientConnected, client.Id);
        }

        Console.WriteLine($"[SERVER] Client connected: {client.Id}");

        NetworkMessage response = new()
        {
            SenderId = SERVER_ID,
            TargetId = client.Id,
            MessageId = message.MessageId,
            MessageType = MessageType.Handshake
        };
        var handshakeResult = MessageBuilder.CreatePacket(response, handshake);

        await client.GetStream().WriteAsync(handshakeResult);
    }
}
