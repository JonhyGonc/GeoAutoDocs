namespace GeoAutoDocs.Core;

public static class LocationOrdering
{
    public static void Apply(Project project, IReadOnlyList<Guid> orderedLocationIds)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(orderedLocationIds);

        var locations = project.Locations.ToDictionary(x => x.Id);
        if (orderedLocationIds.Count != locations.Count ||
            orderedLocationIds.Distinct().Count() != orderedLocationIds.Count ||
            orderedLocationIds.Any(id => !locations.ContainsKey(id)))
            throw new InvalidDataException("A ordenação informada não corresponde aos Locais do projeto.");

        for (var index = 0; index < orderedLocationIds.Count; index++)
            locations[orderedLocationIds[index]].Order = index;
    }
}
