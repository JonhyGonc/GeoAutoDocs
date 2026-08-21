namespace GeoAutoDocs.Core;

public interface IAnomalyCatalogService
{
    Task<IReadOnlyList<AnomalyDefinition>> ListAsync(CancellationToken cancellationToken = default);
    Task<AnomalyDefinition> SaveAsync(AnomalyDefinition definition, CancellationToken cancellationToken = default);
    Task SetActiveAsync(Guid id, bool active, CancellationToken cancellationToken = default);
    Task ReorderAsync(IReadOnlyList<Guid> orderedIds, CancellationToken cancellationToken = default);
}
