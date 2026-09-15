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
            capture.SectionId == sectionId && expected.Contains(capture.Id) &&
            capture.Status == CaptureStatus.Pending && !assignedIds.Contains(capture.Id) &&
            !evidencePaths.Contains(NormalizePath(capture.FilePath))).ToList();
    }

    public static async Task<bool> RemoveAsync(
        Project project, IReadOnlyList<Capture> captures, Func<Task<bool>> persist)
    {
        ArgumentNullException.ThrowIfNull(persist);
        if (captures.Count == 0) return true;
        var valid = FindRemovable(project, captures[0].SectionId, captures.Select(x => x.Id));
        if (valid.Count != captures.Count || captures.Any(x => !valid.Contains(x)))
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
        var normalized = NormalizePath(path);
        return project.Captures.Any(x => SamePath(x.FilePath, normalized)) ||
            project.Locations.SelectMany(x => x.Sections).SelectMany(x => x.Occurrences)
                .SelectMany(x => x.Evidence).Any(x => SamePath(x.FilePath, normalized));
    }

    private static bool SamePath(string path, string normalized) =>
        string.Equals(NormalizePath(path), normalized, StringComparison.OrdinalIgnoreCase);

    private static string NormalizePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return string.Empty;
        try { return Path.GetFullPath(path); }
        catch (ArgumentException) { return path.Trim(); }
        catch (NotSupportedException) { return path.Trim(); }
    }
}
