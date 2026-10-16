using System.Text.Json;
using Listenarr.Application.Audiobookshelf.Models;

namespace Listenarr.Infrastructure.Audiobookshelf;

internal static class AudiobookshelfPayloadParser
{
    public static AudiobookshelfLibrary ParseLibrary(JsonElement element)
    {
        var folderPaths = GetArray(element, "folders")
            .Select(folder => GetString(folder, "fullPath") ?? GetString(folder, "path"))
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(path => path!)
            .Distinct(StringComparer.Ordinal)
            .ToList();
        return new AudiobookshelfLibrary(
            GetString(element, "id") ?? throw new JsonException("Audiobookshelf library has no id."),
            GetString(element, "name") ?? "Audiobookshelf Library",
            GetString(element, "mediaType"),
            folderPaths);
    }

    public static AudiobookshelfLibraryItem ParseLibraryItem(JsonElement element)
    {
        var media = GetProperty(element, "media");
        var metadata = GetProperty(media, "metadata");
        var series = ParseSeries(metadata);
        var seriesMemberships = series.Memberships;
        var primarySeries = seriesMemberships.FirstOrDefault();
        var audioFiles = GetArray(media, "audioFiles")
            .Select(file =>
            {
                var fileMetadata = GetProperty(file, "metadata");
                return GetString(fileMetadata, "path") ?? GetString(file, "path");
            })
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(path => path!)
            .ToList();

        return new AudiobookshelfLibraryItem(
            GetString(element, "id") ?? throw new JsonException("Audiobookshelf item has no id."),
            GetString(element, "path") ?? string.Empty,
            audioFiles,
            new AudiobookshelfMetadata(
                GetString(metadata, "title"),
                GetString(metadata, "subtitle"),
                GetStringList(metadata, "authors", "authorName"),
                GetStringList(metadata, "narrators", "narratorName"),
                primarySeries?.Name,
                primarySeries?.Position,
                GetString(metadata, "description"),
                GetString(media, "coverPath"),
                GetString(metadata, "asin"),
                GetStringList(metadata, "isbn"),
                GetString(metadata, "openlibraryId") ?? GetString(metadata, "openLibraryId"),
                GetString(metadata, "publisher"),
                GetString(metadata, "language"),
                GetString(metadata, "publishedYear"),
                GetStringList(metadata, "genres"),
                seriesMemberships,
                series.State,
                series.SourceCount,
                series.FailureReason));
    }

    private static ParsedSeries ParseSeries(JsonElement metadata)
    {
        if (!TryFindSeriesProperty(metadata, out var series))
        {
            return new ParsedSeries([], AudiobookshelfSeriesMetadataState.NotProvided, 0, null);
        }

        if (series.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return new ParsedSeries([], AudiobookshelfSeriesMetadataState.NoSeries, 0, null);
        }

        if (series.ValueKind == JsonValueKind.String)
        {
            var value = series.GetString()?.Trim();
            if (string.IsNullOrWhiteSpace(value))
            {
                return new ParsedSeries([], AudiobookshelfSeriesMetadataState.NoSeries, 0, null);
            }

            return new ParsedSeries(
                [ParseSeriesString(value)],
                AudiobookshelfSeriesMetadataState.Parsed,
                1,
                null);
        }

        if (series.ValueKind is not (JsonValueKind.Array or JsonValueKind.Object))
        {
            return new ParsedSeries(
                [],
                AudiobookshelfSeriesMetadataState.ParseError,
                1,
                "Audiobookshelf series metadata used an unsupported value type.");
        }

        IEnumerable<JsonElement> entries = series.ValueKind switch
        {
            JsonValueKind.Array => series.EnumerateArray(),
            JsonValueKind.Object => [series],
            _ => []
        };

        var memberships = new List<AudiobookshelfSeriesMembership>();
        var sourceCount = 0;
        var parseError = false;
        foreach (var entry in entries)
        {
            sourceCount++;
            if (entry.ValueKind == JsonValueKind.String)
            {
                var value = entry.GetString()?.Trim();
                if (!string.IsNullOrWhiteSpace(value))
                {
                    memberships.Add(ParseSeriesString(value));
                }
                continue;
            }

            if (entry.ValueKind != JsonValueKind.Object)
            {
                parseError = true;
                continue;
            }

            var name = GetString(entry, "name") ?? GetString(entry, "title");
            if (string.IsNullOrWhiteSpace(name))
            {
                if (TryFindSeriesProperty(entry, out var nested))
                {
                    var nestedSeries = ParseSeriesValue(nested);
                    memberships.AddRange(nestedSeries.Memberships);
                    parseError |= nestedSeries.State == AudiobookshelfSeriesMetadataState.ParseError;
                    sourceCount += Math.Max(0, nestedSeries.SourceCount - 1);
                    continue;
                }

                parseError = true;
                continue;
            }

            memberships.Add(new AudiobookshelfSeriesMembership(
                name.Trim(),
                GetString(entry, "sequence")
                    ?? GetString(entry, "position")
                    ?? GetString(entry, "bookNumber"),
                GetString(entry, "id") ?? GetString(entry, "sourceId"),
                GetString(entry, "asin") ?? GetString(entry, "seriesAsin")));
        }

        var unique = memberships
            .Where(membership => !string.IsNullOrWhiteSpace(membership.Name))
            .DistinctBy(
                membership => $"{membership.Name.Trim()}\u001f{membership.Position?.Trim()}",
                StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (parseError)
        {
            return new ParsedSeries(
                unique,
                AudiobookshelfSeriesMetadataState.ParseError,
                sourceCount,
                "One or more Audiobookshelf series entries were not recognized.");
        }

        return new ParsedSeries(
            unique,
            unique.Count == 0
                ? AudiobookshelfSeriesMetadataState.NoSeries
                : AudiobookshelfSeriesMetadataState.Parsed,
            sourceCount,
            null);
    }

    private static ParsedSeries ParseSeriesValue(JsonElement value)
    {
        using var document = JsonDocument.Parse(
            $$"""{"series":{{value.GetRawText()}}}""");
        return ParseSeries(document.RootElement);
    }

    private static AudiobookshelfSeriesMembership ParseSeriesString(string value)
    {
        var trimmed = value.Trim();
        var marker = trimmed.LastIndexOf(" #", StringComparison.Ordinal);
        if (marker > 0)
        {
            var sequence = trimmed[(marker + 2)..].Trim();
            if (sequence.Length > 0 && !sequence.Any(char.IsWhiteSpace) && !sequence.Contains('#'))
            {
                return new AudiobookshelfSeriesMembership(trimmed[..marker].Trim(), sequence);
            }
        }

        return new AudiobookshelfSeriesMembership(trimmed, null);
    }

    private static bool TryFindSeriesProperty(JsonElement element, out JsonElement series)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in element.EnumerateObject())
            {
                if (string.Equals(property.Name, "series", StringComparison.OrdinalIgnoreCase))
                {
                    series = property.Value;
                    return true;
                }
            }

            foreach (var property in element.EnumerateObject())
            {
                if (string.Equals(property.Name, "metadata", StringComparison.OrdinalIgnoreCase)
                    && TryFindSeriesProperty(property.Value, out series))
                {
                    return true;
                }
            }
        }

        series = default;
        return false;
    }

    private sealed record ParsedSeries(
        IReadOnlyList<AudiobookshelfSeriesMembership> Memberships,
        AudiobookshelfSeriesMetadataState State,
        int SourceCount,
        string? FailureReason);

    private static IReadOnlyList<string> GetStringList(
        JsonElement element,
        string propertyName,
        string? fallbackPropertyName = null)
    {
        var value = GetProperty(element, propertyName);
        if (value.ValueKind == JsonValueKind.Array)
        {
            return value.EnumerateArray()
                .Select(entry => entry.ValueKind == JsonValueKind.String
                    ? entry.GetString()
                    : GetString(entry, "name"))
                .Where(entry => !string.IsNullOrWhiteSpace(entry))
                .Select(entry => entry!)
                .ToList();
        }

        if (value.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(value.GetString()))
        {
            return [value.GetString()!];
        }

        var fallback = fallbackPropertyName == null
            ? null
            : GetString(element, fallbackPropertyName);
        return string.IsNullOrWhiteSpace(fallback) ? [] : [fallback];
    }

    public static IEnumerable<JsonElement> GetArray(JsonElement element, params string[] propertyNames)
    {
        foreach (var propertyName in propertyNames)
        {
            var property = GetProperty(element, propertyName);
            if (property.ValueKind == JsonValueKind.Array)
            {
                return property.EnumerateArray().ToList();
            }
        }

        return [];
    }

    private static JsonElement GetProperty(JsonElement element, string propertyName)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            return default;
        }

        foreach (var property in element.EnumerateObject())
        {
            if (string.Equals(property.Name, propertyName, StringComparison.OrdinalIgnoreCase))
            {
                return property.Value;
            }
        }

        return default;
    }

    private static string? GetString(JsonElement element, string propertyName)
    {
        var value = GetProperty(element, propertyName);
        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString(),
            JsonValueKind.Number => value.GetRawText(),
            _ => null
        };
    }
}
