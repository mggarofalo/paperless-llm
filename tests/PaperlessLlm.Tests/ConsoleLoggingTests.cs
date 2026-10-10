using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using PaperlessLlm.Logging;

namespace PaperlessLlm.Tests;

public class ConsoleLoggingTests
{
    [Fact]
    public void Structured_event_keeps_typed_fields_and_escapes_message_without_template()
    {
        var state = new KeyValuePair<string, object?>[]
        {
            new("Completed", 1), new("Failed", 0), new("Enabled", true), new("Optional", null),
            new("Detail", "quoted \"text\"\nnext line"),
            new("{OriginalFormat}", "Organizer poll: completed {Completed}, failed {Failed}")
        };
        var entry = new LogEntry<KeyValuePair<string, object?>[]>(LogLevel.Information,
            "Organizer", new EventId(7), state, null, (_, _) => "completed 1\nfailed 0");
        using var output = new StringWriter();
        new CompactJsonConsoleFormatter().Write(entry, null, output);
        using var document = JsonDocument.Parse(output.ToString());
        var root = document.RootElement;
        Assert.Equal("completed 1\nfailed 0", root.GetProperty("Message").GetString());
        Assert.Equal(7, root.GetProperty("EventId").GetInt32());
        Assert.Equal("Organizer", root.GetProperty("Category").GetString());
        Assert.Equal("Information", root.GetProperty("LogLevel").GetString());
        Assert.True(DateTimeOffset.TryParseExact(root.GetProperty("Timestamp").GetString(),
            "yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal, out _));
        var fields = root.GetProperty("State");
        Assert.False(fields.TryGetProperty("{OriginalFormat}", out _));
        Assert.Equal(1, fields.GetProperty("Completed").GetInt32());
        Assert.Equal(0, fields.GetProperty("Failed").GetInt32());
        Assert.True(fields.GetProperty("Enabled").GetBoolean());
        Assert.Equal(JsonValueKind.Null, fields.GetProperty("Optional").ValueKind);
        Assert.Equal(state[4].Value, fields.GetProperty("Detail").GetString());
        Assert.Single(output.ToString().Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries));
    }

    [Fact]
    public void Exception_without_message_is_retained()
    {
        var entry = new LogEntry<string?>(LogLevel.Error, "Organizer", default, null,
            new InvalidOperationException("synthetic failure"), (_, _) => null!);
        using var output = new StringWriter();
        new CompactJsonConsoleFormatter().Write(entry, null, output);
        using var document = JsonDocument.Parse(output.ToString());
        Assert.Contains("synthetic failure", document.RootElement.GetProperty("Exception").GetString());
        Assert.False(document.RootElement.TryGetProperty("State", out _));
    }

    [Fact]
    public void Plain_state_is_retained_and_empty_event_is_suppressed()
    {
        using var output = new StringWriter();
        var formatter = new CompactJsonConsoleFormatter();
        var empty = new LogEntry<string?>(LogLevel.Information, "Organizer", default, null, null, (_, _) => null!);
        formatter.Write(empty, null, output);
        Assert.Empty(output.ToString());
        var plain = new LogEntry<string>(LogLevel.Information, "Organizer", default, "started", null, (s, _) => s);
        formatter.Write(plain, null, output);
        using var document = JsonDocument.Parse(output.ToString());
        Assert.Equal("started", document.RootElement.GetProperty("State").GetProperty("Message").GetString());
    }
}
