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

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Listenarr.Infrastructure.HostedServices.Search
{
    public class AutomaticSearchService(
        ILogger<AutomaticSearchService> logger,
        IAutomaticSearchProcessor processor,
        IWorkerCycleRunner cycleRunner) : BackgroundService
    {
        private static readonly TimeSpan SearchCheckInterval = TimeSpan.FromMinutes(1);

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            logger.LogInformation(
                "AutomaticSearchService started. Checking for due monitored audiobooks every {Minutes} minutes",
                SearchCheckInterval.TotalMinutes);

            await cycleRunner.RunPeriodicAsync(
                nameof(AutomaticSearchService),
                initialDelay: SearchCheckInterval,
                intervalProvider: () => SearchCheckInterval,
                runCycle: processor.RunCycleAsync,
                stoppingToken);

            logger.LogInformation("AutomaticSearchService stopped");
        }
    }

    public partial class AutomaticSearchProcessor : IAutomaticSearchProcessor
    {
        private readonly ILogger<AutomaticSearchProcessor> _logger;
        private readonly IServiceScopeFactory _serviceScopeFactory;
        private readonly AutomaticSearchResultClassifier _resultClassifier;
        private readonly AutomaticSearchQualityEvaluator _qualityEvaluator;
        private readonly AutomaticSearchDownloadClientSelector _downloadClientSelector;

        public AutomaticSearchProcessor(
            ILogger<AutomaticSearchProcessor> logger,
            IServiceScopeFactory serviceScopeFactory)
        {
            _logger = logger;
            _serviceScopeFactory = serviceScopeFactory;
            _resultClassifier = new AutomaticSearchResultClassifier(_logger);
            _qualityEvaluator = new AutomaticSearchQualityEvaluator(_logger);
            _downloadClientSelector = new AutomaticSearchDownloadClientSelector(_serviceScopeFactory, _logger);
        }

        public Task RunCycleAsync(CancellationToken cancellationToken) => PerformAutomaticSearchesAsync(cancellationToken);

    }
}
