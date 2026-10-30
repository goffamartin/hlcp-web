using System.Text.Json;

namespace Hlcp.Application;

public static class CommandOutput
{
    public static string Format(string message)
    {
        string text = message;
        try
        {
            using var document = JsonDocument.Parse(message, new() { MaxDepth = 16 });
            if (document.RootElement.ValueKind is JsonValueKind.Object or JsonValueKind.Array)
            {
                var formatted = JsonSerializer.Serialize(document.RootElement, new JsonSerializerOptions { WriteIndented = true });
                if (formatted.Length <= 32768) text = formatted;
            }
        }
        catch (JsonException) { }
        return string.Concat(text.Select(c => char.IsControl(c) && c is not ('\r' or '\n' or '\t') ? '�' : c));
    }
}
