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





public static partial class Server
{
    // ── Send messages ─────────────────────────
    public static async Task SendTcpMessageAsync(int targetId, string methodName, params object?[] args) {
        if (!IsTcpServerRunning()) throw new InvalidOperationException("TCP not initialized.");

        Clients.TryGetValue(targetId, out Connection? client);
        if (client == null) throw new Exception($"Client not found with this id: {targetId}");

        NetworkMessage msg = new()
        {
            SenderId = SERVER_ID,
            TargetId = targetId,
            MessageType = MessageType.Custom
        };

        var methods = MethodBuilder.GetAvailableClientMethods();
        var method = methods.FirstOrDefault(m => m.Name.Equals(methodName, StringComparison.OrdinalIgnoreCase));

        if (method == null)
            throw new InvalidOperationException($"Method '{methodName}' not registered in {(targetId == SERVER_ID ? "server" : "client")} methods.");

        var payload = new MethodRequest { MethodName = methodName, Args = args };
        var packet = MessageBuilder.CreatePacket(msg, payload);

        await client.GetStream().WriteAsync(packet);
    }
}