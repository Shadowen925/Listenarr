using System.Security.Cryptography;
using System.Text;

namespace Listenarr.Domain.Downloads
{
    /// <summary>
    /// Durable per-audiobook record of a release that must not be selected again. It is independent of
    /// download history so history cleanup cannot silently re-enable a known-bad release.
    /// </summary>
    public class ReleaseRejection
    {
        public const int MaxKeyLength = 200;

        public int Id { get; set; }
        public int AudiobookId { get; set; }

        /// <summary>Normalized identifier: <c>hash:&lt;lowercase hash&gt;</c>, <c>id:&lt;release id&gt;</c> or <c>title:&lt;lowercase title&gt;</c>.</summary>
        public string ReleaseKey { get; set; } = string.Empty;
        public string? ReleaseTitle { get; set; }
        public string Reason { get; set; } = string.Empty;
        public string Source { get; set; } = string.Empty;
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

        public static string HashKey(string? hash) => Build("hash", hash);
        public static string IdKey(string? id) => Build("id", id);
        public static string TitleKey(string? title) => Build("title", title);

        private static string Build(string kind, string? value)
        {
            var normalized = (value ?? string.Empty).Trim().ToLowerInvariant();
            if (normalized.Length == 0)
            {
                return string.Empty;
            }

            var key = $"{kind}:{normalized}";
            if (key.Length <= MaxKeyLength)
            {
                return key;
            }

            var digest = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalized)))[..16].ToLowerInvariant();
            return $"{key[..(MaxKeyLength - digest.Length - 1)]}:{digest}";
        }
    }
}
