using Listenarr.Infrastructure.Persistence.Repositories;
using Listenarr.Tests.Common;
using Microsoft.EntityFrameworkCore;
using System.Text.Json;

namespace Listenarr.Tests.Features.Infrastructure.Persistence
{
    [Trait("Name", "EfDownloadClientConfigurationRepositoryTests")]
    [Trait("Category", "DownloadClientConfigurationPersistence")]
    public sealed class EfDownloadClientConfigurationRepositoryTests : BaseTests
    {
        [Fact]
        public async Task SaveAsync_PersistsDownloadPathAndNullableOverridesAcrossFreshContext()
        {
            var configuration = new DownloadClientConfiguration
            {
                Id = "deluge-round-trip",
                Name = "Deluge",
                Type = "deluge",
                DownloadPath = "/data/deluge/completed/",
                Password = "stored-password",
                Settings = new Dictionary<string, object>
                {
                    ["stalledEnabled"] = false,
                    ["stalledIgnoreManuallyPaused"] = true,
                    ["stalledAutoBlocklist"] = false,
                    ["stalledCleanupBehavior"] = "remove_and_delete",
                    ["stalledMaxReplacementAttempts"] = 0
                }
            };

            await _downloadClientConfigurationRepository.SaveAsync(configuration);

            await using var freshContext = await _provider
                .GetRequiredService<IDbContextFactory<ListenArrDbContext>>()
                .CreateDbContextAsync();
            var reloaded = await new EfDownloadClientConfigurationRepository(freshContext)
                .GetByIdAsync(configuration.Id);

            Assert.NotNull(reloaded);
            Assert.Equal(configuration.DownloadPath, reloaded!.DownloadPath);
            Assert.Equal(configuration.Password, reloaded.Password);
            Assert.Equal(JsonValueKind.False, Assert.IsType<JsonElement>(reloaded.Settings["stalledEnabled"]).ValueKind);
            Assert.Equal(JsonValueKind.True, Assert.IsType<JsonElement>(reloaded.Settings["stalledIgnoreManuallyPaused"]).ValueKind);
            Assert.Equal(JsonValueKind.False, Assert.IsType<JsonElement>(reloaded.Settings["stalledAutoBlocklist"]).ValueKind);
            Assert.Equal("remove_and_delete", reloaded.Settings["stalledCleanupBehavior"]?.ToString());
            Assert.Equal("0", reloaded.Settings["stalledMaxReplacementAttempts"]?.ToString());
        }
    }
}
