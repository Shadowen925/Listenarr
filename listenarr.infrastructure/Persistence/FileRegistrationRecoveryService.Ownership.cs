using System.Linq.Expressions;
using Listenarr.Domain.Audiobooks.Enumerations;
using Listenarr.Domain.Common;

namespace Listenarr.Infrastructure.Persistence;

public sealed partial class FileRegistrationRecoveryService
{
    private static bool RegisteredGenerationMatches(
        AudiobookFile file,
        string? targetPhysicalObjectIdentity)
    {
        if (string.IsNullOrWhiteSpace(file.PhysicalObjectIdentity)
            || string.IsNullOrWhiteSpace(targetPhysicalObjectIdentity))
        {
            return false;
        }

        return string.Equals(
                file.PhysicalObjectIdentity,
                targetPhysicalObjectIdentity,
                StringComparison.Ordinal)
            || PinnedDirectoryCreation.ArePersistedObjectIdentitiesDurablyEquivalent(
                file.PhysicalObjectIdentity,
                targetPhysicalObjectIdentity);
    }

    private static bool RegisteredPathMatches(AudiobookFile file, string destinationPath)
    {
        if (file.PathIdentityState != PathIdentityState.Valid
            || !file.PathSyntax.HasValue
            || string.IsNullOrWhiteSpace(file.CanonicalPath))
        {
            return false;
        }

        try
        {
            return FileSystemPathIdentity.AreEquivalent(
                file.CanonicalPath,
                destinationPath,
                new FileSystemPathSemantics(
                    file.PathSyntax.Value,
                    file.PathCaseSensitivity));
        }
        catch (Exception exception) when (exception is
            ArgumentException or InvalidOperationException
                or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    private static bool IsRegistrationPublicationOwner(FileMutationJournal journal) =>
        journal.Action is FileAction.Move or FileAction.Copy or FileAction.HardlinkCopy
        && journal.AudiobookId != null
        && journal.AudiobookFileId == null;

    private static Expression<Func<FileMutationJournal, bool>>
        RegistrationPublicationOwnerPredicate => journal =>
            (journal.Action == FileAction.Move
                || journal.Action == FileAction.Copy
                || journal.Action == FileAction.HardlinkCopy)
            && journal.AudiobookId != null
            && journal.AudiobookFileId == null;
}
