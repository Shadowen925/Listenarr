using Listenarr.Infrastructure.DependencyInjection;
using Listenarr.Tests.Common;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace Listenarr.Tests.Features.Infrastructure.Persistence;

/// <summary>
/// Reproduces startup recovery against a database upgraded from the schema that
/// preceded the Audiobookshelf migration, with a durable deletion intent whose
/// audiobook row no longer exists.
/// </summary>
[Trait("Name", "AudiobookDeletionIntentUpgradeRecoveryTests")]
[Trait("Category", "Infrastructure")]
public sealed class AudiobookDeletionIntentUpgradeRecoveryTests : BaseTests
{
    private const string PreAudiobookshelfMigrationId =
        "20260825021432_AddWeakStorageVerifiedCleanup";
    private const int MissingAudiobookId = 4242;

    [Fact]
    public async Task UpgradedDatabase_PlannedIntentForMissingAudiobook_FailsClosedAndKeepsIntent()
    {
        await using var fixture = await CreateUpgradedFixtureAsync("Planned");
        var filesystem = new Mock<IAudiobookFilesystemDeleteService>(MockBehavior.Strict);
        var reconciler = fixture.CreateReconciler(filesystem.Object);

        var first = await Assert.ThrowsAsync<InvalidOperationException>(
            () => reconciler.ReconcileAsync());
        Assert.StartsWith(
            "The audiobook row disappeared before its durable filesystem cleanup completed.",
            first.Message);
        Assert.Contains(fixture.IntentId.ToString(), first.Message);
        Assert.Equal("NeedsAttention", await fixture.ReadIntentStateAsync());

        // Still fail-closed on the next startup; the intent is never discarded silently.
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => reconciler.ReconcileAsync());
        Assert.Equal("NeedsAttention", await fixture.ReadIntentStateAsync());
        filesystem.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task UpgradedDatabase_OperatorAcknowledgedIntent_IsClosedWithoutFilesystemMutation()
    {
        await using var fixture = await CreateUpgradedFixtureAsync("NeedsAttention");
        var filesystem = new Mock<IAudiobookFilesystemDeleteService>(MockBehavior.Strict);
        var reconciler = fixture.CreateReconciler(
            filesystem.Object,
            fixture.IntentId.ToString());

        await reconciler.ReconcileAsync();

        Assert.Equal("Completed", await fixture.ReadIntentStateAsync());
        filesystem.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task UpgradedDatabase_AcknowledgingDifferentIntent_DoesNotCloseThisIntent()
    {
        await using var fixture = await CreateUpgradedFixtureAsync("Planned");
        var filesystem = new Mock<IAudiobookFilesystemDeleteService>(MockBehavior.Strict);
        var reconciler = fixture.CreateReconciler(
            filesystem.Object,
            Guid.NewGuid().ToString());

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => reconciler.ReconcileAsync());

        Assert.Equal("NeedsAttention", await fixture.ReadIntentStateAsync());
        filesystem.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task Store_CompleteOrphaned_DoesNothingWhileAudiobookRowExists()
    {
        var audiobook = await _audiobookRepository.AddAsync(new Listenarr.Tests.Builders.AudiobookBuilder()
            .WithTitle("Live Row Intent")
            .WithBasePath(FileService.GetTempDirectory("live-row-intent"))
            .Build());
        var store = _provider.GetRequiredService<IAudiobookDeletionIntentStore>();
        var intent = await store.GetOrCreateAsync(audiobook.Id, deleteFolder: true);

        Assert.False(await store.CompleteOrphanedWithoutFilesystemCleanupAsync(
            intent.Id,
            "should not apply"));

        var active = await store.GetActiveAsync();
        Assert.Contains(active, candidate =>
            candidate.Id == intent.Id
            && candidate.State == AudiobookDeletionIntentState.Planned);
    }

    [Fact]
    public async Task Store_DatabaseOnlyDeleteSupersedesPendingIntent()
    {
        var audiobook = await _audiobookRepository.AddAsync(new Listenarr.Tests.Builders.AudiobookBuilder()
            .WithTitle("Superseded Intent")
            .WithBasePath(FileService.GetTempDirectory("superseded-intent"))
            .Build());
        var store = _provider.GetRequiredService<IAudiobookDeletionIntentStore>();
        var intent = await store.GetOrCreateAsync(audiobook.Id, deleteFolder: true);
        Assert.True(await _audiobookRepository.DeleteByIdAsync(audiobook.Id));

        Assert.True(await store.CompleteOrphanedWithoutFilesystemCleanupAsync(
            intent.Id,
            "Superseded by an explicit database-only audiobook deletion."));

        Assert.DoesNotContain(
            await store.GetActiveAsync(),
            candidate => candidate.Id == intent.Id);
    }

    private static async Task<UpgradedFixture> CreateUpgradedFixtureAsync(string state)
    {
        var connection = new SqliteConnection("DataSource=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<ListenArrDbContext>()
            .UseSqlite(connection, sqlite =>
                sqlite.MigrationsAssembly(typeof(ListenArrDbContext).Assembly.GetName().Name))
            .Options;
        await using (var previous = new ListenArrDbContext(options))
        {
            await previous.GetService<IMigrator>().MigrateAsync(PreAudiobookshelfMigrationId);
        }

        var intentId = Guid.NewGuid();
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                INSERT INTO "AudiobookDeletionIntents"
                    ("Id", "AudiobookId", "DeleteFolder", "State", "Error", "CreatedAt", "UpdatedAt")
                VALUES
                    ($id, $audiobookId, 1, $state, NULL, CURRENT_TIMESTAMP, CURRENT_TIMESTAMP);
                """;
            command.Parameters.AddWithValue("$id", intentId.ToString().ToUpperInvariant());
            command.Parameters.AddWithValue("$audiobookId", MissingAudiobookId);
            command.Parameters.AddWithValue("$state", state);
            await command.ExecuteNonQueryAsync();
        }

        var services = new ServiceCollection();
        services.AddDbContextFactory<ListenArrDbContext>(builder =>
            builder.UseSqlite(connection, sqlite =>
                sqlite.MigrationsAssembly(typeof(ListenArrDbContext).Assembly.GetName().Name)));
        var provider = services.BuildServiceProvider();
        provider.ApplyListenarrDatabaseMigrations();
        return new UpgradedFixture(connection, provider, intentId);
    }

    private sealed class UpgradedFixture(
        SqliteConnection connection,
        ServiceProvider provider,
        Guid intentId) : IAsyncDisposable
    {
        public Guid IntentId { get; } = intentId;

        public AudiobookDeletionIntentReconciler CreateReconciler(
            IAudiobookFilesystemDeleteService filesystem,
            string? acknowledgedIntents = null)
        {
            var store = new AudiobookDeletionIntentStore(
                provider.GetRequiredService<IDbContextFactory<ListenArrDbContext>>(),
                TimeProvider.System);
            var repository = new Mock<IAudiobookRepository>();
            repository
                .Setup(candidate => candidate.GetByIdSnapshotAsync(
                    It.IsAny<int>(),
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync((Audiobook?)null);
            var commit = new Mock<IAudiobookDeletionCommitService>(MockBehavior.Strict);
            var configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    [AudiobookDeletionIntentReconciler
                        .AcknowledgedOrphanedIntentsConfigurationKey] = acknowledgedIntents
                })
                .Build();
            return new AudiobookDeletionIntentReconciler(
                store,
                repository.Object,
                commit.Object,
                filesystem,
                NullLogger<AudiobookDeletionIntentReconciler>.Instance,
                configuration);
        }

        public async Task<string> ReadIntentStateAsync()
        {
            await using var command = connection.CreateCommand();
            command.CommandText =
                "SELECT \"State\" FROM \"AudiobookDeletionIntents\" WHERE \"Id\" = $id;";
            command.Parameters.AddWithValue("$id", IntentId.ToString().ToUpperInvariant());
            return (string)(await command.ExecuteScalarAsync())!;
        }

        public async ValueTask DisposeAsync()
        {
            await provider.DisposeAsync();
            await connection.DisposeAsync();
        }
    }
}
