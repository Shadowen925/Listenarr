using Microsoft.AspNetCore.Mvc;

namespace Listenarr.Api.Features.Downloads
{
    /// <summary>Read-only event history plus inspection and clearing of persistently rejected releases.</summary>
    [ApiController]
    [Route("api/v{version:apiVersion}/download")]
    [Tags("Downloads")]
    public class DownloadAuditController(
        IDownloadEventRepository eventRepository,
        IReleaseRejectionRepository rejectionRepository) : ControllerBase
    {
        private const int MaxEvents = 500;

        [HttpGet("events")]
        public async Task<ActionResult> GetEvents([FromQuery] int limit = 100, [FromQuery] string? downloadId = null, CancellationToken ct = default)
        {
            var events = await eventRepository.GetRecentAsync(Math.Clamp(limit, 1, MaxEvents), downloadId, ct);
            return Ok(events);
        }

        [HttpGet("rejections")]
        public async Task<ActionResult> GetRejections([FromQuery] int? audiobookId = null, CancellationToken ct = default)
            => Ok(await rejectionRepository.ListAsync(audiobookId, 500, ct));

        [HttpPost("rejections")]
        public async Task<ActionResult> AddRejection(
            [FromBody] ReleaseRejectionRequest request,
            CancellationToken ct)
        {
            var key = request.ReleaseKey?.Trim();
            if (request.AudiobookId <= 0
                || string.IsNullOrWhiteSpace(key)
                || key.Length > ReleaseRejection.MaxKeyLength
                || string.IsNullOrWhiteSpace(request.Reason)
                || request.Reason.Length > 1000)
            {
                return BadRequest(new { message = "Audiobook, release identity, and rejection reason are required." });
            }

            var rejection = new ReleaseRejection
            {
                AudiobookId = request.AudiobookId,
                ReleaseKey = key,
                ReleaseTitle = string.IsNullOrWhiteSpace(request.ReleaseTitle)
                    ? null
                    : request.ReleaseTitle.Trim(),
                Reason = request.Reason.Trim(),
                Source = "User",
                CreatedAt = DateTime.UtcNow
            };
            var added = await rejectionRepository.AddAsync(rejection, ct);
            var existing = added
                ? rejection
                : (await rejectionRepository.ListAsync(request.AudiobookId, 2000, ct))
                    .FirstOrDefault(item => item.ReleaseKey == key);
            return added
                ? CreatedAtAction(nameof(GetRejections), new { audiobookId = request.AudiobookId }, rejection)
                : existing == null
                    ? Conflict(new { message = "The release rejection already exists but could not be retrieved." })
                    : Ok(existing);
        }

        [HttpDelete("rejections/{id:int}")]
        public async Task<ActionResult> RemoveRejection(int id, CancellationToken ct)
            => await rejectionRepository.RemoveAsync(id, ct) ? NoContent() : NotFound();

        [HttpDelete("rejections")]
        public async Task<ActionResult> ClearRejections([FromQuery] int audiobookId, CancellationToken ct)
            => Ok(new { removed = await rejectionRepository.ClearAsync(audiobookId, ct) });

        public sealed record ReleaseRejectionRequest(
            int AudiobookId,
            string? ReleaseKey,
            string? ReleaseTitle,
            string Reason);
    }
}
