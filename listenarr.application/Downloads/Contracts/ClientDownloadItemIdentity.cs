namespace Listenarr.Application.Downloads.Contracts
{
    /// <summary>Identifies a download-client item within its owning client.</summary>
    public readonly struct ClientDownloadItemIdentity : IEquatable<ClientDownloadItemIdentity>
    {
        public ClientDownloadItemIdentity(string? clientId, string? itemId)
        {
            ClientId = clientId ?? string.Empty;
            ItemId = itemId ?? string.Empty;
        }

        public string ClientId { get; }

        public string ItemId { get; }

        public bool Equals(ClientDownloadItemIdentity other) =>
            string.Equals(ClientId, other.ClientId, StringComparison.OrdinalIgnoreCase)
            && string.Equals(ItemId, other.ItemId, StringComparison.OrdinalIgnoreCase);

        public override bool Equals(object? obj) =>
            obj is ClientDownloadItemIdentity other && Equals(other);

        public override int GetHashCode() =>
            HashCode.Combine(
                StringComparer.OrdinalIgnoreCase.GetHashCode(ClientId),
                StringComparer.OrdinalIgnoreCase.GetHashCode(ItemId));
    }
}
