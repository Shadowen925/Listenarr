using Listenarr.Application.Downloads.Cleanup;
using Listenarr.Application.Downloads.Stalls;
using Microsoft.AspNetCore.Mvc;

namespace Listenarr.Api.Features.Downloads
{
    [ApiController]
    [Route("api/v{version:apiVersion}/download")]
    [Tags("Downloads")]
    public class DownloadManagementController(
        DownloadQueueRemovalService removalService,
        DownloadReplacementService replacementService,
        IDownloadReferenceService downloadReferenceService,
        ILogger<DownloadManagementController> logger) : ControllerBase
    {
        /// <summary>Remove a tracked download using an explicit, safety-checked removal mode.</summary>
        [HttpPost("queue/{downloadId}/remove")]
        public async Task<ActionResult> Remove(string downloadId, [FromBody] RemoveDownloadRequest request, CancellationToken ct)
        {
            if (!TryParseMode(request.Mode, out var mode))
            {
                return BadRequest(new { message = "Mode must be one of: listenarrOnly, clientKeepFiles, clientDeleteData." });
            }

            var result = await removalService.RemoveAsync(downloadId, request.DownloadClientId, mode, ct);
            if (result.Success)
            {
                return Ok(result);
            }

            logger.LogWarning("Download removal refused or failed: {Code}", result.ErrorCode);
            var status = result.ErrorCode switch
            {
                DownloadRemovalErrorCodes.NotFound => StatusCodes.Status404NotFound,
                DownloadRemovalErrorCodes.ClientMismatch => StatusCodes.Status409Conflict,
                DownloadRemovalErrorCodes.UnsafeDelete => StatusCodes.Status409Conflict,
                DownloadRemovalErrorCodes.MissingRemote => StatusCodes.Status409Conflict,
                DownloadRemovalErrorCodes.NoClientIdentity => StatusCodes.Status409Conflict,
                DownloadRemovalErrorCodes.ClientUnavailable => StatusCodes.Status503ServiceUnavailable,
                _ => StatusCodes.Status502BadGateway,
            };
            return StatusCode(status, result);
        }

        /// <summary>Replace a tracked download with another release ("Download This Instead").</summary>
        [HttpPost("replace")]
        public async Task<ActionResult> Replace([FromBody] ReplaceDownloadRequest request, CancellationToken ct)
        {
            if (!TryParseMode(request.Mode, out var mode))
            {
                return BadRequest(new { message = "Mode must be one of: listenarrOnly, clientKeepFiles, clientDeleteData." });
            }

            try
            {
                var candidate = downloadReferenceService.Read(request.DownloadReference);
                var outcome = await replacementService.ReplaceAsync(request.DownloadId, request.DownloadClientId, candidate, mode, ct);
                if (outcome.Success)
                {
                    return Ok(outcome);
                }

                var status = outcome.Kind switch
                {
                    ReplacementOutcomeKind.AlreadyInProgress => StatusCodes.Status409Conflict,
                    ReplacementOutcomeKind.NotAllowed => outcome.ErrorCode == DownloadRemovalErrorCodes.NotFound
                        ? StatusCodes.Status404NotFound
                        : StatusCodes.Status409Conflict,
                    _ => StatusCodes.Status502BadGateway,
                };
                return StatusCode(status, outcome);
            }
            catch (DownloadReferenceException ex)
            {
                return StatusCode(
                    ex.IsExpired ? StatusCodes.Status410Gone : StatusCodes.Status400BadRequest,
                    new { message = "Unable to use download reference", error = ex.Message });
            }
        }

        private static bool TryParseMode(string? value, out DownloadRemovalMode mode)
        {
            mode = DownloadRemovalMode.ListenarrOnly;
            return !string.IsNullOrWhiteSpace(value)
                && Enum.TryParse(value, ignoreCase: true, out mode)
                && Enum.IsDefined(mode);
        }
    }

    public class RemoveDownloadRequest
    {
        public string Mode { get; set; } = string.Empty;
        public string? DownloadClientId { get; set; }
    }

    public class ReplaceDownloadRequest
    {
        public string DownloadId { get; set; } = string.Empty;
        public string DownloadReference { get; set; } = string.Empty;
        public string? DownloadClientId { get; set; }
        public string Mode { get; set; } = string.Empty;
    }
}
