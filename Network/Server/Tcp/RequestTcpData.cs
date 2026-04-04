using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Linq.Expressions;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using DynTypeSerializer;

namespace DynTypeNetwork;




public static partial class Server {
    public static int TIMEOUT_MS { get; set; } = 500;

    private static int _requestId = 0;
    private static readonly List<int> Requests = [];
    private static readonly ConcurrentDictionary<int, NetworkMessage?> Responses = new();

    // ── STRING METHOD ──────────────────────────
    // TODO Validate for errors: Throw error, or just add event?
    public static Task<T?> RequestDataAsync<T>(int targetId, string methodName, params object?[] args) {
        if (!IsTcpServerRunning()) throw new InvalidOperationException("TCP not initialized.");
        if (targetId == SERVER_ID) throw new InvalidOperationException("Server cannot send request to itself.");

        // Make sure client method exists before sending request
        var methods = MethodBuilder.GetAvailableClientMethods();

        var method = methods.FirstOrDefault(m =>
            m.Name.Equals(methodName, StringComparison.OrdinalIgnoreCase));

        if (method == null)
            throw new InvalidOperationException($"Method '{methodName}' not registered in client methods.");

        return RequestDataInternalAsync<MethodRequest, T>(targetId, MessageType.Custom, new MethodRequest { MethodName = methodName, Args = args });
    }
        

    // ── INTERNAL GENERIC ───────────────────────
    private static async Task<TResult?> RequestDataInternalAsync<TPayload, TResult>(int targetId, MessageType type, TPayload payload)
    {

        ushort requestId = MessageBuilder.GenerateRequestId(ref _requestId);
        NetworkMessage msg = new() { SenderId = SERVER_ID, TargetId = targetId, MessageId = requestId, MessageType = type };

        Clients.TryGetValue(targetId, out Connection? client);
        if (client == null) throw new Exception($"Client not found with this id: {targetId}");

        if (!client.Connected) throw new Exception($"Client with id {targetId} is not connected.");
        if (client.GetStream() == null) throw new Exception($"Network stream for client {targetId} is not available.");
        if (!client.GetStream().CanWrite) throw new Exception($"Cannot write to network stream for client {targetId}.");

        Requests.Add(requestId);

        var packet = MessageBuilder.CreatePacket(msg, payload);
        await client.GetStream().WriteAsync(packet);
        
        
        NetworkMessage? returnMessage = await WaitWithTimeout(requestId);
        if (returnMessage == null || returnMessage.Payload == null) return default;

        return MessageBuilder.UnpackPayload<TResult>(returnMessage.Payload);
    }



    private static async Task<NetworkMessage?> WaitWithTimeout(int requestId)
    {
        try
        {
            using var cts = new CancellationTokenSource(TIMEOUT_MS);

            // Polling loop, but asynchronously
            NetworkMessage? response;
            while (!Responses.TryGetValue(requestId, out response))
            {
                if (cts.Token.IsCancellationRequested) throw new TimeoutException($"Request {requestId} timed out after {TIMEOUT_MS} ms");

                await Task.Delay(10, cts.Token); // async wait
            }

            Requests.Remove(requestId);
            Responses.TryRemove(requestId, out _);

            return response;
        }
        catch (TaskCanceledException)
        {
            Requests.Remove(requestId);
            Responses.TryRemove(requestId, out _);
            throw new TimeoutException($"[TIMEOUT] Request {requestId} timed out after {TIMEOUT_MS} ms");
        }
        catch (Exception ex)
        {
            Requests.Remove(requestId);
            Responses.TryRemove(requestId, out _);
            throw new Exception($"[ERROR] Request {requestId} failed: {ex}");
        }
    }
}