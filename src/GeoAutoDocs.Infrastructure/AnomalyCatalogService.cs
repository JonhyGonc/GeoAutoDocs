using GeoAutoDocs.Core;
using Microsoft.EntityFrameworkCore;

namespace GeoAutoDocs.Infrastructure;

public sealed class AnomalyCatalogService(AppDataPaths paths) : IAnomalyCatalogService
{
    private GeoAutoDocsDbContext CreateContext() => new(new DbContextOptionsBuilder<GeoAutoDocsDbContext>()
        .UseSqlite($"Data Source={paths.DatabasePath};Pooling=False").Options);

    public async Task<IReadOnlyList<AnomalyDefinition>> ListAsync(CancellationToken cancellationToken = default)
    {
        await using var db = CreateContext();
        return await db.AnomalyDefinitions.AsNoTracking().OrderBy(x => x.Order).ThenBy(x => x.Name)
            .ToListAsync(cancellationToken);
    }

    public async Task<AnomalyDefinition> SaveAsync(AnomalyDefinition definition,
        CancellationToken cancellationToken = default)
    {
        definition.Name = definition.Name.Trim();
        definition.DescriptionTemplate = definition.DescriptionTemplate.Trim();
        if (definition.Name.Length == 0) throw new InvalidOperationException("O nome da anomalia é obrigatório.");
        AnomalyTemplate.Validate(definition.DescriptionTemplate);
        await using var db = CreateContext();
        if (await db.AnomalyDefinitions.AnyAsync(x => x.Id != definition.Id &&
                EF.Functions.Collate(x.Name, "NOCASE") == definition.Name, cancellationToken))
            throw new InvalidOperationException("Já existe uma anomalia com esse nome.");
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var stored = await db.AnomalyDefinitions.SingleOrDefaultAsync(x => x.Id == definition.Id, cancellationToken);
        var now = DateTime.UtcNow;
        if (stored is null)
        {
            definition.IsBuiltIn = false;
            definition.CreatedAt = now;
            definition.UpdatedAt = now;
            definition.Order = await db.AnomalyDefinitions.CountAsync(cancellationToken);
            db.AnomalyDefinitions.Add(definition);
            stored = definition;
        }
        else
        {
            stored.Name = definition.Name;
            stored.DescriptionTemplate = definition.DescriptionTemplate;
            stored.IsActive = definition.IsActive;
            stored.RequiresPosition = definition.RequiresPosition;
            stored.AllowsCustomText = definition.AllowsCustomText;
            stored.SupportsPosition = definition.SupportsPosition;
            stored.SupportsOrientation = definition.SupportsOrientation;
            stored.SupportsExtension = definition.SupportsExtension;
            stored.SupportsDimension = definition.SupportsDimension;
            stored.SupportsCondition = definition.SupportsCondition;
            stored.SupportsInfiltrationCondition = definition.SupportsInfiltrationCondition;
            stored.SupportsPercentage = definition.SupportsPercentage;
            stored.SupportsComplementaryText = definition.SupportsComplementaryText;
            stored.UpdatedAt = now;
        }
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return stored;
    }

    public async Task SetActiveAsync(Guid id, bool active, CancellationToken cancellationToken = default)
    {
        await using var db = CreateContext();
        var stored = await db.AnomalyDefinitions.SingleAsync(x => x.Id == id, cancellationToken);
        stored.IsActive = active;
        stored.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task ReorderAsync(IReadOnlyList<Guid> orderedIds, CancellationToken cancellationToken = default)
    {
        await using var db = CreateContext();
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var items = await db.AnomalyDefinitions.ToDictionaryAsync(x => x.Id, cancellationToken);
        for (var index = 0; index < orderedIds.Count; index++)
            if (items.TryGetValue(orderedIds[index], out var item)) item.Order = index;
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }
}
