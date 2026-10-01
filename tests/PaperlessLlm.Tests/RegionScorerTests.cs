using PaperlessLlm.Eval;

namespace PaperlessLlm.Tests;

public sealed class RegionScorerTests
{
    [Fact]
    public void AmountSignsAreNotLostBySubstringMatching()
    {
        var amounts = RegionScorer.ExtractAmounts("REFUND -$15.99; credit (42.00); discount 3.00-; due $120.50");
        Assert.Contains("-15.99", amounts);
        Assert.Contains("-42.00", amounts);
        Assert.Contains("-3.00", amounts);
        Assert.Contains("120.50", amounts);
        Assert.DoesNotContain("15.99", amounts);
        Assert.Contains("0.00", RegionScorer.ExtractAmounts("Tax .00"));
    }

    [Fact]
    public void UnrepresentablyLargeOcrAmountsAreSkippedInsteadOfThrowing()
    {
        var text = new string('9', 200) + ".99 and $12.34";
        var amounts = RegionScorer.ExtractAmounts(text);
        Assert.Contains("12.34", amounts);
        Assert.Single(amounts);
    }

    [Fact]
    public void IdentifiersCannotMatchInsideOtherIdentifiers()
    {
        Assert.False(RegionScorer.ContainsToken("1232079", "3207"));
        Assert.True(RegionScorer.ContainsToken("CARD: 3207", "3207"));
        Assert.False(RegionScorer.ContainsToken("XINV-42Y", "INV-42"));
    }

    [Fact]
    public void ReferenceRegionsCanBeLocatedButMissingLinesLoseCredit()
    {
        const string reference = "ITEM A 12.00\nITEM B 4.00\nTOTAL 16.00";
        Assert.Equal(1, RegionScorer.RegionAccuracy(reference, "STORE HEADER\n" + reference + "\nTHANK YOU"));
        Assert.True(RegionScorer.RegionAccuracy(reference, "STORE HEADER\nITEM A 12.00\nTOTAL 16.00") < 1);
        Assert.Equal(0, RegionScorer.RegionAccuracy(reference, ""));
    }

    [Fact]
    public void PageSpecificRegionCannotBeSatisfiedByTextOnAnotherPage()
    {
        var reference = new RichReference { Regions = [new("footer", "PAGE TWO IDENTIFIER", 2)] };
        var score = RegionScorer.ScoreText("PAGE TWO IDENTIFIER", reference, new Dictionary<int, string> { [1] = "PAGE TWO IDENTIFIER", [2] = "" });
        Assert.Equal(0, score.RegionAccuracy);
    }
}
