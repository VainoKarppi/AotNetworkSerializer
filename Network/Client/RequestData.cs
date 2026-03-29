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





public static partial class Client
{
    public static int TIMEOUT_MS { get; set; } = 500;


    // ── STRING METHOD ──────────────────────────
    public static Task<T?> RequestTcpDataAsync<T>(int targetId, string methodName, params object?[] args) =>
        RequestTcpDataInternalAsync<MethodRequest, T>(targetId, MessageType.Custom, new MethodRequest { MethodName = methodName, Args = args });

    // EXPRESSION METHOD
    public static Task<T?> RequestTcpDataAsync<T>(int targetId, Expression<Func<T>> methodExpr)
    {
        if (methodExpr.Body is not MethodCallExpression call) throw new ArgumentException("Expression must be a method call.", nameof(methodExpr));
        
        string methodName = call.Method.Name;

        object?[] args = call.Arguments.Select(a => Expression.Lambda(a).Compile().DynamicInvoke()).ToArray();
        var payload = new MethodRequest { MethodName = methodName, Args = args };

        return RequestTcpDataInternalAsync<MethodRequest, T>(targetId, MessageType.Custom, payload);
    }

    // ── INTERNAL GENERIC ───────────────────────
    internal static async Task<TResult?> RequestTcpDataInternalAsync<TPayload, TResult>(int targetId, MessageType type, TPayload payload)
    {
        if (_tcpStream == null) throw new InvalidOperationException("TCP not initialized.");

        ushort requestId = MessageBuilder.GenerateRequestId(ref _requestId);
        NetworkMessage msg = new() { SenderId = ClientID, TargetId = targetId, MessageId = requestId, MessageType = type };

        
        if (type == MessageType.Custom) {
            // TODO if the target method returns void --> set MessageId = 0
            // TODO read from:

            if (msg.SenderId != Server.SERVER_ID) {
                // private static Dictionary<string, RpcMethodInfo> _clientMethodInfos = [];
                // private static readonly Dictionary<string, Delegate> _clientDelegates = new(StringComparer.OrdinalIgnoreCase);
            } else {
                // private static readonly Dictionary<string, Delegate> _serverDelegates = new(StringComparer.OrdinalIgnoreCase);
                // private static Dictionary<string, RpcMethodInfo> _serverMethodInfos = [];
            }
        }
        Requests.Add(requestId);

        var packet = MessageBuilder.CreateMessage(msg, payload);
        await _tcpStream.WriteAsync(packet);
        
        
        NetworkMessage? returnMessage = await WaitWithTimeout(requestId, TIMEOUT_MS);
        if (returnMessage == null || returnMessage.Payload == null) return default;

        return MessageBuilder.UnpackPayload<TResult>(returnMessage.Payload);
    }

    // ── HELPER OVERLOAD FOR SIMPLE CASE ───────
    private static Task<T?> RequestTcpDataInternalAsync<T>(int targetId, MessageType type, T payload) =>
        RequestTcpDataInternalAsync<T, T>(targetId, type, payload);
    


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