using System.Buffers;
using System.Text;
using System.Text.Json;

namespace MojaveDesktopRadio.Ipc;

/// <summary>
/// Wire format: one UTF-8 JSON object per line ("\n"). Every message carries "v" (protocol
/// version) and "type". Unknown fields are ignored, unknown types are answered with an error.
/// See docs/PROTOCOL.md.
/// </summary>
public static class Protocol
{
    public const int Version = 1;
    public const string DefaultPipeName = "MojaveDesktopRadio";
    public const int MaxLineBytes = 16 * 1024;
    public const string ServerName = "MDR-BridgeHost";

    public static string ServerVersion { get; } =
        typeof(Protocol).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";

    public static string Build(string type, Action<Utf8JsonWriter>? body = null)
    {
        var buffer = new ArrayBufferWriter<byte>(256);
        using (var w = new Utf8JsonWriter(buffer, new JsonWriterOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }))
        {
            w.WriteStartObject();
            w.WriteNumber("v", Version);
            w.WriteString("type", type);
            body?.Invoke(w);
            w.WriteEndObject();
        }

        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    public static string Error(long? id, string code, string message) => Build("error", w =>
    {
        if (id.HasValue) w.WriteNumber("id", id.Value);
        w.WriteString("code", code);
        w.WriteString("message", message);
    });

    public static string Ack(long id, string result) => Build("ack", w =>
    {
        w.WriteNumber("id", id);
        w.WriteBoolean("ok", true);
        w.WriteString("result", result);
    });
}

/// <summary>A parsed inbound message (flat object; nested values are ignored).</summary>
public sealed class InboundMessage
{
    private readonly Dictionary<string, JsonElement> _fields;

    private InboundMessage(int version, string type, Dictionary<string, JsonElement> fields)
    {
        Version = version;
        Type = type;
        _fields = fields;
    }

    public int Version { get; }
    public string Type { get; }

    public static bool TryParse(string line, out InboundMessage? message, out string? error)
    {
        message = null;
        error = null;
        try
        {
            using var doc = JsonDocument.Parse(line, new JsonDocumentOptions { MaxDepth = 4 });
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
            {
                error = "message must be a JSON object";
                return false;
            }

            var fields = new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);
            foreach (var p in doc.RootElement.EnumerateObject())
                fields[p.Name] = p.Value.Clone();

            var version = fields.TryGetValue("v", out var v) && v.TryGetInt32(out var vi) ? vi
                : fields.TryGetValue("version", out var v2) && v2.TryGetInt32(out var vi2) ? vi2 : 0;
            var type = fields.TryGetValue("type", out var t) && t.ValueKind == JsonValueKind.String ? t.GetString() ?? "" : "";
            if (type.Length == 0)
            {
                error = "missing \"type\"";
                return false;
            }

            message = new InboundMessage(version, type, fields);
            return true;
        }
        catch (JsonException ex)
        {
            error = $"invalid JSON: {ex.Message}";
            return false;
        }
    }

    public string? GetString(string name) =>
        _fields.TryGetValue(name, out var e) ? e.ValueKind switch
        {
            JsonValueKind.String => e.GetString(),
            JsonValueKind.Number => e.GetRawText(),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            _ => null,
        } : null;

    public long? GetLong(string name) =>
        _fields.TryGetValue(name, out var e) && e.ValueKind == JsonValueKind.Number && e.TryGetInt64(out var l) ? l : null;

    public double? GetDouble(string name)
    {
        if (!_fields.TryGetValue(name, out var e)) return null;
        if (e.ValueKind == JsonValueKind.Number && e.TryGetDouble(out var d)) return d;
        if (e.ValueKind == JsonValueKind.String && double.TryParse(e.GetString(), System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var ds)) return ds;
        if (e.ValueKind == JsonValueKind.True) return 1;
        if (e.ValueKind == JsonValueKind.False) return 0;
        return null;
    }

    public bool? GetBool(string name)
    {
        if (!_fields.TryGetValue(name, out var e)) return null;
        return e.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Number => e.TryGetDouble(out var d) ? d != 0 : null,
            JsonValueKind.String => e.GetString() is { } s ? s is "1" or "true" or "on" : null,
            _ => null,
        };
    }

    public bool Has(string name) => _fields.ContainsKey(name);
}
