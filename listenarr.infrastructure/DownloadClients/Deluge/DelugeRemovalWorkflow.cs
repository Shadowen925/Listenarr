/*
 * Listenarr - Audiobook Management System
 * Copyright (C) 2024-2026 Listenarr Contributors
 *
 * This program is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Affero General Public License as published
 * by the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 */

using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Listenarr.Infrastructure.DownloadClients.Deluge
{
    internal sealed class DelugeRemovalWorkflow(
        DelugeRpcClient rpcClient,
        ILogger logger)
    {
        private static readonly string[] TorrentLookupFields = ["name", "state"];

        public async Task<bool> RemoveAsync(
            DownloadClientConfiguration client,
            string id,
            bool deleteFiles = false,
            CancellationToken ct = default)
        {
            var torrentId = DelugeTorrentId.Normalize(id);
            logger.LogInformation("Removing Deluge torrent {TorrentId} (deleteFiles={DeleteFiles})", torrentId, deleteFiles);
            var result = await rpcClient.InvokeAsync(client, "core.remove_torrent", [torrentId, deleteFiles], ct);
            if (result.ValueKind == JsonValueKind.True)
            {
                return true;
            }

            // Deluge returns false when a torrent is not present. Only treat that as
            // completed cleanup after a successful, complete live-item query confirms absence.
            var snapshot = await rpcClient.InvokeAsync(
                client,
                "web.update_ui",
                [TorrentLookupFields, new Dictionary<string, object>()],
                ct);
            if (!snapshot.TryGetProperty("torrents", out var torrents) ||
                torrents.ValueKind != JsonValueKind.Object)
            {
                throw new InvalidDataException("Deluge returned no valid torrent snapshot after removal was not confirmed.");
            }

            var stillPresent = torrents.EnumerateObject().Any(torrent =>
                string.Equals(
                    DelugeTorrentId.Normalize(torrent.Name),
                    torrentId,
                    StringComparison.OrdinalIgnoreCase));
            if (!stillPresent)
            {
                logger.LogInformation(
                    "Deluge confirms torrent {TorrentId} is already absent; treating cleanup as complete",
                    torrentId);
                return true;
            }

            logger.LogWarning(
                "Deluge did not remove torrent {TorrentId}; it remains present in the client",
                torrentId);
            return false;
        }
    }
}
