/*
 * Listenarr - Audiobook Management System
 * Copyright (C) 2024-2026 Listenarr Contributors
 *
 * This program is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Affero General Public License as published
 * by the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 *
 * This program is distributed in the hope that it will be useful,
 * but WITHOUT ANY WARRANTY; without even the implied warranty of
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
 * GNU Affero General Public License for more details.
 *
 * You should have received a copy of the GNU Affero General Public License
 * along with this program. If not, see <https://www.gnu.org/licenses/>.
 */

namespace Listenarr.Domain.Search
{
    /// <summary>
    /// DTO representing indexer results in a Prowlarr-like shape for the public API.
    /// </summary>
    public class IndexerResultDto
    {
        public string? Guid { get; set; }
        public int? Age { get; set; }
        public double? AgeHours { get; set; }
        public double? AgeMinutes { get; set; }
        public long Size { get; set; }
        public int Files { get; set; }
        public int Grabs { get; set; }
        public int? IndexerId { get; set; }
        public string? Indexer { get; set; }
        public string Title { get; set; } = string.Empty;
        public string? SortTitle { get; set; }
        public int ImdbId { get; set; }
        public int TmdbId { get; set; }
        public int TvdbId { get; set; }
        public int TvMazeId { get; set; }
        public string? PublishDate { get; set; }
        public string? DownloadUrl { get; set; }
        public string? InfoUrl { get; set; }
        public List<string> IndexerFlags { get; set; } = new();
        public List<object> Categories { get; set; } = new();
        public int? Seeders { get; set; }
        public int? Leechers { get; set; }
        public string? Protocol { get; set; }
        public string? FileName { get; set; }
        public string? DownloadReference { get; set; }

        [System.Text.Json.Serialization.JsonPropertyName("filetype")]
        public string? FileType { get; set; }

        [System.Text.Json.Serialization.JsonPropertyName("lang_code")]
        public string? Language { get; set; }
    }
}
