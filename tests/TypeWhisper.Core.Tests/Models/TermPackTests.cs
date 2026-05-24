using TypeWhisper.Core.Models;

namespace TypeWhisper.Core.Tests.Models;

public class TermPackTests
{
    [Fact]
    public void AllPacks_Has15Packs()
    {
        Assert.Equal(15, TermPack.AllPacks.Length);
    }

    [Fact]
    public void AllPacks_IncludesNewIndustryPacks()
    {
        Assert.Contains(TermPack.AllPacks, p => p.Id == "real-estate");
        Assert.Contains(TermPack.AllPacks, p => p.Id == "architecture");
        Assert.Contains(TermPack.AllPacks, p => p.Id == "legal");
    }

    [Fact]
    public void AllPacks_HaveUniqueIds()
    {
        var ids = TermPack.AllPacks.Select(p => p.Id).ToList();
        Assert.Equal(ids.Count, ids.Distinct().Count());
    }

    [Fact]
    public void AllPacks_HaveTerms()
    {
        Assert.All(
            TermPack.AllPacks,
            p =>
            {
                Assert.NotEmpty(p.Terms);
                Assert.NotEmpty(p.Name);
                Assert.NotEmpty(p.Icon);
            }
        );
    }

    [Theory]
    [InlineData("web-dev")]
    [InlineData("dotnet")]
    [InlineData("devops")]
    [InlineData("data-ai")]
    [InlineData("design")]
    [InlineData("gamedev")]
    [InlineData("mobile")]
    [InlineData("security")]
    [InlineData("databases")]
    [InlineData("medical")]
    [InlineData("legal")]
    [InlineData("finance")]
    [InlineData("music")]
    [InlineData("real-estate")]
    [InlineData("architecture")]
    public void Pack_ExistsById(string id)
    {
        Assert.Contains(TermPack.AllPacks, p => p.Id == id);
    }

    [Theory]
    [InlineData("web-dev")]
    [InlineData("real-estate")]
    [InlineData("ARCHITECTURE")]
    public void FindById_ReturnsMatchingPack(string id)
    {
        var pack = TermPack.FindById(id);

        Assert.NotNull(pack);
        Assert.Equal(id, pack!.Id, ignoreCase: true);
    }

    [Fact]
    public void FindById_ReturnsNullForUnknownId()
    {
        Assert.Null(TermPack.FindById("does-not-exist"));
    }

    [Fact]
    public void IndustryPreset_All_HasFourPresetsWithExpectedIds()
    {
        var ids = IndustryPreset.All.Select(p => p.Id).ToArray();

        Assert.Equal(4, IndustryPreset.All.Length);
        Assert.Equal(new[] { "general", "real-estate", "architecture", "legal" }, ids);
    }

    [Fact]
    public void IndustryPreset_General_HasNullTermPackId()
    {
        var general = IndustryPreset.All.Single(p => p.Id == "general");
        Assert.Null(general.TermPackId);
    }

    [Fact]
    public void IndustryPreset_TermPackIds_MapToExistingPacks()
    {
        foreach (var preset in IndustryPreset.All)
        {
            if (preset.TermPackId is null)
            {
                continue;
            }

            Assert.NotNull(TermPack.FindById(preset.TermPackId));
        }
    }

    [Fact]
    public void MergeIntoEnabledPackIds_General_ReturnsInputUnchanged()
    {
        var input = new[] { "web-dev" };

        var result = IndustryPreset.MergeIntoEnabledPackIds(input, "general");

        Assert.Same(input, result);
    }

    [Fact]
    public void MergeIntoEnabledPackIds_UnknownPreset_ReturnsInputUnchanged()
    {
        var input = new[] { "web-dev" };

        var result = IndustryPreset.MergeIntoEnabledPackIds(input, "does-not-exist");

        Assert.Same(input, result);
    }

    [Fact]
    public void MergeIntoEnabledPackIds_Industry_AppendsPackIdToEnabledList()
    {
        var input = new[] { "web-dev" };

        var result = IndustryPreset.MergeIntoEnabledPackIds(input, "real-estate");

        Assert.Equal(new[] { "web-dev", "real-estate" }, result);
    }

    [Fact]
    public void MergeIntoEnabledPackIds_AlreadyEnabled_ReturnsInputUnchanged()
    {
        var input = new[] { "real-estate", "web-dev" };

        var result = IndustryPreset.MergeIntoEnabledPackIds(input, "real-estate");

        Assert.Same(input, result);
    }

    [Fact]
    public void MergeIntoEnabledPackIds_AlreadyEnabledCaseInsensitive_ReturnsInputUnchanged()
    {
        var input = new[] { "Real-Estate" };

        var result = IndustryPreset.MergeIntoEnabledPackIds(input, "real-estate");

        Assert.Same(input, result);
    }
}