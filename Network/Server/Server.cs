using System;
using System.Net;
using System.Net.Sockets;
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
        // TODO create copy of list to avoid concurrency issues, or return IReadOnlyDictionary?
        // TODO throw error if not connected to any clients?
        return Clients.Keys.ToList();
    }

    private static TcpListener? _tcpListener;
    private static UdpClient? _udpListener;
    private static CancellationTokenSource? _cts;


    public static bool IsTcpServerRunning() =>
        _tcpListener != null && _tcpListener.Server.IsBound;

    public static bool IsUdpServerRunning() =>
        _udpListener != null && _udpListener.Client.IsBound;
    
    public static bool IsRunning() => IsTcpServerRunning() || IsUdpServerRunning();

    // ── Start TCP server ──────────────────────
    public static void Start(int port, bool startUdp = false)
    {
        StartTcp(port);
        if (startUdp) StartUdp(port);
    }
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

    
    
    // ── Start UDP server ──────────────────────
    private static void StartUdp(int port)
    {
        _udpListener = new UdpClient(port);
        _cts = new CancellationTokenSource();
        StartUdpServerReceiveLoop(_cts.Token);
        Console.WriteLine("[SERVER] UDP Server started");
    }

    private static void StartUdpServerReceiveLoop(CancellationToken token)
    {
        _cts ??= new CancellationTokenSource();

        _ = Task.Run(async () =>
        {
            while (!_cts.Token.IsCancellationRequested)
            {
                try
                {
                    while (!_cts.Token.IsCancellationRequested)
                    {
                        var result = await _udpListener!.ReceiveAsync(_cts.Token);
                        NetworkMessage msg = MessageBuilder.ReadMessage(result.Buffer, includeData: true);

                        if (msg.MessageType == MessageType.UdpRegister) {
                            // Bind UDP endpoint to client
                            if (msg.SenderId != 0 && Clients.TryGetValue(msg.SenderId, out var client))
                            {
                                // Only update if not set OR endpoint changed (NAT rebinding)
                                if (client.UdpEndpoint == null || !client.UdpEndpoint.Equals(result.RemoteEndPoint))
                                {
                                    client.UdpEndpoint = result.RemoteEndPoint;
                                    Console.WriteLine($"[SERVER UDP] Registered UDP endpoint for client {client.Id}: {client.UdpEndpoint}");
                                }
                            }
                            continue;
                        }

                        // Send event on thread pool to avoid blocking receive loop
                        _ = Task.Run(() => OnUdpMessageReceived?.Invoke(msg));
                    }
                }
                catch (OperationCanceledException)
                {
                    // Normal shutdown, exit loop
                    break;
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[SERVER UDP] Receive loop failed: {ex.Message}. Restarting in 1s...");

                    // Small delay before retry
                    await Task.Delay(1000);

                    // Attempt to continue receiving if listener is still open
                    if (_udpListener == null || !_udpListener.Client.IsBound)
                    {
                        Console.WriteLine("[SERVER UDP] Listener closed, cannot restart UDP receive loop.");
                        break;
                    }
                    else
                    {
                        Console.WriteLine("[SERVER UDP] Restarting UDP receive loop...");
                        continue; // re-enter inner while
                    }
                }
            }
        }, token);
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

        Clients.Clear();

        _cts?.Cancel();
        _tcpListener?.Stop();
        _udpListener?.Close();
        _tcpListener = null;
        _udpListener = null;
    }

    private static async Task ClientDisconnected(Connection client, bool success) {
        Console.WriteLine($"[SERVER] Client {client.Id} disconnected. Success: {success}");
        Clients.Remove(client.Id);

        foreach (var otherClient in Clients.Values) {
            await SendMessageAsync(otherClient, otherClient.Id, MessageType.ClientDisconnected, new object[] { client.Id, success });
        }
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

        // Notify other clients that client was connected
        foreach (var otherClient in Clients.Values) {
            await SendMessageAsync(otherClient, otherClient.Id, MessageType.ClientConnected, client.Id);
        }

        Clients.Add(client.Id, client);

        Console.WriteLine($"[SERVER] Client connected: {client.Id}");

        NetworkMessage response = new()
        {
            SenderId = SERVER_ID,
            TargetId = client.Id,
            MessageId = message.MessageId,
            MessageType = MessageType.Handshake
        };
        var packet2 = MessageBuilder.CreateMessage(response, handshake);

        await client.GetStream().WriteAsync(packet2);
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

        object? result = await RequestTcpDataAsync<object>(target.Id, request.MethodName!, request.Args);

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



