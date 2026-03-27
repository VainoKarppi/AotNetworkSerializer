using System;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using DynTypeSerializer;

namespace DynTypeNetwork;

public static class Client
{
    public static readonly List<int> Requests = [];
    public static readonly ConcurrentDictionary<int, NetworkMessage?> Responses = new();
    
    public static int ClientID;
    internal const int SERVER_ID = 1;

    private static TcpClient? _tcpClient;
    private static NetworkStream? _tcpStream;
    private static UdpClient? _udpClient;
    private static IPEndPoint? _udpEndpoint;
    private static CancellationTokenSource _cts = new();
    
    /// <summary>
    /// True if intentional / false if not
    /// </summary>
    public static event Action<bool>? OnServerShutdown;
    public static event Action<HandshakeMessage>? OnClientConnected;
    public static event Action<NetworkMessage>? OnClientDisconnected;
    public static event Action<NetworkMessage>? OnOtherClientConnected;
    public static event Action<NetworkMessage>? OnOtherClientDisconnected;

    public static event Action<NetworkMessage>? OnTcpMessageSent;
    public static event Action<NetworkMessage>? OnTcpMessageReceived;
    public static event Action<NetworkMessage>? OnTcpRequestMessageReceived;

    public static event Action<NetworkMessage>? OnUdpMessageSent;
    public static event Action<NetworkMessage>? OnUdpMessageReceived;


    public static bool IsUdpConnected() => _udpClient != null && _udpEndpoint != null;

    public static bool IsTcpConnected() => _tcpClient != null && _tcpClient.Connected;

    public class HandshakeMessage
    {
        public bool Success { get; set; }
        public string? Message { get; set; }
        public string Hash { get; set; } = "";
        public int ClientId { get; set; }
    }

    // ── Connect TCP ──────────────────────────
    public static async Task<int> ConnectTcp(string host, int port, string? customHash = null)
    {
        _tcpClient = new TcpClient();
        _tcpClient.Connect(host, port);
        _tcpStream = _tcpClient.GetStream();
        StartTcpReceiveLoop(_tcpStream);

        string assemblyHash = Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "";



        return 0;
    }

    private static void StartTcpReceiveLoop(NetworkStream stream)
    {
        _cts = new CancellationTokenSource();
        _ = Task.Run(async () =>
        {
            var buffer = new byte[8192];
            try
            {
                while (!_cts.Token.IsCancellationRequested)
                {
                    int bytesRead = await stream.ReadAsync(buffer, _cts.Token);
                    if (bytesRead == 0) { // Connection lost
                        HandleServerShutdown(false);
                        break;
                    }

                    byte[] packet = new byte[bytesRead];
                    Array.Copy(buffer, packet, bytesRead);
                    NetworkMessage msg = Network.ReadMessage(packet, includeData: true);

                    // Check if is response to specific request
                    if (msg.MessageType == MessageType.Response) {
                        Responses[msg.MessageId] = msg;
                        continue;
                    }

                    if (msg.MessageType == MessageType.Request) {
                        //TODO CALUCLATE RESPONSE HERE AND SEND RESPONSE (just invoke message reseiced, and let user send result themselves?)
                        //TODO or invoke requested method, and run automatically specifically that method?
                        //TODO Send error message if necessary
                        //TODO build ClientMethods and ServerMethods?
                        continue;
                    }

                    if (msg.MessageType == MessageType.ServerShutdown) {
                        HandleServerShutdown(true);
                        break;
                    }

                    OnTcpMessageReceived?.Invoke(msg);
                }
            } catch (Exception ex) when (ex is ObjectDisposedException || ex is IOException) {
                // Connection was forcibly closed
                HandleServerShutdown(false);
            }
        });
    }

    // ── Connect UDP ──────────────────────────
    public static void ConnectUdp(string host, int port)
    {
        _udpClient = new UdpClient();
        _udpEndpoint = new IPEndPoint(IPAddress.Parse(host), port);
        _cts = new CancellationTokenSource();
        StartUdpReceiveLoop(_udpClient);
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
                    NetworkMessage msg = Network.ReadMessage(result.Buffer, includeData: true);
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
                HandleServerShutdown(false);
            }
        });
    }

    public static async Task SendUdpMessageAsync<T>(int targetId, MessageType type, T data, bool runAsync = true)
    {
        if (_udpClient == null || _udpEndpoint == null)
            throw new InvalidOperationException("UDP client not connected.");

        NetworkMessage msg = new()
        {
            SenderId = ClientID,
            TargetId = targetId,
            MessageType = type
        };

        var packet = Network.Pack(msg, data);

        if (OnUdpMessageSent != null) {
            if (runAsync)
                _ = Task.Run(() => OnUdpMessageSent.Invoke(msg));
            else
                OnUdpMessageSent.Invoke(msg);
        }

        await _udpClient.SendAsync(packet, packet.Length, _udpEndpoint);
    }


    // ── Send messages ─────────────────────────
    public static async Task<T?> RequestTcpDataAsync<T>(int targetId, MessageType type, T? data, int timeoutMs = 500)
    {
        return await RequestTcpDataAsync(targetId, type, data, timeoutMs);
    }
    public static async Task<T?> RequestTcpDataAsync<T>(int targetId, MessageType type, object? data, int timeoutMs = 500)
    {
        if (_tcpStream == null)
            throw new InvalidOperationException("TCP not initialized.");

        NetworkMessage msg = new()
        {
            SenderId = ClientID,
            TargetId = targetId,
            MessageType = type
        };

        int requestId = GenerateRequestId();
        Requests.Add(requestId);

        var packet = Network.Pack(msg, data);
        await _tcpStream.WriteAsync(packet);

        NetworkMessage? returnMessage = await WaitWithTimeout(requestId, timeoutMs);
        if (returnMessage == null) return default;

        if (returnMessage.PayloadLength == 0) return default;

        T? deserialized = Network.Unpack<T>(returnMessage.PayloadBytes);

        return deserialized;
    }

    // ── Send messages ─────────────────────────
    public static async Task SendTcpMessageAsync<T>(int targetId, MessageType type, T data, bool runAsync = true)
    {
        if (_tcpStream == null)
            throw new InvalidOperationException("TCP not initialized.");

        NetworkMessage msg = new()
        {
            SenderId = ClientID,
            TargetId = targetId,
            MessageType = type
        };

        var packet = Network.Pack(msg, data);

        if (OnTcpMessageSent != null) {
            if (runAsync)
                _ = Task.Run(() => OnTcpMessageSent.Invoke(msg));
            else
                OnTcpMessageSent.Invoke(msg);
        }

        await _tcpStream.WriteAsync(packet);
    }

    private static void HandleServerShutdown(bool intentional)
    {
        // Invoke event
        _ = Task.Run(() => OnServerShutdown?.Invoke(intentional));

        // Clean up connections
        Disconnect();
    }

    public static void Disconnect()
    {
        try
        {
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






    // TODO Move to Sahred
    private static int _requestId;
    public static int GenerateRequestId() => Interlocked.Increment(ref _requestId);

    private static async Task<NetworkMessage?> WaitWithTimeout(int requestId, int timeoutMs)
    {
        try
        {
            using var cts = new CancellationTokenSource(timeoutMs);

            // Polling loop, but asynchronously
            NetworkMessage? response;
            while (!Responses.TryGetValue(requestId, out response))
            {
                if (cts.Token.IsCancellationRequested)
                    throw new TimeoutException($"Request {requestId} timed out after {timeoutMs} ms");

                await Task.Delay(1, cts.Token); // async wait
            }

            Requests.Remove(requestId);
            Responses.TryRemove(requestId, out _);

            return response;
        }
        catch (TimeoutException)
        {
            Console.WriteLine($"[TIMEOUT] Request {requestId} timed out after {timeoutMs} ms");
            Requests.Remove(requestId);
            Responses.TryRemove(requestId, out _);
            return null;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[ERROR] Request {requestId} failed: {ex}");
            Requests.Remove(requestId);
            Responses.TryRemove(requestId, out _);
            throw;
        }
    }
}