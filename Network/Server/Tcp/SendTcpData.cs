using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Linq.Expressions;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using DynTypeSerializer;

namespace DynTypeNetwork;





public static partial class Server
{
    // ── Send messages ─────────────────────────
    public static async Task SendTcpMessageAsync(int targetId, string methodName, params object?[] args)
    {
        if (!IsTcpServerRunning()) 
            throw new InvalidOperationException("TCP not initialized.");

        var methods = MethodBuilder.GetAvailableClientMethods();
        var method = methods.FirstOrDefault(m => m.Name.Equals(methodName, StringComparison.OrdinalIgnoreCase));
        if (method == null)
            throw new InvalidOperationException($"Method '{methodName}' not registered in client methods.");

        var payload = new MethodRequest { MethodName = methodName, Args = args };

        // Helper to send to a single client
        async Task SendToClient(Connection client)
        {
            if (client == null || !client.Connected) return;

            var msg = new NetworkMessage
            {
                SenderId = SERVER_ID,
                TargetId = client.Id,
                MessageType = MessageType.Custom
            };

            var packet = MessageBuilder.CreatePacket(msg, payload);
            await client.GetStream().WriteAsync(packet);
        }

        // Send to specific client
        if (targetId > 1) {
            if (!Clients.TryGetValue(targetId, out var client) || client == null)
                throw new Exception($"Client not found with this id: {targetId}");

            await SendToClient(client);
            return;
        }

        // Broadcast to all clients
        foreach (var client in Clients.Values) {
            await SendToClient(client);
        }
    }
}