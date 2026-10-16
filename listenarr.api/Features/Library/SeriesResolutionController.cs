using Listenarr.Application.Audiobooks.SeriesResolution;
using Listenarr.Api.Features.Metadata;
using Microsoft.AspNetCore.Mvc;

namespace Listenarr.Api.Features.Library;

/// <summary>Endpoints for previewing, confirming, and refreshing imported-series resolution.</summary>
[ApiController]
[Route("api/v{version:apiVersion}/series/resolution")]
[Tags("Series")]
public sealed class SeriesResolutionController : ControllerBase
{
    private readonly IAudiobookSeriesResolutionService _resolutionService;
    private readonly ISeriesCatalogService _seriesCatalogService;

    public SeriesResolutionController(
        IAudiobookSeriesResolutionService resolutionService,
        ISeriesCatalogService seriesCatalogService)
    {
        _resolutionService = resolutionService;
        _seriesCatalogService = seriesCatalogService;
    }

    /// <summary>Returns Audible candidates for one imported series or all unresolved series.</summary>
    [HttpGet("preview")]
    [ProducesResponseType(typeof(IReadOnlyList<SeriesResolutionPreview>), StatusCodes.Status200OK)]
    public async Task<ActionResult<IReadOnlyList<SeriesResolutionPreview>>> Preview(
        [FromQuery] string? seriesId = null,
        [FromQuery] string region = "us",
        CancellationToken cancellationToken = default) =>
        Ok(await _resolutionService.PreviewAsync(seriesId, region, cancellationToken));

    /// <summary>Transfers imported-series memberships to a reviewed Audible series.</summary>
    [HttpPost("merge")]
    [ProducesResponseType(typeof(SeriesResolutionMergeResult), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<SeriesResolutionMergeResult>> Merge(
        [FromBody] SeriesResolutionMergeRequest? request,
        CancellationToken cancellationToken = default)
    {
        if (request == null
            || string.IsNullOrWhiteSpace(request.SeriesId)
            || string.IsNullOrWhiteSpace(request.CandidateAsin))
        {
            return BadRequest("An imported series ID and candidate Audible series ASIN are required.");
        }

        try
        {
            return Ok(await _resolutionService.MergeAsync(
                request.SeriesId,
                request.CandidateAsin,
                request.Region,
                cancellationToken));
        }
        catch (KeyNotFoundException)
        {
            return NotFound("The imported series no longer exists.");
        }
        catch (InvalidOperationException exception)
        {
            return Conflict(exception.Message);
        }
        catch (ArgumentException exception)
        {
            return BadRequest(exception.Message);
        }
    }

    /// <summary>Refreshes the Audible catalog selected by series ASIN.</summary>
    [HttpPost("catalog/refresh")]
    [ProducesResponseType(typeof(MetadataController.SeriesCatalogResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<MetadataController.SeriesCatalogResponse>> RefreshCatalog(
        [FromBody] SeriesCatalogRefreshRequest? request,
        CancellationToken cancellationToken = default)
    {
        if (request == null
            || string.IsNullOrWhiteSpace(request.Asin)
            || string.IsNullOrWhiteSpace(request.Name))
        {
            return BadRequest("An Audible series ASIN and name are required.");
        }

        var catalog = await _seriesCatalogService.GetCatalogByAsinAsync(
            request.Asin,
            request.Name,
            request.Region,
            forceRefresh: true,
            cancellationToken: cancellationToken);
        if (catalog == null || string.IsNullOrWhiteSpace(catalog.Series.Asin))
        {
            return NotFound("The selected Audible series catalog could not be refreshed.");
        }

        return Ok(new MetadataController.SeriesCatalogResponse
        {
            Series = new MetadataController.SeriesCatalogInfo
            {
                Asin = catalog.Series.Asin,
                Name = catalog.Series.Name ?? request.Name,
                Image = catalog.Series.Image,
                Description = catalog.Series.Description
            },
            Books = catalog.Books.Select(MetadataResponseMapper.MapSeriesCatalogBook).ToList(),
            TotalBooks = catalog.TotalBooks
        });
    }

    /// <summary>Identifies an imported series and the reviewed Audible series candidate.</summary>
    public sealed record SeriesResolutionMergeRequest
    {
        public required string SeriesId { get; init; }
        public required string CandidateAsin { get; init; }
        public string Region { get; init; } = "us";
    }

    /// <summary>Identifies an Audible series catalog to refresh.</summary>
    public sealed record SeriesCatalogRefreshRequest
    {
        public required string Asin { get; init; }
        public required string Name { get; init; }
        public string Region { get; init; } = "us";
    }
}
