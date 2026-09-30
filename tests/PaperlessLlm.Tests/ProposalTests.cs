using System.Text.Json;
using System.Text.Json.Nodes;
using PaperlessLlm.Paperless;
using PaperlessLlm.Review;

namespace PaperlessLlm.Tests;

public sealed class ProposalTests
{
    internal static PaperlessDocument Document(int id = 1, string revision = "revision-1") =>
        new(id, "Synthetic refund", "<script>private OCR</script> TOTAL -15.99", "2026-01-01", "2026-01-02", null, null, [2, 23], "application/pdf", "private.pdf", revision);
    internal static PaperlessTaxonomy Taxonomy => new([new(2, "needs review", true), new(23, "HSA reimbursed"), new(12, "receipts")], [new(1, "Synthetic merchant")], [new(1, "Receipt")]);
    internal const string Valid = """
        {"title":"Synthetic refund","date":"2024-02-29","correspondent":1,"document_type":1,"add_tags":[12],"ocr_text":null,"evidence":["Receipt says TOTAL -15.99"],"uncertainty":[]}
        """;

    [Fact]
    public void ValidationPreservesExistingProtectedMembershipsAndOriginalOcr()
    {
        var document = Document();
        var result = ProposalValidator.Validate(Valid, document, Taxonomy, false);
        Assert.Equal([2, 23], document.Tags);
        Assert.Contains("-15.99", document.Content);
        Assert.Equal(12, result.GetProperty("add_tags")[0].GetInt32());
    }

    [Theory]
    [InlineData("date", "2023-02-29")]
    [InlineData("date", "2024-2-29")]
    [InlineData("date", "20240229")]
    [InlineData("title", " ")]
    [InlineData("ocr_text", " ")]
    [InlineData("ocr_text", "Invented text without scans")]
    public void RejectsUnsafeText(string field, string text)
    {
        var value = JsonNode.Parse(Valid)!;
        value[field] = text;
        Assert.Throws<ProposalValidationException>(() => ProposalValidator.Validate(value.ToJsonString(), Document(), Taxonomy, false));
    }

    [Theory]
    [InlineData("correspondent", "999")]
    [InlineData("correspondent", "true")]
    [InlineData("document_type", "\"1\"")]
    [InlineData("add_tags", "[999]")]
    [InlineData("add_tags", "[2]")]
    [InlineData("add_tags", "[23]")]
    [InlineData("add_tags", "[12,12]")]
    [InlineData("evidence", "[]")]
    [InlineData("uncertainty", "\"Certain\"")]
    public void RejectsUnsafeFieldValues(string field, string json)
    {
        var value = JsonNode.Parse(Valid)!;
        value[field] = JsonNode.Parse(json);
        Assert.Throws<ProposalValidationException>(() => ProposalValidator.Validate(value.ToJsonString(), Document(), Taxonomy, false));
    }

    [Theory]
    [InlineData("receipt to log")]
    [InlineData("HSA unreimbursed")]
    [InlineData("expense")]
    [InlineData("sweetgum")]
    [InlineData("wallingford")]
    [InlineData("inbox")]
    public void ProtectedTagsCannotBeSuggested(string name)
    {
        var taxonomy = Taxonomy with { Tags = [.. Taxonomy.Tags, new NamedEntity(99, name)] };
        var value = JsonNode.Parse(Valid)!;
        value["add_tags"] = new JsonArray(99);
        var exception = Assert.Throws<ProposalValidationException>(() => ProposalValidator.Validate(value.ToJsonString(), Document(), taxonomy, false));
        Assert.Equal("protected_tag", exception.Code);
    }

    [Fact]
    public void RejectsExtraOperationsDuplicateKeysAndExcessiveTitle()
    {
        foreach (var key in new[] { "remove_tags", "tags", "owner", "permissions", "delete", "storage_path" })
        {
            var value = JsonNode.Parse(Valid)!;
            value[key] = true;
            Assert.Throws<ProposalValidationException>(() => ProposalValidator.Validate(value.ToJsonString(), Document(), Taxonomy, true));
        }
        Assert.Throws<ProposalValidationException>(() => ProposalValidator.Validate(Valid.Replace("{", "{\"title\":\"injected\","), Document(), Taxonomy, true));
        var longTitle = JsonNode.Parse(Valid)!;
        longTitle["title"] = new string('a', 129);
        Assert.Throws<ProposalValidationException>(() => ProposalValidator.Validate(longTitle.ToJsonString(), Document(), Taxonomy, false));
    }

    [Fact]
    public void RealVisualInputAllowsLiteralOcrButNeverBlankReplacement()
    {
        var value = JsonNode.Parse(Valid)!;
        value["ocr_text"] = "TOTAL -15.99\n";
        Assert.Equal("TOTAL -15.99\n", ProposalValidator.Validate(value.ToJsonString(), Document(), Taxonomy, true).GetProperty("ocr_text").GetString());
        value["ocr_text"] = "";
        Assert.Throws<ProposalValidationException>(() => ProposalValidator.Validate(value.ToJsonString(), Document(), Taxonomy, true));
    }

    [Fact]
    public void AbstentionRequiresExplanationAndPromptTreatsTextAsUntrusted()
    {
        const string abstain = """{"title":null,"date":null,"correspondent":null,"document_type":null,"add_tags":[],"ocr_text":null,"evidence":[],"uncertainty":["Illegible date"]}""";
        ProposalValidator.Validate(abstain, Document(), Taxonomy, false);
        var prompt = ProposalPrompt.Build(Document(), Taxonomy, false);
        Assert.Contains("UNTRUSTED DATA", prompt);
        Assert.Contains("ocr_text MUST be null", prompt);
        Assert.DoesNotContain("HSA reimbursed", prompt);
    }
}
