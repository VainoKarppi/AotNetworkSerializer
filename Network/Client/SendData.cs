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

        var packet = MessageBuilder.Pack(msg, data);

        if (OnUdpMessageSent != null) {
            if (runAsync)
                _ = Task.Run(() => OnUdpMessageSent.Invoke(msg));
            else
                OnUdpMessageSent.Invoke(msg);
        }

        await _udpClient.SendAsync(packet, packet.Length, _udpEndpoint);
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

        var packet = MessageBuilder.Pack(msg, data);

        if (OnTcpMessageSent != null) {
            if (runAsync)
                _ = Task.Run(() => OnTcpMessageSent.Invoke(msg));
            else
                OnTcpMessageSent.Invoke(msg);
        }

        await _tcpStream.WriteAsync(packet);
    }
}