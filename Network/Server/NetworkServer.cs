using System;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using DynTypeSerializer;

namespace DynTypeNetwork;

public static class Server
{
    private static int _clientIdCounter = 1;
    public class Connection : TcpClient
    {
        public int Id { get; set; } = Interlocked.Increment(ref _clientIdCounter);
        public bool HandshakeDone { get; set; } = false;
        public Dictionary<string, object?> CustomData { get; set; } = [];
    }

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
        var stream = client.GetStream();
        var buffer = new byte[8192];

        while (!token.IsCancellationRequested)
        {
            int bytesRead = await stream.ReadAsync(buffer, token);
            if (bytesRead == 0) break;

            byte[] packet = new byte[bytesRead];
            Array.Copy(buffer, packet, bytesRead);
            NetworkMessage? msg = MessageBuilder.ReadMessage(packet, includeData: true);
            if (msg.MessageType == MessageType.Handshake)
            {
                HandleClientHandshake(client, msg);
                continue;
            }
            MessageReceived?.Invoke(msg);
        }
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
        _cts?.Cancel();
        _tcpListener?.Stop();
        _udpListener?.Close();
        _tcpListener = null;
        _udpListener = null;
    }


    // TODO get target id from TcpClient
    private static async Task SendResponseMessage<T>(Connection client, ushort messageId, T? data)
    {
        NetworkMessage message = new()
        {
            SenderId = 1,
            TargetId = 2,
            MessageId = messageId,
            MessageType = MessageType.Response
        };
        var packet = MessageBuilder.Pack(message, data);

        await client.GetStream().WriteAsync(packet);
    }


    private static void HandleClientHandshake(Connection client, NetworkMessage message)
    {
        // TODO valida hash etc, and calulate real client id.
        HandshakeMessage handshake = new() {
            Success = true,
            Message = "SUCCESS",
            ClientId = client.Id
        };

        _ = Task.Run(() => SendResponseMessage(client, message.MessageId, handshake));
    }
}



