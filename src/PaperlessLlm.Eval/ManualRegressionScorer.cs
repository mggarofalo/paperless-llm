using System.Text.Json;
using System.Text.RegularExpressions;

namespace PaperlessLlm.Eval;

public sealed record ManualRegressionCheck(int Page, string Text, string Kind, string? Note = null);

public sealed record ManualRegressionReference
{
    public required List<ManualRegressionCheck> Checks { get; init; }
    public string? Origin { get; init; }
    public string? AcceptanceRule { get; init; }
}

public sealed record ManualRegressionCaseResult(string CaseId, string Outcome, int Matched, int Total, IReadOnlyList<int> MissingCheckIndices);

public sealed record ManualRegressionReport
{
    public required string Split { get; init; }
    public string Gate { get; init; } = "post-audit-partial-reference";
    public required int PassedSetCases { get; init; }
    public required int FailedSetCases { get; init; }
    public required int SafeAbstentions { get; init; }
    public required int InvalidCases { get; init; }
    public required int MatchedSetChecks { get; init; }
    public required int TotalSetChecks { get; init; }
    public required List<ManualRegressionCaseResult> Cases { get; init; }
}

/// <summary>Partial post-audit reference gate. A pass is not proof of complete transcription.</summary>
public static class ManualRegressionScorer
{
    private sealed record CheckGroup(int Page, string Kind, string NormalizedText, List<int> Indices);

    public static ManualRegressionReport Score(string split, IReadOnlyList<EvalCase> cases,
        IReadOnlyDictionary<string, string> outputs, IReadOnlySet<string> validCaseIds,
        IReadOnlyDictionary<string, ManualRegressionReference> references)
    {
        if (split is not ("train" or "holdout")) throw new ArgumentException("Split must be train or holdout.", nameof(split));
        var results = new List<ManualRegressionCaseResult>();
        foreach (var c in cases.Where(c => c.Split == split && references.ContainsKey(c.CaseId)))
        {
            var checks = references[c.CaseId].Checks;
            ValidateReference(c, checks);
            if (!validCaseIds.Contains(c.CaseId) || !outputs.TryGetValue(c.CaseId, out var raw))
            {
                results.Add(new(c.CaseId, "invalid", 0, checks.Count, []));
                continue;
            }

            using var doc = JsonDocument.Parse(raw);
            var ocr = doc.RootElement.GetProperty("ocr");
            if (ocr.GetProperty("action").GetString() == "keep")
            {
                results.Add(new(c.CaseId, "safe-abstention", 0, 0, []));
                continue;
            }

            var groups = GroupChecks(checks);
            var matched = 0;
            var missing = new List<int>();
            foreach (var group in groups)
            {
                var pageText = ocr.GetProperty("pages").EnumerateArray()
                    .Where(p => p.GetProperty("page").GetInt32() == group.Page)
                    .Select(p => p.GetProperty("text").GetString() ?? "").FirstOrDefault() ?? "";
                var normalizedPage = Normalize(pageText);
                var pattern = Regex.Escape(group.NormalizedText).Replace(@"\ ", @"\s*").Replace(":", @":\s*");
                if (group.Kind == "identifier") pattern = @"(?<![\p{L}\p{N}])" + pattern + @"(?![\p{L}\p{N}])";
                var found = Regex.Matches(normalizedPage, pattern, RegexOptions.CultureInvariant).Count;
                var groupMatches = Math.Min(group.Indices.Count, found);
                matched += groupMatches;
                missing.AddRange(group.Indices.Skip(groupMatches));
            }
            var misses = missing.Order().ToArray();
            results.Add(new(c.CaseId, misses.Length == 0 ? "passed-set" : "failed-set", matched, checks.Count, misses));
        }

        var setRows = results.Where(r => r.Outcome is "passed-set" or "failed-set").ToArray();
        return new ManualRegressionReport
        {
            Split = split,
            PassedSetCases = results.Count(r => r.Outcome == "passed-set"),
            FailedSetCases = results.Count(r => r.Outcome == "failed-set"),
            SafeAbstentions = results.Count(r => r.Outcome == "safe-abstention"),
            InvalidCases = results.Count(r => r.Outcome == "invalid"),
            MatchedSetChecks = setRows.Sum(r => r.Matched),
            TotalSetChecks = setRows.Sum(r => r.Total),
            Cases = results
        };
    }

    private static List<CheckGroup> GroupChecks(IReadOnlyList<ManualRegressionCheck> checks) => checks
        .Select((check, index) => (check.Page, check.Kind, Text: Normalize(check.Text), Index: index))
        .GroupBy(x => (x.Page, x.Kind, x.Text))
        .Select(g => new CheckGroup(g.Key.Page, g.Key.Kind, g.Key.Text, g.Select(x => x.Index).ToList()))
        .ToList();

    private static void ValidateReference(EvalCase c, IReadOnlyList<ManualRegressionCheck> checks)
    {
        if (checks.Count == 0) throw new InvalidDataException($"Regression reference for '{c.CaseId}' has no checks.");
        foreach (var check in checks)
            if (check.Page < 1 || check.Page > c.PageCount || string.IsNullOrWhiteSpace(check.Text) || check.Kind is not ("identifier" or "text"))
                throw new InvalidDataException($"Regression reference for '{c.CaseId}' has an invalid check.");
    }

    private static string Normalize(string? value) => Regex.Replace(value ?? "", @"\s+", " ").Trim().ToUpperInvariant();
}
