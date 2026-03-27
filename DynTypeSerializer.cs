
using System.Collections;
using System.Collections.Concurrent;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;



namespace DynTypeSerializer;


// *! AVAILABLE METHODS:
/*
    Serialize(object? obj, Options? options = null)        - Serialize any object to JSON string, preserving runtime type.
    Serialize<T>(T obj, Options? options = null)           - Serialize with known declared type, suppresses $t tag if runtime matches declared.
    Deserialize<T>(string json)                            - Deserialize JSON string back to T, restoring all dynamic types.
    DeserializeDynamic(string json)                        - Deserialize JSON when root type is unknown, returns object.
    ContainsRootType(string json)                          - Checks if JSON contains a root type ('$r') tag.
    GetRootType(string json)                               - Gets the root Type from JSON with 'IncludeRootType' option.
*/


/*
    {
    "Name": "Alice",
    "Age": 30,
    "Items": [
        {
            "$t": "i",
            "$v": 42
        },
        {
            "$t": "s",
            "$v": "hello"
        },
        null,
        {
        "$t": "oa",
        "$v": [
            {
                "$t": "s",
                "$v": "nested"
            },
            {
                "$t": "i",
                "$v": 123
            },
            null
        ]
        }
    ],
    "Flags": {
        "IsActive": {
            "$t": "b",
            "$v": true
        },
        "Score": {
            "$t": "d",
            "$v": 99.5
        }
    },
    "Test": "03:30:00",
    "Sub": null
    }
*/

/// <summary>
/// A fully dynamic type-preserving JSON serializer.
///
/// RULES:
///   1. If the runtime type matches the declared (static) type exactly → emit bare value, no $t tag.
///   2. If the runtime type differs from the declared type, OR the declared type is object/interface
///      → wrap as { "$t": "&lt;code&gt;", "$v": &lt;value&gt; } so the deserializer knows the real type.
///   3. Every complex object's properties are always serialized (not just on mismatch).
///   4. Primitives / value-types that JSON handles natively are emitted as JsonValue leaves.
///   5. Round-trip fidelity: Deserialize&lt;T&gt;(Serialize(x)) == x for all supported types.
/// </summary>
public static class Serializer
{
    public class Options
    {
        public bool IncludeRootType { get; set; } = false;
        public bool IncludeFullAssemblyInfo { get; set; } = false;
        public bool WriteIndented { get; set; } = false;
    }

    // ── Short type codes ────────────────────────────────────────────────────
    // Only primitive / well-known value types get short codes.
    // Complex user types use their assembly-qualified name.
    private static readonly Dictionary<Type, string> TypeToCode = new()
    {
        [typeof(bool)]           = "b",
        [typeof(bool?)]          = "b?",
        [typeof(byte)]           = "by",
        [typeof(byte?)]          = "by?",
        [typeof(sbyte)]          = "sb",
        [typeof(sbyte?)]         = "sb?",
        [typeof(char)]           = "c",
        [typeof(char?)]          = "c?",
        [typeof(short)]          = "sh",
        [typeof(short?)]         = "sh?",
        [typeof(ushort)]         = "ush",
        [typeof(ushort?)]        = "ush?",
        [typeof(int)]            = "i",
        [typeof(int?)]           = "i?",
        [typeof(uint)]           = "ui",
        [typeof(uint?)]          = "ui?",
        [typeof(long)]           = "l",
        [typeof(long?)]          = "l?",
        [typeof(ulong)]          = "ul",
        [typeof(ulong?)]         = "ul?",
        [typeof(float)]          = "f",
        [typeof(float?)]         = "f?",
        [typeof(double)]         = "d",
        [typeof(double?)]        = "d?",
        [typeof(decimal)]        = "dec",
        [typeof(decimal?)]       = "dec?",
        [typeof(string)]         = "s",
        [typeof(DateTime)]       = "dt",
        [typeof(DateTime?)]      = "dt?",
        [typeof(DateTimeOffset)] = "dto",
        [typeof(DateTimeOffset?)]= "dto?",
        [typeof(TimeSpan)]       = "ts",
        [typeof(TimeSpan?)]      = "ts?",
        [typeof(Guid)]           = "g",
        [typeof(Guid?)]          = "g?",
        [typeof(Uri)]            = "uri",
        [typeof(Version)]        = "ver",
        [typeof(object)]         = "o",
        [typeof(object[])]       = "oa",
    };
 
    // Reverse map built once at startup
    private static readonly Dictionary<string, Type> CodeToType =
        TypeToCode.ToDictionary(kv => kv.Value, kv => kv.Key);
 
    // Cache of assembly-scan results for user types (full name → Type)
    private static readonly ConcurrentDictionary<string, Type> NameToType = new();
 
    // Cache of property lists per type to avoid repeated reflection
    private static readonly ConcurrentDictionary<Type, PropertyInfo[]> PropCache = new();
 
 
    // ════════════════════════════════════════════════════════════════════════
    // PUBLIC API
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
 
    /// <summary>Deserialize back to T, restoring all dynamic types exactly.</summary>
    public static T? Deserialize<T>(string json)
    {
        using var doc = JsonDocument.Parse(json);
        JsonElement root = doc.RootElement;

        if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("$r", out var rProp) && root.TryGetProperty("$v", out var vProp))
        {
            // Ignore the $r for type T; just deserialize the $v node
            return (T?)ReadNode(vProp, typeof(T));
        }

        object? result = ReadNode(root, typeof(T));
        return result is null ? default : (T)result;
    }
 
    /// <summary>Deserialize when the root type is unknown (returns object / boxed value).</summary>
    public static object? DeserializeDynamic(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return ReadNode(doc.RootElement, typeof(object));
    }

    /// <summary>Checks if the JSON string contains a root type ('$r') tag. [Serialized with 'IncludeRootType' option]</summary>
    public static bool ContainsRootType(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        if (root.ValueKind != JsonValueKind.Object) return false;
        return root.TryGetProperty("$r", out _);
    }

    /// <summary>Gets the root <see cref="Type"/> from JSON string with 'IncludeRootType'.</summary>
    public static Type GetRootType(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        if (root.ValueKind != JsonValueKind.Object)
            throw new InvalidOperationException("JSON root is not an object.");

        if (root.TryGetProperty("$r", out var rProp))
        {
            string code = rProp.GetString() ?? throw new InvalidOperationException("$r type code was null.");
            return ResolveType(code);
        }

        return typeof(object);
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
 
    private static JsonObject ObjectToNode(object obj, Type actualType, Options options)
    {
        var node = new JsonObject();
        foreach (var prop in GetProperties(actualType))
        {
            object? val = prop.GetValue(obj);
            node[prop.Name] = BuildNode(val, prop.PropertyType, options);
        }
        return node;
    }
 
    // ════════════════════════════════════════════════════════════════════════
    // DESERIALIZATION
    // ════════════════════════════════════════════════════════════════════════
 
    private static object? ReadNode(JsonElement el, Type declaredType)
    {
        if (el.ValueKind == JsonValueKind.Null) return null;
 
        // Detect $t/$v envelope
        Type targetType = declaredType;
        JsonElement valueEl = el;
 
        if (el.ValueKind == JsonValueKind.Object
            && el.TryGetProperty("$t", out var tProp)
            && el.TryGetProperty("$v", out var vProp))
        {
            string code = tProp.GetString()
                ?? throw new InvalidOperationException("$t code was null.");
            targetType = ResolveType(code);
            valueEl    = vProp;
        }
 
        return ReadValue(valueEl, targetType);
    }
 
    private static object? ReadValue(JsonElement el, Type targetType)
    {
        if (el.ValueKind == JsonValueKind.Null) return null;
 
        // Handle Nullable<T> → unwrap to T
        Type innerType = Nullable.GetUnderlyingType(targetType) ?? targetType;
 
        // ── Primitive / value-type ──────────────────────────────────────────
        if (IsPrimitiveLike(innerType))
            return ReadPrimitive(el, innerType);
 
        // ── Dictionary ──────────────────────────────────────────────────────
        if (el.ValueKind == JsonValueKind.Object && typeof(IDictionary).IsAssignableFrom(innerType))
            return ReadDict(el, innerType);
 
        // ── Object with properties ──────────────────────────────────────────
        if (el.ValueKind == JsonValueKind.Object)
            return ReadObject(el, innerType);
 
        // ── Array / List ────────────────────────────────────────────────────
        if (el.ValueKind == JsonValueKind.Array)
            return ReadList(el, innerType);
 
        // ── Fallback: raw primitive read ────────────────────────────────────
        return ReadPrimitive(el, innerType);
    }
 
    private static object ReadPrimitive(JsonElement el, Type t)
    {
        string raw = el.ValueKind == JsonValueKind.String
            ? el.GetString()!
            : el.GetRawText();
 
        if (t == typeof(string))         return el.GetString()!;
        if (t == typeof(bool))           return el.GetBoolean();
        if (t == typeof(byte))           return el.GetByte();
        if (t == typeof(sbyte))          return el.GetSByte();
        if (t == typeof(short))          return el.GetInt16();
        if (t == typeof(ushort))         return el.GetUInt16();
        if (t == typeof(int))            return el.GetInt32();
        if (t == typeof(uint))           return el.GetUInt32();
        if (t == typeof(long))           return el.GetInt64();
        if (t == typeof(ulong))          return el.GetUInt64();
        if (t == typeof(float))          return el.GetSingle();
        if (t == typeof(double))         return el.GetDouble();
        if (t == typeof(decimal))        return decimal.Parse(raw);
        if (t == typeof(char))           return raw.Length > 0 ? raw[0] : '\0';
        if (t == typeof(Guid))           return Guid.Parse(raw);
        if (t == typeof(DateTime))       return DateTime.Parse(raw);
        if (t == typeof(DateTimeOffset)) return DateTimeOffset.Parse(raw);
        if (t == typeof(TimeSpan))       return TimeSpan.Parse(raw);
        if (t == typeof(Uri))            return new Uri(raw);
        if (t == typeof(Version))        return Version.Parse(raw);
        if (t.IsEnum)                    return Enum.Parse(t, raw);
 
        // last resort
        return Convert.ChangeType(raw, t);
    }
 
    private static IDictionary ReadDict(JsonElement el, Type dictType)
    {
        // Build a concrete Dictionary<TKey,TValue> or plain Hashtable
        Type keyType   = dictType.IsGenericType ? dictType.GetGenericArguments()[0] : typeof(string);
        Type valueType = dictType.IsGenericType ? dictType.GetGenericArguments()[1] : typeof(object);
 
        // If the declared type is an interface (IDictionary<,>), construct Dictionary<,>
        Type concrete = dictType.IsInterface || dictType.IsAbstract
            ? typeof(Dictionary<,>).MakeGenericType(keyType, valueType)
            : dictType;
 
        var dict = (IDictionary)Activator.CreateInstance(concrete)!;
 
        foreach (var prop in el.EnumerateObject())
        {
            object key = keyType == typeof(string)
                ? prop.Name
                : Convert.ChangeType(prop.Name, keyType);
            dict[key] = ReadNode(prop.Value, valueType);
        }
        return dict;
    }
 
    private static object ReadObject(JsonElement el, Type targetType)
    {
        // If targetType is object/interface and no $t, return a Dictionary<string,object>
        if (targetType == typeof(object) || targetType.IsInterface)
        {
            var fallback = new Dictionary<string, object?>();
            foreach (var prop in el.EnumerateObject())
                fallback[prop.Name] = ReadNode(prop.Value, typeof(object));
            return fallback;
        }
 
        object instance = Activator.CreateInstance(targetType)
            ?? throw new InvalidOperationException($"Cannot create instance of {targetType}");
 
        foreach (var prop in GetProperties(targetType))
        {
            if (el.TryGetProperty(prop.Name, out var val) && prop.CanWrite)
                prop.SetValue(instance, ReadNode(val, prop.PropertyType));
        }
        return instance;
    }
 
    private static object ReadList(JsonElement el, Type targetType)
    {
        Type elemType = targetType.IsArray
            ? targetType.GetElementType()!
            : targetType.IsGenericType
                ? targetType.GetGenericArguments()[0]
                : typeof(object);
 
        // Build a List<T> first, then convert to array if needed
        Type listType = typeof(List<>).MakeGenericType(elemType);
        var  list     = (IList)Activator.CreateInstance(listType)!;
 
        foreach (var item in el.EnumerateArray())
            list.Add(ReadNode(item, elemType));
 
        if (targetType.IsArray)
        {
            var arr = Array.CreateInstance(elemType, list.Count);
            list.CopyTo(arr, 0);
            return arr;
        }
 
        // If target is a concrete IList type (List<T>, etc.) return directly
        if (targetType.IsAssignableFrom(listType)) return list;
 
        // Otherwise try to construct the target type from the list
        return Activator.CreateInstance(targetType, list)
               ?? throw new InvalidOperationException($"Cannot construct {targetType} from list.");
    }
 
    // ════════════════════════════════════════════════════════════════════════
    // HELPERS
    // ════════════════════════════════════════════════════════════════════════
 
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
 
    /// <summary>
    /// Types whose values are JSON leaf nodes — do NOT recurse into their properties.
    /// </summary>
    private static bool IsPrimitiveLike(Type t)
    {
        t = Nullable.GetUnderlyingType(t) ?? t;
        return t.IsPrimitive      // bool, byte, sbyte, char, short, ushort,
                                  // int, uint, long, ulong, float, double
            || t == typeof(string)
            || t == typeof(decimal)
            || t == typeof(DateTime)
            || t == typeof(DateTimeOffset)
            || t == typeof(TimeSpan)
            || t == typeof(Guid)
            || t == typeof(Uri)
            || t == typeof(Version)
            || t.IsEnum;
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

    private static Type ResolveType(string code)
    {
        // 1. Short code table
        if (CodeToType.TryGetValue(code, out var t)) return t;
 
        // 2. Cache hit
        if (NameToType.TryGetValue(code, out t)) return t!;
 
        // 3. Type.GetType (handles assembly-qualified names)
        t = Type.GetType(code);
        if (t is not null) { NameToType[code] = t; return t; }
 
        // 4. Scan loaded assemblies by FullName or Name
        t = AppDomain.CurrentDomain
                     .GetAssemblies()
                     .SelectMany(a => { try { return a.GetTypes(); } catch { return []; } })
                     .FirstOrDefault(x => x.FullName == code || x.Name == code);
 
        if (t is not null) { NameToType[code] = t; return t; }
 
        throw new InvalidOperationException(
            $"DynTypeSerializer: cannot resolve type '{code}'. " +
            $"If this is a user type, ensure the assembly is loaded.");
    }
 
    private static PropertyInfo[] GetProperties(Type t)
        => PropCache.GetOrAdd(t, static type =>
            type.GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .Where(p => p.CanRead && p.GetIndexParameters().Length == 0)
                .ToArray());
                
}