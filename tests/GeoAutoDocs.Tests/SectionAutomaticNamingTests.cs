using GeoAutoDocs.Core;
using Xunit;

namespace GeoAutoDocs.Tests;

public sealed class SectionAutomaticNamingTests
{
    [Fact]
    public void DefaultSequenceFollowsLocationThenSectionOrder()
    {
        var project = new Project
        {
            Locations =
            [
                Location("Local B", 2, Section("Antigo 04", 1), Section("Antigo 03", 0)),
                Location("Local A", 1, Section("Antigo 02", 1), Section("Antigo 01", 0))
            ]
        };

        var plan = SectionAutomaticNaming.CreatePlan(project, "Trecho", 1);

        Assert.Equal(
            ["Local A", "Local A", "Local B", "Local B"],
            plan.Select(x => x.LocationName).ToArray());
        Assert.Equal(
            ["Antigo 01", "Antigo 02", "Antigo 03", "Antigo 04"],
            plan.Select(x => x.CurrentName).ToArray());
        Assert.Equal(
            ["Trecho 01", "Trecho 02", "Trecho 03", "Trecho 04"],
            plan.Select(x => x.ProposedName).ToArray());
    }

    [Fact]
    public void CustomPrefixAndStartingNumberAreApplied()
    {
        var project = new Project
        {
            Locations = [Location("PV 07", 0, Section("Entrada", 0), Section("Saída", 1))]
        };

        var plan = SectionAutomaticNaming.CreatePlan(project, "  Linha  ", 8);

        Assert.Equal(["Linha 08", "Linha 09"], plan.Select(x => x.ProposedName).ToArray());
        Assert.Equal([8, 9], plan.Select(x => x.Sequence).ToArray());
    }

    [Fact]
    public void SequenceAboveNinetyNineIsNotTruncated()
    {
        var project = new Project
        {
            Locations = [Location("PV", 0, Section("A", 0), Section("B", 1))]
        };

        var plan = SectionAutomaticNaming.CreatePlan(project, "Trecho", 99);

        Assert.Equal(["Trecho 99", "Trecho 100"], plan.Select(x => x.ProposedName).ToArray());
    }

    [Fact]
    public void EqualOrderPreservesOriginalCreationListOrder()
    {
        var first = Section("Primeiro", 0);
        var second = Section("Segundo", 0);
        var project = new Project
        {
            Locations = [Location("PV", 0, first, second)]
        };

        var plan = SectionAutomaticNaming.CreatePlan(project, "Trecho", 1);

        Assert.Equal([first.Id, second.Id], plan.Select(x => x.SectionId).ToArray());
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void BlankPrefixIsRejected(string prefix)
    {
        Assert.Throws<ArgumentException>(() =>
            SectionAutomaticNaming.CreatePlan(new Project(), prefix, 1));
    }

    [Fact]
    public void StartingNumberBelowOneIsRejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            SectionAutomaticNaming.CreatePlan(new Project(), "Trecho", 0));
    }

    private static Location Location(string name, int order, params InspectionSection[] sections)
    {
        var location = new Location { Name = name, Order = order };
        foreach (var section in sections)
        {
            section.LocationId = location.Id;
            location.Sections.Add(section);
        }
        return location;
    }

    private static InspectionSection Section(string name, int order) =>
        new() { Name = name, Order = order };
}
