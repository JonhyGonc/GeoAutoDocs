namespace GeoAutoDocs.Core;

public interface IProjectStore
{
    Task InitializeAsync(CancellationToken cancellationToken = default);
    Task<IReadOnlyList<Project>> ListAsync(CancellationToken cancellationToken = default);
    Task<Project?> GetAsync(Guid id, CancellationToken cancellationToken = default);
    Task<Project> CreateAsync(string name, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<AnomalyDefinition>> ListAnomalyDefinitionsAsync(CancellationToken cancellationToken = default);
    Task SaveAsync(Project project, CancellationToken cancellationToken = default);
    Task DeleteAsync(Guid id, CancellationToken cancellationToken = default);
}
