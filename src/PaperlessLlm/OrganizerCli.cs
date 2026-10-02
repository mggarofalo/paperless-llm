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
                probe organization     Check the configured text policy and name resolution with a synthetic receipt
                check                  Check Paperless access, review tag and saved auth
                --version              Show release version

                Set PPLLM_DRY_RUN=true to suppress mutations. Changed documents receive needs review.
                Configuration and deployment: docs/operations.md and docs/authentication.md.
                """);
            return 0;
        }
        var stateDirectory = Setting("STATE_DIRECTORY", "/data/state");
        var interval = TimeSpan.FromSeconds(Integer("POLL_SECONDS", 3600, 60, 86400));
        if (command is "status" or "health") return await ReadStatusAsync(command, stateDirectory, interval, ct);

        var model = Setting("MODEL", "gpt-6-sol");
        var runner = new PiRunner(new(Setting("RUNNER_HOME", Path.Combine(Setting("AUTH_DIRECTORY", "/data/auth"), "pi")),
            Setting("RUNNER_BRIDGE", Path.Combine(AppContext.BaseDirectory, "runner", "bridge.mjs")),
            Setting("NODE", "node"), Reasoning: "low"));
        if (command == "auth") return await AuthAsync(args, runner, ct);
        if (command == "models") { Console.WriteLine(JsonSerializer.Serialize(await runner.ListModelsAsync(ct))); return 0; }
        if (command == "probe") return await ProbeAsync(args, runner, model, ct);
        return await RunWorkerAsync(command, args, runner, model, stateDirectory, interval, ct);
    }

    private static async Task<int> ReadStatusAsync(string command, string stateDirectory, TimeSpan interval, CancellationToken ct)
    {
        var status = await OrganizerStatusReader.ReadAsync(stateDirectory, ct);
        if (command == "status") { Console.WriteLine(JsonSerializer.Serialize(status)); return 0; }
        return status is not null && status.PauseReason is null && status.LastActivityAt is { } activity
            && DateTimeOffset.UtcNow - activity < interval + TimeSpan.FromMinutes(15) ? 0 : 1;
    }

    private static async Task<int> AuthAsync(string[] args, PiRunner runner, CancellationToken ct)
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

    private static async Task<int> ProbeAsync(string[] args, PiRunner runner, string model, CancellationToken ct)
    {
        if (args.Length == 2 && args[1] == "organization")
        {
            return await ProbeOrganizationAsync(runner, model, ct);
        }
        if (args.Length != 1) throw new ArgumentException("Use probe or probe organization.");
        var schema = JsonDocument.Parse("""{"type":"object","properties":{"ready":{"type":"boolean"}},"required":["ready"],"additionalProperties":false}""").RootElement.Clone();
        var result = await runner.RunAsync(model, "Return only JSON matching the supplied schema.",
            "This is a synthetic transport check with a one-pixel image. Return ready true.", [SyntheticImage], schema, ct);
        using var parsed = JsonDocument.Parse(result);
        if (parsed.RootElement.ValueKind != JsonValueKind.Object || parsed.RootElement.EnumerateObject().Count() != 1
            || !parsed.RootElement.TryGetProperty("ready", out var ready) || ready.ValueKind != JsonValueKind.True)
            throw new InferenceException("Synthetic probe returned an invalid result.");
        Console.WriteLine("Synthetic JSON/image transport probe passed. This does not measure OCR accuracy. No Paperless documents were accessed."); return 0;
    }

    private static async Task<int> RunWorkerAsync(string command, string[] args, PiRunner runner, string model, string stateDirectory, TimeSpan interval, CancellationToken ct)
    {
        if (command is not ("worker" or "once" or "check" or "retry" or "reprocess")) throw new ArgumentException("Unknown command. Run --help.");
        var paperlessOptions = new PaperlessOptions { BaseUrl = new Uri(Required("PAPERLESS_URL")), TokenFile = Required("PAPERLESS_TOKEN_FILE") };
        using var reader = new PaperlessClient(paperlessOptions);
        using var writer = new PaperlessWriter(paperlessOptions);
        var reviewTag = Setting("TAG", "needs review");
        var prompt = new OrganizationPrompt(Setting("PROMPT_FILE", OrganizationPrompt.DefaultPath), model);
        if (command is "check" or "worker" or "once")
            if (await CheckSetupAsync(command, prompt, runner, reader, reviewTag, model, ct)) return 0;
        var options = new OrganizerOptions
        {
            StateDirectory = stateDirectory,
            SourceUrl = paperlessOptions.BaseUrl.AbsoluteUri,
            Model = model,
            ReviewTag = reviewTag,
            UseExistingOcr = true,
            PollInterval = interval,
            BatchSize = Integer("BATCH_SIZE", 5, 1, 100),
            BackfillLimit = Integer("BACKFILL_LIMIT", 0, 0, 100),
            MaxStateBytes = (long)Integer("MAX_STATE_MIB", 2048, 100, 1048576) * 1024 * 1024
        };
        var builder = Host.CreateApplicationBuilder(Array.Empty<string>());
        builder.Logging.ClearProviders();
        builder.Logging.AddJsonConsole(o => o.TimestampFormat = "yyyy-MM-ddTHH:mm:ss.fffZ");
        builder.Services.AddSingleton(options);
        builder.Services.AddSingleton<IPaperlessClient>(reader);
        builder.Services.AddSingleton<IIntentRunner>(new RunnerAdapter(runner));
        builder.Services.AddSingleton<IIntentContextBuilder>(prompt);
        builder.Services.AddSingleton<IIntentSynchronizer>(new IntentSynchronizer(reader, writer,
            Path.Combine(Setting("AUDIT_DIRECTORY", "/data/audit"), "operations"), reviewTag, Boolean("DRY_RUN", false)));
        builder.Services.AddSingleton<IDocumentRenderer>(new ImageRenderer());
        builder.Services.AddSingleton<OrganizerWorker>();
        if (command == "worker") builder.Services.AddHostedService(sp => sp.GetRequiredService<OrganizerWorker>());
        using var host = builder.Build();
        var worker = host.Services.GetRequiredService<OrganizerWorker>();
        await ExecuteWorkerCommandAsync(command, args, worker, host, ct);
        return 0;
    }

    private static async Task<int> ProbeOrganizationAsync(PiRunner runner, string model, CancellationToken ct)
    {
        var synthetic = new PaperlessDocument(1, "Synthetic receipt", "CEDAR MARKET\nReceipt\nTransaction date: 2026-04-02\nTOTAL $15.99\nPaid cash",
            "2025-01-01", null, null, null, [], null, null, "synthetic");
        var taxonomy = new PaperlessTaxonomy([new(1, "receipts"), new(2, "inbox", true)],
            [new(1, "Cedar Market")], [new(1, "Receipt")]);
        var context = await new OrganizationPrompt(Setting("PROMPT_FILE", OrganizationPrompt.DefaultPath), model)
            .BuildAsync(synthetic, taxonomy, 0, ct);
        var raw = await runner.RunAsync(model, context.Instructions, context.Prompt, [], context.Schema, ct);
        using var parsedIntent = JsonDocument.Parse(NamedIntentContract.Resolve(raw, synthetic, taxonomy, 0));
        var intent = parsedIntent.RootElement;
        if (intent.GetProperty("date").GetProperty("value").GetString() != "2026-04-02"
            || intent.GetProperty("correspondent").GetProperty("value").GetInt32() != 1
            || intent.GetProperty("document_type").GetProperty("value").GetInt32() != 1
            || intent.GetProperty("add_tags").GetArrayLength() != 1
            || intent.GetProperty("add_tags")[0].GetProperty("id").GetInt32() != 1)
            throw new InferenceException("Synthetic organization probe did not recover the expected receipt fields.");
        Console.WriteLine(JsonSerializer.Serialize(new
        {
            OrganizationProbe = "passed",
            Model = model,
            Reasoning = "low",
            context.PromptSha256,
            Note = "Synthetic receipt only. No Paperless documents accessed or changed; not an accuracy benchmark."
        }));
        return 0;
    }

    private static async Task<bool> CheckSetupAsync(string command, OrganizationPrompt prompt, PiRunner runner, PaperlessClient reader, string reviewTag, string model, CancellationToken ct)
    {
        await prompt.ReadAsync(ct);
        if (!await runner.StatusAsync(ct)) throw new AuthException("Sign in with auth login before starting the worker.", true);
        var taxonomy = await reader.GetTaxonomyAsync(ct);
        SetupValidation.RequireReviewTag(taxonomy, reviewTag);
        if (!(await runner.ListModelsAsync(ct)).Contains(model)) throw new ArgumentException("The configured model is absent from the runner catalog.");
        if (command == "check")
        {
            var latest = await reader.GetLatestDocumentIdAsync(ct);
            Console.WriteLine(JsonSerializer.Serialize(new
            {
                SavedAuth = true,
                VisibleLatestDocumentId = latest,
                Note = "Read access checked. Run probe to verify inference. Confirm document change permissions separately; check makes no writes."
            })); return true;
        }
        return false;
    }

    private static async Task ExecuteWorkerCommandAsync(string command, string[] args, OrganizerWorker worker, IHost host, CancellationToken ct)
    {
        if (command is "retry" or "reprocess")
        {
            if (args.Length != 2 || !int.TryParse(args[1], out int id) || id <= 0) throw new ArgumentException("Specify a positive enrolled document ID.");
            await worker.RetryAsync(id, command == "reprocess", ct);
            Console.WriteLine("Job queued. Start the worker or run once.");
        }
        else if (command == "once") Console.WriteLine(JsonSerializer.Serialize(await worker.RunOnceAsync(ct)));
        else await host.RunAsync(ct);
    }

    private sealed class RunnerAdapter(PiRunner runner) : IIntentRunner
    {
        public Task<string> GenerateAsync(string model, string instructions, string prompt, IReadOnlyList<string> images, JsonElement schema, CancellationToken ct)
            => runner.RunAsync(model, instructions, prompt, images, schema, ct);
    }
    private static string Setting(string name, string fallback) => Environment.GetEnvironmentVariable("PPLLM_" + name) ?? fallback;
    private static string Required(string name) => Environment.GetEnvironmentVariable("PPLLM_" + name) is { Length: > 0 } value ? value : throw new ArgumentException($"Set PPLLM_{name}.");
    private static int Integer(string name, int fallback, int min, int max) => int.TryParse(Setting(name, fallback.ToString()), out int value) && value >= min && value <= max
        ? value : throw new ArgumentException($"PPLLM_{name} must be between {min} and {max}.");
    private static bool Boolean(string name, bool fallback) => bool.TryParse(Setting(name, fallback.ToString()), out bool value)
        ? value : throw new ArgumentException($"PPLLM_{name} must be true or false.");
}
