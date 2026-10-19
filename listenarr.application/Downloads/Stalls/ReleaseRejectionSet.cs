using System.Text.RegularExpressions;

namespace Listenarr.Application.Downloads.Stalls
{
    public sealed class ReleaseRejectionSet
    {
        private readonly HashSet<string> _keys = new(StringComparer.Ordinal);

        public static string[] GetDownloadReleaseKeys(Download download)
        {
            var releaseKeys = new[]
            {
                ReleaseRejection.IdKey(download.GetMetadataString("ReleaseId")),
                ReleaseRejection.IdKey(download.GetMetadataString("Replacement.ReleaseId")),
                ReleaseRejection.HashKey(download.GetMetadataString("TorrentHash"))
            };
            var keys = releaseKeys.Where(key => !string.IsNullOrWhiteSpace(key)).ToList();
            var externalId = download.GetExternalId();
            if (string.Equals(
                    download.GetMetadataString("DownloadType"),
                    "Torrent",
                    StringComparison.OrdinalIgnoreCase)
                || IsTorrentHash(externalId))
            {
                var externalHashKey = ReleaseRejection.HashKey(externalId);
                if (!string.IsNullOrWhiteSpace(externalHashKey))
                {
                    keys.Add(externalHashKey);
                }
            }

            if (keys.Count == 0)
            {
                var titleKey = ReleaseRejection.TitleKey(download.Title);
                if (!string.IsNullOrWhiteSpace(titleKey))
                {
                    keys.Add(titleKey);
                }
            }

            return [.. keys.Distinct(StringComparer.Ordinal)];
        }

        public void AddKey(string releaseKey)
        {
            if (!string.IsNullOrWhiteSpace(releaseKey))
            {
                _keys.Add(releaseKey);
            }
        }

        public void AddDownload(Download download)
        {
            foreach (var key in GetDownloadReleaseKeys(download))
            {
                AddKey(key);
            }
        }

        public bool Matches(SearchResult result)
        {
            var magnetHash = TryReadMagnetHash(result.MagnetLink);
            return _keys.Contains(ReleaseRejection.IdKey(result.Id))
                || _keys.Contains(ReleaseRejection.HashKey(result.Id))
                || _keys.Contains(ReleaseRejection.TitleKey(result.Title))
                || _keys.Contains(ReleaseRejection.HashKey(magnetHash));
        }

        private static bool IsTorrentHash(string? value) =>
            !string.IsNullOrWhiteSpace(value)
            && Regex.IsMatch(value, @"^(?:[a-fA-F0-9]{40}|[a-zA-Z2-7]{32})$");

        private static string? TryReadMagnetHash(string? magnet)
        {
            if (string.IsNullOrWhiteSpace(magnet))
            {
                return null;
            }

            var match = Regex.Match(magnet, @"btih:([a-fA-F0-9]{40}|[a-zA-Z2-7]{32})");
            return match.Success ? match.Groups[1].Value : null;
        }
    }
}
