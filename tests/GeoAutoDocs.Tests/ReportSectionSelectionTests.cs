using GeoAutoDocs.Core;
using Xunit;

namespace GeoAutoDocs.Tests;

public sealed class ReportSectionSelectionTests
{
    [Fact]
    public void SelectAllAndClearUpdateEverySectionInExactOrder()
    {
        var report = new Report();
        var sections = new[]
        {
            new InspectionSection { Name = "Trecho 01" },
            new InspectionSection { Name = "Trecho 02" },
            new InspectionSection { Name = "Trecho 03" }
        };
        report.SelectSection(sections[1]);

        ReportSectionSelection.SetAll(report, sections, true);

        Assert.Equal(sections.Select(x => x.Id), report.SectionLinks.Select(x => x.SectionId));
        Assert.Equal(new[] { 0, 1, 2 }, report.SectionLinks.Select(x => x.Order));
        Assert.Equal(3, report.SectionLinks.Select(x => x.SectionId).Distinct().Count());

        ReportSectionSelection.SetAll(report, sections, true);
        Assert.Equal(3, report.SectionLinks.Count);

        ReportSectionSelection.SetAll(report, sections, false);
        Assert.Empty(report.SectionLinks);
    }
}
