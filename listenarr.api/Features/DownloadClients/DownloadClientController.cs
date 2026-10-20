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

using Listenarr.Api.Attributes;
using Listenarr.Api.Features.DownloadClients.Models;
using Microsoft.AspNetCore.Mvc;

namespace Listenarr.Api.Features.DownloadClients
{
    [ApiController]
    [Route("api/v{version:apiVersion}/download-clients")]
    [RequireAdminOrApiKey]
    [Tags("Download Clients")]
    public class DownloadClientController : ControllerBase
    {
        private readonly IConfigurationService _configurationService;
        private readonly IDownloadClientGateway _downloadClientGateway;
        private readonly ILogger<DownloadClientController> _logger;

        public DownloadClientController(
            IConfigurationService configurationService,
            IDownloadClientGateway downloadClientGateway,
            ILogger<DownloadClientController> logger)
        {
            _configurationService = configurationService;
            _downloadClientGateway = downloadClientGateway;
            _logger = logger;
        }

        /// <summary>
        /// Get all download client configurations.
        /// </summary>
        [HttpGet]
        [ProducesResponseType(typeof(List<DownloadClientConfiguration>), 200)]
        [ProducesResponseType(500)]
        public async Task<ActionResult<List<DownloadClientConfiguration>>> GetDownloadClientConfigurations()
        {
            try
            {
                var configs = await _configurationService.GetDownloadClientConfigurationsAsync();
                var response = configs
                    .Select(ApiResponseRedactor.RedactDownloadClientConfiguration)
                    .Select(ApiResponseRedactor.ToDownloadClientSummaryResponse)
                    .ToList();

                return Ok(response);
            }
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
            {
                _logger.LogError(ex, "Error retrieving download client configurations");
                return StatusCode(500, "Internal server error");
            }
        }

        /// <summary>
        /// Get a specific download client configuration by ID.
        /// </summary>
        /// <param name="id">Download client configuration ID</param>
        [HttpGet("{id}")]
        [ProducesResponseType(typeof(DownloadClientConfiguration), 200)]
        [ProducesResponseType(404)]
        [ProducesResponseType(500)]
        public async Task<ActionResult<DownloadClientConfiguration>> GetDownloadClientConfiguration(string id)
        {
            try
            {
                var config = await _configurationService.GetDownloadClientConfigurationAsync(id);
                if (config == null)
                {
                    return NotFound();
                }

                var responseConfig = ApiResponseRedactor.RedactDownloadClientConfiguration(config);
                var response = ApiResponseRedactor.ToDownloadClientDetailResponse(responseConfig);

                return Ok(response);
            }
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
            {
                _logger.LogError(ex, "Error retrieving download client configuration {Id}", id);
                return StatusCode(500, "Internal server error");
            }
        }

        /// <summary>
        /// Save or update a download client configuration. Preserves existing credentials when incoming values are blank.
        /// </summary>
        /// <param name="request">Download client configuration fields to save.</param>
        [HttpPost]
        public async Task<ActionResult<object>> SaveDownloadClientConfiguration([FromBody] DownloadClientConfigurationRequest request)
        {
            try
            {
                if (request == null)
                {
                    return BadRequest("Missing download client configuration");
                }

                var existing = !string.IsNullOrWhiteSpace(request.Id)
                    ? await _configurationService.GetDownloadClientConfigurationAsync(request.Id)
                    : null;
                var incomingSettings = request.Settings;
                var settings = incomingSettings == null
                    ? existing?.Settings ?? []
                    : new Dictionary<string, object>(incomingSettings);
                if (existing?.Settings?.TryGetValue("apiKey", out var existingApiKeyObject) == true)
                {
                    var incomingApiKey = settings.TryGetValue("apiKey", out var incomingApiKeyObject)
                        ? incomingApiKeyObject?.ToString()
                        : null;
                    if (string.IsNullOrWhiteSpace(incomingApiKey)
                        || string.Equals(incomingApiKey, ApiResponseRedactor.RedactedValue, StringComparison.Ordinal))
                    {
                        settings["apiKey"] = existingApiKeyObject;
                    }
                }

                var username = PreserveSavedCredential(request.Username, existing?.Username);
                var password = PreserveSavedCredential(request.Password, existing?.Password);
                var config = new DownloadClientConfiguration
                {
                    Id = request.Id ?? existing?.Id ?? Guid.NewGuid().ToString(),
                    Name = request.Name ?? existing?.Name ?? string.Empty,
                    Type = request.Type ?? existing?.Type ?? string.Empty,
                    Host = request.Host ?? existing?.Host ?? string.Empty,
                    Port = request.Port ?? existing?.Port ?? 0,
                    Username = username,
                    Password = password,
                    DownloadPath = request.DownloadPath ?? existing?.DownloadPath ?? string.Empty,
                    UseSSL = request.UseSSL ?? existing?.UseSSL ?? false,
                    IsEnabled = request.IsEnabled ?? existing?.IsEnabled ?? true,
                    RemoveCompletedDownloads = request.RemoveCompletedDownloads
                        ?? existing?.RemoveCompletedDownloads
                        ?? "none",
                    Settings = settings
                };

                var id = await _configurationService.SaveDownloadClientConfigurationAsync(config);
                return Ok(new { id });
            }
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
            {
                _logger.LogError(ex, "Error saving download client configuration");
                return StatusCode(500, "Internal server error");
            }
        }

        private static string PreserveSavedCredential(string? incoming, string? existing)
        {
            return string.IsNullOrWhiteSpace(incoming)
                || string.Equals(incoming, ApiResponseRedactor.RedactedValue, StringComparison.Ordinal)
                    ? existing ?? string.Empty
                    : incoming;
        }

        /// <summary>
        /// Delete a download client configuration by ID.
        /// </summary>
        /// <param name="id">Download client configuration ID.</param>
        [HttpDelete("{id}")]
        public async Task<ActionResult<bool>> DeleteDownloadClientConfiguration(string id)
        {
            try
            {
                var deleted = await _configurationService.DeleteDownloadClientConfigurationAsync(id);
                return Ok(deleted);
            }
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
            {
                _logger.LogError(ex, "Error deleting download client configuration {Id}", id);
                return StatusCode(500, "Internal server error");
            }
        }

        /// <summary>
        /// Test connectivity to a download client. Send the full configuration so credentials can be included in the test.
        /// </summary>
        /// <param name="config">Download client configuration to test.</param>
        /// <returns>Success flag, message, and the (optionally redacted) client configuration.</returns>
        [HttpPost("test")]
        public async Task<ActionResult<object>> TestDownloadClientConfiguration([FromBody] DownloadClientConfiguration config)
        {
            try
            {
                if (config == null)
                {
                    return BadRequest(new { success = false, message = "Missing download client configuration" });
                }

                if (!string.IsNullOrWhiteSpace(config.Id))
                {
                    var existing = await _configurationService.GetDownloadClientConfigurationAsync(config.Id);
                    if (existing != null)
                    {
                        if (string.IsNullOrWhiteSpace(config.Username) && !string.IsNullOrWhiteSpace(existing.Username))
                        {
                            config.Username = existing.Username;
                        }

                        if (string.IsNullOrWhiteSpace(config.Password) && !string.IsNullOrWhiteSpace(existing.Password))
                        {
                            config.Password = existing.Password;
                        }

                        try
                        {
                            if (existing.Settings != null)
                            {
                                var settings = config.Settings;

                                foreach (var kvp in existing.Settings.Where(kvp => !settings.ContainsKey(kvp.Key)))
                                {
                                    settings[kvp.Key] = kvp.Value;
                                }
                                if (settings.TryGetValue("apiKey", out var incomingApiKey)
                                    && string.Equals(incomingApiKey?.ToString(), ApiResponseRedactor.RedactedValue, StringComparison.Ordinal)
                                    && existing.Settings.TryGetValue("apiKey", out var storedApiKey))
                                {
                                    settings["apiKey"] = storedApiKey;
                                }
                                else if (settings.TryGetValue("apiKey", out incomingApiKey)
                                    && string.Equals(
                                        incomingApiKey?.ToString(),
                                        ApiResponseRedactor.RedactedValue,
                                        StringComparison.Ordinal))
                                {
                                    settings.Remove("apiKey");
                                }
                                config.Settings = settings;
                            }
                        }
                        catch (Exception caughtEx) when (caughtEx is not OperationCanceledException && caughtEx is not OutOfMemoryException && caughtEx is not StackOverflowException)
                        {
                            _logger.LogDebug(caughtEx, "Unable to merge existing download client settings while testing configuration {Id}", config.Id);
                        }
                    }
                }

                var (success, message) = await _downloadClientGateway.TestConnectionAsync(config);
                var clientResponse = ApiResponseRedactor.RedactDownloadClientConfiguration(config);
                return Ok(new { success, message, client = clientResponse });
            }
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
            {
                _logger.LogError(ex, "Error testing download client configuration");
                return StatusCode(500, new { success = false, message = ex.Message });
            }
        }
    }
}
