namespace GeoAutoDocs.Core;

public static class PendingCaptureRemoval
{
    public static IReadOnlyList<Capture> FindRemovable(
        Project project, Guid sectionId, IEnumerable<Guid> expectedCaptureIds)
    {
        var expected = expectedCaptureIds.ToHashSet();
        var evidence = project.Locations.SelectMany(x => x.Sections)
            .SelectMany(x => x.Occurrences).SelectMany(x => x.Evidence).ToList();
        var assignedIds = evidence.Where(x => x.CaptureId.HasValue)
            .Select(x => x.CaptureId!.Value).ToHashSet();
        var evidencePaths = evidence.Where(x => !string.IsNullOrWhiteSpace(x.FilePath))
            .Select(x => NormalizePath(x.FilePath)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return project.Captures.Where(capture =>
            capture.ProjectId == project.Id && capture.SectionId == sectionId && expected.Contains(capture.Id) &&
            capture.Status == CaptureStatus.Pending && !assignedIds.Contains(capture.Id) &&
            !evidencePaths.Contains(NormalizePath(capture.FilePath))).ToList();
    }

    public static async Task<bool> RemoveAsync(
        Project project, IReadOnlyList<Capture> captures, Func<Task<bool>> persist)
    {
        ArgumentNullException.ThrowIfNull(persist);
        if (captures.Count == 0) return true;
        var valid = FindRemovable(project, captures[0].SectionId, captures.Select(x => x.Id));
        var validSet = valid.ToHashSet();
        if (valid.Count != captures.Count || captures.Any(x => !validSet.Contains(x)))
            throw new InvalidOperationException(
                "As capturas mudaram ou estão vinculadas a evidências. Execute a revisão novamente.");
        var original = project.Captures.ToList();
        var ids = captures.Select(x => x.Id).ToHashSet();
        var saved = false;
        project.Captures.RemoveAll(x => ids.Contains(x.Id));
        try
        {
            saved = await persist();
            return saved;
        }
        finally
        {
            if (!saved)
            {
                project.Captures.Clear();
                project.Captures.AddRange(original);
            }
        }
    }

    public static bool IsPathReferenced(Project project, string path)
    {
        return GetReferencedPaths(project).Contains(NormalizePath(path));
    }

    public static IReadOnlySet<string> GetReferencedPaths(Project project) =>
        project.Captures.Select(x => x.FilePath)
            .Concat(project.Locations.SelectMany(x => x.Sections).SelectMany(x => x.Occurrences)
                .SelectMany(x => x.Evidence).Select(x => x.FilePath))
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Select(NormalizePath).ToHashSet(StringComparer.OrdinalIgnoreCase);

    public static string NormalizePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return string.Empty;
        try { return Path.GetFullPath(path); }
        catch (ArgumentException) { return path.Trim(); }
        catch (NotSupportedException) { return path.Trim(); }
    }
}
