using AutoFixScout.Areas;
using Xunit;

namespace AutoFixScout.Tests;

public class SymptomRankingTests
{
    [Fact]
    public void ClearDescription_TopMatchesBest_IsConfident()
    {
        const string text = "there is no sound from my laptop speakers";
        var r = SymptomRouter.Rank(text);
        Assert.False(r.NoGoodMatch);
        Assert.Equal(SymptomRouter.Best(text).Area, r.Candidates[0].Route.Area);
        Assert.True(r.Candidates[0].Confidence >= 0.5);
        Assert.InRange(r.Candidates.Count, 1, 3);
    }

    [Fact]
    public void Ambiguous_OffersSeveralChecks_OncePerCheck_ConfidencesSumToOne()
    {
        var r = SymptomRouter.Rank("my bluetooth headphones have no sound and the mic does not work");
        Assert.True(r.Candidates.Count >= 2);
        Assert.Equal(r.Candidates.Count, r.Candidates.Select(c => c.Route.Area).Distinct().Count());
        Assert.InRange(r.Candidates.Sum(c => c.Confidence), 0.97, 1.03);
        Assert.True(r.Candidates.Zip(r.Candidates.Skip(1)).All(p => p.First.Score >= p.Second.Score));
    }

    [Theory]
    [InlineData("")]
    [InlineData("it is broken somehow")]
    public void NoGoodMatch_OffersTheFullCheckUp(string text)
    {
        var r = SymptomRouter.Rank(text);
        Assert.True(r.NoGoodMatch);
        Assert.Contains(r.Candidates, c => c.Route.Area == "health");
        Assert.InRange(r.Candidates.Count, 1, 3);
    }

    [Fact]
    public void EveryRoute_HasACoversLine_InTheWritingStyle()
    {
        foreach (var area in SymptomRouter.Routes.Keys)
        {
            Assert.True(SymptomRouter.Covers.ContainsKey(area), area);
            Assert.DoesNotMatch(@"n't|'d |'ll |\bI\b|\bwe\b|!", SymptomRouter.Covers[area]);
        }
    }

    [Fact]
    public void Max_IsRespected() => Assert.Single(SymptomRouter.Rank("no sound, no wifi, printer offline", max: 1).Candidates);
}
