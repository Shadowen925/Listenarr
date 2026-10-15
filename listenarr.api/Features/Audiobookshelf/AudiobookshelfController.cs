using Listenarr.Api.Attributes;
using Listenarr.Application.Audiobookshelf.Contracts;
using Listenarr.Application.Audiobookshelf.Models;
using Microsoft.AspNetCore.Mvc;

namespace Listenarr.Api.Features.Audiobookshelf;

[ApiController]
[Route("api/v{version:apiVersion}/audiobookshelf")]
[Tags("Audiobookshelf")]
[RequireAdminOrApiKey]
public sealed class AudiobookshelfController(
    IAudiobookshelfIntegrationService integrationService,
    ILogger<AudiobookshelfController> logger) : ControllerBase
{
    private static readonly HashSet<string> AllowedCoverContentTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "image/jpeg",
        "image/png",
        "image/webp",
        "image/gif",
        "image/avif"
    };

    [HttpGet("settings")]
    public async Task<ActionResult<AudiobookshelfImportConnectionSettings>> GetSettings()
    {
        var settings = await integrationService.GetConnectionSettingsAsync();
        settings.ApiKey = null;
        return Ok(settings);
    }

    [HttpPut("settings")]
    public async Task<ActionResult<AudiobookshelfImportConnectionSettings>> SaveSettings(
        [FromBody] AudiobookshelfImportConnectionSettings settings)
    {
        try
        {
            var saved = await integrationService.SaveConnectionSettingsAsync(settings);
            saved.ApiKey = null;
            return Ok(saved);
        }
        catch (ArgumentException exception)
        {
            return BadRequest(new { message = exception.Message });
        }
    }

    [HttpPost("test")]
    public async Task<IActionResult> TestConnection(CancellationToken cancellationToken)
    {
        try
        {
            await integrationService.TestConnectionAsync(cancellationToken);
            return Ok(new { success = true, message = "Audiobookshelf connection succeeded." });
        }
        catch (HttpRequestException exception)
        {
            logger.LogWarning(exception, "Audiobookshelf connection test failed");
            return StatusCode(StatusCodes.Status502BadGateway, new
            {
                success = false,
                message = "Audiobookshelf could not complete the connection request."
            });
        }
        catch (InvalidOperationException exception)
        {
            return BadRequest(new { success = false, message = exception.Message });
        }
    }

    [HttpGet("libraries")]
    public async Task<ActionResult<IReadOnlyList<AudiobookshelfLibrary>>> GetLibraries(
        CancellationToken cancellationToken)
    {
        try
        {
            return Ok(await integrationService.GetLibrariesAsync(cancellationToken));
        }
        catch (HttpRequestException exception)
        {
            logger.LogWarning(exception, "Audiobookshelf library discovery failed");
            return StatusCode(StatusCodes.Status502BadGateway, new
            {
                message = "Audiobookshelf library discovery failed."
            });
        }
        catch (InvalidOperationException exception)
        {
            return BadRequest(new { message = exception.Message });
        }
    }

    [HttpPost("import/preview")]
    public async Task<ActionResult<AudiobookshelfImportPreview>> PreviewImport(
        [FromBody] AudiobookshelfPreviewRequest request,
        CancellationToken cancellationToken)
    {
        try
        {
            return Ok(await integrationService.PreviewImportAsync(
                request.LibraryId,
                cancellationToken));
        }
        catch (ArgumentException exception)
        {
            return BadRequest(new { message = exception.Message });
        }
        catch (KeyNotFoundException exception)
        {
            return NotFound(new { message = exception.Message });
        }
        catch (HttpRequestException exception)
        {
            logger.LogWarning(exception, "Audiobookshelf import preview failed");
            return StatusCode(StatusCodes.Status502BadGateway, new
            {
                message = "Audiobookshelf import preview failed."
            });
        }
        catch (InvalidOperationException exception)
        {
            return BadRequest(new { message = exception.Message });
        }
    }

    [HttpPost("import")]
    public async Task<ActionResult<AudiobookshelfImportResult>> Import(
        [FromBody] AudiobookshelfImportRequest request,
        CancellationToken cancellationToken)
    {
        try
        {
            return Ok(await integrationService.ImportAsync(request, cancellationToken));
        }
        catch (ArgumentException exception)
        {
            return BadRequest(new { message = exception.Message });
        }
        catch (KeyNotFoundException exception)
        {
            return NotFound(new { message = exception.Message });
        }
        catch (HttpRequestException exception)
        {
            logger.LogWarning(exception, "Audiobookshelf import failed");
            return StatusCode(StatusCodes.Status502BadGateway, new
            {
                message = "Audiobookshelf import failed."
            });
        }
    }

    [HttpPost("libraries/{libraryId}/scan")]
    public async Task<IActionResult> TriggerLibraryScan(
        string libraryId,
        CancellationToken cancellationToken)
    {
        try
        {
            await integrationService.TriggerLibraryScanAsync(
                libraryId,
                cancellationToken);
            return Ok(new { success = true });
        }
        catch (ArgumentException exception)
        {
            return BadRequest(new { message = exception.Message });
        }
        catch (HttpRequestException exception)
        {
            logger.LogWarning(exception, "Audiobookshelf scan request failed for library {LibraryId}", libraryId);
            return StatusCode(StatusCodes.Status502BadGateway, new
            {
                message = "Audiobookshelf scan request failed."
            });
        }
        catch (InvalidOperationException exception)
        {
            return BadRequest(new { message = exception.Message });
        }
    }

    [HttpGet("items/{itemId}/cover")]
    public async Task<IActionResult> GetItemCover(
        string itemId,
        CancellationToken cancellationToken)
    {
        try
        {
            var cover = await integrationService.GetItemCoverAsync(itemId, cancellationToken);
            if (!AllowedCoverContentTypes.Contains(cover.ContentType))
            {
                return StatusCode(StatusCodes.Status502BadGateway, new
                {
                    message = "Audiobookshelf returned an unsupported cover image type."
                });
            }

            Response.Headers["X-Content-Type-Options"] = "nosniff";
            Response.Headers.CacheControl = "private, max-age=3600";
            return File(cover.Content, cover.ContentType);
        }
        catch (ArgumentException exception)
        {
            return BadRequest(new { message = exception.Message });
        }
        catch (HttpRequestException exception)
        {
            logger.LogWarning(exception, "Audiobookshelf cover request failed for item {ItemId}", itemId);
            return StatusCode(StatusCodes.Status502BadGateway, new
            {
                message = "Audiobookshelf cover request failed."
            });
        }
    }

    public sealed record AudiobookshelfPreviewRequest(string LibraryId);
}
