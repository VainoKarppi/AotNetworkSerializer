using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using DynTypeSerializer;

namespace DynTypeNetwork;

public static partial class Server
{
    public const int SERVER_ID = 1;
    public  static string CustomHash { get; set; } = "";
    private static int _clientIdCounter = 1;

    public class Connection : TcpClient
    {
        internal int Id { get; set; } = Interlocked.Increment(ref _clientIdCounter);
        internal bool HandshakeDone { get; set; } = false;
        internal IPEndPoint? UdpEndpoint { get; set; }
    }

    public readonly static Dictionary<int, Connection> Clients = [];

    public static List<int> GetClients() {
        if (!IsTcpServerRunning()) throw new Exception("Server is not running");
        return Clients.Keys.ToList();
    }

    
    private static CancellationTokenSource? _cts;


    public static bool IsRunning() => IsTcpServerRunning() || IsUdpServerRunning();

    // ── Start TCP server ──────────────────────
    public static async Task StartAsync(int port, bool startUdp = false, string? customHash = null)
    {
        CustomHash = customHash ?? "";
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
        KeyExchange.RemoveServerKeyExchange(client.Id);
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
        NetworkMessage response = new() {
            SenderId = SERVER_ID,
            TargetId = client.Id,
            MessageId = message.MessageId,
            MessageType = MessageType.Handshake
        };

        try {
            HandshakeMessage? payload = MessageBuilder.UnpackPayload<HandshakeMessage>(message.Payload);

            if (payload == null) {
                Console.WriteLine($"[SERVER] Invalid handshake from client {client.Id}");
                client.Close();
                return;
            }

            if (string.IsNullOrEmpty(payload.ClientPublicKey)) {
                Console.WriteLine($"[SERVER] Missing client public key for handshake from {client.Id}");
                client.Close();
                return;
            }


            string buildId = Assembly.GetExecutingAssembly().ManifestModule.ModuleVersionId.ToString();

            var parts = payload.Hash.Split('|');

            if (parts.Length != 3) throw new Exception($"Invalid handshake format: {payload.Hash}");

            string clientBuild = parts[0];
            string clientCustomHash = parts[1];
            string clientMethodsHash = parts[2];

            if (!string.IsNullOrEmpty(CustomHash)) {
                if (string.IsNullOrEmpty(clientCustomHash)) throw new Exception($"Client custom hash is empty, but server requires custom hash"); 
                if (!CustomHash.Equals(clientCustomHash, StringComparison.OrdinalIgnoreCase)) throw new Exception($"Client custom hash mismatch");
            }
    
            if (buildId != clientBuild) throw new Exception($"Client build ID mismatch. Server: {buildId}, Client: {clientBuild}");

            // Register client methods from handshake, if not already registered (eg. from previous client handshakes)
            if (MethodBuilder.GetAvailableClientMethods().Length == 0) {
                MethodBuilder.RegisterFromHandshake(payload.AvailableMethods, isServer: true);
            } else {
                if (!MethodBuilder.ComputeMethodsHash(MethodBuilder.GetAvailableClientMethods()) .Equals(clientMethodsHash, StringComparison.OrdinalIgnoreCase)) {
                    throw new Exception($"Client methods hash mismatch. Server: {MethodBuilder.ComputeMethodsHash(MethodBuilder.GetAvailableClientMethods())}, Client: {clientMethodsHash}");
                }
            }

            KeyExchange.InitializeServerKeyExchange(client.Id, payload.ClientPublicKey);

            Clients.Add(client.Id, client);
            client.HandshakeDone = true;

            OnClientConnected?.Invoke(client.Id);

            // Notify other clients that client was connected
            foreach (var otherClient in Clients.Values) {
                if (!otherClient.Connected || otherClient.Id == client.Id || !otherClient.HandshakeDone) continue;
                await SendMessageAsync(otherClient, otherClient.Id, MessageType.ClientConnected, client.Id);
            }

            HandshakeMessage handshakeResponse = new() {
                Success = true,
                Message = "SUCCESS",
                ClientId = client.Id,
                OtherConnectedClients = Clients.Keys.Where(id => id != client.Id).ToList(),
                AvailableMethods = MethodBuilder.GetAvailableServerMethods(),
                ServerPublicKey = KeyExchange.GetServerPublicKey(client.Id)
            };

            
            var handshakeResult = MessageBuilder.CreatePacket(response, handshakeResponse);

            await client.GetStream().WriteAsync(handshakeResult);
        } catch (Exception ex) {

            // Send handshake failure response to client, before closing connection and removing from clients list
            HandshakeMessage handshakeResponse = new() {
                Success = false,
                Message = ex.Message
            };

            var handshakeResult = MessageBuilder.CreatePacket(response, handshakeResponse);

            await client.GetStream().WriteAsync(handshakeResult);

            KeyExchange.RemoveServerKeyExchange(client.Id);
            Clients.Remove(client.Id);

            if (client.HandshakeDone) {
                OnClientDisconnected?.Invoke(client.Id, false);
            }
            Console.WriteLine($"[SERVER] Handshake failed for client {client.Id}: {ex.Message}");

            Thread.Sleep(100); // Give client some time to receive handshake failure message before closing connection
            client.Close();
        }
    }
}
