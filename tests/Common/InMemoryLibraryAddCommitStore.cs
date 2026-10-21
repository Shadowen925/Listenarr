namespace Listenarr.Tests.Common;

internal sealed class InMemoryLibraryAddCommitStore(ListenArrDbContext db)
    : ILibraryAddCommitStore
{
    public async Task CommitAsync(
        Audiobook audiobook,
        History history,
        CancellationToken cancellationToken = default,
        QuickAddOperation? operation = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        db.Audiobooks.Add(audiobook);
        if (audiobook.Id <= 0)
        {
            throw new InvalidOperationException(
                "The in-memory provider did not allocate an audiobook identity before the atomic test commit.");
        }

        history.AudiobookId = audiobook.Id;
        db.History.Add(history);
        if (operation != null)
        {
            operation.AudiobookId = audiobook.Id;
            operation.AudiobookTitle = audiobook.Title;
            db.QuickAddOperations.Add(operation);
        }
        await db.SaveChangesAsync(cancellationToken);
    }
}
