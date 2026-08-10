using GeoAutoDocs.Core;
using Microsoft.EntityFrameworkCore;

namespace GeoAutoDocs.Infrastructure;

public sealed class ProjectStore : IProjectStore
{
    private readonly DbContextOptions<GeoAutoDocsDbContext> _options;
    private readonly string _projectsDirectory;

    public ProjectStore(AppDataPaths paths)
    {
        paths.EnsureDirectories();
        _projectsDirectory = paths.ProjectsDirectory;
        _options = new DbContextOptionsBuilder<GeoAutoDocsDbContext>()
            .UseSqlite($"Data Source={paths.DatabasePath};Pooling=False")
            .Options;
    }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        await using var db = CreateContext();
        await db.Database.EnsureCreatedAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<Project>> ListAsync(CancellationToken cancellationToken = default)
    {
        await using var db = CreateContext();
        return await db.Projects.AsNoTracking().Include(x => x.ReportConfiguration)
            .OrderByDescending(x => x.UpdatedAt).ToListAsync(cancellationToken);
    }

    public async Task<Project?> GetAsync(Guid id, CancellationToken cancellationToken = default)
    {
        await using var db = CreateContext();
        return await db.Projects.AsNoTracking()
            .Include(x => x.ReportConfiguration)
            .Include(x => x.Locations).ThenInclude(x => x.Sections).ThenInclude(x => x.VideoReference)
            .Include(x => x.Locations).ThenInclude(x => x.Sections).ThenInclude(x => x.Occurrences).ThenInclude(x => x.Anomalies)
            .Include(x => x.Locations).ThenInclude(x => x.Sections).ThenInclude(x => x.Occurrences).ThenInclude(x => x.Evidence)
            .Include(x => x.PendingCaptures).Include(x => x.CustomSections)
            .SingleOrDefaultAsync(x => x.Id == id, cancellationToken);
    }

    public async Task<Project> CreateAsync(string name, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("O nome do projeto é obrigatório.", nameof(name));

        var now = DateTime.UtcNow;
        var project = new Project
        {
            Name = name.Trim(), CreatedAt = now, UpdatedAt = now,
            ProjectPath = Path.Combine(_projectsDirectory, Guid.NewGuid().ToString("N"))
        };
        project.ReportConfiguration.ProjectId = project.Id;
        Directory.CreateDirectory(project.ProjectPath);

        await using var db = CreateContext();
        db.Projects.Add(project);
        await db.SaveChangesAsync(cancellationToken);
        return project;
    }

    public async Task SaveAsync(Project project, CancellationToken cancellationToken = default)
    {
        project.Name = project.Name.Trim();
        if (project.Name.Length == 0) throw new InvalidOperationException("O nome do projeto é obrigatório.");
        project.UpdatedAt = DateTime.UtcNow;

        await using var lookup = CreateContext();
        await using var db = CreateContext();
        db.ChangeTracker.TrackGraph(project, node =>
        {
            var key = node.Entry.Metadata.FindPrimaryKey();
            if (key is null)
            {
                node.Entry.State = EntityState.Added;
                return;
            }

            var values = key.Properties.Select(property => node.Entry.Property(property.Name).CurrentValue).ToArray();
            node.Entry.State = lookup.Find(node.Entry.Metadata.ClrType, values) is null
                ? EntityState.Added
                : EntityState.Modified;
        });
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task DeleteAsync(Guid id, CancellationToken cancellationToken = default)
    {
        await using var db = CreateContext();
        var project = await db.Projects.SingleOrDefaultAsync(x => x.Id == id, cancellationToken);
        if (project is null) return;
        db.Projects.Remove(project);
        await db.SaveChangesAsync(cancellationToken);
    }

    private GeoAutoDocsDbContext CreateContext() => new(_options);
}
