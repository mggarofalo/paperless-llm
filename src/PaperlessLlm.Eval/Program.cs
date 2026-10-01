using System.Text.Json;
using PaperlessLlm.Eval;
using PaperlessLlm.Intent;

return await EvalCli.RunAsync(args);

internal static class EvalCli
{
    public static async Task<int> RunAsync(string[] args)
    {
        try
        {
            if (args.Length == 0 || args[0] is "help" or "--help" or "-h") { Usage(); return args.Length == 0 ? 2 : 0; }
            var options = Parse(args.Skip(1).ToArray());
            switch (args[0])
            {
                case "export": await ExportAsync(options); break;
                case "score": await ScoreAsync(options); break;
                case "compare": await CompareAsync(options); break;
                case "experiment": await ExperimentAsync(options); break;
                default: throw new ArgumentException($"Unknown command '{args[0]}'.");
            }
            return 0;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidDataException or IOException or JsonException)
        {
            Console.Error.WriteLine(ex.Message);
            return 2;
        }
    }

    private static async Task ExperimentAsync(Dictionary<string, string> o)
    {
        var cases = Argument(o, "cases", 0);
        var recipe = Required(o, "recipe");
        var output = Required(o, "out");
        var split = Required(o, "split");
        var concurrency = int.TryParse(o.GetValueOrDefault("concurrency", "1"), out var parsed) ? parsed : 0;
        var timeout = int.TryParse(o.GetValueOrDefault("timeout", "300"), out var parsedTimeout) ? parsedTimeout : 0;
        await ExperimentRunner.RunAsync(new ExperimentOptions(cases, recipe, output, concurrency,
            o.GetValueOrDefault("model", "gpt-6-luna"), timeout, split));
        Console.WriteLine($"Experiment outputs and provenance written to {Path.GetFullPath(output)}");
    }

    private static async Task ExportAsync(Dictionary<string, string> o)
    {
        var cases = await ReadCases(Argument(o, "cases", 0));
        var split = Required(o, "split");
        if (split is not ("train" or "holdout")) throw new ArgumentException("--split must be train or holdout.");
        var selected = cases.Where(c => c.Split == split).ToArray();
        if (selected.Length == 0) throw new InvalidDataException($"No cases found for split '{split}'.");
        var output = Path.GetFullPath(Argument(o, "out", 1));
        Directory.CreateDirectory(output);
        var instructions = o.TryGetValue("instructions-file", out var file)
            ? await File.ReadAllTextAsync(file)
            : IntentPrompt.Instructions;
        var tasks = new List<string>();
        foreach (var c in selected)
        {
            var input = JsonDocument.Parse(IntentPrompt.Build(c.ToDocument(), c.ToTaxonomy(), c.PageCount)).RootElement.Clone();
            var task = new
            {
                case_id = c.CaseId,
                split = c.Split,
                page_images = c.Document.PageImages,
                system_prompt = instructions,
                schema = DocumentIntent.Schema,
                input
            };
            tasks.Add(JsonSerializer.Serialize(task, EvalJson.Options));
        }
        await File.WriteAllLinesAsync(Path.Combine(output, "tasks.jsonl"), tasks);
        Console.WriteLine($"Exported {tasks.Count} {split} case(s) to {output}");
    }

    private static async Task ScoreAsync(Dictionary<string, string> o)
    {
        var split = Required(o, "split");
        if (split is not ("train" or "holdout")) throw new ArgumentException("--split must be train or holdout.");
        var cases = await ReadCases(Argument(o, "cases", 0));
        var (outputs, invalid) = await ReadOutputs(Argument(o, "outputs", 1), cases.Select(c => c.CaseId).ToHashSet(StringComparer.Ordinal));
        var report = EvalEngine.Score(Required(o, "version"), split, o.GetValueOrDefault("model"),
            o.GetValueOrDefault("harness"), cases, outputs, invalid);
        var target = Path.GetFullPath(Required(o, "out"));
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        await File.WriteAllTextAsync(target, JsonSerializer.Serialize(report, new JsonSerializerOptions(EvalJson.Options) { WriteIndented = true }));
        if (o.TryGetValue("references", out var referencesPath))
        {
            var references = EvalJson.Read<Dictionary<string, RichReference>>(await File.ReadAllTextAsync(referencesPath));
            var richRows = cases.Where(c => c.Split == split && references.ContainsKey(c.CaseId))
                .Select(c => RegionScorer.Score(c, outputs.GetValueOrDefault(c.CaseId), references[c.CaseId])).ToArray();
            var regionsPath = Path.Combine(Path.GetDirectoryName(target)!, Path.GetFileNameWithoutExtension(target) + ".regions.json");
            await File.WriteAllTextAsync(regionsPath, JsonSerializer.Serialize(richRows, new JsonSerializerOptions(EvalJson.Options) { WriteIndented = true }));
            Console.WriteLine($"Region and signed-amount report: {regionsPath}");
        }
        Console.WriteLine($"{report.Version} ({report.Split}): {report.TotalCases} cases, {report.MissingOutputs} missing, {report.SchemaFailures} schema failures, {report.ValidatorFailures} validator failures, {report.CriticalFailures} critical failures. Report: {target}");
    }

    private static async Task CompareAsync(Dictionary<string, string> o)
    {
        var baseline = EvalJson.Read<EvalReport>(await File.ReadAllTextAsync(Required(o, "baseline")));
        var candidate = EvalJson.Read<EvalReport>(await File.ReadAllTextAsync(Required(o, "candidate")));
        if (baseline.Split != candidate.Split) throw new InvalidDataException("Baseline and candidate splits differ.");
        if (!baseline.Cases.Select(c => c.CaseId).ToHashSet(StringComparer.Ordinal)
                .SetEquals(candidate.Cases.Select(c => c.CaseId)))
            throw new InvalidDataException("Baseline and candidate case sets differ.");
        var keys = baseline.Checks.Keys.Union(candidate.Checks.Keys).Order(StringComparer.Ordinal);
        var rows = keys.Select(key => new
        {
            check = key,
            baseline = baseline.Checks.GetValueOrDefault(key, new FieldScore(0, 0)),
            candidate = candidate.Checks.GetValueOrDefault(key, new FieldScore(0, 0)),
            delta_passes = candidate.Checks.GetValueOrDefault(key, new FieldScore(0, 0)).Passed - baseline.Checks.GetValueOrDefault(key, new FieldScore(0, 0)).Passed
        });
        var comparison = new
        {
            split = baseline.Split, baseline_version = baseline.Version, candidate_version = candidate.Version,
            baseline_critical_failures = baseline.CriticalFailures, candidate_critical_failures = candidate.CriticalFailures,
            critical_failure_delta = candidate.CriticalFailures - baseline.CriticalFailures,
            baseline_schema_failures = baseline.SchemaFailures, candidate_schema_failures = candidate.SchemaFailures,
            baseline_ocr_key_facts_matched = baseline.OcrKeyFactsMatched,
            baseline_ocr_key_facts_total = baseline.OcrKeyFactsTotal,
            candidate_ocr_key_facts_matched = candidate.OcrKeyFactsMatched,
            candidate_ocr_key_facts_total = candidate.OcrKeyFactsTotal,
            ocr_key_fact_match_delta = candidate.OcrKeyFactsMatched - baseline.OcrKeyFactsMatched,
            checks = rows
        };
        var target = Path.GetFullPath(Required(o, "out"));
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        await File.WriteAllTextAsync(target, JsonSerializer.Serialize(comparison, new JsonSerializerOptions(EvalJson.Options) { WriteIndented = true }));
        Console.WriteLine($"Comparison written to {target}");
    }

    private static async Task<List<EvalCase>> ReadCases(string path)
    {
        var cases = new List<EvalCase>();
        var ids = new HashSet<string>(StringComparer.Ordinal);
        var lines = await File.ReadAllLinesAsync(path);
        for (var index = 0; index < lines.Length; index++)
        {
            var line = lines[index];
            var number = index + 1;
            if (string.IsNullOrWhiteSpace(line)) continue;
            EvalCase c;
            try { c = EvalJson.Read<EvalCase>(line); }
            catch (JsonException ex) { throw new InvalidDataException($"Invalid case JSON at line {number}: {ex.Message}"); }
            if (string.IsNullOrWhiteSpace(c.CaseId) || !ids.Add(c.CaseId)) throw new InvalidDataException($"Case IDs must be nonempty and unique (line {number}).");
            if (c.Split is not ("train" or "holdout")) throw new InvalidDataException($"Case '{c.CaseId}' has invalid split.");
            if (c.PageCount is < 0 or > IntentPrompt.MaxPages) throw new InvalidDataException($"Case '{c.CaseId}' has invalid page_count.");
            if (c.Expected.Title.Action is not ("keep" or "set" or "preserve" or "ignore") || c.Expected.Date.Action is not ("keep" or "set" or "ignore") ||
                c.Expected.Correspondent.Action is not ("keep" or "set" or "ignore") || c.Expected.DocumentType.Action is not ("keep" or "set" or "ignore"))
                throw new InvalidDataException($"Case '{c.CaseId}' expected field action must be keep, set, preserve, or ignore as allowed for that field.");
            ValidateExpected(c);
            cases.Add(c);
        }
        if (cases.Count == 0) throw new InvalidDataException("Case file contains no cases.");
        return cases;
    }

    private static void ValidateExpected(EvalCase c)
    {
        static bool HasValue(ExpectedField field) => field.Value is not null;
        if (c.Expected.Title.Action == "set" && !HasValue(c.Expected.Title) ||
            c.Expected.Date.Action == "set" && !HasValue(c.Expected.Date) ||
            c.Expected.Correspondent.Action == "set" && !HasValue(c.Expected.Correspondent) ||
            c.Expected.DocumentType.Action == "set" && !HasValue(c.Expected.DocumentType))
            throw new InvalidDataException($"Case '{c.CaseId}' set references require a value.");
        if (c.Expected.Ocr.MustReplace && c.PageCount == 0)
            throw new InvalidDataException($"Case '{c.CaseId}' requires OCR replacement but has no pages.");
        var tagIds = c.Taxonomy.Tags.Select(t => t.Id).ToHashSet();
        var correspondentIds = c.Taxonomy.Correspondents.Select(t => t.Id).ToHashSet();
        var typeIds = c.Taxonomy.DocumentTypes.Select(t => t.Id).ToHashSet();
        if (c.Expected.AddTagIds.Any(id => !tagIds.Contains(id)) || c.Expected.ProtectedTagIds.Any(id => !tagIds.Contains(id)))
            throw new InvalidDataException($"Case '{c.CaseId}' reference tag IDs must exist in its taxonomy.");
        if (c.Expected.ProtectedTagIds.Any(id => !c.Document.Tags.Contains(id)))
            throw new InvalidDataException($"Case '{c.CaseId}' protected tag IDs must be present in the input document.");
        if (c.Expected.Correspondent.Action == "set" && (!TryExpectedId(c.Expected.Correspondent.Value, out var correspondentId) || !correspondentIds.Contains(correspondentId)) ||
            c.Expected.DocumentType.Action == "set" && (!TryExpectedId(c.Expected.DocumentType.Value, out var typeId) || !typeIds.Contains(typeId)))
            throw new InvalidDataException($"Case '{c.CaseId}' metadata reference IDs must exist in its taxonomy.");
    }

    private static bool TryExpectedId(object? value, out int id)
    {
        id = 0;
        if (value is JsonElement element) return element.ValueKind == JsonValueKind.Number && element.TryGetInt32(out id) && id > 0;
        if (value is int number) { id = number; return number > 0; }
        id = 0;
        return false;
    }

    private static async Task<(Dictionary<string, string> Outputs, int Invalid)> ReadOutputs(string path, HashSet<string> caseIds)
    {
        var outputs = new Dictionary<string, string>(StringComparer.Ordinal);
        var invalid = 0;
        if (Directory.Exists(path))
        {
            foreach (var file in Directory.EnumerateFiles(path, "*.json").Order(StringComparer.Ordinal))
            {
                var id = Path.GetFileNameWithoutExtension(file);
                if (id.Equals("provenance", StringComparison.OrdinalIgnoreCase) || !caseIds.Contains(id)) continue;
                if (!outputs.TryAdd(id, await File.ReadAllTextAsync(file))) invalid++;
            }
            return (outputs, invalid);
        }
        var lines = await File.ReadAllLinesAsync(path);
        for (var i = 0; i < lines.Length; i++)
        {
            if (string.IsNullOrWhiteSpace(lines[i])) continue;
            try
            {
                using var doc = JsonDocument.Parse(lines[i]);
                var root = doc.RootElement;
                var id = root.GetProperty("case_id").GetString();
                if (string.IsNullOrWhiteSpace(id)) { invalid++; continue; }
                if (!caseIds.Contains(id)) continue;
                string raw;
                if (root.TryGetProperty("intent", out var intent)) raw = intent.GetRawText();
                else if (root.TryGetProperty("intent_json", out var intentJson) && intentJson.ValueKind == JsonValueKind.String) raw = intentJson.GetString()!;
                else { invalid++; continue; }
                if (!outputs.TryAdd(id, raw)) invalid++;
            }
            catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException) { invalid++; }
        }
        return (outputs, invalid);
    }

    private static Dictionary<string, string> Parse(string[] args)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var i = 0; i < args.Length; i++)
        {
            var key = args[i];
            if (!key.StartsWith("--", StringComparison.Ordinal))
            {
                var positionals = result.GetValueOrDefault("$positionals", "");
                result["$positionals"] = string.IsNullOrEmpty(positionals) ? key : positionals + "\n" + key;
                continue;
            }
            if (i + 1 >= args.Length || args[i + 1].StartsWith("--", StringComparison.Ordinal))
                throw new ArgumentException($"Expected --option value near '{key}'.");
            if (!result.TryAdd(key[2..], args[++i])) throw new ArgumentException($"Duplicate option '{key}'.");
        }
        return result;
    }

    private static string Required(Dictionary<string, string> options, string name) =>
        options.TryGetValue(name, out var value) ? value : throw new ArgumentException($"Missing --{name}.");

    private static string Argument(Dictionary<string, string> options, string name, int position)
    {
        if (options.TryGetValue(name, out var named)) return named;
        var positional = options.GetValueOrDefault("$positionals", "").Split('\n', StringSplitOptions.RemoveEmptyEntries);
        if (position < positional.Length) return positional[position];
        throw new ArgumentException($"Missing {name} input.");
    }

    private static void Usage() => Console.WriteLine("""
        Offline intent evaluation (no network or Paperless access)
          export CASES.jsonl DIR --split train|holdout [--instructions-file FILE]
          score CASES.jsonl OUTPUTS.jsonl|DIR --split train|holdout --version NAME --out report.json [--model NAME] [--harness NAME] [--references FILE]
          compare --baseline report.json --candidate report.json --out comparison.json
          experiment --cases CASES.jsonl --split train|holdout --recipe RECIPE.json --out DIR --concurrency N [--model gpt-6-luna] [--timeout 300]
        """);
}
