namespace GeoAutoDocs.Core;

public sealed record ProjectDashboardSummary(int Projects, int Reports, int InProgressReports, int SharedProjects);

public static class ProjectDashboard
{
    public static ProjectDashboardSummary Summarize(IEnumerable<Project> projects)
    {
        var items = projects.ToList();
        return new ProjectDashboardSummary(
            items.Count,
            items.Sum(x => x.Reports.Count),
            items.Sum(x => x.Reports.Count(report => string.IsNullOrWhiteSpace(report.GeneratedReportPath))),
            items.Count(x => x.StorageKind == ProjectStorageKind.Shared));
    }

    public static bool Matches(Project project, string? query)
    {
        query = query?.Trim();
        return string.IsNullOrEmpty(query) ||
               project.Name.Contains(query, StringComparison.CurrentCultureIgnoreCase) ||
               project.DefaultClient?.Contains(query, StringComparison.CurrentCultureIgnoreCase) == true;
    }
}
