using System.Net;
using System.Text.Json;
using Listenarr.Tests.Common;

namespace Listenarr.Tests.Mocks.Api
{
    public class DelugeApiMock : BaseApiMock
    {
        public bool Authenticated { get; set; } = false;
        public bool AuthenticationSucceeded { get; set; } = true;
        public bool DaemonConnected { get; set; } = true;
        public string? UpdateUiResponseOverride { get; set; }
        public string? RemoveTorrentResponseOverride { get; set; }
        public bool FailRequests { get; set; } = false;
        public bool FailUpdateUi { get; set; } = false;
        public bool FailLabelAssignment { get; set; } = false;
        public int RemoveTorrentCallCount { get; private set; }
        public string? LastRemovedTorrentId { get; private set; }
        public bool LastRemoveDeleteFiles { get; private set; }

        public DelugeApiMock()
        {
            AddRoute("json", HandleRpc, HttpMethod.Post);
        }

        private async Task<HttpResponseMessage> HandleRpc(HttpRequestMessage request, CancellationToken ct)
        {
            if (FailRequests)
            {
                return new HttpResponseMessage(HttpStatusCode.InternalServerError);
            }

            var body = await request.Content!.ReadAsStringAsync(ct);
            using var document = JsonDocument.Parse(body);
            if (document.RootElement.TryGetProperty("method", out var methodProp))
            {
                var method = methodProp.GetString();
                if (method == "core.remove_torrent")
                {
                    RemoveTorrentCallCount++;
                    if (document.RootElement.TryGetProperty("params", out var parameters) &&
                        parameters.ValueKind == JsonValueKind.Array &&
                        parameters.GetArrayLength() >= 2)
                    {
                        LastRemovedTorrentId = parameters[0].GetString();
                        LastRemoveDeleteFiles = parameters[1].GetBoolean();
                    }
                }

                if (method == "web.update_ui" && FailUpdateUi)
                {
                    return new HttpResponseMessage(HttpStatusCode.InternalServerError);
                }

                string responseBody = method switch
                {
                    "auth.login" => $$"""
                    {
                      "id": 1,
                      "result": {{AuthenticationSucceeded.ToString().ToLowerInvariant()}},
                      "error": null
                    }
                    """,
                    "web.connected" => $$"""
                    {
                      "id": 1,
                      "result": {{DaemonConnected.ToString().ToLowerInvariant()}},
                      "error": null
                    }
                    """,
                    "web.get_hosts" => """
                    {
                      "id": 1,
                      "result": [["host_id_1", "localhost", 58846, "connected"]],
                      "error": null
                    }
                    """,
                    "web.connect" => """
                    {
                      "id": 1,
                      "result": true,
                      "error": null
                    }
                    """,
                    "core.add_torrent_file" => """
                    {
                      "id": 1,
                      "result": "ABCDEF1234567890",
                      "error": null
                    }
                    """,
                    "core.add_torrent_magnet" => """
                    {
                      "id": 1,
                      "result": "ABCDEF1234567890",
                      "error": null
                    }
                    """,
                    "web.download_torrent_from_url" => """
                    {
                      "id": 1,
                      "result": "/tmp/torrent.torrent",
                      "error": null
                    }
                    """,
                    "web.add_torrents" => """
                    {
                      "id": 1,
                      "result": ["ABCDEF1234567890"],
                      "error": null
                    }
                    """,
                    "label.set_torrent" when FailLabelAssignment => """
                    {
                      "id": 1,
                      "result": null,
                      "error": { "message": "Unknown method label.set_torrent", "code": 2 }
                    }
                    """,
                    "label.set_torrent" => """
                    {
                      "id": 1,
                      "result": true,
                      "error": null
                    }
                    """,
                    "core.remove_torrent" when RemoveTorrentResponseOverride != null => RemoveTorrentResponseOverride,
                    "core.remove_torrent" => """
                    {
                      "id": 1,
                      "result": true,
                      "error": null
                    }
                    """,
                    "web.update_ui" => UpdateUiResponseOverride ?? """
                    {
                      "id": 1,
                      "result": {
                        "torrents": {}
                      },
                      "error": null
                    }
                    """,
                    _ => """
                    {
                      "id": 1,
                      "result": null,
                      "error": null
                    }
                    """
                };

                return MockUtils.GetCannedResponse(responseBody);
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }
    }
}
