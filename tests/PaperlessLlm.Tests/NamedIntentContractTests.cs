using System.Text.Json.Nodes;
using PaperlessLlm.Eval;
using PaperlessLlm.Intent;
using PaperlessLlm.Paperless;
using PaperlessLlm.Review;

namespace PaperlessLlm.Tests;

public sealed class NamedIntentContractTests
{
    private static PaperlessDocument Document => new(1, "Scan", "ACME Receipt 2026-01-02", "2026-01-02", null, 8, null, [4], null, null, "eval");
    private static PaperlessTaxonomy Taxonomy => new([new(4, "cars"), new(9, "receipts"), new(10, "inbox", true)],
        [new(8, "ACME"), new(19, "Other")], [new(2, "Receipt")]);
    private const string Intent = """
        {"schema_version":"1","title":{"action":"keep","value":null,"evidence":[]},
         "date":{"action":"keep","value":null,"evidence":[]},
         "correspondent":{"action":"set","value":"ACME","evidence":["ACME"]},
         "document_type":{"action":"set","value":"Receipt","evidence":["Receipt"]},
         "add_tags":[{"name":"receipts","evidence":["Receipt"]}],
         "ocr":{"action":"keep","pages":[],"evidence":[]},"uncertainty":[]}
        """;

    [Fact]
    public void ConvertsNamesWithoutSendingNumericTaxonomyOrReferences()
    {
        var payload = JsonNode.Parse(NamedIntentContract.Payload(IntentPrompt.Build(Document, Taxonomy, 0), Taxonomy))!;
        Assert.Equal("ACME", payload["document"]!["correspondent"]!.GetValue<string>());
        Assert.Equal("cars", payload["document"]!["tags"]![0]!.GetValue<string>());
        Assert.DoesNotContain("inbox", payload["allowed_taxonomy"]!.ToJsonString());
        Assert.Equal("Receipt", payload["allowed_taxonomy"]!["document_types"]![0]!.GetValue<string>());
        var resolved = JsonNode.Parse(NamedIntentContract.Resolve(Intent, Document, Taxonomy, 0))!;
        Assert.Equal(8, resolved["correspondent"]!["value"]!.GetValue<int>());
        Assert.Equal(2, resolved["document_type"]!["value"]!.GetValue<int>());
        Assert.Equal(9, resolved["add_tags"]![0]!["id"]!.GetValue<int>());
    }

    [Theory]
    [InlineData("\"ACME\"", "\"acme\"")]
    [InlineData("\"ACME\"", "\"Unknown\"")]
    [InlineData("\"ACME\"", "8")]
    [InlineData("\"name\":\"receipts\"", "\"name\":\"receipts\",\"id\":9")]
    [InlineData("\"schema_version\":\"1\"", "\"schema_version\":\"1\",\"schema_version\":\"1\"")]
    public void RejectsUnknownNamesNumericIdsExtraKeysAndDuplicateKeys(string from, string to) =>
        Assert.Throws<InvalidDataException>(() => NamedIntentContract.Resolve(Intent.Replace(from, to), Document, Taxonomy, 0));

    [Fact]
    public void RejectsAmbiguousExactNames()
    {
        var taxonomy = Taxonomy with { Correspondents = [new(8, "ACME"), new(19, "ACME")] };
        Assert.Throws<InvalidDataException>(() => NamedIntentContract.Resolve(Intent, Document, taxonomy, 0));
    }

    [Theory]
    [InlineData("receipts", "cars", "duplicate_tag_addition")]
    [InlineData("receipts", "inbox", "protected_tag")]
    public void PreservesProductionTagRejections(string from, string to, string error)
    {
        var ex = Assert.Throws<ProposalValidationException>(() => NamedIntentContract.Resolve(Intent.Replace(from, to), Document, Taxonomy, 0));
        Assert.Equal(error, ex.Code);
    }

    [Fact]
    public void DoesNotRepairKeepValuesOrMissingEvidence()
    {
        var raw = JsonNode.Parse(Intent)!;
        raw["correspondent"]!["action"] = "keep";
        Assert.Throws<ProposalValidationException>(() => NamedIntentContract.Resolve(raw.ToJsonString(), Document, Taxonomy, 0));
        raw["correspondent"]!["action"] = "set";
        raw["correspondent"]!["evidence"] = new JsonArray();
        Assert.Throws<ProposalValidationException>(() => NamedIntentContract.Resolve(raw.ToJsonString(), Document, Taxonomy, 0));
    }

    [Fact]
    public void RestrictsNamedContractToSingleStageAndRejectsUnknownContract()
    {
        var recipe = new ExperimentRecipe { Id = "test", PromptFile = "prompt.txt", OutputContract = "names" };
        ExperimentRunner.ValidateRecipe(recipe);
        Assert.Throws<InvalidDataException>(() => ExperimentRunner.ValidateRecipe(recipe with { Pipeline = "dual" }));
        Assert.Throws<InvalidDataException>(() => ExperimentRunner.ValidateRecipe(recipe with { Taxonomy = "shortlist" }));
        Assert.Throws<InvalidDataException>(() => ExperimentRunner.ValidateRecipe(recipe with { OutputContract = "guess" }));
    }
}
