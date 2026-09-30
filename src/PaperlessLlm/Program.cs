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
            var command = args.FirstOrDefault() ?? "worker";
            if (command is "--version" or "version")
            {
                Console.WriteLine(typeof(Program).Assembly.GetName().Version!.ToString(3));
                return 0;
            }
            if (command is "--help" or "help")
            {
                Console.WriteLine("""
                    Paperless LLM — read-only OCR and metadata review

                    worker           Poll Paperless and save private review reports
                    once             Run one bounded polling cycle
                    status           Read durable job state without network access
                    health           Check polling freshness and global pause state
                    retry ID         Requeue one failed job (stop worker first)
                    auth login       Authorize ChatGPT plan usage in your browser
                    auth status      Show authorization state without credentials
                    auth logout      Remove this installation's ChatGPT credentials
                    models           List models available to the ChatGPT grant
                    probe            Verify model access with synthetic text only
                    check            Verify both connections before enrolling documents
                    --version        Print release version

                    Configure PPLLM_* environment variables; see docs/operations.md.
                    Paperless credentials are read from PPLLM_PAPERLESS_TOKEN_FILE.
                    """);
                return 0;
            }
            if (command is "status" or "health")
            {
                var status = await WorkerStatusReader.ReadAsync(Setting("STATE_DIRECTORY", "/data/state"), cancellation.Token);
                if (command == "health")
                    return status.Initialized && status.LastPollAt > DateTimeOffset.UtcNow.AddSeconds(
                        -(2 * Integer("POLL_SECONDS", 300, 10, 86400) + 600)) && status.PauseReason is null
                        && !status.AuthenticationPaused ? 0 : 1;
                Console.WriteLine(JsonSerializer.Serialize(status, new JsonSerializerOptions { WriteIndented = true }));
                return 0;
            }
            if (command == "retry")
            {
                if (args.Length != 2 || !int.TryParse(args[1], out int id) || id <= 0)
                    throw new ArgumentException("Use retry DOCUMENT_ID with the worker stopped.");
                await WorkerStatusReader.RetryFailedAsync(Setting("STATE_DIRECTORY", "/data/state"), id, cancellation.Token);
                Console.WriteLine($"Document {id} is queued for another review attempt.");
                return 0;
            }
            using var http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false })
            { Timeout = TimeSpan.FromMinutes(3) };
            var auth = new ChatGptAuth(http, new TokenStore(Setting("AUTH_DIRECTORY", "/data/auth")));
            var responses = new ResponsesClient(http, auth);
            if (command == "auth")
            {
                if (args.Length != 2) throw new ArgumentException("Use auth login, auth status, or auth logout.");
                switch (args[1])
                {
                    case "login":
                        Console.WriteLine("Authorize Paperless LLM to use your ChatGPT plan. Open this URL in your browser:");
                        var status = await auth.LoginAsync(uri => { Console.WriteLine(uri.AbsoluteUri); return Task.CompletedTask; },
                            Integer("AUTH_PORT", 1455, 1024, 65535), Setting("AUTH_BIND", "127.0.0.1"), cancellation.Token);
                        Console.WriteLine(JsonSerializer.Serialize(status));
                        return 0;
                    case "status":
                        Console.WriteLine(JsonSerializer.Serialize(await auth.GetStatusAsync(cancellation.Token)));
                        return 0;
                    case "logout":
                        var logout = await auth.LogoutAsync(cancellation.Token);
                        Console.WriteLine(logout.RemoteRevocationConfirmed
                            ? "Signed out. Remote authorization revoked and local credentials cleared."
                            : "Local credentials cleared; remote revocation was not confirmed. Disconnect this app in ChatGPT settings.");
                        return 0;
                    default: throw new ArgumentException("Use auth login, auth status, or auth logout.");
                }
            }
            if (command == "models")
            {
                Console.WriteLine(JsonSerializer.Serialize(await responses.ListModelsAsync(cancellation.Token), new JsonSerializerOptions { WriteIndented = true }));
                return 0;
            }
            if (command == "probe")
            {
                var result = await responses.CompleteAsync(Setting("MODEL", "gpt-6-luna"), "Follow the user's instruction.",
                    [new InferenceInput("Reply with exactly: paperless-llm ready")], cancellationToken: cancellation.Token);
                if (result.Trim() != "paperless-llm ready") throw new InvalidOperationException("Synthetic probe returned an unexpected response.");
                Console.WriteLine("Synthetic model probe passed. No Paperless documents were accessed.");
                return 0;
            }
            if (command is not ("worker" or "once" or "check")) throw new ArgumentException("Unknown command. Run --help.");
            var paperlessOptions = new PaperlessOptions
            {
                BaseUrl = new Uri(Required("PAPERLESS_URL")),
                TokenFile = Required("PAPERLESS_TOKEN_FILE")
            };
            if (command == "check")
            {
                using var paperless = new PaperlessClient(paperlessOptions);
                var latest = await paperless.GetLatestDocumentIdAsync(cancellation.Token);
                var taxonomy = await paperless.GetTaxonomyAsync(cancellation.Token);
                var tagCount = taxonomy.Tags.Count(t => t.Name.Equals(Setting("TAG", "needs review"), StringComparison.OrdinalIgnoreCase));
                if (tagCount != 1) throw new ArgumentException("The configured review tag must be visible and unambiguous.");
                var models = await responses.ListModelsAsync(cancellation.Token);
                if (!models.Any(m => m.Slug == Setting("MODEL", "gpt-6-luna")))
                    throw new ArgumentException("The configured model is not available to this ChatGPT grant. Run models.");
                Console.WriteLine(JsonSerializer.Serialize(new { ConnectionsVerified = true, VisibleLatestDocumentId = latest,
                    ModelAvailable = true, Note = latest == 0 ? "No documents visible; verify object permissions before enrollment." : "No documents sent to the model; enrollment state unchanged." }));
                return 0;
            }
            var options = new WorkerOptions
            {
                StateDirectory = Setting("STATE_DIRECTORY", "/data/state"),
                Model = Setting("MODEL", "gpt-6-luna"),
                Tag = Setting("TAG", "needs review"),
                BatchSize = Integer("BATCH_SIZE", 5, 1, 100),
                BackfillLimit = Integer("BACKFILL_LIMIT", 0, 0, 100),
                PollInterval = TimeSpan.FromSeconds(Integer("POLL_SECONDS", 300, 10, 86400)),
                MaxAuditBytes = (long)Integer("MAX_AUDIT_MIB", 2048, 100, 1048576) * 1024 * 1024,
                SourceUrl = paperlessOptions.BaseUrl.AbsoluteUri
            };
            var builder = Host.CreateApplicationBuilder(Array.Empty<string>());
            builder.Logging.ClearProviders();
            builder.Logging.AddJsonConsole(o => o.TimestampFormat = "yyyy-MM-ddTHH:mm:ss.fffZ");
            builder.Services.AddSingleton<IPaperlessClient>(new PaperlessClient(paperlessOptions));
            builder.Services.AddSingleton(options);
            builder.Services.AddSingleton(new AuditWriter(Setting("AUDIT_DIRECTORY", "/data/audit")));
            builder.Services.AddSingleton<IProposalGenerator>(new ResponsesProposalGenerator(responses));
            builder.Services.AddSingleton<IDocumentRenderer>(new ImageRenderer());
            builder.Services.AddSingleton<ReviewWorker>();
            if (command == "worker") builder.Services.AddHostedService(sp => sp.GetRequiredService<ReviewWorker>());
            using var host = builder.Build();
            if (command == "once") await host.Services.GetRequiredService<ReviewWorker>().RunOnceAsync(cancellation.Token);
            else await host.RunAsync(cancellation.Token);
            return 0;
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { return 130; }
        catch (Exception ex) when (ex is AuthException or InferenceException or PaperlessException or ArgumentException)
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

    private static string Setting(string name, string fallback) => Environment.GetEnvironmentVariable("PPLLM_" + name) ?? fallback;
    private static string Required(string name) => Environment.GetEnvironmentVariable("PPLLM_" + name) is { Length: > 0 } value
        ? value : throw new ArgumentException($"Set PPLLM_{name} before starting the worker.");
    private static int Integer(string name, int fallback, int min, int max) =>
        int.TryParse(Setting(name, fallback.ToString()), out int value) && value >= min && value <= max
            ? value : throw new ArgumentException($"PPLLM_{name} must be between {min} and {max}.");
}
