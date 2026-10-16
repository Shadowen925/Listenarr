namespace Listenarr.Application.Audiobooks.SeriesResolution;

public sealed partial class AudiobookSeriesResolutionService
{
    private sealed class SourceGroup(string id, string name)
    {
        private readonly Dictionary<int, SourceBook> _books = [];

        public string Id { get; } = id;
        public string Name { get; } = name;
        public IReadOnlyCollection<SourceBook> Books => _books.Values;
        public IReadOnlyList<int> MembershipIds =>
            _books.Values.SelectMany(book => book.Memberships).Select(membership => membership.Id).Distinct().ToList();

        public void Add(Audiobook audiobook, AudiobookSeriesMembership membership)
        {
            if (!_books.TryGetValue(audiobook.Id, out var book))
            {
                book = new SourceBook(audiobook, ResolveAsin(audiobook));
                _books.Add(audiobook.Id, book);
            }

            book.Memberships.Add(membership);
        }

        private static string? ResolveAsin(Audiobook audiobook)
        {
            if (!string.IsNullOrWhiteSpace(audiobook.Asin))
            {
                return audiobook.Asin;
            }

            var identifier = audiobook.ExternalIdentifiers?
                .FirstOrDefault(item => item.Type == AudiobookExternalIdentifierType.Asin);
            return !string.IsNullOrWhiteSpace(identifier?.ValueNormalized)
                ? identifier.ValueNormalized
                : identifier?.ValueRaw;
        }
    }

    private sealed class SourceBook(Audiobook audiobook, string? asin)
    {
        public Audiobook Audiobook { get; } = audiobook;
        public string? Asin { get; } = asin;
        public List<AudiobookSeriesMembership> Memberships { get; } = [];
    }

    private sealed class CandidateEvidence(string asin, string name)
    {
        public string Asin { get; } = asin;
        public string Name { get; } = name;
        public int NameSimilarity { get; private set; }
        public HashSet<string> Authors { get; } = new(StringComparer.Ordinal);
        public HashSet<string> GroupAuthors { get; } = new(StringComparer.Ordinal);
        public HashSet<int> OverlappingBooks { get; } = [];
        public int PositionMatches { get; private set; }
        public List<string> PositionConflicts { get; } = [];

        public void Add(
            SourceBook sourceBook,
            string? audiblePosition,
            HashSet<string> audibleAuthors,
            HashSet<string> groupAuthors,
            int nameSimilarity)
        {
            NameSimilarity = Math.Max(NameSimilarity, nameSimilarity);
            Authors.UnionWith(audibleAuthors);
            GroupAuthors.UnionWith(groupAuthors);
            OverlappingBooks.Add(sourceBook.Audiobook.Id);

            var sourcePositions = sourceBook.Memberships
                .Select(membership => membership.SeriesNumber?.Trim())
                .Where(position => !string.IsNullOrWhiteSpace(position))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (sourcePositions.Count == 1 && !string.IsNullOrWhiteSpace(audiblePosition))
            {
                if (string.Equals(sourcePositions[0], audiblePosition.Trim(), StringComparison.OrdinalIgnoreCase))
                {
                    PositionMatches++;
                }
                else
                {
                    PositionConflicts.Add(
                        $"{BookLabel(sourceBook.Audiobook)} has imported position '{sourcePositions[0]}' but Audible reports '{audiblePosition.Trim()}'.");
                }
            }
            else if (sourcePositions.Count > 1)
            {
                PositionConflicts.Add($"{BookLabel(sourceBook.Audiobook)} has conflicting imported positions.");
            }
        }
    }

    private sealed record SeriesAnalysis(
        SeriesResolutionPreview Preview,
        IReadOnlyList<int> MembershipIds,
        IReadOnlyList<SeriesResolutionCandidate> Candidates);
}
