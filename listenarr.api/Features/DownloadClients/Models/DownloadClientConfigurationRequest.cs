namespace Listenarr.Api.Features.DownloadClients.Models
{
    public sealed class DownloadClientConfigurationRequest
    {
        public string? Id { get; set; }
        public string? Name { get; set; }
        public string? Type { get; set; }
        public string? Host { get; set; }
        public int? Port { get; set; }
        public string? Username { get; set; }
        public string? Password { get; set; }
        public string? DownloadPath { get; set; }
        public bool? UseSSL { get; set; }
        public bool? IsEnabled { get; set; }
        public string? RemoveCompletedDownloads { get; set; }
        public Dictionary<string, object>? Settings { get; set; }
    }
}
