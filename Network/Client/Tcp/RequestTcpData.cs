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



public class RemoteMethodException(int targetId, string methodName, string message) : Exception(message) {
    public int TargetId { get; } = targetId;
    public string MethodName { get; } = methodName;
}

public static class MethodResponseExtensions {

}

public static partial class Client {
    public static int TIMEOUT_MS { get; set; } = 500;
    private static int _requestId = 0;

    private static readonly List<int> Requests = [];
    private static readonly ConcurrentDictionary<int, NetworkMessage?> Responses = new();

    

    // ── STRING METHOD ──────────────────────────
    // TODO Validate for errors: Throw error, or just add event?
    public static Task<T?> RequestDataAsync<T>(int targetId, string methodName, params object?[] args)
    {
        bool isVoid = IsVoidMethod(targetId, methodName);

        ushort requestId = isVoid ? (ushort)0 : MessageBuilder.GenerateRequestId(ref _requestId);

        var payload = new MethodRequest { MethodName = methodName, Args = args };

        return RequestDataInternalAsync<MethodRequest, T>(targetId, MessageType.Custom, payload, requestId, waitForResponse: !isVoid);
    }
        


    private static bool IsVoidMethod(int targetId, string methodName)
    {
        var methods = targetId == Server.SERVER_ID
            ? MethodBuilder.GetAvailableServerMethods()
            : MethodBuilder.GetAvailableClientMethods();

        var method = methods.FirstOrDefault(m =>
            m.Name.Equals(methodName, StringComparison.OrdinalIgnoreCase));

        if (method == null)
            throw new InvalidOperationException($"Method '{methodName}' not registered in {(targetId == Server.SERVER_ID ? "server" : "client")} methods.");

        return method.ReturnType == typeof(void);
    }

    // ── INTERNAL GENERIC ───────────────────────
    private static async Task<TResult?> RequestDataInternalAsync<TPayload, TResult>(int targetId, MessageType type, TPayload payload, ushort requestId, bool waitForResponse) {
        if (_tcpStream == null) throw new InvalidOperationException("TCP not initialized.");

        NetworkMessage msg = new()
        {
            SenderId = ClientID,
            TargetId = targetId,
            MessageId = requestId,
            MessageType = type
        };

        if (waitForResponse)
            Requests.Add(requestId);

        byte[] packet = MessageBuilder.CreatePacket(msg, payload);
        await _tcpStream.WriteAsync(packet);

        if (!waitForResponse)
            return default;

        NetworkMessage? returnMessage = await WaitWithTimeout(requestId, TIMEOUT_MS);
        if (returnMessage?.Payload == null)
            return default;

        return MessageBuilder.UnpackPayload<TResult>(returnMessage.Payload);
    }

    // ── HELPER OVERLOAD FOR SIMPLE CASE ───────
    internal static Task<T?> RequestDataInternalAsync<T>(int targetId, MessageType type, T payload)
    {
        ushort requestId = MessageBuilder.GenerateRequestId(ref _requestId);

        return RequestDataInternalAsync<T, T>(targetId, type, payload, requestId, waitForResponse: true);
    }
    


    private static async Task<NetworkMessage?> WaitWithTimeout(int requestId, int timeoutMs)
    {
        try
        {
            using var cts = new CancellationTokenSource(timeoutMs);

            // Polling loop, but asynchronously
            NetworkMessage? response;
            while (!Responses.TryGetValue(requestId, out response))
            {
                if (cts.Token.IsCancellationRequested) throw new TimeoutException($"Request {requestId} timed out after {timeoutMs} ms");

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
            throw new TimeoutException($"[TIMEOUT] Request {requestId} timed out after {timeoutMs} ms");
        }
        catch (Exception ex)
        {
            Requests.Remove(requestId);
            Responses.TryRemove(requestId, out _);
            throw new Exception($"[ERROR] Request {requestId} failed: {ex}");
        }
    }
}