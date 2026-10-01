using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using PaperlessLlm.Auth;
using PaperlessLlm.Inference;
using PaperlessLlm.Intent;
using PaperlessLlm.Organizer;
using PaperlessLlm.Paperless;
using PaperlessLlm.Runner;
using PaperlessLlm.Sync;

namespace PaperlessLlm;

public static class OrganizerCli
{
    internal const string SyntheticImage = "data:image/png;base64,iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+ip1sAAAAASUVORK5CYII=";

    public static async Task<int> RunAsync(string[] args, CancellationToken ct)
    {
        var command = args.FirstOrDefault() ?? "worker";
        if (command is "version" or "--version")
        {
            Console.WriteLine(typeof(Program).Assembly.GetName().Version!.ToString(3)); return 0;
        }
        if (command is "help" or "--help")
        {
            Console.WriteLine("""
                Paperless LLM — scheduled automatic document organization
                worker                 Discover new documents and apply validated changes hourly
                once                   Run one bounded discovery/processing cycle
                status                 Show durable jobs and schedule without network access
                health                 Check schedule freshness and pauses
                retry ID               Retry a failed job with its saved intent
                reprocess ID           Explicitly regenerate an enrolled document (stop worker first)
                auth login             Print a device code to approve in any browser; no inbound port
                auth status            Report whether this deployment has saved ChatGPT auth
                auth logout            Remove this deployment's local ChatGPT credentials
                models                 Show the provider's model catalog (not entitlement proof)
                probe                  Check model/image transport with a synthetic JSON request
                check                  Check Paperless access, review tag and saved auth
                --version              Show release version

                Set PPLLM_DRY_RUN=true to suppress mutations. Changed documents receive needs review.
                Configuration and deployment: docs/operations.md and docs/authentication.md.
                """);
            return 0;
        }
        var stateDirectory = Setting("STATE_DIRECTORY", "/data/state");
        var interval = TimeSpan.FromSeconds(Integer("POLL_SECONDS", 3600, 60, 86400));
        if (command is "status" or "health")
        {
            var status = await OrganizerStatusReader.ReadAsync(stateDirectory, ct);
            if (command == "status") { Console.WriteLine(JsonSerializer.Serialize(status)); return 0; }
            return status is not null && status.PauseReason is null && status.LastActivityAt is { } activity
                && DateTimeOffset.UtcNow - activity < interval + TimeSpan.FromMinutes(15) ? 0 : 1;
        }

        var model = Setting("MODEL", "gpt-6-luna");
        var runner = new PiRunner(new(Setting("RUNNER_HOME", Path.Combine(Setting("AUTH_DIRECTORY", "/data/auth"), "pi")),
            Setting("RUNNER_BRIDGE", Path.Combine(AppContext.BaseDirectory, "runner", "bridge.mjs")),
            Setting("NODE", "node")));
        if (command == "auth")
        {
            if (args.Length != 2) throw new ArgumentException("Use auth login, auth status or auth logout.");
            switch (args[1])
            {
                case "login":
                    await runner.LoginAsync(code =>
                    {
                        Console.WriteLine($"Open {code.Url} in your browser and enter code: {code.Code}");
                        Console.WriteLine("Waiting for approval. Keep this command running.");
                    }, ct);
                    Console.WriteLine("Sign-in completed. Credentials are stored in the auth volume."); return 0;
                case "status": Console.WriteLine(JsonSerializer.Serialize(new { SignedIn = await runner.StatusAsync(ct) })); return 0;
                case "logout": await runner.LogoutAsync(ct); Console.WriteLine("Local credentials removed. Use your ChatGPT account settings for remote revocation."); return 0;
                default: throw new ArgumentException("Use auth login, auth status or auth logout.");
            }
        }
        if (command == "models") { Console.WriteLine(JsonSerializer.Serialize(await runner.ListModelsAsync(ct))); return 0; }
        if (command == "probe")
        {
            var schema = JsonDocument.Parse("""{"type":"object","properties":{"ready":{"type":"boolean"}},"required":["ready"],"additionalProperties":false}""").RootElement.Clone();
            var result = await runner.RunAsync(model, "Return only JSON matching the supplied schema.",
                "This is a synthetic transport check with a one-pixel image. Return ready true.", [SyntheticImage], schema, ct);
            using var parsed = JsonDocument.Parse(result);
            if (parsed.RootElement.ValueKind != JsonValueKind.Object || parsed.RootElement.EnumerateObject().Count() != 1
                || !parsed.RootElement.TryGetProperty("ready", out var ready) || ready.ValueKind != JsonValueKind.True)
                throw new InferenceException("Synthetic probe returned an invalid result.");
            Console.WriteLine("Synthetic JSON/image transport probe passed. This does not measure OCR accuracy. No Paperless documents were accessed."); return 0;
        }
        if (command is not ("worker" or "once" or "check" or "retry" or "reprocess")) throw new ArgumentException("Unknown command. Run --help.");
        var paperlessOptions = new PaperlessOptions { BaseUrl = new Uri(Required("PAPERLESS_URL")), TokenFile = Required("PAPERLESS_TOKEN_FILE") };
        using var reader = new PaperlessClient(paperlessOptions);
        using var writer = new PaperlessWriter(paperlessOptions);
        var reviewTag = Setting("TAG", "needs review");
        if (command is "check" or "worker" or "once")
        {
            if (!await runner.StatusAsync(ct)) throw new AuthException("Sign in with auth login before starting the worker.", true);
            var taxonomy = await reader.GetTaxonomyAsync(ct);
            SetupValidation.RequireReviewTag(taxonomy, reviewTag);
            if (!(await runner.ListModelsAsync(ct)).Contains(model)) throw new ArgumentException("The configured model is absent from the runner catalog.");
            if (command == "check")
            {
                var latest = await reader.GetLatestDocumentIdAsync(ct);
                Console.WriteLine(JsonSerializer.Serialize(new { SavedAuth = true, VisibleLatestDocumentId = latest,
                    Note = "Read access checked. Run probe to verify inference. Confirm document change permissions separately; check makes no writes." })); return 0;
            }
        }
        var options = new OrganizerOptions
        {
            StateDirectory = stateDirectory, SourceUrl = paperlessOptions.BaseUrl.AbsoluteUri, Model = model, ReviewTag = reviewTag,
            PollInterval = interval, BatchSize = Integer("BATCH_SIZE", 5, 1, 100), BackfillLimit = Integer("BACKFILL_LIMIT", 0, 0, 100),
            MaxStateBytes = (long)Integer("MAX_STATE_MIB", 2048, 100, 1048576) * 1024 * 1024
        };
        var builder = Host.CreateApplicationBuilder(Array.Empty<string>());
        builder.Logging.ClearProviders();
        builder.Logging.AddJsonConsole(o => o.TimestampFormat = "yyyy-MM-ddTHH:mm:ss.fffZ");
        builder.Services.AddSingleton(options);
        builder.Services.AddSingleton<IPaperlessClient>(reader);
        builder.Services.AddSingleton<IIntentRunner>(new RunnerAdapter(runner));
        builder.Services.AddSingleton<IIntentContextBuilder>(new ContextBuilder(model));
        builder.Services.AddSingleton<IIntentSynchronizer>(new IntentSynchronizer(reader, writer,
            Path.Combine(Setting("AUDIT_DIRECTORY", "/data/audit"), "operations"), reviewTag, Boolean("DRY_RUN", false)));
        builder.Services.AddSingleton<IDocumentRenderer>(new ImageRenderer());
        builder.Services.AddSingleton<OrganizerWorker>();
        if (command == "worker") builder.Services.AddHostedService(sp => sp.GetRequiredService<OrganizerWorker>());
        using var host = builder.Build();
        var worker = host.Services.GetRequiredService<OrganizerWorker>();
        if (command is "retry" or "reprocess")
        {
            if (args.Length != 2 || !int.TryParse(args[1], out int id) || id <= 0) throw new ArgumentException("Specify a positive enrolled document ID.");
            await worker.RetryAsync(id, command == "reprocess", ct);
            Console.WriteLine("Job queued. Start the worker or run once.");
        }
        else if (command == "once") Console.WriteLine(JsonSerializer.Serialize(await worker.RunOnceAsync(ct)));
        else await host.RunAsync(ct);
        return 0;
    }

    private sealed class RunnerAdapter(PiRunner runner) : IIntentRunner
    {
        public Task<string> GenerateAsync(string model, string instructions, string prompt, IReadOnlyList<string> images, JsonElement schema, CancellationToken ct)
            => runner.RunAsync(model, instructions, prompt, images, schema, ct);
    }
    private sealed class ContextBuilder(string model) : IIntentContextBuilder
    {
        public Task<IntentContext> BuildAsync(PaperlessDocument source, PaperlessTaxonomy taxonomy, int pageCount, CancellationToken ct)
            => Task.FromResult(new IntentContext(IntentPrompt.Instructions, IntentPrompt.Build(source, taxonomy, pageCount),
                DocumentIntent.Schema, IntentPrompt.Fingerprint(model)));
    }
    private static string Setting(string name, string fallback) => Environment.GetEnvironmentVariable("PPLLM_" + name) ?? fallback;
    private static string Required(string name) => Environment.GetEnvironmentVariable("PPLLM_" + name) is { Length: > 0 } value ? value : throw new ArgumentException($"Set PPLLM_{name}.");
    private static int Integer(string name, int fallback, int min, int max) => int.TryParse(Setting(name, fallback.ToString()), out int value) && value >= min && value <= max
        ? value : throw new ArgumentException($"PPLLM_{name} must be between {min} and {max}.");
    private static bool Boolean(string name, bool fallback) => bool.TryParse(Setting(name, fallback.ToString()), out bool value)
        ? value : throw new ArgumentException($"PPLLM_{name} must be true or false.");
}
