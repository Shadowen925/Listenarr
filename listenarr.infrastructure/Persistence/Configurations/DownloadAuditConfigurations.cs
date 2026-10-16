using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Listenarr.Infrastructure.Persistence.Configurations
{
    public class ReleaseRejectionConfiguration : IEntityTypeConfiguration<ReleaseRejection>
    {
        public void Configure(EntityTypeBuilder<ReleaseRejection> builder)
        {
            builder.ToTable("ReleaseRejections");
            builder.HasKey(r => r.Id);
            builder.Property(r => r.ReleaseKey).IsRequired().HasMaxLength(ReleaseRejection.MaxKeyLength);
            builder.Property(r => r.ReleaseTitle).HasMaxLength(500);
            builder.Property(r => r.Reason).IsRequired().HasMaxLength(500);
            builder.Property(r => r.Source).IsRequired().HasMaxLength(50);
            builder.HasIndex(r => new { r.AudiobookId, r.ReleaseKey }).IsUnique();
        }
    }

    public class DownloadEventConfiguration : IEntityTypeConfiguration<DownloadEvent>
    {
        public void Configure(EntityTypeBuilder<DownloadEvent> builder)
        {
            builder.ToTable("DownloadEvents");
            builder.HasKey(e => e.Id);
            builder.Property(e => e.EventType).IsRequired().HasMaxLength(50);
            builder.Property(e => e.Outcome).IsRequired().HasMaxLength(20);
            builder.Property(e => e.DownloadId).HasMaxLength(100);
            builder.Property(e => e.ClientId).HasMaxLength(100);
            builder.Property(e => e.ExternalId).HasMaxLength(200);
            builder.Property(e => e.ReleaseId).HasMaxLength(200);
            builder.Property(e => e.Title).HasMaxLength(500);
            builder.Property(e => e.Message).HasMaxLength(1000);
            builder.HasIndex(e => e.Timestamp);
            builder.HasIndex(e => e.DownloadId);
        }
    }
}
