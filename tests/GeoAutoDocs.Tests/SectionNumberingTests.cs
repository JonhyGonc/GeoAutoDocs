using GeoAutoDocs.Core;
using Xunit;

namespace GeoAutoDocs.Tests;

public sealed class SectionNumberingTests
{
    [Fact]
    public void TryParse_reads_prefix_and_number()
    {
        Assert.True(SectionNumbering.TryParse("Grelha 32", out var parsed));
        Assert.Equal("Grelha ", parsed!.Prefix);
        Assert.Equal(32, parsed.Number);
    }

    [Theory]
    [InlineData("")]
    [InlineData("Grelha")]
    [InlineData("32")]
    [InlineData("Trecho A")]
    public void TryParse_rejects_names_without_prefix_and_number(string name)
    {
        Assert.False(SectionNumbering.TryParse(name, out _));
    }

    [Fact]
    public void Insertion_shifts_only_the_tail_of_the_same_prefix()
    {
        var sections = new List<InspectionSection>
        {
            Section("Grelha 30"),
            Section("Grelha 31"),
            Section("Grelha 32"),
            Section("Grelha 33"),
            Section("Trecho especial")
        };

        var renames = SectionNumbering.PlanInsertion(sections, "Grelha 32");

        Assert.Equal(["Grelha 32", "Grelha 33"], renames.Select(x => x.OldName));
        Assert.Equal(["Grelha 33", "Grelha 34"], renames.Select(x => x.NewName));
    }

    [Fact]
    public void Appending_at_the_end_renames_nothing()
    {
        var sections = new List<InspectionSection> { Section("Grelha 30"), Section("Grelha 31") };

        Assert.Empty(SectionNumbering.PlanInsertion(sections, "Grelha 32"));
    }

    [Fact]
    public void Unnumbered_names_are_never_planned()
    {
        var sections = new List<InspectionSection> { Section("Trecho A"), Section("Trecho B") };

        Assert.Empty(SectionNumbering.PlanInsertion(sections, "Grelha 1"));
    }

    private static InspectionSection Section(string name) => new() { Name = name };
}
