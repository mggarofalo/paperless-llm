namespace PaperlessLlm.Alerts;

public enum AlertPriority { Default, High }

/// <summary>An operator notification. Never carries document content, metadata or credentials.</summary>
public sealed record OperatorAlert(string Title, string Message, AlertPriority Priority, string Tag);

/// <summary>Delivers operator notifications. Implementations must be bounded and throw only <see cref="AlertDeliveryException"/>.</summary>
public interface IAlertSink
{
    Task SendAsync(OperatorAlert alert, CancellationToken ct);
}

public sealed class AlertDeliveryException(string code) : Exception(code);
