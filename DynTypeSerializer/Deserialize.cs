
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
            {
                if (prop.PropertyType == typeof(Type))
                {
                    string typeName = val.GetString() ?? throw new InvalidOperationException("Type property is null");
                    prop.SetValue(instance, Type.GetType(typeName) ?? throw new InvalidOperationException($"Cannot resolve type {typeName}"));
                }
                else
                {
                    prop.SetValue(instance, ReadNode(val, prop.PropertyType));
                }
            }
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


}