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
    private static int _requestId;
    private static ushort GenerateRequestId() => (ushort)Interlocked.Increment(ref _requestId);

    private sealed class TcpRequestPayload
    {
        public string? MethodName { get; init; }
        public MethodInfo? MethodInfo { get; init; }
        public object?[] Args { get; init; } = [];
    }

    // ── STRING METHOD ──────────────────────────
    public static Task<T?> RequestTcpDataAsync<T>(int targetId, string methodName, params object?[] args) =>
        RequestTcpDataInternalAsync<TcpRequestPayload, T>(targetId, MessageType.Custom, new TcpRequestPayload { MethodName = methodName, Args = args });

    // EXPRESSION METHOD
    public static Task<T?> RequestTcpDataAsync<T>(int targetId, Expression<Func<T>> methodExpr)
    {
        if (methodExpr.Body is not MethodCallExpression call)
            throw new ArgumentException("Expression must be a method call.", nameof(methodExpr));

        object?[] args = call.Arguments.Select(a => Expression.Lambda(a).Compile().DynamicInvoke()).ToArray();
        var payload = new TcpRequestPayload { MethodInfo = call.Method, Args = args };

        return RequestTcpDataInternalAsync<TcpRequestPayload, T>(targetId, MessageType.Custom, payload);
    }

    // ── INTERNAL GENERIC ───────────────────────
    private static async Task<TResult?> RequestTcpDataInternalAsync<TPayload, TResult>(int targetId, MessageType type, TPayload payload, int timeoutMs = 500)
    {
        if (_tcpStream == null)
            throw new InvalidOperationException("TCP not initialized.");

        ushort requestId = GenerateRequestId();
        NetworkMessage msg = new() { SenderId = ClientID, TargetId = targetId, MessageId = requestId, MessageType = type };

        Requests.Add(requestId);

        var packet = MessageBuilder.Pack(msg, payload);
        await _tcpStream.WriteAsync(packet);

        NetworkMessage? returnMessage = await WaitWithTimeout(requestId, timeoutMs);
        if (returnMessage == null || returnMessage.PayloadBytes.Length == 0) return default;

        return MessageBuilder.Unpack<TResult>(returnMessage.PayloadBytes);
    }

    // ── HELPER OVERLOAD FOR SIMPLE CASE ───────
    private static Task<T?> RequestTcpDataInternalAsync<T>(int targetId, MessageType type, T payload, int timeoutMs = 500) =>
        RequestTcpDataInternalAsync<T, T>(targetId, type, payload, timeoutMs);
    


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