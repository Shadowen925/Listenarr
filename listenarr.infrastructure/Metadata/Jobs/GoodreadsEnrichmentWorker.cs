/*
 * Listenarr - Audiobook Management System
 * Copyright (C) 2024-2026 Listenarr Contributors
 *
 * This program is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Affero General Public License as published
 * by the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 */

using System.Collections.Concurrent;
using Listenarr.Application.Audiobooks.Contracts.Repositories;
using Listenarr.Application.Metadata.Goodreads;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Listenarr.Infrastructure.Metadata.Jobs
{
    // Slowly walks the whole library (monitored or not, files or not) and enriches entries from Goodreads.
    public class GoodreadsEnrichmentBackgroundService(
        ILogger<GoodreadsEnrichmentBackgroundService> logger,
        IGoodreadsEnrichmentProcessor processor,
        IWorkerCycleRunner cycleRunner) : BackgroundService
    {
        private static readonly TimeSpan Interval = TimeSpan.FromMinutes(2);
        private static readonly TimeSpan InitialDelay = TimeSpan.FromMinutes(1);

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            logger.LogInformation("GoodreadsEnrichmentBackgroundService starting");
            await cycleRunner.RunPeriodicAsync(
                nameof(GoodreadsEnrichmentBackgroundService),
                initialDelay: InitialDelay,
                intervalProvider: () => Interval,
                runCycle: processor.RunCycleAsync,
                stoppingToken);
        }
    }

    public class GoodreadsEnrichmentProcessor(
        IServiceScopeFactory scopeFactory,
        ILogger<GoodreadsEnrichmentProcessor> logger) : IGoodreadsEnrichmentProcessor
    {
        private const int BatchSize = 10;
        private static readonly TimeSpan RequestSpacing = TimeSpan.FromSeconds(2);
        private static readonly TimeSpan NoMatchRetry = TimeSpan.FromDays(7);

        // In-memory only: after a restart entries are re-checked, which is cheap for already-enriched books.
        private readonly ConcurrentDictionary<int, DateTime> _nextAttemptUtc = new();

        public async Task RunCycleAsync(CancellationToken cancellationToken)
        {
            using var scope = scopeFactory.CreateScope();
            var repository = scope.ServiceProvider.GetRequiredService<IAudiobookRepository>();
            var ids = (await repository.GetAllAsync()).Select(book => book.Id).ToList();
            var now = DateTime.UtcNow;
            var due = ids.Where(id => !_nextAttemptUtc.TryGetValue(id, out var next) || next <= now).ToList();
            if (due.Count == 0)
            {
                return;
            }

            var networkCalls = 0;
            foreach (var id in due)
            {
                if (cancellationToken.IsCancellationRequested || networkCalls >= BatchSize)
                {
                    break;
                }

                try
                {
                    using var itemScope = scopeFactory.CreateScope();
                    var service = itemScope.ServiceProvider.GetRequiredService<IGoodreadsEnrichmentService>();
                    var result = await service.EnrichAsync(id, cancellationToken);
                    _nextAttemptUtc[id] = result.Status == GoodreadsEnrichmentStatus.NoMatch
                        ? DateTime.UtcNow.Add(NoMatchRetry)
                        : DateTime.MaxValue;

                    if (result.Status != GoodreadsEnrichmentStatus.Skipped)
                    {
                        networkCalls++;
                        await Task.Delay(RequestSpacing, cancellationToken);
                    }
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "Goodreads enrichment failed for audiobook {Id}", id);
                    _nextAttemptUtc[id] = DateTime.UtcNow.AddHours(6);
                }
            }
        }
    }
}
