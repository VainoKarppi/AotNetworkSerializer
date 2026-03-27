using System;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace DynTypeNetwork;

public static class Server
{
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
            var client = await _tcpListener!.AcceptTcpClientAsync(token);
            _ = HandleTcpClientAsync(client, token);
        }
    }

    private static async Task HandleTcpClientAsync(TcpClient client, CancellationToken token)
    {
        var stream = client.GetStream();
        var buffer = new byte[8192];

        while (!token.IsCancellationRequested)
        {
            int bytesRead = await stream.ReadAsync(buffer, token);
            if (bytesRead == 0) break;

            byte[] packet = new byte[bytesRead];
            Array.Copy(buffer, packet, bytesRead);
            var msg = Network.ReadMessage(packet, includeData: true);
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
            var msg = Network.ReadMessage(result.Buffer, includeData: true);
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
}



