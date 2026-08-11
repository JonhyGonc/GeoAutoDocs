using GeoAutoDocs.Core;
using Microsoft.EntityFrameworkCore;

namespace GeoAutoDocs.Infrastructure;

public sealed class GeoAutoDocsDbContext(DbContextOptions<GeoAutoDocsDbContext> options) : DbContext(options)
{
    public DbSet<Project> Projects => Set<Project>();
    public DbSet<Report> Reports => Set<Report>();
    public DbSet<ReportSection> ReportSections => Set<ReportSection>();
    public DbSet<AnomalyDefinition> AnomalyDefinitions => Set<AnomalyDefinition>();
    public DbSet<OccurrenceAnomaly> OccurrenceAnomalies => Set<OccurrenceAnomaly>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Project>(entity =>
        {
            entity.Property(x => x.Name).HasMaxLength(200).IsRequired();
            entity.HasMany(x => x.Reports).WithOne()
                .HasForeignKey(x => x.ProjectId).OnDelete(DeleteBehavior.Cascade);
            entity.HasMany(x => x.Locations).WithOne().HasForeignKey(x => x.ProjectId).OnDelete(DeleteBehavior.Cascade);
            entity.HasMany(x => x.Captures).WithOne().HasForeignKey(x => x.ProjectId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<Report>().ToTable("ReportConfiguration");
        modelBuilder.Entity<Report>().HasMany(x => x.CustomSections).WithOne()
            .HasForeignKey(x => x.ReportId).OnDelete(DeleteBehavior.Cascade);
        modelBuilder.Entity<Report>().HasMany(x => x.DevelopmentImages).WithOne()
            .HasForeignKey(x => x.ReportId).OnDelete(DeleteBehavior.Cascade);
        modelBuilder.Entity<DevelopmentImage>().ToTable("DevelopmentImage");
        modelBuilder.Entity<Report>().HasMany(x => x.SectionLinks).WithOne()
            .HasForeignKey(x => x.ReportId).OnDelete(DeleteBehavior.Cascade);
        modelBuilder.Entity<ReportSection>().ToTable("ReportSection");
        modelBuilder.Entity<ReportSection>().HasKey(x => new { x.ReportId, x.SectionId });
        modelBuilder.Entity<ReportSection>().HasOne(x => x.Section).WithMany()
            .HasForeignKey(x => x.SectionId).OnDelete(DeleteBehavior.Cascade);

        modelBuilder.Entity<CustomSection>().HasMany(x => x.Images).WithOne()
            .HasForeignKey(x => x.CustomSectionId).OnDelete(DeleteBehavior.Cascade);
        modelBuilder.Entity<CustomSectionImage>().ToTable("CustomSectionImages");

        modelBuilder.Entity<Location>().HasMany(x => x.Sections).WithOne()
            .HasForeignKey(x => x.LocationId).OnDelete(DeleteBehavior.Cascade);
        modelBuilder.Entity<InspectionSection>().HasMany(x => x.VideoReferences).WithOne()
            .HasForeignKey(x => x.InspectionSectionId).OnDelete(DeleteBehavior.Cascade);
        modelBuilder.Entity<InspectionSection>().HasMany(x => x.Occurrences).WithOne()
            .HasForeignKey(x => x.InspectionSectionId).OnDelete(DeleteBehavior.Cascade);
        modelBuilder.Entity<Capture>().ToTable("PendingCapture");
        modelBuilder.Entity<Capture>().HasOne<InspectionSection>().WithMany()
            .HasForeignKey(x => x.SectionId).OnDelete(DeleteBehavior.Cascade);
        modelBuilder.Entity<Capture>().HasOne<VideoReference>().WithMany()
            .HasForeignKey(x => x.VideoReferenceId).OnDelete(DeleteBehavior.Restrict);
        modelBuilder.Entity<Occurrence>().HasMany(x => x.Evidence).WithOne()
            .HasForeignKey(x => x.OccurrenceId).OnDelete(DeleteBehavior.Cascade);
        modelBuilder.Entity<Occurrence>().HasOne<VideoReference>().WithMany()
            .HasForeignKey(x => x.VideoReferenceId).OnDelete(DeleteBehavior.SetNull);
        modelBuilder.Entity<Occurrence>().HasMany(x => x.Anomalies).WithOne()
            .HasForeignKey(x => x.OccurrenceId).OnDelete(DeleteBehavior.Cascade);
        modelBuilder.Entity<OccurrenceAnomaly>(entity =>
        {
            entity.ToTable("OccurrenceAnomaly");
            entity.HasKey(x => new { x.AnomalyTypeId, x.OccurrenceId });
            entity.Property(x => x.AnomalyTypeId).HasColumnName("AnomaliesId");
            entity.Property(x => x.OccurrenceId).HasColumnName("OccurrencesId");
            entity.HasOne(x => x.AnomalyType).WithMany()
                .HasForeignKey(x => x.AnomalyTypeId).OnDelete(DeleteBehavior.Restrict);
        });
        modelBuilder.Entity<Evidence>().HasOne<Capture>().WithMany()
            .HasForeignKey(x => x.CaptureId).OnDelete(DeleteBehavior.Restrict);
        modelBuilder.Entity<Evidence>().HasIndex(x => x.CaptureId).IsUnique();

        modelBuilder.Entity<Report>().Property(x => x.TotalInvestigatedMeters).HasPrecision(12, 3);
        modelBuilder.Entity<InspectionSection>().Property(x => x.Diameter).HasPrecision(12, 3);
        modelBuilder.Entity<InspectionSection>().Property(x => x.InvestigatedMeters).HasPrecision(12, 3);
        modelBuilder.Entity<Occurrence>().Property(x => x.Meterage).HasPrecision(12, 3);
        modelBuilder.Entity<Occurrence>().Property(x => x.EndMeterage).HasPrecision(12, 3);
    }
}
