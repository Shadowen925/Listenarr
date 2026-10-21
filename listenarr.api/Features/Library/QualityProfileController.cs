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

using Microsoft.AspNetCore.Mvc;
using Listenarr.Application.Search.Matching;

namespace Listenarr.Api.Features.Library
{
    [ApiController]
    [Route("api/v{version:apiVersion}/qualityprofile")]
    [Tags("Quality Profiles")]
    public class QualityProfileController : ControllerBase
    {
        private readonly IQualityProfileService _qualityProfileService;
        private readonly ILogger<QualityProfileController> _logger;
        private readonly IAudiobookRepository? _audiobookRepository;

        public QualityProfileController(
            IQualityProfileService qualityProfileService,
            ILogger<QualityProfileController> logger,
            IAudiobookRepository? audiobookRepository = null)
        {
            _qualityProfileService = qualityProfileService;
            _logger = logger;
            _audiobookRepository = audiobookRepository;
        }

        /// <summary>
        /// Get all quality profiles.
        /// </summary>
        [HttpGet]
        public async Task<ActionResult<List<QualityProfile>>> GetAll()
        {
            try
            {
                var profiles = await _qualityProfileService.GetAllAsync();
                return Ok(profiles);
            }
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
            {
                _logger.LogError(ex, "Error retrieving quality profiles");
                return StatusCode(500, new { error = "Failed to retrieve quality profiles" });
            }
        }

        /// <summary>
        /// Get a quality profile by ID.
        /// </summary>
        /// <param name="id">Quality profile ID.</param>
        [HttpGet("{id}")]
        public async Task<ActionResult<QualityProfile>> GetById(int id)
        {
            try
            {
                var profile = await _qualityProfileService.GetByIdAsync(id);
                if (profile == null)
                {
                    return NotFound(new { error = $"Quality profile with ID {id} not found" });
                }
                return Ok(profile);
            }
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
            {
                _logger.LogError(ex, "Error retrieving quality profile {Id}", id);
                return StatusCode(500, new { error = "Failed to retrieve quality profile" });
            }
        }

        /// <summary>
        /// Get the default quality profile.
        /// </summary>
        [HttpGet("default")]
        public async Task<ActionResult<QualityProfile>> GetDefault()
        {
            try
            {
                var profile = await _qualityProfileService.GetDefaultAsync();
                if (profile == null)
                {
                    return NotFound(new { error = "No default quality profile set" });
                }
                return Ok(profile);
            }
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
            {
                _logger.LogError(ex, "Error retrieving default quality profile");
                return StatusCode(500, new { error = "Failed to retrieve default quality profile" });
            }
        }

        /// <summary>
        /// Create a new quality profile.
        /// </summary>
        /// <param name="profile">Quality profile to create.</param>
        [HttpPost]
        public async Task<ActionResult<QualityProfile>> Create([FromBody] QualityProfile profile)
        {
            try
            {
                if (!ModelState.IsValid)
                {
                    return BadRequest(ModelState);
                }

                var created = await _qualityProfileService.CreateAsync(profile);
                return CreatedAtAction(nameof(GetById), new { id = created.Id }, created);
            }
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
            {
                _logger.LogError(ex, "Error creating quality profile");
                return StatusCode(500, new { error = "Failed to create quality profile" });
            }
        }

        /// <summary>
        /// Update an existing quality profile.
        /// </summary>
        /// <param name="id">Quality profile ID.</param>
        /// <param name="profile">Updated quality profile data.</param>
        [HttpPut("{id}")]
        public async Task<ActionResult<QualityProfile>> Update(int id, [FromBody] QualityProfile profile)
        {
            try
            {
                if (!ModelState.IsValid)
                {
                    return BadRequest(ModelState);
                }

                if (id != profile.Id)
                {
                    return BadRequest(new { error = "ID mismatch" });
                }

                var updated = await _qualityProfileService.UpdateAsync(profile);
                return Ok(updated);
            }
            catch (InvalidOperationException ex)
            {
                _logger.LogWarning(ex, "Quality profile not found: {Id}", id);
                return NotFound(new { error = ex.Message });
            }
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
            {
                _logger.LogError(ex, "Error updating quality profile {Id}", id);
                return StatusCode(500, new { error = "Failed to update quality profile" });
            }
        }

        /// <summary>
        /// Delete a quality profile.
        /// </summary>
        /// <param name="id">Quality profile ID.</param>
        [HttpDelete("{id}")]
        public async Task<ActionResult> Delete(int id)
        {
            try
            {
                var deleted = await _qualityProfileService.DeleteAsync(id);
                if (!deleted)
                {
                    return NotFound(new { error = $"Quality profile with ID {id} not found" });
                }
                return Ok(new { message = "Quality profile deleted successfully", id });
            }
            catch (InvalidOperationException ex)
            {
                _logger.LogWarning(ex, "Cannot delete quality profile: {Id}", id);
                return BadRequest(new { error = ex.Message });
            }
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
            {
                _logger.LogError(ex, "Error deleting quality profile {Id}", id);
                return StatusCode(500, new { error = "Failed to delete quality profile" });
            }
        }

        /// <summary>
        /// Score a list of search results against a quality profile's preferred criteria.
        /// </summary>
        /// <param name="id">Quality profile ID.</param>
        /// <param name="searchResults">Search results to score.</param>
        /// <param name="audiobookId">Optional audiobook whose edition should be matched against each result.</param>
        [HttpPost("{id}/score")]
        public async Task<ActionResult<List<QualityScore>>> ScoreResults(
            int id,
            [FromBody] List<SearchResult> searchResults,
            [FromQuery] int? audiobookId = null)
        {
            try
            {
                var profile = await _qualityProfileService.GetByIdAsync(id);
                if (profile == null)
                {
                    return NotFound(new { error = $"Quality profile with ID {id} not found" });
                }

                var scores = await _qualityProfileService.ScoreSearchResults(searchResults, profile);
                if (audiobookId.HasValue)
                {
                    if (_audiobookRepository == null)
                    {
                        return StatusCode(503, new { error = "Audiobook edition matching is unavailable" });
                    }

                    var audiobook = await _audiobookRepository.GetByIdAsync(audiobookId.Value);
                    if (audiobook == null)
                    {
                        return NotFound(new { error = $"Audiobook with ID {audiobookId.Value} not found" });
                    }

                    foreach (var score in scores)
                    {
                        var evidence = AudiobookSearchResultMatcher.EvaluateIdentity(audiobook, score.SearchResult);
                        score.MatchesAudiobookEdition = evidence.IsAccepted;
                        score.EditionMatchConfidence = evidence.Confidence;
                        score.EditionMatchEvidence = [.. evidence.PositiveEvidence];
                        score.EditionMatchConflicts = [.. evidence.Conflicts];
                    }
                }

                return Ok(scores);
            }
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
            {
                _logger.LogError(ex, "Error scoring search results with profile {Id}", id);
                return StatusCode(500, new { error = "Failed to score search results" });
            }
        }
    }
}
