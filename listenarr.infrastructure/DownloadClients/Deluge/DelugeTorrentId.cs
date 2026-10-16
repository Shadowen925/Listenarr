namespace Listenarr.Infrastructure.DownloadClients.Deluge
{
    /// <summary>
    /// Deluge identifies torrents by lowercase info-hash. Listenarr stores that hash as the durable
    /// download identifier, so every lookup normalizes representation before comparing.
    /// </summary>
    internal static class DelugeTorrentId
    {
        public static string Normalize(string? id) => (id ?? string.Empty).Trim().ToLowerInvariant();
    }
}
