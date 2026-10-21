using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Listenarr.Infrastructure.Persistence.Configurations;

public sealed class QuickAddOperationConfiguration : IEntityTypeConfiguration<QuickAddOperation>
{
    public void Configure(EntityTypeBuilder<QuickAddOperation> builder)
    {
        builder.ToTable("QuickAddOperations");
        builder.HasKey(operation => operation.Id);
        builder.Property(operation => operation.Id).HasMaxLength(32).IsRequired();
        builder.Property(operation => operation.RequestKey).HasMaxLength(64).IsRequired();
        builder.Property(operation => operation.AudiobookTitle).HasMaxLength(500).IsRequired();
        builder.Property(operation => operation.State).IsRequired();
        builder.Property(operation => operation.SelectedRelease).HasMaxLength(500);
        builder.Property(operation => operation.DownloadClientId).HasMaxLength(200);
        builder.Property(operation => operation.DownloadId).HasMaxLength(200);
        builder.Property(operation => operation.ProcessingJobId).HasMaxLength(64);
        builder.Property(operation => operation.FailureReason).HasMaxLength(2000);
        builder.HasIndex(operation => operation.RequestKey)
            .IsUnique()
            .HasFilter("\"State\" <> 12");
        builder.HasIndex(operation => operation.AudiobookId).IsUnique();
        builder.HasIndex(operation => new { operation.State, operation.UpdatedAt });
    }
}
