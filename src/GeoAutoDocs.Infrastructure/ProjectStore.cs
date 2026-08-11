using GeoAutoDocs.Core;
using Microsoft.EntityFrameworkCore;

namespace GeoAutoDocs.Infrastructure;

public sealed class ProjectStore : IProjectStore
{
    private readonly DbContextOptions<GeoAutoDocsDbContext> _options;
    private readonly string _projectsDirectory;
    private readonly string _databasePath;

    public ProjectStore(AppDataPaths paths)
    {
        paths.EnsureDirectories();
        _projectsDirectory = paths.ProjectsDirectory;
        _databasePath = paths.DatabasePath;
        _options = new DbContextOptionsBuilder<GeoAutoDocsDbContext>()
            .UseSqlite($"Data Source={paths.DatabasePath};Pooling=False")
            .Options;
    }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        await using var db = CreateContext();
        await db.Database.EnsureCreatedAsync(cancellationToken);
        await BackupLegacyDatabaseAsync(db, cancellationToken);
        await EnsureStage25SchemaAsync(db, cancellationToken);
        await BackupStage4DatabaseAsync(db, cancellationToken);
        await EnsureStage4SchemaAsync(db, cancellationToken);
        await EnsureStage5SchemaAsync(db, cancellationToken);
        await EnsureTechnicalAnomalySchemaAsync(db, cancellationToken);
        await EnsureAnomalyCatalogAsync(db, cancellationToken);
    }

    public async Task<IReadOnlyList<Project>> ListAsync(CancellationToken cancellationToken = default)
    {
        await using var db = CreateContext();
        return await db.Projects.AsNoTracking().Include(x => x.Reports)
            .OrderByDescending(x => x.UpdatedAt).ToListAsync(cancellationToken);
    }

    public async Task<Project?> GetAsync(Guid id, CancellationToken cancellationToken = default)
    {
        await using var db = CreateContext();
        return await db.Projects.AsNoTrackingWithIdentityResolution()
            .AsSplitQuery()
            .Include(x => x.Reports).ThenInclude(x => x.CustomSections).ThenInclude(x => x.Images)
            .Include(x => x.Reports).ThenInclude(x => x.DevelopmentImages)
            .Include(x => x.Reports).ThenInclude(x => x.SectionLinks).ThenInclude(x => x.Section)
            .Include(x => x.Locations).ThenInclude(x => x.Sections).ThenInclude(x => x.VideoReferences)
            .Include(x => x.Locations).ThenInclude(x => x.Sections).ThenInclude(x => x.Occurrences)
                .ThenInclude(x => x.Anomalies).ThenInclude(x => x.AnomalyType)
            .Include(x => x.Locations).ThenInclude(x => x.Sections).ThenInclude(x => x.Occurrences).ThenInclude(x => x.Evidence)
            .Include(x => x.Captures)
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
        Directory.CreateDirectory(project.ProjectPath);

        await using var db = CreateContext();
        db.Projects.Add(project);
        await db.SaveChangesAsync(cancellationToken);
        return project;
    }

    public async Task<IReadOnlyList<AnomalyDefinition>> ListAnomalyDefinitionsAsync(
        CancellationToken cancellationToken = default)
    {
        await using var db = CreateContext();
        return await db.AnomalyDefinitions.AsNoTracking().Where(x => x.IsActive)
            .OrderBy(x => x.Order).ThenBy(x => x.Name).ToListAsync(cancellationToken);
    }

    public async Task SaveAsync(Project project, CancellationToken cancellationToken = default)
    {
        project.Name = project.Name.Trim();
        if (project.Name.Length == 0) throw new InvalidOperationException("O nome do projeto é obrigatório.");
        project.UpdatedAt = DateTime.UtcNow;
        NormalizeAggregate(project);

        await RemoveMissingAggregateContentAsync(project, cancellationToken);
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

    private static void NormalizeAggregate(Project project)
    {
        foreach (var report in project.Reports)
        {
            report.ProjectId = project.Id;
            foreach (var custom in report.CustomSections)
            {
                custom.ProjectId = project.Id;
                custom.ReportId = report.Id;
                foreach (var image in custom.Images) image.CustomSectionId = custom.Id;
            }
            foreach (var image in report.DevelopmentImages) image.ReportId = report.Id;
            foreach (var link in report.SectionLinks) link.ReportId = report.Id;
        }

        foreach (var location in project.Locations)
        {
            location.ProjectId = project.Id;
            foreach (var section in location.Sections)
            {
                section.LocationId = location.Id;
                foreach (var video in section.VideoReferences) video.InspectionSectionId = section.Id;
                foreach (var occurrence in section.Occurrences)
                {
                    occurrence.InspectionSectionId = section.Id;
                    foreach (var anomaly in occurrence.Anomalies) anomaly.OccurrenceId = occurrence.Id;
                    foreach (var evidence in occurrence.Evidence) evidence.OccurrenceId = occurrence.Id;
                }
            }
        }

        foreach (var capture in project.Captures) capture.ProjectId = project.Id;
    }

    private async Task RemoveMissingAggregateContentAsync(Project project, CancellationToken cancellationToken)
    {
        await using var db = CreateContext();
        var existingReports = await db.Reports.Include(x => x.CustomSections).ThenInclude(x => x.Images)
            .Include(x => x.DevelopmentImages)
            .Include(x => x.SectionLinks).Where(x => x.ProjectId == project.Id).ToListAsync(cancellationToken);
        var currentReportIds = project.Reports.Select(x => x.Id).ToHashSet();
        db.Reports.RemoveRange(existingReports.Where(x => !currentReportIds.Contains(x.Id)));

        foreach (var storedReport in existingReports.Where(x => currentReportIds.Contains(x.Id)))
        {
            var current = project.Reports.Single(x => x.Id == storedReport.Id);
            var currentCustomIds = current.CustomSections.Select(x => x.Id).ToHashSet();
            db.RemoveRange(storedReport.CustomSections.Where(x => !currentCustomIds.Contains(x.Id)));
            foreach (var storedCustom in storedReport.CustomSections.Where(x => currentCustomIds.Contains(x.Id)))
            {
                var currentCustom = current.CustomSections.Single(x => x.Id == storedCustom.Id);
                var imageIds = currentCustom.Images.Select(x => x.Id).ToHashSet();
                db.RemoveRange(storedCustom.Images.Where(x => !imageIds.Contains(x.Id)));
            }
            var linkIds = current.SectionLinks.Select(x => x.SectionId).ToHashSet();
            db.ReportSections.RemoveRange(storedReport.SectionLinks.Where(x => !linkIds.Contains(x.SectionId)));
            var developmentImageIds = current.DevelopmentImages.Select(x => x.Id).ToHashSet();
            db.RemoveRange(storedReport.DevelopmentImages.Where(x => !developmentImageIds.Contains(x.Id)));
        }

        var sectionIds = project.Locations.SelectMany(x => x.Sections).Select(x => x.Id).ToList();
        if (sectionIds.Count > 0)
        {
            var currentVideoIds = project.Locations.SelectMany(x => x.Sections)
                .SelectMany(x => x.VideoReferences).Select(x => x.Id).ToHashSet();
            var storedVideos = await db.Set<VideoReference>()
                .Where(x => sectionIds.Contains(x.InspectionSectionId)).ToListAsync(cancellationToken);
            db.RemoveRange(storedVideos.Where(x => !currentVideoIds.Contains(x.Id)));

            var currentOccurrences = project.Locations.SelectMany(x => x.Sections)
                .SelectMany(x => x.Occurrences).ToDictionary(x => x.Id);
            var storedOccurrences = await db.Set<Occurrence>().Include(x => x.Anomalies).Include(x => x.Evidence)
                .Where(x => sectionIds.Contains(x.InspectionSectionId)).ToListAsync(cancellationToken);
            db.RemoveRange(storedOccurrences.Where(x => !currentOccurrences.ContainsKey(x.Id)));
            foreach (var stored in storedOccurrences.Where(x => currentOccurrences.ContainsKey(x.Id)))
            {
                var current = currentOccurrences[stored.Id];
                var evidenceIds = current.Evidence.Select(x => x.Id).ToHashSet();
                db.RemoveRange(stored.Evidence.Where(x => !evidenceIds.Contains(x.Id)));
                var anomalyIds = current.Anomalies.Select(x => x.AnomalyTypeId).ToHashSet();
                db.OccurrenceAnomalies.RemoveRange(stored.Anomalies.Where(x => !anomalyIds.Contains(x.AnomalyTypeId)));
            }
        }

        var currentCaptureIds = project.Captures.Select(x => x.Id).ToHashSet();
        var storedCaptures = await db.Set<Capture>().Where(x => x.ProjectId == project.Id)
            .ToListAsync(cancellationToken);
        db.RemoveRange(storedCaptures.Where(x => !currentCaptureIds.Contains(x.Id)));
        await db.SaveChangesAsync(cancellationToken);
    }

    private async Task BackupLegacyDatabaseAsync(GeoAutoDocsDbContext db, CancellationToken cancellationToken)
    {
        if (!File.Exists(_databasePath) || await ColumnExistsAsync(db, "ReportConfiguration", "Name", cancellationToken)) return;
        var backup = _databasePath + ".pre-stage25.bak";
        if (!File.Exists(backup)) File.Copy(_databasePath, backup);
    }

    private async Task BackupStage4DatabaseAsync(GeoAutoDocsDbContext db, CancellationToken cancellationToken)
    {
        if (!File.Exists(_databasePath) || await ColumnExistsAsync(db, "Occurrence", "GeneratedDescription", cancellationToken)) return;
        var backup = _databasePath + ".pre-stage4.bak";
        if (!File.Exists(backup)) File.Copy(_databasePath, backup);
    }

    private static async Task EnsureStage25SchemaAsync(GeoAutoDocsDbContext db, CancellationToken cancellationToken)
    {
        var projectColumns = new (string, string)[]
        {
            ("StorageKind", "INTEGER NOT NULL DEFAULT 0"), ("SharedProjectPath", "TEXT NULL"),
            ("ProjectDataVersion", "INTEGER NOT NULL DEFAULT 0"), ("DefaultClient", "TEXT NOT NULL DEFAULT ''"),
            ("DefaultCnpj", "TEXT NOT NULL DEFAULT ''"), ("DefaultArea", "TEXT NOT NULL DEFAULT ''"),
            ("DefaultContract", "TEXT NOT NULL DEFAULT ''"), ("DefaultPurchaseOrder", "TEXT NOT NULL DEFAULT ''"),
            ("DefaultLogoPath", "TEXT NULL")
        };
        var reportColumns = new (string, string)[]
        {
            ("LogoPath", "TEXT NULL"), ("LocationImagePath", "TEXT NULL"), ("TemplatePath", "TEXT NULL"),
            ("GeneratedReportPath", "TEXT NULL"), ("Equipments", "INTEGER NOT NULL DEFAULT 0"),
            ("Diameters", "INTEGER NOT NULL DEFAULT 0"), ("CustomDiameters", "TEXT NOT NULL DEFAULT ''"),
            ("IncludeInspectionOpening", "INTEGER NOT NULL DEFAULT 0"), ("IncludeGpr", "INTEGER NOT NULL DEFAULT 0"),
            ("Name", "TEXT NOT NULL DEFAULT ''"), ("Order", "INTEGER NOT NULL DEFAULT 0"),
            ("Cnpj", "TEXT NOT NULL DEFAULT ''")
        };
        var videoColumns = new (string, string)[]
        {
            ("DisplayName", "TEXT NOT NULL DEFAULT ''"), ("Order", "INTEGER NOT NULL DEFAULT 0"),
            ("Description", "TEXT NULL"), ("CreatedAt", "TEXT NULL")
        };

        foreach (var column in projectColumns) await AddColumnIfMissingAsync(db, "Projects", column.Item1, column.Item2, cancellationToken);
        foreach (var column in reportColumns) await AddColumnIfMissingAsync(db, "ReportConfiguration", column.Item1, column.Item2, cancellationToken);
        foreach (var column in videoColumns) await AddColumnIfMissingAsync(db, "VideoReference", column.Item1, column.Item2, cancellationToken);
        var captureColumns = new (string, string)[]
        {
            ("SectionId", "TEXT NOT NULL DEFAULT '00000000-0000-0000-0000-000000000000'"),
            ("VideoReferenceId", "TEXT NOT NULL DEFAULT '00000000-0000-0000-0000-000000000000'"),
            ("Status", "INTEGER NOT NULL DEFAULT 0"), ("Order", "INTEGER NOT NULL DEFAULT 0"),
            ("ManagedByGeoAutoDocs", "INTEGER NOT NULL DEFAULT 1"), ("SharedFilePath", "TEXT NULL")
        };
        foreach (var column in captureColumns) await AddColumnIfMissingAsync(db, "PendingCapture", column.Item1, column.Item2, cancellationToken);
        await AddColumnIfMissingAsync(db, "Occurrence", "VideoReferenceId", "TEXT NULL", cancellationToken);
        await AddColumnIfMissingAsync(db, "CustomSection", "ReportId", "TEXT NOT NULL DEFAULT '00000000-0000-0000-0000-000000000000'", cancellationToken);

        await db.Database.ExecuteSqlRawAsync("DROP INDEX IF EXISTS \"IX_ReportConfiguration_ProjectId\"", cancellationToken);
        await db.Database.ExecuteSqlRawAsync("CREATE INDEX IF NOT EXISTS \"IX_ReportConfiguration_ProjectId\" ON \"ReportConfiguration\" (\"ProjectId\")", cancellationToken);
        await db.Database.ExecuteSqlRawAsync("DROP INDEX IF EXISTS \"IX_VideoReference_InspectionSectionId\"", cancellationToken);
        await db.Database.ExecuteSqlRawAsync("CREATE INDEX IF NOT EXISTS \"IX_VideoReference_InspectionSectionId\" ON \"VideoReference\" (\"InspectionSectionId\")", cancellationToken);
        await db.Database.ExecuteSqlRawAsync("CREATE INDEX IF NOT EXISTS \"IX_Occurrence_VideoReferenceId\" ON \"Occurrence\" (\"VideoReferenceId\")", cancellationToken);
        await db.Database.ExecuteSqlRawAsync("CREATE INDEX IF NOT EXISTS \"IX_PendingCapture_ProjectId\" ON \"PendingCapture\" (\"ProjectId\")", cancellationToken);
        await db.Database.ExecuteSqlRawAsync("CREATE INDEX IF NOT EXISTS \"IX_PendingCapture_SectionId\" ON \"PendingCapture\" (\"SectionId\")", cancellationToken);
        await db.Database.ExecuteSqlRawAsync("CREATE INDEX IF NOT EXISTS \"IX_PendingCapture_VideoReferenceId\" ON \"PendingCapture\" (\"VideoReferenceId\")", cancellationToken);
        await db.Database.ExecuteSqlRawAsync("CREATE INDEX IF NOT EXISTS \"IX_CustomSection_ReportId\" ON \"CustomSection\" (\"ReportId\")", cancellationToken);
        await db.Database.ExecuteSqlRawAsync("""
            CREATE TABLE IF NOT EXISTS "ReportSection" (
                "ReportId" TEXT NOT NULL,
                "SectionId" TEXT NOT NULL,
                "Order" INTEGER NOT NULL,
                CONSTRAINT "PK_ReportSection" PRIMARY KEY ("ReportId", "SectionId"),
                CONSTRAINT "FK_ReportSection_ReportConfiguration_ReportId" FOREIGN KEY ("ReportId") REFERENCES "ReportConfiguration" ("Id") ON DELETE CASCADE,
                CONSTRAINT "FK_ReportSection_InspectionSection_SectionId" FOREIGN KEY ("SectionId") REFERENCES "InspectionSection" ("Id") ON DELETE CASCADE
            );
            CREATE INDEX IF NOT EXISTS "IX_ReportSection_SectionId" ON "ReportSection" ("SectionId");
            """, cancellationToken);
        await db.Database.ExecuteSqlRawAsync("UPDATE \"ReportConfiguration\" SET \"Name\" = 'Report 001' WHERE trim(\"Name\") = ''", cancellationToken);
        await db.Database.ExecuteSqlRawAsync("""
            UPDATE "Projects" SET
                "DefaultClient" = COALESCE((SELECT "Client" FROM "ReportConfiguration" r WHERE r."ProjectId" = "Projects"."Id" ORDER BY r."Order", r."Id" LIMIT 1), ''),
                "DefaultCnpj" = COALESCE((SELECT "Cnpj" FROM "ReportConfiguration" r WHERE r."ProjectId" = "Projects"."Id" ORDER BY r."Order", r."Id" LIMIT 1), ''),
                "DefaultArea" = COALESCE((SELECT "Area" FROM "ReportConfiguration" r WHERE r."ProjectId" = "Projects"."Id" ORDER BY r."Order", r."Id" LIMIT 1), ''),
                "DefaultContract" = COALESCE((SELECT "Contract" FROM "ReportConfiguration" r WHERE r."ProjectId" = "Projects"."Id" ORDER BY r."Order", r."Id" LIMIT 1), ''),
                "DefaultPurchaseOrder" = COALESCE((SELECT "PurchaseOrder" FROM "ReportConfiguration" r WHERE r."ProjectId" = "Projects"."Id" ORDER BY r."Order", r."Id" LIMIT 1), ''),
                "DefaultLogoPath" = (SELECT "LogoPath" FROM "ReportConfiguration" r WHERE r."ProjectId" = "Projects"."Id" ORDER BY r."Order", r."Id" LIMIT 1)
            WHERE "DefaultClient" = '' AND EXISTS (SELECT 1 FROM "ReportConfiguration" r WHERE r."ProjectId" = "Projects"."Id");
            UPDATE "CustomSection" SET "ReportId" = COALESCE(
                (SELECT "Id" FROM "ReportConfiguration" r WHERE r."ProjectId" = "CustomSection"."ProjectId" ORDER BY r."Order", r."Id" LIMIT 1),
                "ReportId")
            WHERE "ReportId" = '00000000-0000-0000-0000-000000000000';
            """, cancellationToken);
    }

    private static async Task EnsureStage4SchemaAsync(GeoAutoDocsDbContext db, CancellationToken cancellationToken)
    {
        var sectionColumns = new (string, string)[]
        {
            ("Description", "TEXT NULL"), ("StartPoint", "TEXT NULL"), ("EndPoint", "TEXT NULL")
        };
        var occurrenceColumns = new (string, string)[]
        {
            ("ReferencePoint", "TEXT NULL"), ("GeneratedDescription", "TEXT NOT NULL DEFAULT ''"),
            ("ManualDescriptionOverride", "TEXT NULL"),
            ("CreatedAt", "TEXT NOT NULL DEFAULT '0001-01-01 00:00:00'"),
            ("UpdatedAt", "TEXT NOT NULL DEFAULT '0001-01-01 00:00:00'")
        };
        var anomalyColumns = new (string, string)[]
        {
            ("RequiresPosition", "INTEGER NOT NULL DEFAULT 0"),
            ("AllowsCustomText", "INTEGER NOT NULL DEFAULT 0")
        };
        var occurrenceAnomalyColumns = new (string, string)[]
        {
            ("Position", "INTEGER NOT NULL DEFAULT 0"), ("CustomText", "TEXT NULL"),
            ("Order", "INTEGER NOT NULL DEFAULT 0")
        };
        var evidenceColumns = new (string, string)[]
        {
            ("CaptureId", "TEXT NULL"), ("SourceType", "INTEGER NOT NULL DEFAULT 1"),
            ("VideoReferenceId", "TEXT NULL"), ("ManagedByGeoAutoDocs", "INTEGER NOT NULL DEFAULT 0"),
            ("SharedFilePath", "TEXT NULL")
        };

        foreach (var column in sectionColumns) await AddColumnIfMissingAsync(db, "InspectionSection", column.Item1, column.Item2, cancellationToken);
        foreach (var column in occurrenceColumns) await AddColumnIfMissingAsync(db, "Occurrence", column.Item1, column.Item2, cancellationToken);
        foreach (var column in anomalyColumns) await AddColumnIfMissingAsync(db, "AnomalyDefinitions", column.Item1, column.Item2, cancellationToken);
        foreach (var column in occurrenceAnomalyColumns) await AddColumnIfMissingAsync(db, "OccurrenceAnomaly", column.Item1, column.Item2, cancellationToken);
        foreach (var column in evidenceColumns) await AddColumnIfMissingAsync(db, "Evidence", column.Item1, column.Item2, cancellationToken);

        await db.Database.ExecuteSqlRawAsync("CREATE UNIQUE INDEX IF NOT EXISTS \"IX_Evidence_CaptureId\" ON \"Evidence\" (\"CaptureId\") WHERE \"CaptureId\" IS NOT NULL", cancellationToken);
        await db.Database.ExecuteSqlRawAsync("CREATE INDEX IF NOT EXISTS \"IX_Evidence_VideoReferenceId\" ON \"Evidence\" (\"VideoReferenceId\")", cancellationToken);
    }

    private static async Task EnsureStage5SchemaAsync(GeoAutoDocsDbContext db, CancellationToken cancellationToken)
    {
        await db.Database.ExecuteSqlRawAsync("""
            CREATE TABLE IF NOT EXISTS "DevelopmentImage" (
                "Id" TEXT NOT NULL CONSTRAINT "PK_DevelopmentImage" PRIMARY KEY,
                "ReportId" TEXT NOT NULL,
                "Group" INTEGER NOT NULL,
                "FilePath" TEXT NOT NULL,
                "Order" INTEGER NOT NULL,
                CONSTRAINT "FK_DevelopmentImage_ReportConfiguration_ReportId"
                    FOREIGN KEY ("ReportId") REFERENCES "ReportConfiguration" ("Id") ON DELETE CASCADE
            );
            CREATE INDEX IF NOT EXISTS "IX_DevelopmentImage_ReportId" ON "DevelopmentImage" ("ReportId");
            """, cancellationToken);
    }

    private static async Task EnsureTechnicalAnomalySchemaAsync(GeoAutoDocsDbContext db,
        CancellationToken cancellationToken)
    {
        var definitionColumns = new (string, string)[]
        {
            ("SupportsPosition", "INTEGER NOT NULL DEFAULT 1"),
            ("SupportsOrientation", "INTEGER NOT NULL DEFAULT 0"),
            ("SupportsExtension", "INTEGER NOT NULL DEFAULT 0"),
            ("SupportsDimension", "INTEGER NOT NULL DEFAULT 0"),
            ("SupportsCondition", "INTEGER NOT NULL DEFAULT 0"),
            ("SupportsInfiltrationCondition", "INTEGER NOT NULL DEFAULT 0"),
            ("SupportsPercentage", "INTEGER NOT NULL DEFAULT 0")
        };
        var anomalyColumns = new (string, string)[]
        {
            ("Orientation", "INTEGER NOT NULL DEFAULT 0"),
            ("ApproximateExtension", "TEXT NULL"),
            ("Dimension", "TEXT NULL"),
            ("Condition", "INTEGER NOT NULL DEFAULT 0"),
            ("InfiltrationCondition", "INTEGER NOT NULL DEFAULT 0"),
            ("EstimatedPercentage", "TEXT NULL"),
            ("Notes", "TEXT NULL")
        };
        foreach (var column in definitionColumns)
            await AddColumnIfMissingAsync(db, "AnomalyDefinitions", column.Item1, column.Item2, cancellationToken);
        foreach (var column in anomalyColumns)
            await AddColumnIfMissingAsync(db, "OccurrenceAnomaly", column.Item1, column.Item2, cancellationToken);
        await AddColumnIfMissingAsync(db, "Occurrence", "InterruptedRecording",
            "INTEGER NOT NULL DEFAULT 0", cancellationToken);
        await AddColumnIfMissingAsync(db, "Evidence", "IsPrimary", "INTEGER NOT NULL DEFAULT 0", cancellationToken);
    }

    private static async Task EnsureAnomalyCatalogAsync(GeoAutoDocsDbContext db, CancellationToken cancellationToken)
    {
        var existing = await db.AnomalyDefinitions.ToListAsync(cancellationToken);
        foreach (var item in AnomalyCatalog.Defaults)
        {
            var stored = existing.FirstOrDefault(x => x.Id == item.Id) ??
                         existing.FirstOrDefault(x => x.Name.Equals(item.Name, StringComparison.OrdinalIgnoreCase));
            if (stored is null)
            {
                db.AnomalyDefinitions.Add(new AnomalyDefinition
                {
                    Id = item.Id, Name = item.Name, IsActive = item.IsActive, Order = item.Order,
                    RequiresPosition = item.RequiresPosition, AllowsCustomText = item.AllowsCustomText,
                    SupportsPosition = item.SupportsPosition, SupportsOrientation = item.SupportsOrientation,
                    SupportsExtension = item.SupportsExtension, SupportsDimension = item.SupportsDimension,
                    SupportsCondition = item.SupportsCondition,
                    SupportsInfiltrationCondition = item.SupportsInfiltrationCondition,
                    SupportsPercentage = item.SupportsPercentage
                });
                continue;
            }
            stored.Name = item.Name;
            stored.RequiresPosition = item.RequiresPosition;
            stored.AllowsCustomText = item.AllowsCustomText;
            stored.Order = item.Order;
            stored.SupportsPosition = item.SupportsPosition;
            stored.SupportsOrientation = item.SupportsOrientation;
            stored.SupportsExtension = item.SupportsExtension;
            stored.SupportsDimension = item.SupportsDimension;
            stored.SupportsCondition = item.SupportsCondition;
            stored.SupportsInfiltrationCondition = item.SupportsInfiltrationCondition;
            stored.SupportsPercentage = item.SupportsPercentage;
        }
        await db.SaveChangesAsync(cancellationToken);
    }

    private static async Task AddColumnIfMissingAsync(GeoAutoDocsDbContext db, string table, string name,
        string definition, CancellationToken cancellationToken)
    {
        if (await ColumnExistsAsync(db, table, name, cancellationToken)) return;
        var connection = db.Database.GetDbConnection();
        var close = connection.State != System.Data.ConnectionState.Open;
        if (close) await connection.OpenAsync(cancellationToken);
        try
        {
            await using var command = connection.CreateCommand();
            command.CommandText = $"ALTER TABLE \"{table}\" ADD COLUMN \"{name}\" {definition}";
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        finally
        {
            if (close) await connection.CloseAsync();
        }
    }

    private static async Task<bool> ColumnExistsAsync(GeoAutoDocsDbContext db, string table, string name,
        CancellationToken cancellationToken)
    {
        var connection = db.Database.GetDbConnection();
        var close = connection.State != System.Data.ConnectionState.Open;
        if (close) await connection.OpenAsync(cancellationToken);
        try
        {
            await using var command = connection.CreateCommand();
            command.CommandText = $"PRAGMA table_info('{table.Replace("'", "''")}')";
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
                if (string.Equals(reader.GetString(1), name, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }
        finally
        {
            if (close) await connection.CloseAsync();
        }
    }
}
