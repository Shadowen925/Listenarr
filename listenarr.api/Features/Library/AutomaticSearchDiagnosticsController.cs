using Microsoft.AspNetCore.Mvc;

namespace Listenarr.Api.Features.Library;

[ApiController]
[Route("api/v{version:apiVersion}/library/{audiobookId:int}/search-history")]
[Tags("Library")]
public sealed class AutomaticSearchDiagnosticsController(
    IAutomaticSearchDiagnosticQueryService queryService) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> GetRecent(
        int audiobookId,
        [FromQuery] int limit = 25,
        CancellationToken cancellationToken = default)
    {
        var records = await queryService.GetRecentAsync(audiobookId, limit, cancellationToken);
        return records is null ? NotFound() : Ok(records);
    }
}
