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
    public static async Task<int> ConnectTcp(string host, int port, string? customHash = null)
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
        
        
        HandshakeMessage? response = await RequestTcpDataInternalAsync(Server.SERVER_ID, MessageType.Handshake, handshake);
        if (response == null) throw new Exception("Handshake failed (Connection lost)");

        if (!response.Success) throw new Exception(response.Message ?? "Handshake failed (Unknown reason)");
        
        ClientID = response.ClientId;
        Clients.AddRange(response.OtherConnectedClients);

        int count = MethodBuilder.RegisterFromHandshake(response.AvailableMethods, isServer: false);

        // Allow API user to request custom data from server, before connect success (eg. other clients etc)
        OnClientConnected?.Invoke(response);

        return ClientID;
    }

    // ── Connect UDP ──────────────────────────
    public static void ConnectUdp(string host, int port)
    {
        _udpClient = new UdpClient();
        _udpEndpoint = new IPEndPoint(IPAddress.Parse(host), port);
        _cts = new CancellationTokenSource();
        StartUdpReceiveLoop(_udpClient);
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
                    NetworkMessage? msg = MessageBuilder.ReadStreamMessage(stream);
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


    private static void StartUdpReceiveLoop(UdpClient udp)
    {
        _cts = new CancellationTokenSource();
        _ = Task.Run(async () =>
        {
            try
            {
                while (!_cts.Token.IsCancellationRequested)
                {
                    UdpReceiveResult result = await udp.ReceiveAsync(_cts.Token);
                    NetworkMessage msg = MessageBuilder.ReadMessage(result.Buffer, includeData: true);
                    OnUdpMessageReceived?.Invoke(msg);
                }
            }
            catch (OperationCanceledException)
            {
                // Normal cancellation, do nothing
            }
            catch (Exception)
            {
                // Connection lost or socket closed unexpectedly
                await HandleServerShutdown(false);
            }
        });
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

            ClientID = 0;
            Clients.Clear();

            _cts?.Cancel();

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