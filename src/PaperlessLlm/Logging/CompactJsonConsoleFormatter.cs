using System.Globalization;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Logging.Console;

namespace PaperlessLlm.Logging;

public sealed class CompactJsonConsoleFormatter() : ConsoleFormatter(FormatterName)
{
    public const string FormatterName = "compact-json";

    public override void Write<TState>(in LogEntry<TState> logEntry,
        IExternalScopeProvider? scopeProvider, TextWriter textWriter)
    {
        var message = logEntry.Formatter(logEntry.State, logEntry.Exception);
        if (message is null && logEntry.Exception is null) return;

        using var buffer = new MemoryStream();
        using (var json = new Utf8JsonWriter(buffer))
        {
            json.WriteStartObject();
            json.WriteString("Timestamp", DateTimeOffset.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture));
            json.WriteNumber("EventId", logEntry.EventId.Id);
            json.WriteString("LogLevel", logEntry.LogLevel.ToString());
            json.WriteString("Category", logEntry.Category);
            json.WriteString("Message", message);
            if (logEntry.Exception is not null) json.WriteString("Exception", logEntry.Exception.ToString());
            WriteState(json, logEntry.State);
            json.WriteEndObject();
        }
        textWriter.WriteLine(Encoding.UTF8.GetString(buffer.ToArray()));
    }

    private static void WriteState<TState>(Utf8JsonWriter json, TState state)
    {
        if (state is null) return;
        json.WriteStartObject("State");
        if (state is IEnumerable<KeyValuePair<string, object?>> fields)
        {
            foreach (var field in fields)
            {
                if (field.Key == "{OriginalFormat}") continue;
                json.WritePropertyName(field.Key);
                WriteValue(json, field.Value);
            }
        }
        else json.WriteString("Message", state.ToString());
        json.WriteEndObject();
    }

    private static void WriteValue(Utf8JsonWriter json, object? value)
    {
        // Match console logging's scalar treatment; do not serialize object graphs.
        if (value is null or bool or byte or sbyte or short or ushort or int or uint
            or long or ulong or float or double or decimal)
            JsonSerializer.Serialize(json, value);
        else
            json.WriteStringValue(Convert.ToString(value, CultureInfo.InvariantCulture));
    }
}
