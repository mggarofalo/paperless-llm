using System.Reflection;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using PaperlessLlm.Auth;
using PaperlessLlm.Inference;
using PaperlessLlm.Paperless;
using PaperlessLlm.Review;
using PaperlessLlm.Worker;

namespace PaperlessLlm;

public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        using var cancellation = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; cancellation.Cancel(); };
        try
        {
            return await OrganizerCli.RunAsync(args, cancellation.Token);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { return 130; }
        catch (Exception ex) when (ex is AuthException or InferenceException or PaperlessException or ArgumentException or Intent.OrganizationPromptException)
        {
            Console.Error.WriteLine(ex.Message);
            return 1;
        }
        catch (Exception)
        {
            // Untrusted provider responses, documents and credential paths must never reach stdout.
            Console.Error.WriteLine("Operation failed. Check configuration, volume access and service connectivity.");
            return 1;
        }
    }

    internal static bool IsHealthy(WorkerStatus status, DateTimeOffset now, TimeSpan freshness)
    {
        // A bounded batch can outlast the polling interval. Job transitions are
        // heartbeats too, so useful ongoing work is not mistaken for a hung poll.
        var activity = status.Jobs.Select(job => job.UpdatedAt)
            .Append(status.LastPollAt ?? DateTimeOffset.MinValue).Max();
        return status.Initialized && activity > now - freshness && status.PauseReason is null && !status.AuthenticationPaused;
    }

    private static string Setting(string name, string fallback) => Environment.GetEnvironmentVariable("PPLLM_" + name) ?? fallback;
    private static string Required(string name) => Environment.GetEnvironmentVariable("PPLLM_" + name) is { Length: > 0 } value
        ? value : throw new ArgumentException($"Set PPLLM_{name} before starting the worker.");
    private static int Integer(string name, int fallback, int min, int max) =>
        int.TryParse(Setting(name, fallback.ToString()), out int value) && value >= min && value <= max
            ? value : throw new ArgumentException($"PPLLM_{name} must be between {min} and {max}.");
}
