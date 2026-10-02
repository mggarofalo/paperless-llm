using System.Text.Json;
using PaperlessLlm.Intent;
using PaperlessLlm.Review;

namespace PaperlessLlm.Eval;

public static class EvalEngine
{
    public static EvalReport Score(string version, string split, string? model, string? harnessVersion,
        IReadOnlyList<EvalCase> cases, IReadOnlyDictionary<string, string> outputs, int invalidJsonOutputs = 0)
    {
        if (split is not ("train" or "holdout")) throw new ArgumentException("Split must be train or holdout.", nameof(split));
        var selected = cases.Where(c => string.Equals(c.Split, split, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (selected.Length == 0) throw new InvalidDataException($"No cases found for split '{split}'.");
        var scoreRows = new List<CaseScore>();
        var checkCounts = new Dictionary<string, (int Passed, int Total)>(StringComparer.Ordinal);
        var validatorFailures = 0;
        var schemaFailures = 0;
        var criticalFailures = 0;

        foreach (var testCase in selected)
        {
            var result = ScoreCase(testCase, outputs);
            var (checks, valid, failure, ocrKeyFactsMatched) = result;
            if (failure is not null && failure != "missing_output") validatorFailures++;
            if (failure is not null && (IsSchemaFailure(failure) || failure == "invalid_intent")) schemaFailures++;
            AccumulateChecks(checkCounts, checks);
            var failed = HasCorrectnessFailure(failure, checks);
            if (testCase.Expected.Critical && failed) criticalFailures++;
            scoreRows.Add(new CaseScore(testCase.CaseId, testCase.Split, testCase.Expected.Critical, valid, failure,
                ocrKeyFactsMatched, testCase.Expected.Ocr.KeyFacts.Count, checks));
        }

        var absent = selected.Count(c => !outputs.ContainsKey(c.CaseId));
        var checksResult = checkCounts.ToDictionary(kv => kv.Key, kv => new FieldScore(kv.Value.Passed, kv.Value.Total), StringComparer.Ordinal);
        checksResult["output.present"] = new FieldScore(selected.Length - absent, selected.Length);
        checksResult["validator.valid"] = new FieldScore(selected.Count(c => scoreRows.First(r => r.CaseId == c.CaseId).Valid), selected.Length);
        return new EvalReport
        {
            Version = version,
            Model = model,
            HarnessVersion = harnessVersion,
            Split = split,
            CreatedUtc = DateTimeOffset.UtcNow,
            TotalCases = selected.Length,
            ImportedOutputs = selected.Count(c => outputs.ContainsKey(c.CaseId)),
            MissingOutputs = absent,
            InvalidJsonOutputs = invalidJsonOutputs,
            ValidatorFailures = validatorFailures,
            SchemaFailures = schemaFailures,
            CriticalFailures = criticalFailures,
            Checks = checksResult,
            Cases = scoreRows,
            OcrKeyFactsMatched = scoreRows.Sum(r => r.OcrKeyFactsMatched),
            OcrKeyFactsTotal = scoreRows.Sum(r => r.OcrKeyFactsTotal)
        };
    }

    private static bool HasCorrectnessFailure(string? failure, Dictionary<string, bool> checks) =>
        failure is not null || checks.Any(check => !check.Value && !check.Key.EndsWith(".no_unnecessary_write", StringComparison.Ordinal));

    private static void AccumulateChecks(Dictionary<string, (int Passed, int Total)> checkCounts, Dictionary<string, bool> checks)
    {
        foreach (var check in checks)
        {
            if (!checkCounts.TryGetValue(check.Key, out var count)) count = (0, 0);
            checkCounts[check.Key] = (count.Passed + (check.Value ? 1 : 0), count.Total + 1);
        }
    }

    private static (Dictionary<string, bool> Checks, bool Valid, string? Failure, int OcrKeyFactsMatched) ScoreCase(
        EvalCase testCase, IReadOnlyDictionary<string, string> outputs)
    {
        var checks = new Dictionary<string, bool>(StringComparer.Ordinal)
        {
            ["output.present"] = false,
            ["validator.valid"] = false,
            ["title.correct"] = false,
            ["title.no_unnecessary_write"] = false,
            ["date.correct"] = false,
            ["date.no_unnecessary_write"] = false,
            ["correspondent.correct"] = false,
            ["correspondent.no_unnecessary_write"] = false,
            ["document_type.correct"] = false,
            ["document_type.no_unnecessary_write"] = false,
            ["tags.expected_added"] = false,
            ["tags.no_unexpected"] = false,
            ["protected_tags.present_in_input"] = false,
            ["protected_tags.not_added"] = false,
            ["ocr.must_replace"] = false,
            ["ocr.key_facts"] = false,
            ["ocr.forbidden_facts_absent"] = false
        };
        foreach (var (field, expected) in new[]
        {
            ("title", testCase.Expected.Title.Action), ("date", testCase.Expected.Date.Action),
            ("correspondent", testCase.Expected.Correspondent.Action), ("document_type", testCase.Expected.DocumentType.Action)
        })
            if (expected == "ignore") checks.Remove(field + ".correct");
        string? failure = null;
        var valid = false;
        var ocrKeyFactsMatched = 0;
        if (!outputs.TryGetValue(testCase.CaseId, out var raw)) failure = "missing_output";
        else
        {
            checks["output.present"] = true;
            try
            {
                var document = testCase.ToDocument();
                var taxonomy = testCase.ToTaxonomy();
                var intent = IntentValidator.Validate(raw, document, taxonomy, testCase.PageCount);
                valid = true;
                checks["validator.valid"] = true;
                ocrKeyFactsMatched = ScoreIntent(testCase, intent, checks);
            }
            catch (ProposalValidationException ex)
            {
                failure = ex.Code;
                if (ex.Code == "protected_tag") checks["protected_tags.not_added"] = false;
            }
            catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException)
            {
                failure = "invalid_intent";
                checks["validator.valid"] = false;
            }
        }

        if (!valid)
            foreach (var key in checks.Keys.Where(k => k.EndsWith(".no_unnecessary_write", StringComparison.Ordinal)).ToArray())
                checks.Remove(key);
        return (checks, valid, failure, ocrKeyFactsMatched);
    }

    private static int ScoreIntent(EvalCase testCase, JsonElement intent, Dictionary<string, bool> checks)
    {
        CheckField("title", testCase.Expected.Title, "title", testCase.Document.Title);
        CheckField("date", testCase.Expected.Date, "date", testCase.Document.Created);
        CheckField("correspondent", testCase.Expected.Correspondent, "correspondent", testCase.Document.CorrespondentId);
        CheckField("document_type", testCase.Expected.DocumentType, "document_type", testCase.Document.DocumentTypeId);

        var actualTags = intent.GetProperty("add_tags").EnumerateArray().Select(t => t.GetProperty("id").GetInt32()).ToHashSet();
        var expectedTags = testCase.Expected.AddTagIds.ToHashSet();
        checks["tags.expected_added"] = expectedTags.IsSubsetOf(actualTags);
        checks["tags.no_unexpected"] = actualTags.IsSubsetOf(expectedTags);
        var protectedInInput = testCase.Expected.ProtectedTagIds.All(testCase.Document.Tags.Contains);
        checks["protected_tags.present_in_input"] = protectedInInput;
        checks["protected_tags.not_added"] = !testCase.Expected.ProtectedTagIds.Any(actualTags.Contains);

        var ocr = intent.GetProperty("ocr");
        var resultingOcr = ocr.GetProperty("action").GetString() == "set"
            ? string.Join("\n", ocr.GetProperty("pages").EnumerateArray().Select(p => p.GetProperty("text").GetString()))
            : testCase.Document.Content;
        var normalizedOcr = Normalize(resultingOcr);
        checks["ocr.must_replace"] = !testCase.Expected.Ocr.MustReplace || ocr.GetProperty("action").GetString() == "set";
        var keyFactsMatched = testCase.Expected.Ocr.KeyFacts.Count(f => normalizedOcr.Contains(Normalize(f), StringComparison.OrdinalIgnoreCase));
        checks["ocr.key_facts"] = keyFactsMatched == testCase.Expected.Ocr.KeyFacts.Count;
        checks["ocr.forbidden_facts_absent"] = testCase.Expected.Ocr.ForbiddenFacts.All(f => !normalizedOcr.Contains(Normalize(f), StringComparison.OrdinalIgnoreCase));
        return keyFactsMatched;

        void CheckField(string name, ExpectedField expected, string intentName, object? currentValue)
        {
            if (expected.Action == "ignore")
            {
                checks.Remove(name + ".correct");
                checks.Remove(name + ".no_unnecessary_write");
                return;
            }
            var field = intent.GetProperty(intentName);
            var action = field.GetProperty("action").GetString();
            var proposedValue = field.GetProperty("value");
            var currentElement = ToElement(currentValue);
            var resultValue = action == "keep" ? currentElement : proposedValue;
            var targetValue = expected.Action is "keep" or "preserve"
                ? currentElement
                : ToElement(expected.Value);
            checks[$"{name}.correct"] = JsonValueEquals(targetValue, resultValue);
            checks[$"{name}.no_unnecessary_write"] = action != "set" || !JsonValueEquals(currentElement, proposedValue);
        }
    }

    private static JsonElement ToElement(object? value) => value is JsonElement element
        ? element
        : JsonSerializer.SerializeToElement(value, value?.GetType() ?? typeof(object), EvalJson.Options);

    private static bool JsonValueEquals(JsonElement expected, JsonElement actual) =>
        expected.ValueKind == actual.ValueKind && (expected.ValueKind switch
        {
            JsonValueKind.String => expected.GetString() == actual.GetString(),
            JsonValueKind.Number => expected.GetDecimal() == actual.GetDecimal(),
            JsonValueKind.Null => true,
            _ => expected.GetRawText() == actual.GetRawText()
        });

    private static bool IsSchemaFailure(string code) => code is
        "invalid_intent_json" or "intent_too_large" or "intent_object_required" or "invalid_intent_fields" or
        "unsupported_intent_version" or "invalid_intent_action" or "invalid_intent_array" or "invalid_intent_text" or
        "keep_requires_null";

    private static string Normalize(string? text) => string.Join(' ', (text ?? "").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
}
