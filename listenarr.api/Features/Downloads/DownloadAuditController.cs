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

        [HttpDelete("rejections/{id:int}")]
        public async Task<ActionResult> RemoveRejection(int id, CancellationToken ct)
            => await rejectionRepository.RemoveAsync(id, ct) ? NoContent() : NotFound();

        [HttpDelete("rejections")]
        public async Task<ActionResult> ClearRejections([FromQuery] int audiobookId, CancellationToken ct)
            => Ok(new { removed = await rejectionRepository.ClearAsync(audiobookId, ct) });
    }
}
