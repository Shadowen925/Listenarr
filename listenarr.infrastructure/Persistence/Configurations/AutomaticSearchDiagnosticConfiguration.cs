using Listenarr.Infrastructure.Persistence.Converters;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace Listenarr.Infrastructure.Persistence.Configurations;

public sealed class AutomaticSearchDiagnosticConfiguration : IEntityTypeConfiguration<AutomaticSearchDiagnostic>
{
    public void Configure(EntityTypeBuilder<AutomaticSearchDiagnostic> builder)
    {
        builder.ToTable("AutomaticSearchDiagnostics");
        builder.HasKey(diagnostic => diagnostic.Id);
        builder.Property(diagnostic => diagnostic.SearchTrigger).IsRequired().HasMaxLength(20);
        builder.Property(diagnostic => diagnostic.ResultsReturned).IsRequired();
        builder.Property(diagnostic => diagnostic.SelectedRelease).HasMaxLength(500);
        builder.Property(diagnostic => diagnostic.DownloadClient).HasMaxLength(50);
        builder.Property(diagnostic => diagnostic.FinalOutcome).IsRequired().HasMaxLength(50);
        builder.Property(diagnostic => diagnostic.IndexersQueried)
            .HasConversion((ValueConverter<List<string>, string>)new JsonValueConverter<List<string>>())
            .HasColumnType("TEXT");
        builder.Property(diagnostic => diagnostic.RejectionReasons)
            .HasConversion((ValueConverter<List<string>, string>)new JsonValueConverter<List<string>>())
            .HasColumnType("TEXT");
        builder.Property(diagnostic => diagnostic.SubmissionAttempts)
            .HasConversion((ValueConverter<List<AutomaticSearchSubmissionAttempt>, string>)new JsonValueConverter<List<AutomaticSearchSubmissionAttempt>>())
            .HasColumnType("TEXT");
        builder.HasIndex(diagnostic => new { diagnostic.AudiobookId, diagnostic.SearchTimestamp });
        builder.HasIndex(diagnostic => diagnostic.SearchTimestamp);
    }
}
