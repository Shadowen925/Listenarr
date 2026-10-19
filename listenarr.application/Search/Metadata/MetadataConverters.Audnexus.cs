using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;

namespace Listenarr.Application.Search.Metadata;

public partial class MetadataConverters
{
    public AudibleBookMetadata ConvertAudnexusToMetadata(AudnexusBookResponse audnexusData, string asin, string source = "Audible")
    {
        var metadata = new AudibleBookMetadata
        {
            Asin = audnexusData.Asin ?? asin,
            Source = source,
            Region = audnexusData.Region,
            Title = audnexusData.Title,
            Subtitle = audnexusData.Subtitle,
            Authors = audnexusData.Authors?.Where(a => !string.IsNullOrEmpty(a.Name)).Select(a => a.Name!).ToList(),
            Narrators = audnexusData.Narrators?.Where(n => !string.IsNullOrEmpty(n.Name)).Select(n => n.Name!).ToList(),
            Publisher = audnexusData.PublisherName,
            Description = !string.IsNullOrWhiteSpace(audnexusData.Summary) ? audnexusData.Summary : audnexusData.Description,
            Genres = audnexusData.Genres?.Where(g => !string.IsNullOrEmpty(g.Name)).Select(g => g.Name!).ToList(),
            Language = audnexusData.Language,
            Isbn = !string.IsNullOrWhiteSpace(audnexusData.Isbn) ? new List<string> { audnexusData.Isbn! } : new List<string>(),
            ImageUrl = audnexusData.Image,
            Abridged = audnexusData.FormatType?.Contains("abridged", StringComparison.OrdinalIgnoreCase) ?? false,
            Explicit = audnexusData.IsAdult ?? false
        };
        metadata.ProductionType = AudibleEditionClassifier.ClassifyProductionType(
            audnexusData.FormatType,
            audnexusData.Title,
            audnexusData.Subtitle);
        (metadata.PartNumber, metadata.TotalParts) = AudibleEditionClassifier.ParseParts(
            audnexusData.Title,
            audnexusData.Subtitle);

        var audnexusSeriesMemberships = new List<AudiobookSeriesMembership>();
        if (audnexusData.SeriesPrimary != null)
        {
            audnexusSeriesMemberships.Add(new AudiobookSeriesMembership
            {
                SeriesName = audnexusData.SeriesPrimary.Name,
                SeriesNumber = audnexusData.SeriesPrimary.Position,
                IsPrimary = true,
                SortOrder = 0
            });
        }

        if (audnexusData.SeriesSecondary != null)
        {
            audnexusSeriesMemberships.Add(new AudiobookSeriesMembership
            {
                SeriesName = audnexusData.SeriesSecondary.Name,
                SeriesNumber = audnexusData.SeriesSecondary.Position,
                IsPrimary = audnexusSeriesMemberships.Count == 0,
                SortOrder = audnexusSeriesMemberships.Count
            });
        }

        metadata.SeriesMemberships = audnexusSeriesMemberships.Count == 0
            ? null
            : AudiobookSeriesMembershipHelper.Normalize(audnexusSeriesMemberships);
        ApplyPrimarySeriesFields(metadata);

        if (metadata.SeriesMemberships != null && metadata.SeriesMemberships.Count > 0)
        {
            _logger.LogInformation("Extracted {Count} series memberships from Audnexus for ASIN {Asin}", metadata.SeriesMemberships.Count, asin);
        }
        else
        {
            _logger.LogDebug("No series information from Audnexus for ASIN {Asin}", asin);
        }

        if (audnexusData.RuntimeLengthMin.HasValue && audnexusData.RuntimeLengthMin > 0)
        {
            metadata.Runtime = audnexusData.RuntimeLengthMin.Value;
        }

        if (!string.IsNullOrEmpty(audnexusData.ReleaseDate))
        {
            var yearMatch = Regex.Match(audnexusData.ReleaseDate, @"\d{4}");
            if (yearMatch.Success)
            {
                metadata.PublishYear = yearMatch.Value;
            }
        }
        else if (audnexusData.Copyright.HasValue)
        {
            metadata.PublishYear = audnexusData.Copyright.Value.ToString();
        }

        _logger.LogInformation("Converted Audnexus data for {Asin}: Title={Title}, Runtime={Runtime}min, Year={Year}, Series={Series}, ImageUrl={ImageUrl}",
            asin, metadata.Title, metadata.Runtime, metadata.PublishYear, metadata.Series, metadata.ImageUrl);

        return metadata;
    }
}
