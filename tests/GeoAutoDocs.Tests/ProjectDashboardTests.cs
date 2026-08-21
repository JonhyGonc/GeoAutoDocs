using GeoAutoDocs.Core;
using Xunit;

namespace GeoAutoDocs.Tests;

public sealed class ProjectDashboardTests
{
    [Fact]
    public void SummarizesRealProjectStates()
    {
        Assert.Equal(new ProjectDashboardSummary(0, 0, 0, 0), ProjectDashboard.Summarize([]));
        var local = new Project { Name = "Local", DefaultClient = "Buckman" };
        local.Reports.AddRange([new Report(), new Report { GeneratedReportPath = "final.docx" }]);
        var shared = new Project { Name = "Rede", StorageKind = ProjectStorageKind.Shared };
        shared.Reports.Add(new Report());

        Assert.Equal(new ProjectDashboardSummary(2, 3, 2, 1), ProjectDashboard.Summarize([local, shared]));
        Assert.True(ProjectDashboard.Matches(local, "buck"));
        Assert.True(ProjectDashboard.Matches(shared, "REDE"));
        Assert.False(ProjectDashboard.Matches(local, "inexistente"));
    }
}
