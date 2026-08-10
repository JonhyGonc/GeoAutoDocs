using GeoAutoDocs.Core;
using Microsoft.EntityFrameworkCore;

namespace GeoAutoDocs.Infrastructure;

public sealed class GeoAutoDocsDbContext(DbContextOptions<GeoAutoDocsDbContext> options) : DbContext(options)
{
    public DbSet<Project> Projects => Set<Project>();
    public DbSet<AnomalyDefinition> AnomalyDefinitions => Set<AnomalyDefinition>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Project>(entity =>
        {
            entity.Property(x => x.Name).HasMaxLength(200).IsRequired();
            entity.HasOne(x => x.ReportConfiguration).WithOne()
                .HasForeignKey<ReportConfiguration>(x => x.ProjectId).OnDelete(DeleteBehavior.Cascade);
            entity.HasMany(x => x.Locations).WithOne().HasForeignKey(x => x.ProjectId).OnDelete(DeleteBehavior.Cascade);
            entity.HasMany(x => x.PendingCaptures).WithOne().HasForeignKey(x => x.ProjectId).OnDelete(DeleteBehavior.Cascade);
            entity.HasMany(x => x.CustomSections).WithOne().HasForeignKey(x => x.ProjectId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<CustomSection>().HasMany(x => x.Images).WithOne()
            .HasForeignKey(x => x.CustomSectionId).OnDelete(DeleteBehavior.Cascade);
        modelBuilder.Entity<CustomSectionImage>().ToTable("CustomSectionImages");

        modelBuilder.Entity<Location>().HasMany(x => x.Sections).WithOne()
            .HasForeignKey(x => x.LocationId).OnDelete(DeleteBehavior.Cascade);
        modelBuilder.Entity<InspectionSection>().HasOne(x => x.VideoReference).WithOne()
            .HasForeignKey<VideoReference>(x => x.InspectionSectionId).OnDelete(DeleteBehavior.Cascade);
        modelBuilder.Entity<InspectionSection>().HasMany(x => x.Occurrences).WithOne()
            .HasForeignKey(x => x.InspectionSectionId).OnDelete(DeleteBehavior.Cascade);
        modelBuilder.Entity<Occurrence>().HasMany(x => x.Evidence).WithOne()
            .HasForeignKey(x => x.OccurrenceId).OnDelete(DeleteBehavior.Cascade);
        modelBuilder.Entity<Occurrence>().HasMany(x => x.Anomalies).WithMany(x => x.Occurrences)
            .UsingEntity("OccurrenceAnomaly");

        modelBuilder.Entity<ReportConfiguration>().Property(x => x.TotalInvestigatedMeters).HasPrecision(12, 3);
        modelBuilder.Entity<InspectionSection>().Property(x => x.Diameter).HasPrecision(12, 3);
        modelBuilder.Entity<InspectionSection>().Property(x => x.InvestigatedMeters).HasPrecision(12, 3);
        modelBuilder.Entity<Occurrence>().Property(x => x.Meterage).HasPrecision(12, 3);
        modelBuilder.Entity<Occurrence>().Property(x => x.EndMeterage).HasPrecision(12, 3);
    }
}
