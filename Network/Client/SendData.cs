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

    public static async Task SendUdpMessageAsync(int targetId, string methodName, params object?[] args)
    {
        if (_udpClient == null || _udpEndpoint == null) throw new InvalidOperationException("UDP client not connected.");

        var methods = targetId == Server.SERVER_ID
            ? MethodBuilder.GetAvailableServerMethods()
            : MethodBuilder.GetAvailableClientMethods();
            
        var method = methods.FirstOrDefault(m => m.Name.Equals(methodName, StringComparison.OrdinalIgnoreCase));

        if (method == null)
            throw new InvalidOperationException($"Method '{methodName}' not registered in {(targetId == Server.SERVER_ID ? "server" : "client")} methods.");

        var payload = new MethodRequest { MethodName = methodName, Args = args };

        NetworkMessage msg = new()
        {
            SenderId = ClientID,
            TargetId = targetId,
            MessageType = MessageType.Custom,
            Payload = Serializer.Serialize(payload)
        };

        var packet = MessageBuilder.CreateUdpMessage(msg);

        if (OnUdpMessageSent != null) {
            _ = Task.Run(() => OnUdpMessageSent.Invoke(msg));
        }

        await _udpClient.SendAsync(packet, packet.Length, _udpEndpoint);
    }

    
    

    // ── Send messages ─────────────────────────
    public static async Task SendTcpMessageAsync(int targetId, string methodName, params object?[] args) {
        if (_tcpStream == null) throw new InvalidOperationException("TCP not initialized.");

        NetworkMessage msg = new()
        {
            SenderId = ClientID,
            TargetId = targetId,
            MessageType = MessageType.Custom
        };

        var methods = targetId == Server.SERVER_ID
            ? MethodBuilder.GetAvailableServerMethods()
            : MethodBuilder.GetAvailableClientMethods();
            
        var method = methods.FirstOrDefault(m => m.Name.Equals(methodName, StringComparison.OrdinalIgnoreCase));

        if (method == null)
            throw new InvalidOperationException($"Method '{methodName}' not registered in {(targetId == Server.SERVER_ID ? "server" : "client")} methods.");

        var payload = new MethodRequest { MethodName = methodName, Args = args };
        var packet = MessageBuilder.CreateMessage(msg, payload);

        if (OnTcpMessageSent != null) {
            _ = Task.Run(() => OnTcpMessageSent.Invoke(msg));
        }

        await _tcpStream.WriteAsync(packet);
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
}