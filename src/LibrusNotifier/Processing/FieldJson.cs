using System.Text.Encodings.Web;
using System.Text.Json;
using LibrusNotifier.Librus;

namespace LibrusNotifier.Processing;

/// <summary>Zapis pól elementu jako obiekt JSON (etykieta → wartość) z zachowaniem kolejności.</summary>
public static class FieldJson
{
    private static readonly JsonWriterOptions WriterOptions = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    public static string Serialize(IEnumerable<Field> fields)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, WriterOptions))
        {
            writer.WriteStartObject();
            var written = new HashSet<string>(StringComparer.Ordinal);
            foreach (var field in fields)
                if (written.Add(field.Label))
                    writer.WriteString(field.Label, field.Value);
            writer.WriteEndObject();
        }
        return System.Text.Encoding.UTF8.GetString(stream.ToArray());
    }

    public static IReadOnlyList<Field> Deserialize(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return [];
        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Object) return [];
            return document.RootElement.EnumerateObject()
                .Select(p => new Field(p.Name, p.Value.ValueKind == JsonValueKind.String ? p.Value.GetString() ?? "" : p.Value.ToString()))
                .ToList();
        }
        catch (JsonException)
        {
            return [];
        }
    }
}
