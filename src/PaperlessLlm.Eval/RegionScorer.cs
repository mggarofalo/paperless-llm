using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using PaperlessLlm.Intent;
using PaperlessLlm.Review;

namespace PaperlessLlm.Eval;

public sealed record RegionReference(string Name, string Text);
public sealed record RichReference
{
    public List<RegionReference> Regions { get; init; } = [];
    public List<string> Amounts { get; init; } = [];
    public List<string> Identifiers { get; init; } = [];
    public List<string> Anchors { get; init; } = [];
    public bool? MustReplace { get; init; }
    public string? Notes { get; init; }
}

public sealed record RichTextScore(double RegionAccuracy, int Regions,
    int AmountsMatched, int AmountsTotal, int IdentifiersMatched, int IdentifiersTotal,
    int AnchorsMatched, int AnchorsTotal)
{
    public double Quality => new[]
        {
            (RegionAccuracy, Regions),
            (AmountsTotal == 0 ? 0 : (double)AmountsMatched / AmountsTotal, AmountsTotal),
            (IdentifiersTotal == 0 ? 0 : (double)IdentifiersMatched / IdentifiersTotal, IdentifiersTotal),
            (AnchorsTotal == 0 ? 0 : (double)AnchorsMatched / AnchorsTotal, AnchorsTotal)
        }.Where(x => x.Item2 > 0).Select(x => x.Item1).DefaultIfEmpty(0).Average();
}

public sealed record RichCaseScore(string CaseId, bool Valid, string? Failure, bool OcrReplaced,
    RichTextScore Original, RichTextScore Result, double Improvement, int NewOmissions);

/// <summary>Deterministic partial-reference metrics; not proof of full-page transcription.</summary>
public static class RegionScorer
{
    private static readonly Regex Words = new(@"[\p{L}\p{N}]+(?:[.,/'’\-][\p{L}\p{N}]+)*|[$+−-]", RegexOptions.Compiled);
    private static readonly Regex Money = new(@"(?<![\p{L}\d.,])(?<open>\()?\s*(?<sign>[-−+])?\s*\$?\s*(?<number>(?:\d{1,3}(?:,\d{3})+|\d+)\.\d{2})(?<trailing>-)?\s*(?<close>\))?(?!\d)", RegexOptions.Compiled);

    public static RichCaseScore Score(EvalCase c, string? intentJson, RichReference reference)
    {
        var original = ScoreText(c.Document.Content, reference);
        if (intentJson is null) return new(c.CaseId, false, "missing_output", false, original, Empty(reference), -original.Quality, 0);
        try
        {
            var intent = IntentValidator.Validate(intentJson, c.ToDocument(), c.ToTaxonomy(), c.PageCount);
            var ocr = intent.GetProperty("ocr");
            var replaced = ocr.GetProperty("action").GetString() == "set";
            var text = replaced ? string.Join('\n', ocr.GetProperty("pages").EnumerateArray().Select(p => p.GetProperty("text").GetString())) : c.Document.Content;
            var result = ScoreText(text, reference);
            var omissions = reference.Identifiers.Concat(reference.Anchors).Count(s => ContainsToken(c.Document.Content, s) && !ContainsToken(text, s));
            var oldAmounts = ExtractAmounts(c.Document.Content);
            var newAmounts = ExtractAmounts(text);
            omissions += reference.Amounts.Count(s => oldAmounts.Contains(CanonicalAmount(s)) && !newAmounts.Contains(CanonicalAmount(s)));
            return new(c.CaseId, true, null, replaced, original, result, result.Quality - original.Quality, omissions);
        }
        catch (ProposalValidationException ex)
        {
            return new(c.CaseId, false, ex.Code, false, original, Empty(reference), -original.Quality, 0);
        }
    }

    public static RichTextScore ScoreText(string text, RichReference reference)
    {
        var amounts = ExtractAmounts(text);
        return new(reference.Regions.Select(r => RegionAccuracy(r.Text, text)).DefaultIfEmpty(0).Average(), reference.Regions.Count,
            reference.Amounts.Count(a => amounts.Contains(CanonicalAmount(a))), reference.Amounts.Count,
            reference.Identifiers.Count(a => ContainsToken(text, a)), reference.Identifiers.Count,
            reference.Anchors.Count(a => ContainsToken(text, a)), reference.Anchors.Count);
    }

    public static bool ContainsToken(string text, string reference)
    {
        var pattern = string.Join(@"\s+", reference.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Select(Regex.Escape));
        return pattern.Length > 0 && Regex.IsMatch(text, @"(?<![\p{L}\p{N}])" + pattern + @"(?![\p{L}\p{N}])", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }

    public static double RegionAccuracy(string reference, string candidate)
    {
        var expected = Words.Matches(reference).Select(m => m.Value.ToUpperInvariant()).ToArray();
        var actual = Words.Matches(candidate).Select(m => m.Value.ToUpperInvariant()).ToArray();
        if (expected.Length == 0) return 1;
        // Free candidate prefixes/suffixes permit locating a reference region in a full document.
        var prior = new int[actual.Length + 1];
        for (var i = 1; i <= expected.Length; i++)
        {
            var next = new int[actual.Length + 1];
            next[0] = i;
            for (var j = 1; j <= actual.Length; j++)
                next[j] = Math.Min(Math.Min(prior[j] + 1, next[j - 1] + 1), prior[j - 1] + (expected[i - 1] == actual[j - 1] ? 0 : 1));
            prior = next;
        }
        return Math.Max(0, 1 - (double)prior.Min() / expected.Length);
    }

    public static HashSet<string> ExtractAmounts(string text) => Money.Matches(text).Select(m =>
    {
        var negative = m.Groups["sign"].Value is "-" or "−" || m.Groups["trailing"].Success || m.Groups["open"].Success && m.Groups["close"].Success;
        var number = decimal.Parse(m.Groups["number"].Value, NumberStyles.Number, CultureInfo.InvariantCulture);
        return (negative ? -number : number).ToString("0.00", CultureInfo.InvariantCulture);
    }).ToHashSet(StringComparer.Ordinal);

    private static string CanonicalAmount(string amount) => decimal.Parse(amount, NumberStyles.Number | NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture).ToString("0.00", CultureInfo.InvariantCulture);
    private static RichTextScore Empty(RichReference reference) => new(0, reference.Regions.Count, 0, reference.Amounts.Count, 0, reference.Identifiers.Count, 0, reference.Anchors.Count);
}
