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
    private static CancellationTokenSource _cts = new();

    public static async Task<int> ConnectAsync(string host, int port, bool startUdp = false, string? customHash = null)
    {
        int userId = await ConnectTcp(host, port, null);
        if (startUdp) await ConnectUdp(host, port);

        return userId;
    }

    public static List<int> GetOtherClients() {
        if (!IsTcpConnected()) throw new  Exception("Not connected to server");

        return Clients;
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

            OnClientDisconnected?.Invoke(true);

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