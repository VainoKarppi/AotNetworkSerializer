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

    private static Dictionary<string, RpcMethodInfo> _clientMethodInfos = [];
    private static Dictionary<string, RpcMethodInfo> _serverMethodInfos = [];

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

    // Call a registered method by name
    internal static T? CallServerMethod<T>(string methodName, params object[] args) {
        if (!_serverDelegates.TryGetValue(methodName, out var del))
            throw new InvalidOperationException($"Server Method {methodName} not registered.");
        return (T?)del.DynamicInvoke(args);
    }

    internal static T? CallClientMethod<T>(string methodName, params object[] args) {
        if (!_clientDelegates.TryGetValue(methodName, out var del))
            throw new InvalidOperationException($"Client Method {methodName} not registered.");
        return (T?)del.DynamicInvoke(args);
    }
}
