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

    private readonly static List<int> Clients = [];
    public static int ClientID;

    private static TcpClient? _tcpClient;
    private static NetworkStream? _tcpStream;
    private static UdpClient? _udpClient;
    private static IPEndPoint? _udpEndpoint;
    private static CancellationTokenSource _cts = new();
    
    public static bool IsUdpConnected() => _udpClient != null && _udpEndpoint != null;

    public static bool IsTcpConnected() => _tcpClient != null && _tcpClient.Connected;

    public static List<int> GetOtherClients() {
        if (!IsTcpConnected()) throw new  Exception("Not connected to server");

        return Clients;
    }

    // ── Connect TCP ──────────────────────────
    public static async Task<int> ConnectAsync(string host, int port, bool startUdp = false, string? customHash = null)
    {
        int userId = await ConnectTcp(host, port, null);
        if (startUdp) await ConnectUdp(host, port);

        return userId;
    }
    private static async Task<int> ConnectTcp(string host, int port, string? customHash = null)
    {
        _tcpClient = new TcpClient();
        await _tcpClient.ConnectAsync(host, port);
        _tcpStream = _tcpClient.GetStream();
        StartTcpReceiveLoop(_tcpStream);

        string assemblyHash = Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "";

        // Combine with customHash if provided
        var availableMethods = MethodBuilder.GetAvailableClientMethods();
        string methodsHash = MethodBuilder.ComputeMethodsHash(availableMethods);

        HandshakeMessage handshake = new() {
            Hash = $"{assemblyHash}-{methodsHash}-{customHash ?? ""}",
            AvailableMethods = availableMethods
        };
        
        HandshakeMessage? response = await RequestDataInternalAsync(Server.SERVER_ID, MessageType.Handshake, handshake);
        if (response == null) throw new Exception("Handshake failed (Connection lost)");

        if (!response.Success) throw new Exception(response.Message ?? "Handshake failed (Unknown reason)");
        
        ClientID = response.ClientId;
        Clients.AddRange(response.OtherConnectedClients);

        int count = MethodBuilder.RegisterFromHandshake(response.AvailableMethods, isServer: false);

        // Allow API user to request custom data from server, before connect success (eg. other clients etc)
        OnClientConnected?.Invoke(response.ClientId);

        return ClientID;
    }

    // ── Connect UDP ──────────────────────────
    private static async Task ConnectUdp(string host, int port)
    {
        _udpClient = new UdpClient();
        _udpEndpoint = new IPEndPoint(IPAddress.Parse(host), port);
        _cts = new CancellationTokenSource();
        StartUdpReceiveLoop(_udpClient);

        NetworkMessage registerMsg = new() {
            SenderId = ClientID,
            TargetId = Server.SERVER_ID,
            MessageType = MessageType.UdpRegister
        };

        var packet = MessageBuilder.CreateUdpMessage(registerMsg);
        
        await _udpClient.SendAsync(packet.AsMemory(), _udpEndpoint, _cts.Token);
    }


    private static void StartTcpReceiveLoop(NetworkStream stream)
    {
        _cts = new CancellationTokenSource();
        var token = _cts.Token;

        _ = Task.Run(async () =>
        {
            try
            {
                while (!_cts.Token.IsCancellationRequested)
                {
                    // Read ONE full message from stream using the proper helper
                    NetworkMessage? msg = MessageBuilder.ReadTcpMessage(stream);
                    if (msg == null)
                    {
                        // Connection lost or stream closed
                        await HandleServerShutdown(false);
                        break;
                    }
                    if (msg.MessageType == MessageType.Handshake)
                    {
                        Responses[msg.MessageId] = msg;
                        continue;
                    }

                    if (msg.MessageType == MessageType.Response)
                    {
                        Responses[msg.MessageId] = msg;
                        continue;
                    }

                    if (msg.MessageType == MessageType.ClientConnected)
                    {
                        if (msg.Payload == null) continue;
                        int? newClient = MessageBuilder.UnpackPayload<int>(msg.Payload);
                        if (newClient == null) continue;

                        Clients.Add(newClient.Value);

                        _ = Task.Run(() => OnOtherClientConnected?.Invoke(newClient.Value));
                        
                        break;
                    }

                    if (msg.MessageType == MessageType.ClientDisconnected) {
                        object[]? data = MessageBuilder.UnpackPayload<object[]>(msg.Payload);
                        if (data == null || data.Length != 2) continue;

                        int client_id = (int)data[0];
                        bool success = (bool)data[1];
                        
                        Clients.Remove(client_id);
                        _ = Task.Run(() => OnOtherClientDisconnected?.Invoke(client_id, success));

                        continue;
                    }

                    if (msg.MessageType == MessageType.ServerShutdown)
                    {
                        await HandleServerShutdown(true);
                        break;
                    }

                    if (msg.MessageType == MessageType.Custom) {
                        await MessageBuilder.HandleCustomMessage(stream, msg, token);
                        continue;
                    }

                    // --- CUSTOM EVENT ---
                    OnTcpMessageReceived?.Invoke(msg);
                }
            }
            catch (OperationCanceledException)
            {
                // normal shutdown
            }
            catch (Exception ex) when (ex is ObjectDisposedException || ex is IOException)
            {
                // Connection was forcibly closed
                Console.WriteLine($"[CLIENT] Connection lost: {ex.Message}");
                await HandleServerShutdown(false);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[CLIENT] Receive loop exception: {ex}");
                await HandleServerShutdown(false);
            }
        });
    }



    private static void StartUdpReceiveLoop(UdpClient client)
    {
        _cts ??= new CancellationTokenSource();

        _ = Task.Run(() => ReceiveLoop(client, _cts.Token), _cts.Token);
    }

    private static async Task ReceiveLoop(UdpClient client, CancellationToken token)
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


    private static async Task SendMessageAsync(int targetId, MessageType type, object? data)
    {
        if (!IsTcpConnected()) throw new Exception("Not connected to server");

        NetworkMessage message = new()
        {
            SenderId = ClientID,
            TargetId = targetId,
            MessageType = type
        };
        var packet = MessageBuilder.CreateMessage(message, data);

        await _tcpClient!.GetStream().WriteAsync(packet);
    }

    private static async Task HandleServerShutdown(bool intentional)
    {
        // Invoke event
        _ = Task.Run(() => OnServerShutdown?.Invoke(intentional));

        // Clean up connections
        await Disconnect();
    }

    public static async Task Disconnect()
    {
        try
        {
            await SendMessageAsync(Server.SERVER_ID, MessageType.ClientDisconnected, null);

            _cts?.Cancel();

            ClientID = 0;

            Clients.Clear();

            _tcpStream?.Dispose();
            _tcpStream = null;
            _tcpClient?.Close();
            _tcpClient = null;

            _udpClient?.Dispose();
            _udpClient = null;
            _udpEndpoint = null;
        }
        catch {}
    }



    
}