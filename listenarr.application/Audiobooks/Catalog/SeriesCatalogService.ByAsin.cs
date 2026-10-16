using Microsoft.Extensions.Logging;

namespace Listenarr.Application.Audiobooks.Catalog
{
    public partial class SeriesCatalogService
    {
        public async Task<SeriesCatalogFetchResult?> GetCatalogByAsinAsync(
            string asin,
            string name,
            string region = "us",
            int limit = 250,
            bool forceRefresh = false,
            CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(asin) || string.IsNullOrWhiteSpace(name))
            {
                return null;
            }

            var normalizedAsin = new string(asin.Where(char.IsLetterOrDigit).ToArray()).ToUpperInvariant();
            var normalizedName = name.Trim();
            var normalizedRegion = NormalizeRegion(region);
            var cachedEntry = await _audiobookRepository.GetCachedSeriesByAsinAsync(
                normalizedAsin,
                normalizedRegion);
            var nameCacheEntry = cachedEntry == null
                ? await _audiobookRepository.GetCachedSeriesByNameAsync(normalizedName, normalizedRegion)
                : null;
            var nameCacheHasDifferentAsin = !string.IsNullOrWhiteSpace(nameCacheEntry?.SeriesAsin)
                && !string.Equals(
                    NormalizeCatalogToken(nameCacheEntry.SeriesAsin),
                    normalizedAsin,
                    StringComparison.Ordinal);

            if (!forceRefresh && cachedEntry?.CatalogBooks is { Count: > 0 })
            {
                return new SeriesCatalogFetchResult
                {
                    Series = MapCachedSeries(cachedEntry, normalizedName, normalizedRegion),
                    Books = cachedEntry.CatalogBooks
                        .Select(MapCachedCatalogBook)
                        .Take(Math.Clamp(limit, 1, 500))
                        .ToList()
                };
            }

            var series = await _audibleService.GetSeriesByAsinAsync(normalizedAsin, normalizedRegion);
            if (series == null)
            {
                return cachedEntry?.CatalogBooks is { Count: > 0 }
                    ? new SeriesCatalogFetchResult
                    {
                        Series = MapCachedSeries(cachedEntry, normalizedName, normalizedRegion),
                        Books = cachedEntry.CatalogBooks.Select(MapCachedCatalogBook).ToList()
                    }
                    : null;
            }

            series.Asin = normalizedAsin;
            series.Name = string.IsNullOrWhiteSpace(series.Name) ? normalizedName : series.Name;
            series.Region = normalizedRegion;
            var books = await _audibleService.GetTypedBooksBySeriesAsinAsync(
                normalizedAsin,
                normalizedRegion);
            if (books == null || books.Count == 0)
            {
                if (cachedEntry?.CatalogBooks is { Count: > 0 })
                {
                    return new SeriesCatalogFetchResult
                    {
                        Series = MapCachedSeries(cachedEntry, normalizedName, normalizedRegion),
                        Books = cachedEntry.CatalogBooks.Select(MapCachedCatalogBook).ToList()
                    };
                }

                return null;
            }

            var limitedBooks = books
                .DistinctBy(BuildSeriesCatalogBookKey)
                .Take(Math.Clamp(limit, 1, 500))
                .ToList();
            foreach (var book in limitedBooks)
            {
                book.Series = PrioritizeCatalogSeries(book.Series, normalizedAsin, series.Name);
            }

            series.Image ??= limitedBooks.FirstOrDefault(book =>
                !string.IsNullOrWhiteSpace(book.ImageUrl))?.ImageUrl;
            if (!nameCacheHasDifferentAsin)
            {
                await PersistCatalogAsync(
                    cachedEntry ?? nameCacheEntry,
                    normalizedName,
                    normalizedRegion,
                    series,
                    limitedBooks,
                    cancellationToken);
            }
            else
            {
                _logger.LogWarning(
                    "Refreshed explicit Audible series {SeriesAsin} without replacing the name cache for a different series {SeriesName}",
                    normalizedAsin,
                    normalizedName);
            }

            return new SeriesCatalogFetchResult
            {
                Series = series,
                Books = limitedBooks
            };
        }
    }
}
