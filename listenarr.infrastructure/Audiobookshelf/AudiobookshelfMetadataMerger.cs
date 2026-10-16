using Listenarr.Application.Audiobookshelf.Models;

namespace Listenarr.Infrastructure.Audiobookshelf;

internal static class AudiobookshelfMetadataMerger
{
    public static AudiobookshelfMetadata Merge(
        AudiobookshelfMetadata listMetadata,
        AudiobookshelfMetadata detailMetadata)
    {
        var detailHasSeriesMetadata =
            detailMetadata.SeriesMetadataState != AudiobookshelfSeriesMetadataState.NotProvided;
        var seriesMetadata = detailHasSeriesMetadata ? detailMetadata : listMetadata;

        return detailMetadata with
        {
            Title = Prefer(detailMetadata.Title, listMetadata.Title),
            Subtitle = Prefer(detailMetadata.Subtitle, listMetadata.Subtitle),
            Authors = Prefer(detailMetadata.Authors, listMetadata.Authors),
            Narrators = Prefer(detailMetadata.Narrators, listMetadata.Narrators),
            Series = seriesMetadata.Series,
            SeriesNumber = seriesMetadata.SeriesNumber,
            Description = Prefer(detailMetadata.Description, listMetadata.Description),
            CoverPath = Prefer(detailMetadata.CoverPath, listMetadata.CoverPath),
            Asin = Prefer(detailMetadata.Asin, listMetadata.Asin),
            Isbn = Prefer(detailMetadata.Isbn, listMetadata.Isbn),
            OpenLibraryId = Prefer(detailMetadata.OpenLibraryId, listMetadata.OpenLibraryId),
            Publisher = Prefer(detailMetadata.Publisher, listMetadata.Publisher),
            Language = Prefer(detailMetadata.Language, listMetadata.Language),
            PublishedYear = Prefer(detailMetadata.PublishedYear, listMetadata.PublishedYear),
            Genres = Prefer(detailMetadata.Genres, listMetadata.Genres),
            SeriesMemberships = seriesMetadata.SeriesMemberships,
            SeriesMetadataState = seriesMetadata.SeriesMetadataState,
            SourceSeriesCount = seriesMetadata.SourceSeriesCount,
            SeriesMetadataFailureReason = seriesMetadata.SeriesMetadataFailureReason
        };
    }

    private static string? Prefer(string? preferred, string? fallback) =>
        string.IsNullOrWhiteSpace(preferred) ? fallback : preferred;

    private static IReadOnlyList<string> Prefer(
        IReadOnlyList<string> preferred,
        IReadOnlyList<string> fallback) =>
        preferred.Count == 0 ? fallback : preferred;
}
