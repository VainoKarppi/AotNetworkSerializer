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

    public static async Task SendUdpMessageAsync(int targetId, string methodName, params object?[] args)
    {
        if (!IsUdpServerRunning()) 
            throw new InvalidOperationException("UDP server not running.");

        // Get client
        if (!Clients.TryGetValue(targetId, out Connection? client) || client == null)
            throw new Exception($"Client not found with ID: {targetId}");
        
        // Validate UDP endpoint
        if (client.UdpEndpoint == null)
            throw new InvalidOperationException($"Client {targetId} has no registered UDP endpoint.");


        // Validate method exists
        var methods = MethodBuilder.GetAvailableClientMethods();
        var method = methods.FirstOrDefault(m => m.Name.Equals(methodName, StringComparison.OrdinalIgnoreCase));
        if (method == null)
            throw new InvalidOperationException($"Method '{methodName}' is not registered in client methods.");

        // Pack payload
        var payload = new MethodRequest { MethodName = methodName, Args = args };

        NetworkMessage msg = new()
        {
            SenderId = targetId,
            TargetId = targetId,
            MessageType = MessageType.Custom,
            Payload = Serializer.Serialize(payload)
        };

        var packet = MessageBuilder.CreateUdpMessage(msg);

        try
        {
            // Send via server UDP to the client's registered endpoint
            await _udpListener!.SendAsync(packet, packet.Length, client.UdpEndpoint);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[SERVER UDP] Failed to send to client {targetId}: {ex.Message}");
            throw;
        }
    }
}