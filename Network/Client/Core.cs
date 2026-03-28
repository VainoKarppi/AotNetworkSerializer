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
    public static readonly List<int> Requests = [];
    public static readonly ConcurrentDictionary<int, NetworkMessage?> Responses = new();
    
    public static int ClientID;
    internal const int SERVER_ID = 1;

    private static TcpClient? _tcpClient;
    private static NetworkStream? _tcpStream;
    private static UdpClient? _udpClient;
    private static IPEndPoint? _udpEndpoint;
    private static CancellationTokenSource _cts = new();
    


    public static bool IsUdpConnected() => _udpClient != null && _udpEndpoint != null;

    public static bool IsTcpConnected() => _tcpClient != null && _tcpClient.Connected;

    

    // ── Connect TCP ──────────────────────────
    public static async Task<int> ConnectTcp(string host, int port, string? customHash = null)
    {
        _tcpClient = new TcpClient();
        _tcpClient.Connect(host, port);
        _tcpStream = _tcpClient.GetStream();
        StartTcpReceiveLoop(_tcpStream);

        string assemblyHash = Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "";

        // Combine with customHash if provided
        HandshakeMessage handshake = new() {
            Hash = string.IsNullOrEmpty(customHash) ? assemblyHash : $"{assemblyHash}-{customHash}"
        };
        
        HandshakeMessage? response = await RequestTcpDataInternalAsync(SERVER_ID, MessageType.Handshake, handshake);
        if (response == null) throw new Exception("Handshake failed (Connection lost)");

        if (!response.Success) throw new Exception(response.Message ?? "Handshake failed (Unknown reason)");
        
        ClientID = response.ClientId;

        // Allow API user to request custom data from server, before connect success (eg. other clients etc)
        OnClientConnected?.Invoke(response);

        return ClientID;
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
                    NetworkMessage msg = MessageBuilder.ReadMessage(packet, includeData: true);

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
                HandleServerShutdown(false);
            }
        });
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






    
}