using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;

namespace DynTypeNetwork;


public static class MethodBuilder {
    public class RpcMethodParameter {
        public string Name { get; set; } = null!;
        public Type Type { get; set; } = null!;
    }

    public class RpcMethodInfo {
        public string Name { get; set; } = null!;
        public RpcMethodParameter[] Parameters { get; set; } = null!;
        public Type? ReturnType { get; set; }
    }

    private static readonly Dictionary<string, Delegate> _serverDelegates = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, Delegate> _clientDelegates = new(StringComparer.OrdinalIgnoreCase);

    private static readonly Dictionary<string, RpcMethodInfo> _clientMethodInfos = [];
    private static readonly Dictionary<string, RpcMethodInfo> _serverMethodInfos = [];

    // Get metadata
    public static RpcMethodInfo[] GetAvailableClientMethods() => _clientMethodInfos.Values.ToArray();
    public static RpcMethodInfo[] GetAvailableServerMethods() => _serverMethodInfos.Values.ToArray();

    // Register all public static methods from a type as client methods
    public static void RegisterClientMethods(object instance) {
        RegisterMethodsFromType(instance.GetType(), isServer: false);
    }

    // Register all public static methods from a type as server methods
    public static void RegisterServerMethods(object instance) {
        RegisterMethodsFromType(instance.GetType(), isServer: true);
    }

    internal static int RegisterFromHandshake(RpcMethodInfo[] methods, bool isServer)
    {
        int registeredCount = 0;

        foreach (var rpcInfo in methods)
        {
            var dictInfos = !isServer ? _serverMethodInfos : _clientMethodInfos;
            if (dictInfos.ContainsKey(rpcInfo.Name)) continue;

            // Create parameter expressions
            var paramExprs = rpcInfo.Parameters
                .Select(p => Expression.Parameter(p.Type, p.Name))
                .ToArray();

            Type[] parameterTypes = rpcInfo.Parameters.Select(p => p.Type).ToArray();

            Delegate del;
            Type delegateType;

            if (rpcInfo.ReturnType == null)
            {
                delegateType = Expression.GetActionType(parameterTypes);

                var body = Expression.Throw(
                    Expression.Constant(new InvalidOperationException($"Remote method '{rpcInfo.Name}' cannot be called locally"))
                );

                del = Expression.Lambda(delegateType, body, paramExprs).Compile();
            }
            else
            {
                delegateType = Expression.GetFuncType(parameterTypes.Concat([rpcInfo.ReturnType]).ToArray());

                var body = Expression.Throw(
                    Expression.Constant(new InvalidOperationException($"Remote method '{rpcInfo.Name}' cannot be called locally")),
                    rpcInfo.ReturnType
                );

                del = Expression.Lambda(delegateType, body, paramExprs).Compile();
            }

            if (!isServer)
                _serverDelegates[rpcInfo.Name] = del;
            else
                _clientDelegates[rpcInfo.Name] = del;

            dictInfos[rpcInfo.Name] = rpcInfo;
            registeredCount++;
        }

        return registeredCount;
    }

    private static void RegisterMethodsFromType(Type type, bool isServer) {
        var methods = type.GetMethods(BindingFlags.Public | BindingFlags.Static);
        foreach (var method in methods) {
            if (method.IsSpecialName) continue;

            var del = Delegate.CreateDelegate(
                Expression.GetDelegateType(
                    method.GetParameters()
                            .Select(p => p.ParameterType)
                            .Concat(method.ReturnType == typeof(void) ? Array.Empty<Type>() : [method.ReturnType])
                            .ToArray()
                ),
                method
            );

            var rpcInfo = new RpcMethodInfo {
                Name = method.Name,
                Parameters = method.GetParameters()
                                    .Where(p => p.ParameterType != typeof(NetworkMessage))
                                    .Select(p => new RpcMethodParameter { Name = p.Name!, Type = p.ParameterType })
                                    .ToArray(),
                ReturnType = method.ReturnType == typeof(void) ? null : method.ReturnType
            };

            if (isServer) {
                _serverDelegates[method.Name] = del;
                _serverMethodInfos[method.Name] = rpcInfo;
            } else {
                _clientDelegates[method.Name] = del;
                _clientMethodInfos[method.Name] = rpcInfo;
            }
        }
    }

    internal static T? CallServerMethod<T>(string methodName, NetworkMessage message, params object[] args) =>
        CallWithNetworkMessage<T>(_serverDelegates, methodName, message, args);

    internal static T? CallClientMethod<T>(string methodName, NetworkMessage message, params object[] args) =>
        CallWithNetworkMessage<T>(_clientDelegates, methodName, message, args);

    // Helper for both server and client
    private static T? CallWithNetworkMessage<T>(Dictionary<string, Delegate> delegates, string methodName, NetworkMessage message, object[] args)
    {
        if (!delegates.TryGetValue(methodName, out var del))
            throw new InvalidOperationException($"{methodName} not registered.");
        
        MethodInfo method = del.Method;
        ParameterInfo[] parameters = method.GetParameters();

        object?[] finalArgs;

        if (FirstParameterIsNetworkMessage(method, out _)) {
            finalArgs = new object?[parameters.Length];

            // Inject message as first parameter
            finalArgs[0] = message;

            // Shift args right
            for (int i = 0; i < args.Length && i + 1 < finalArgs.Length; i++)
                finalArgs[i + 1] = args[i];
        } else {
            finalArgs = args;
        }

        return (T?)del.DynamicInvoke(finalArgs);
    }

    private static bool FirstParameterIsNetworkMessage(MethodInfo method, out bool isOptional) {
        ParameterInfo[] parameters = method.GetParameters();
        isOptional = false;
        if (parameters.Length == 0) return false;

        ParameterInfo firstParam = parameters[0];
        Type firstType = Nullable.GetUnderlyingType(firstParam.ParameterType) ?? firstParam.ParameterType;

        if (firstType == typeof(NetworkMessage))
        {
            isOptional = firstParam.HasDefaultValue || firstParam.IsOptional;
            return true;
        }

        return false;
    }

    public static string ComputeMethodsHash(RpcMethodInfo[] methods)
    {
        // Create a string representation of the method signatures
        var methodSignatures = methods
            .OrderBy(m => m.Name) // Ensure consistent order
            .Select(m =>
            {
                string parameters = string.Join(",", m.Parameters.Select(p => $"{p.Type.FullName} {p.Name}"));
                string returnType = m.ReturnType?.FullName ?? "void";
                return $"{m.Name}({parameters}):{returnType}";
            });

        string combined = string.Join("|", methodSignatures);

        // Compute a hash (e.g., SHA256) of the combined string
        using var sha256 = System.Security.Cryptography.SHA256.Create();
        byte[] hashBytes = sha256.ComputeHash(System.Text.Encoding.UTF8.GetBytes(combined));
        return Convert.ToBase64String(hashBytes);
    }
}
