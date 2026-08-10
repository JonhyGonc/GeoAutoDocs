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
        await EnsureStage2SchemaAsync(db, cancellationToken);
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
            .Include(x => x.PendingCaptures).Include(x => x.CustomSections).ThenInclude(x => x.Images)
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

        await RemoveMissingCustomContentAsync(project, cancellationToken);
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

    private async Task RemoveMissingCustomContentAsync(Project project, CancellationToken cancellationToken)
    {
        await using var db = CreateContext();
        var existing = await db.Set<CustomSection>().Include(x => x.Images)
            .Where(x => x.ProjectId == project.Id).ToListAsync(cancellationToken);

        foreach (var storedSection in existing)
        {
            var currentSection = project.CustomSections.SingleOrDefault(x => x.Id == storedSection.Id);
            if (currentSection is null)
            {
                db.Remove(storedSection);
                continue;
            }

            var currentImageIds = currentSection.Images.Select(x => x.Id).ToHashSet();
            db.RemoveRange(storedSection.Images.Where(x => !currentImageIds.Contains(x.Id)));
        }

        await db.SaveChangesAsync(cancellationToken);
    }

    private static async Task EnsureStage2SchemaAsync(GeoAutoDocsDbContext db, CancellationToken cancellationToken)
    {
        var columns = new (string Name, string Definition)[]
        {
            ("LogoPath", "TEXT NULL"),
            ("LocationImagePath", "TEXT NULL"),
            ("TemplatePath", "TEXT NULL"),
            ("GeneratedReportPath", "TEXT NULL"),
            ("Equipments", "INTEGER NOT NULL DEFAULT 0"),
            ("Diameters", "INTEGER NOT NULL DEFAULT 0"),
            ("CustomDiameters", "TEXT NOT NULL DEFAULT ''"),
            ("IncludeInspectionOpening", "INTEGER NOT NULL DEFAULT 0"),
            ("IncludeGpr", "INTEGER NOT NULL DEFAULT 0")
        };

        var connection = db.Database.GetDbConnection();
        await connection.OpenAsync(cancellationToken);
        try
        {
            foreach (var column in columns)
            {
                await using var info = connection.CreateCommand();
                info.CommandText = "PRAGMA table_info('ReportConfiguration')";
                var exists = false;
                await using (var reader = await info.ExecuteReaderAsync(cancellationToken))
                {
                    while (await reader.ReadAsync(cancellationToken))
                    {
                        if (string.Equals(reader.GetString(1), column.Name, StringComparison.OrdinalIgnoreCase))
                        {
                            exists = true;
                            break;
                        }
                    }
                }

                if (exists) continue;
                await using var alter = connection.CreateCommand();
                alter.CommandText = $"ALTER TABLE \"ReportConfiguration\" ADD COLUMN \"{column.Name}\" {column.Definition}";
                await alter.ExecuteNonQueryAsync(cancellationToken);
            }

            await using var createImages = connection.CreateCommand();
            createImages.CommandText = """
                CREATE TABLE IF NOT EXISTS "CustomSectionImages" (
                    "Id" TEXT NOT NULL CONSTRAINT "PK_CustomSectionImages" PRIMARY KEY,
                    "CustomSectionId" TEXT NOT NULL,
                    "FilePath" TEXT NOT NULL,
                    "Order" INTEGER NOT NULL,
                    CONSTRAINT "FK_CustomSectionImages_CustomSection_CustomSectionId"
                        FOREIGN KEY ("CustomSectionId") REFERENCES "CustomSection" ("Id") ON DELETE CASCADE
                );
                CREATE INDEX IF NOT EXISTS "IX_CustomSectionImages_CustomSectionId"
                    ON "CustomSectionImages" ("CustomSectionId");
                """;
            await createImages.ExecuteNonQueryAsync(cancellationToken);
        }
        finally
        {
            await connection.CloseAsync();
        }
    }
}
