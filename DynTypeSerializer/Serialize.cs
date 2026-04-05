
using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;



namespace DynTypeSerializer;


public static partial class Serializer
{
    // ════════════════════════════════════════════════════════════════════════
    // PUBLIC METHODS
    // ════════════════════════════════════════════════════════════════════════
    /// <summary>Serialize any object to a type-preserving JSON string.</summary>
    public static string Serialize(object? obj, Options? options = null)
    {
        options ??= new Options();

        // Create a fresh options instance
        var jsonOpts = new JsonSerializerOptions
        {
            TypeInfoResolver = new System.Text.Json.Serialization.Metadata.DefaultJsonTypeInfoResolver(),
            WriteIndented = options.WriteIndented
        };

        JsonNode? node = BuildNode(obj, obj?.GetType() ?? typeof(object), options);

        if (node is null) return "null";

        if (options.IncludeRootType && obj != null)
        {
            string rootType = GetTypeCode(obj.GetType(), options);
            var rootWrapper = new JsonObject
            {
                ["$r"] = JsonValue.Create(rootType),
                ["$v"] = node
            };
            return rootWrapper.ToJsonString(jsonOpts);
        }

        return node.ToJsonString(jsonOpts);
    }
 
    /// <summary>
    /// Serialize with a known declared type (suppresses $t when runtime == declared).
    /// Use this when the static type is known at the call site.
    /// </summary>
    public static string Serialize<T>(T obj, Options? options = null)
    {
        options ??= new Options();

        var jsonOpts = new JsonSerializerOptions
        {
            TypeInfoResolver = new System.Text.Json.Serialization.Metadata.DefaultJsonTypeInfoResolver(),
            WriteIndented = options.WriteIndented
        };

        JsonNode? node = BuildNode(obj, typeof(T), options);
        if (node is null) return "null";

        if (options.IncludeRootType && obj != null)
        {
            string rootType = GetTypeCode(obj.GetType(), options);
            var rootWrapper = new JsonObject
            {
                ["$r"] = JsonValue.Create(rootType),
                ["$v"] = node
            };
            return rootWrapper.ToJsonString(jsonOpts);
        }

        return node.ToJsonString(jsonOpts);
    }

    // ════════════════════════════════════════════════════════════════════════
    // SERIALIZATION
    // ════════════════════════════════════════════════════════════════════════
 
    private static JsonNode? BuildNode(object? obj, Type declaredType, Options options)
    {
        if (obj is null) return null;

        Type actualType = obj.GetType();

        bool needTag = NeedsTypeTag(actualType, declaredType);
        string? tag  = needTag ? GetTypeCode(actualType, options) : null;

        JsonNode valueNode = BuildValueNode(obj, actualType, options);

        if (tag is null) return valueNode;

        return new JsonObject
        {
            ["$t"] = JsonValue.Create(tag),
            ["$v"] = valueNode,
        };
    }
 
    private static JsonNode BuildValueNode(object obj, Type actualType, Options options)
    {
        // ── Primitives / value-type leaves ─────────────────────────────────
        if (IsPrimitiveLike(actualType))
            return PrimitiveToNode(obj, actualType);

        // ── Dictionary ──────────────────────────────────────────────────────
        if (obj is IDictionary dict)
            return DictToNode(dict, actualType, options);

        // ── Enumerable (not string) ─────────────────────────────────────────
        if (obj is IEnumerable enumerable)
            return EnumerableToNode(enumerable, actualType, options);

        // ── Complex object (class / struct with properties) ─────────────────
        return ObjectToNode(obj, actualType, options);
    }
 
    private static JsonNode PrimitiveToNode(object obj, Type t)
    {
        // Types that need string representation (not natively JSON)
        if (t == typeof(TimeSpan)  || t == typeof(TimeSpan?))
            return JsonValue.Create(((TimeSpan)obj).ToString("c"))!;
        if (t == typeof(DateTime)  || t == typeof(DateTime?))
            return JsonValue.Create(((DateTime)obj).ToString("O"))!;
        if (t == typeof(DateTimeOffset) || t == typeof(DateTimeOffset?))
            return JsonValue.Create(((DateTimeOffset)obj).ToString("O"))!;
        if (t == typeof(Guid)      || t == typeof(Guid?))
            return JsonValue.Create(((Guid)obj).ToString())!;
        if (t == typeof(char)      || t == typeof(char?))
            return JsonValue.Create(obj.ToString())!;
        if (t == typeof(decimal)   || t == typeof(decimal?))
            return JsonValue.Create(obj.ToString())!;      // avoid float precision loss
        if (t == typeof(Uri))
            return JsonValue.Create(((Uri)obj).ToString())!;
        if (t == typeof(Version))
            return JsonValue.Create(((Version)obj).ToString())!;
        if (t.IsEnum)
            return JsonValue.Create(obj.ToString())!;
 
        // Natively supported: bool, byte, sbyte, short, ushort, int, uint, long, ulong, float, double, string
        return JsonValue.Create(obj)!;
    }
 
    private static JsonObject DictToNode(IDictionary dict, Type actualType, Options options)
    {
        Type valueType = actualType.IsGenericType
            ? actualType.GetGenericArguments()[1]
            : typeof(object);

        var obj = new JsonObject();
        foreach (DictionaryEntry kv in dict)
        {
            string key  = kv.Key?.ToString() ?? "null";
            obj[key] = BuildNode(kv.Value, valueType, options);
        }
        return obj;
    }
 
    private static JsonArray EnumerableToNode(IEnumerable enumerable, Type actualType, Options options)
    {
        // Determine element type
        Type elemType = actualType.IsArray
            ? actualType.GetElementType()!
            : actualType.IsGenericType
                ? actualType.GetGenericArguments()[0]
                : typeof(object);
 
        var arr = new JsonArray();
        foreach (object? item in enumerable)
            arr.Add(BuildNode(item, elemType, options));
        return arr;
    }
 
    private static JsonObject ObjectToNode(object obj, Type actualType, Serializer.Options options)
    {
        var node = new JsonObject();
        foreach (var prop in GetProperties(actualType))
        {
            object? val = prop.GetValue(obj);

            // Special handling for Type properties
            if (val is Type typeVal)
                node[prop.Name] = JsonValue.Create(typeVal.FullName);
            else
                node[prop.Name] = BuildNode(val, prop.PropertyType, options);
        }
        return node;
    }

    
    /// <summary>
    /// Should we emit a $t tag?
    /// Yes when:
    ///   - declared type is object, interface, or abstract (deserializer has no concrete type)
    ///   - runtime type differs from the declared type (polymorphic assignment)
    /// </summary>
    private static bool NeedsTypeTag(Type actualType, Type declaredType)
    {
        if (declaredType == typeof(object)) return true;
        if (declaredType.IsInterface) return true;
        if (declaredType.IsAbstract) return true;

        // Include tag if runtime type differs from declared type
        if (actualType != declaredType) return true;

        return false;
    }

    private static string GetTypeCode(Type t, Options? options = null)
    {
        if (options?.IncludeFullAssemblyInfo == true)
            return t.AssemblyQualifiedName ?? t.FullName ?? t.Name;

        if (TypeToCode.TryGetValue(t, out var code))
            return code;

        // Default: strip assembly
        return t.FullName ?? t.Name;
    }
}