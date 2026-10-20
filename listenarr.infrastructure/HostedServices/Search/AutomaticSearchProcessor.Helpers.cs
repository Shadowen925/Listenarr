namespace Listenarr.Infrastructure.HostedServices.Search
{
    public partial class AutomaticSearchProcessor
    {
        private static void ValidateSearchInterval(int intervalHours)
        {
            if (intervalHours is < 1 or > 168)
            {
                throw new InvalidOperationException(
                    $"Automatic search interval must be between 1 and 168 hours, but is {intervalHours}.");
            }
        }

        private static bool HaveSameRelease(SearchResult left, SearchResult right)
        {
            if (!string.IsNullOrWhiteSpace(left.Id)
                && !string.IsNullOrWhiteSpace(right.Id)
                && string.Equals(left.Id, right.Id, StringComparison.OrdinalIgnoreCase)
                && string.Equals(left.Source, right.Source, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            var leftLink = !string.IsNullOrWhiteSpace(left.MagnetLink)
                ? left.MagnetLink
                : !string.IsNullOrWhiteSpace(left.TorrentUrl) ? left.TorrentUrl : left.NzbUrl;
            var rightLink = !string.IsNullOrWhiteSpace(right.MagnetLink)
                ? right.MagnetLink
                : !string.IsNullOrWhiteSpace(right.TorrentUrl) ? right.TorrentUrl : right.NzbUrl;
            if (!string.IsNullOrWhiteSpace(leftLink) && !string.IsNullOrWhiteSpace(rightLink))
            {
                return string.Equals(leftLink, rightLink, StringComparison.OrdinalIgnoreCase);
            }

            return string.Equals(left.Title, right.Title, StringComparison.OrdinalIgnoreCase)
                && string.Equals(left.Source, right.Source, StringComparison.OrdinalIgnoreCase);
        }
    }
}
