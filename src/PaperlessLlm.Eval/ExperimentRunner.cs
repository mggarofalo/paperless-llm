using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using PaperlessLlm.Intent;

namespace PaperlessLlm.Eval;

public sealed record ExperimentRecipe
{
    public required string Id { get; init; }
    public string? Parent { get; init; }
    public string? Hypothesis { get; init; }
    public required string PromptFile { get; init; }
    public string? AuxiliaryPromptFile { get; init; }
    public string Pipeline { get; init; } = "single";
    public string Reasoning { get; init; } = "medium";
    public string OcrContext { get; init; } = "full";
    public string AuxiliaryContext { get; init; } = "full";
    public string DraftContext { get; init; } = "all";
    public string Taxonomy { get; init; } = "full";
    public string ContextOrder { get; init; } = "instructions-first";
    public string ImageMode { get; init; } = "full";
    public string? ImageVariants { get; init; }
    public bool IncludeFinalImages { get; init; } = true;
    public string OutputContract { get; init; } = "ids";
}

public sealed record ExperimentOptions(string Cases, string Recipe, string Output, int Concurrency,
    string Model = "gpt-6-luna", int TimeoutSeconds = 300, string Split = "train")
{ }

public static partial class ExperimentRunner
{
    private const int MaxStreamCharacters = 4 * 1024 * 1024;
    public static async Task RunAsync(ExperimentOptions options, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (options.Concurrency is < 1 or > 32) throw new ArgumentException("Concurrency must be 1..32.");
        if (options.TimeoutSeconds is < 10 or > 1800) throw new ArgumentException("Timeout must be 10..1800 seconds.");
        var allCases = await ReadCasesAsync(options.Cases, cancellationToken);
        var cases = SelectCasesForSplit(allCases, options.Split);
        ValidateOutputCaseIds(cases);
        var recipePath = Path.GetFullPath(options.Recipe);
        var recipe = EvalJson.Read<ExperimentRecipe>(await File.ReadAllTextAsync(recipePath, cancellationToken));
        ValidateRecipe(recipe);
        var recipeDir = Path.GetDirectoryName(recipePath)!;
        Dictionary<string, ImageVariantCase>? imageVariants = null;
        if (recipe.ImageVariants is not null)
            imageVariants = EvalJson.Read<Dictionary<string, ImageVariantCase>>(await File.ReadAllTextAsync(Path.GetFullPath(recipe.ImageVariants, recipeDir), cancellationToken));
        var finalInstructions = await File.ReadAllTextAsync(Path.GetFullPath(recipe.PromptFile, recipeDir), cancellationToken);
        var auxInstructions = recipe.AuxiliaryPromptFile is null ? "" :
            await File.ReadAllTextAsync(Path.GetFullPath(recipe.AuxiliaryPromptFile, recipeDir), cancellationToken);
        var output = Path.GetFullPath(options.Output);
        EnsureFreshOutputDirectory(output);
        var started = DateTimeOffset.UtcNow;
        var failures = 0;
        using var semaphore = new SemaphoreSlim(options.Concurrency);
        var tasks = cases.Select(async c =>
        {
            await semaphore.WaitAsync(cancellationToken);
            try { await RunCaseAsync(c, recipe, imageVariants, recipeDir, finalInstructions, auxInstructions, options, output, cancellationToken); }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Interlocked.Increment(ref failures);
                await File.WriteAllTextAsync(Path.Combine(output, OutputName(c.CaseId) + ".error.txt"), ex.ToString(), CancellationToken.None);
            }
            finally { semaphore.Release(); }
        });
        try { await Task.WhenAll(tasks); }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            await WriteRunProvenanceAsync(cancelled: true);
            throw;
        }
        await WriteRunProvenanceAsync(cancelled: false);

        async Task WriteRunProvenanceAsync(bool cancelled)
        {
            var provenance = new
            {
                recipe = recipe,
                recipe_sha256 = Convert.ToHexStringLower(SHA256.HashData(await File.ReadAllBytesAsync(recipePath, CancellationToken.None))),
                prompt_sha256 = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(finalInstructions))),
                auxiliary_prompt_sha256 = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(auxInstructions))),
                model = options.Model,
                reasoning = recipe.Reasoning,
                concurrency = options.Concurrency,
                timeout_seconds = options.TimeoutSeconds,
                started_utc = started,
                finished_utc = DateTimeOffset.UtcNow,
                split = options.Split,
                cases = cases.Count,
                failures,
                cancelled,
                runner = "codex exec --json --ephemeral --ignore-user-config --sandbox read-only"
            };
            await File.WriteAllTextAsync(Path.Combine(output, "provenance.json"), JsonSerializer.Serialize(provenance, new JsonSerializerOptions(EvalJson.Options) { WriteIndented = true }), CancellationToken.None);
        }
    }

    public static List<EvalCase> SelectCasesForSplit(IReadOnlyList<EvalCase> cases, string split)
    {
        if (split is not ("train" or "holdout")) throw new ArgumentException("Split must be train or holdout.", nameof(split));
        var selected = cases.Where(c => string.Equals(c.Split, split, StringComparison.OrdinalIgnoreCase)).ToList();
        if (selected.Count == 0) throw new InvalidDataException($"Case file contains no cases for split '{split}'.");
        return selected;
    }

    public static void ValidateOutputCaseIds(IEnumerable<EvalCase> cases)
    {
        foreach (var c in cases) _ = OutputName(c.CaseId);
    }

    public static void EnsureFreshOutputDirectory(string output)
    {
        if (Directory.Exists(output) && Directory.EnumerateFileSystemEntries(output).Any())
            throw new InvalidDataException($"Output directory '{output}' must be empty; choose a fresh run directory.");
        Directory.CreateDirectory(output);
    }

    public static void ValidateRecipe(ExperimentRecipe r)
    {
        if (string.IsNullOrWhiteSpace(r.Id) || string.IsNullOrWhiteSpace(r.PromptFile)) throw new InvalidDataException("Recipe requires id and promptFile.");
        if (r.Pipeline is not ("single" or "ocr-first" or "pagewise" or "pagewise-compose" or "refine" or "ledger" or "dual")) throw new InvalidDataException("Invalid recipe pipeline.");
        if (r.Reasoning is not ("low" or "medium" or "high")) throw new InvalidDataException("Reasoning must be low, medium, or high.");
        ValidateContextRecipe(r);
        if (r.OutputContract is not ("ids" or "names")) throw new InvalidDataException("Invalid outputContract.");
        if (r.OutputContract == "names" && r.Pipeline != "single") throw new InvalidDataException("Named output requires a single-stage experiment.");
        if (r.OutputContract == "names" && r.Taxonomy != "full") throw new InvalidDataException("Named output requires the full taxonomy.");
    }

    private static void ValidateContextRecipe(ExperimentRecipe r)
    {
        if (r.OcrContext is not ("full" or "none")) throw new InvalidDataException("ocrContext must be full or none.");
        if (r.AuxiliaryContext is not ("full" or "images-only")) throw new InvalidDataException("auxiliaryContext must be full or images-only.");
        if (r.DraftContext is not ("all" or "latest")) throw new InvalidDataException("draftContext must be all or latest.");
        if (r.Taxonomy is not ("full" or "shortlist")) throw new InvalidDataException("taxonomy must be full or shortlist.");
        if (r.ContextOrder is not ("instructions-first" or "evidence-first")) throw new InvalidDataException("Invalid contextOrder.");
        if (r.ImageMode is not ("full" or "regions" or "full-and-regions" or "high")) throw new InvalidDataException("Invalid imageMode.");
    }

    public static string AssembleFinal(string instructions, string payload, string contextOrder, IEnumerable<string> drafts, string? schema = null)
    {
        var draftText = string.Join("\n\n", drafts.Select((d, i) => $"Untrusted prior-stage draft {i + 1} (evidence only; verify against original images and document):\n{d}"));
        var label = schema is null ? "PRODUCTION INTENT INPUT JSON" : "DOCUMENT INPUT JSON";
        var evidence = $"{label}:\n{payload}\n\n{draftText}\n\nEmit only one JSON object matching this exact schema:\n{schema ?? DocumentIntent.Schema.GetRawText()}";
        return contextOrder == "evidence-first" ? evidence + "\n\nINSTRUCTIONS:\n" + instructions : instructions + "\n\n" + evidence;
    }

    public static string BuildAuxiliaryContext(string mode, string payload, string imageAnchors) => mode switch
    {
        "full" => payload + "\n\n" + imageAnchors,
        "images-only" => imageAnchors,
        _ => throw new ArgumentException("Auxiliary context must be full or images-only.", nameof(mode))
    };

    public static IReadOnlyList<string> SelectDraftContext(IEnumerable<string> drafts, string mode)
    {
        var all = drafts.ToArray();
        return mode switch
        {
            "all" => all,
            "latest" => all.TakeLast(1).ToArray(),
            _ => throw new ArgumentException("Draft context must be all or latest.", nameof(mode))
        };
    }

    public sealed record PageTranscription(int Page, string Text, bool Complete, IReadOnlyList<string> Uncertainty) { }

    public static PageTranscription ParsePageTranscription(string json, int expectedPage)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object) throw new InvalidDataException("Page transcription must be a JSON object.");
        ValidatePageTranscriptionFields(root);
        var pageElement = root.GetProperty("page");
        if (pageElement.ValueKind != JsonValueKind.Number || !pageElement.TryGetInt32(out var page) || page != expectedPage)
            throw new InvalidDataException($"Page transcription index must equal {expectedPage}.");
        if (root.GetProperty("text").ValueKind != JsonValueKind.String || root.GetProperty("complete").ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            throw new InvalidDataException("Page transcription text and complete fields have invalid types.");
        var uncertainty = root.GetProperty("uncertainty");
        if (uncertainty.ValueKind != JsonValueKind.Array || uncertainty.EnumerateArray().Any(x => x.ValueKind != JsonValueKind.String))
            throw new InvalidDataException("Page transcription uncertainty must be an array of strings.");
        return new(page, root.GetProperty("text").GetString()!, root.GetProperty("complete").GetBoolean(), uncertainty.EnumerateArray().Select(x => x.GetString()!).ToArray());
    }

    private static void ValidatePageTranscriptionFields(JsonElement root)
    {
        var allowed = new HashSet<string>(["page", "text", "complete", "uncertainty"], StringComparer.Ordinal);
        var properties = root.EnumerateObject().ToArray();
        if (properties.Length != allowed.Count || properties.Any(p => !allowed.Contains(p.Name)) || allowed.Any(name => !root.TryGetProperty(name, out _)))
            throw new InvalidDataException("Page transcription must contain exactly page, text, complete, and uncertainty.");
    }

    public static string ApplyPagewiseComposition(string finalIntentJson, IReadOnlyList<PageTranscription> pages)
    {
        if (pages.Count == 0 || pages.Select((p, i) => p.Page == i + 1).Any(ok => !ok))
            throw new InvalidDataException("Page transcription records must be nonempty and ordered from page 1.");
        var intent = JsonNode.Parse(finalIntentJson) as JsonObject ?? throw new InvalidDataException("Final response was not a JSON object.");
        var complete = pages.All(p => p.Complete && p.Uncertainty.Count == 0) && pages.Any(p => !string.IsNullOrWhiteSpace(p.Text));
        var existingUncertainty = ReadMetadataUncertainty(intent);
        var mergedUncertainty = existingUncertainty.Concat(pages.SelectMany(p => p.Uncertainty.Select(u => $"Page {p.Page}: {u}")))
            .Concat(pages.Where(p => !p.Complete).Select(p => $"Page {p.Page}: source transcription marked incomplete."))
            .Distinct(StringComparer.Ordinal).ToArray();
        string[] finalUncertainty = !complete && mergedUncertainty.Length == 0
            ? ["OCR was kept because no page transcription contained nonblank text."]
            : mergedUncertainty;
        intent["ocr"] = ComposeOcr(pages, complete);
        intent["uncertainty"] = new JsonArray(finalUncertainty.Select(x => (JsonNode?)JsonValue.Create(x)).ToArray());
        return intent.ToJsonString(new JsonSerializerOptions(EvalJson.Options) { WriteIndented = false });
    }

    private static JsonObject ComposeOcr(IReadOnlyList<PageTranscription> pages, bool complete)
    {
        return new JsonObject
        {
            ["action"] = complete ? "set" : "keep",
            ["pages"] = complete ? new JsonArray(pages.Select(p => (JsonNode?)new JsonObject { ["page"] = p.Page, ["text"] = p.Text, ["complete"] = true, ["uncertainty"] = new JsonArray() }).ToArray()) : new JsonArray(),
            ["evidence"] = complete ? new JsonArray("OCR is composed deterministically from the validated, independent source-page transcriptions.") : new JsonArray(),
        };
    }

    private static List<string> ReadMetadataUncertainty(JsonObject intent)
    {
        var existingUncertainty = new List<string>();
        if (intent.TryGetPropertyValue("uncertainty", out var priorUncertainty))
        {
            if (priorUncertainty is not JsonArray priorArray || priorArray.Any(x => x is not JsonValue v || !v.TryGetValue<string>(out _)))
                throw new InvalidDataException("Final metadata uncertainty must be an array of strings.");
            existingUncertainty.AddRange(priorArray.Select(x => x!.GetValue<string>()));
        }
        return existingUncertainty;
    }

    public static ProcessStartInfo CreateCodexStartInfo(string model, string reasoning, string workingDirectory, IReadOnlyList<string> images)
    {
        var utf8 = new UTF8Encoding(false);
        var psi = new ProcessStartInfo("codex")
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardInputEncoding = utf8,
            StandardOutputEncoding = utf8,
            StandardErrorEncoding = utf8,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        foreach (var arg in new[] { "exec", "--json", "--ephemeral", "--ignore-user-config", "--skip-git-repo-check", "--disable", "shell_tool", "--disable", "multi_agent", "--sandbox", "read-only", "--model", model, "--cd", workingDirectory, "-c", "model_reasoning_effort=\"" + reasoning + "\"", "-c", "web_search=\"disabled\"" }) psi.ArgumentList.Add(arg);
        foreach (var image in images) { psi.ArgumentList.Add("--image"); psi.ArgumentList.Add(image); }
        psi.ArgumentList.Add("-");
        return psi;
    }

    public sealed record ImageVariantPage
    {
        public required int Page { get; init; }
        public required string Full { get; init; }
        public string? High { get; init; }
        public List<string> Regions { get; init; } = [];
    }
    public sealed record ImageVariantCase
    {
        public List<string> Full { get; init; } = [];
        public List<string> High { get; init; } = [];
        public List<string> Regions { get; init; } = [];
        [System.Text.Json.Serialization.JsonPropertyName("full-and-regions")]
        public List<string> FullAndRegions { get; init; } = [];
        public List<ImageVariantPage> Pages { get; init; } = [];
    }

    public static string ExtractFinalMessage(string events)
    {
        string? last = null;
        foreach (var line in events.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;
            RejectToolEvent(root);
            if (TryFinalMessage(root, out var message)) last = message;
        }
        if (string.IsNullOrWhiteSpace(last)) throw new InvalidDataException("Codex returned no final agent message.");
        return last;
    }

    private static bool IsToolType(string? type) => type is not null &&
        (type.Contains("tool", StringComparison.OrdinalIgnoreCase) || type.EndsWith("_call", StringComparison.OrdinalIgnoreCase));

    private static void RejectToolEvent(JsonElement root)
    {
        if (root.TryGetProperty("type", out var type) && IsToolType(type.GetString()))
            throw new InvalidDataException("Codex emitted a tool event; result rejected.");
        if (!root.TryGetProperty("item", out var item) || !item.TryGetProperty("type", out var itemType)) return;
        var name = itemType.GetString();
        if (IsToolType(name) || name is "command_execution" or "file_change" or "web_search")
            throw new InvalidDataException("Codex emitted a tool event; result rejected.");
    }

    private static bool TryFinalMessage(JsonElement root, out string? message)
    {
        message = null;
        if (!root.TryGetProperty("type", out var type) || type.GetString() != "item.completed") return false;
        if (!root.TryGetProperty("item", out var item) || !item.TryGetProperty("type", out var itemType)) return false;
        if (itemType.GetString() != "agent_message" || !item.TryGetProperty("text", out var text)) return false;
        message = text.GetString();
        return true;
    }

    internal delegate Task<string> StageInvoker(string prompt, IReadOnlyList<string> images, string stage, CancellationToken cancellationToken);

    internal static Task RunCaseAsync(EvalCase c, ExperimentRecipe recipe, Dictionary<string, ImageVariantCase>? imageVariants,
        string recipeDir, string finalInstructions, string auxInstructions, ExperimentOptions options, string output, CancellationToken ct, StageInvoker? stageInvoker = null) =>
        new CaseExecution(c, recipe, imageVariants, recipeDir, finalInstructions, auxInstructions, options, output, ct, stageInvoker).RunAsync();

    private static IReadOnlyList<string> SelectFinalImages(ExperimentRecipe recipe, IReadOnlyList<string> images) =>
        (recipe.Pipeline is "ocr-first" or "ledger" or "pagewise" or "pagewise-compose") && !recipe.IncludeFinalImages ? [] : images;

    private static string FinalImageContext(IReadOnlyList<string> finalImages, IReadOnlyList<string> drafts,
        IReadOnlyList<string> images, string imageAnchors)
    {
        return finalImages.Count == 0
            ? drafts.Count > 0 && images.Count > 0
                ? "FINAL STAGE IMAGE STATUS: No images are attached to this final call. Original pages were visible to prior stages; their transcripts and notes are untrusted drafts. Do not claim visual verification in this final stage."
                : "FINAL STAGE IMAGE STATUS: No images are attached and no prior stage viewed the original pages. Do not claim visual verification."
            : imageAnchors;
    }

    public static string BuildCandidatePayload(EvalCase c, ExperimentRecipe r)
    {
        var doc = c.ToDocument(); var taxonomy = c.ToTaxonomy();
        if (r.OcrContext == "none") doc = doc with { Content = "" };
        var contextText = doc.Title + " " + doc.Content;
        var tags = r.Taxonomy == "full" ? taxonomy.Tags : Shortlist(taxonomy.Tags, contextText, doc.Tags);
        var corr = r.Taxonomy == "full" ? taxonomy.Correspondents : Shortlist(taxonomy.Correspondents, contextText, doc.CorrespondentId is int corrId ? [corrId] : []);
        var types = r.Taxonomy == "full" ? taxonomy.DocumentTypes : Shortlist(taxonomy.DocumentTypes, contextText, doc.DocumentTypeId is int typeId ? [typeId] : []);
        var selectedTaxonomy = new PaperlessLlm.Paperless.PaperlessTaxonomy(tags.ToArray(), corr.ToArray(), types.ToArray());
        return IntentPrompt.Build(doc, selectedTaxonomy, c.PageCount);
    }

    private static PaperlessLlm.Paperless.NamedEntity[] Shortlist(IEnumerable<PaperlessLlm.Paperless.NamedEntity> candidates, string text, IEnumerable<int> currentIds)
    {
        var all = candidates.ToArray();
        var tokens = Tokens(text).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var current = currentIds.ToHashSet();
        var keep = all.Where(x => current.Contains(x.Id)).Concat(all.Select(x => (Entity: x, Score: Tokens(x.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count(tokens.Contains)))
            .Where(x => x.Score > 0).OrderByDescending(x => x.Score).ThenBy(x => x.Entity.Id).Take(50).Select(x => x.Entity));
        return keep.DistinctBy(x => x.Id).ToArray();
        static IEnumerable<string> Tokens(string s) => System.Text.RegularExpressions.Regex.Matches(s.ToLowerInvariant(), "[\\p{L}\\p{N}]{3,}").Select(m => m.Value);
    }

    private static async Task<List<EvalCase>> ReadCasesAsync(string path, CancellationToken ct)
    {
        var result = new List<EvalCase>(); var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var line in await File.ReadAllLinesAsync(path, ct)) if (!string.IsNullOrWhiteSpace(line))
        { var c = EvalJson.Read<EvalCase>(line); if (!ids.Add(c.CaseId)) throw new InvalidDataException("Case IDs must be unique."); result.Add(c); }
        if (result.Count == 0) throw new InvalidDataException("Case file contains no cases."); return result;
    }
    private static (long InputTokens, long OutputTokens) ReadUsage(string events)
    {
        long input = 0, output = 0;
        foreach (var line in events.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                using var doc = JsonDocument.Parse(line);
                if (!doc.RootElement.TryGetProperty("usage", out var usage)) continue;
                if (usage.TryGetProperty("input_tokens", out var i) && i.TryGetInt64(out var inputValue)) input += inputValue;
                if (usage.TryGetProperty("output_tokens", out var o) && o.TryGetInt64(out var outputValue)) output += outputValue;
            }
            catch (JsonException) { /* partial tail can occur after a timeout or capture limit */ }
        }
        return (input, output);
    }
    private static string SafeName(string value) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)))[..16];
    private static bool IsReservedWindowsName(string stem)
    {
        return stem.Equals("CON", StringComparison.OrdinalIgnoreCase) ||
            stem.Equals("PRN", StringComparison.OrdinalIgnoreCase) ||
            stem.Equals("AUX", StringComparison.OrdinalIgnoreCase) ||
            stem.Equals("NUL", StringComparison.OrdinalIgnoreCase) ||
            System.Text.RegularExpressions.Regex.IsMatch(stem, @"^(COM|LPT)[1-9¹²³]$", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
    }

    private static string OutputName(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new InvalidDataException($"Case ID '{value}' cannot be represented as an output filename.");
        const string crossPlatformInvalid = "<>:\"|?*";
        var stem = value.TrimEnd(' ', '.').Split('.')[0];
        if (value.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ||
            value.IndexOfAny(crossPlatformInvalid.ToCharArray()) >= 0 || value.Contains('/') || value.Contains('\\') ||
            value.EndsWith(' ') || value.EndsWith('.') || value is "." or ".." || IsReservedWindowsName(stem))
            throw new InvalidDataException($"Case ID '{value}' cannot be represented as an output filename.");
        return value;
    }
}
