using Microsoft.Extensions.Logging;

namespace Listenarr.Application.Audiobooks.SeriesResolution;

public sealed partial class AudiobookSeriesResolutionService : IAudiobookSeriesResolutionService
{
    private readonly AudibleService _audibleService;
    private readonly IAudiobookRepository _audiobookRepository;
    private readonly ISeriesCatalogService _seriesCatalogService;
    private readonly ILogger<AudiobookSeriesResolutionService> _logger;

    public AudiobookSeriesResolutionService(
        AudibleService audibleService,
        IAudiobookRepository audiobookRepository,
        ISeriesCatalogService seriesCatalogService,
        ILogger<AudiobookSeriesResolutionService> logger)
    {
        _audibleService = audibleService;
        _audiobookRepository = audiobookRepository;
        _seriesCatalogService = seriesCatalogService;
        _logger = logger;
    }

    public async Task<IReadOnlyList<SeriesResolutionPreview>> PreviewAsync(
        string? seriesId,
        string region,
        CancellationToken cancellationToken = default)
    {
        var analysis = await AnalyzeAsync(seriesId, region, cancellationToken);
        return analysis.Select(item => item.Preview).ToList();
    }

    public async Task<SeriesResolutionMergeResult> MergeAsync(
        string seriesId,
        string candidateAsin,
        string region,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(seriesId))
        {
            throw new ArgumentException("A source series ID is required.", nameof(seriesId));
        }

        var normalizedCandidateAsin = NormalizeAsin(candidateAsin);
        if (normalizedCandidateAsin.Length == 0)
        {
            throw new ArgumentException("A candidate Audible series ASIN is required.", nameof(candidateAsin));
        }

        var analysis = await AnalyzeAsync(seriesId, region, cancellationToken);
        var source = analysis.SingleOrDefault(item =>
            string.Equals(item.Preview.SeriesId, seriesId, StringComparison.OrdinalIgnoreCase));
        if (source == null)
        {
            var alreadyMerged = await FindAlreadyMergedAsync(
                seriesId,
                normalizedCandidateAsin,
                cancellationToken);
            if (alreadyMerged != null)
            {
                return new SeriesResolutionMergeResult(
                    seriesId,
                    normalizedCandidateAsin,
                    alreadyMerged.Value.Name,
                    0,
                    0,
                    alreadyMerged.Value.BookCount,
                    null);
            }

            throw new KeyNotFoundException($"Unresolved series '{seriesId}' was not found.");
        }

        var candidate = source.Candidates.SingleOrDefault(item =>
            string.Equals(NormalizeAsin(item.Asin), normalizedCandidateAsin, StringComparison.Ordinal));
        if (candidate == null)
        {
            throw new InvalidOperationException(
                "The selected Audible series is not a current candidate for this imported series.");
        }

        cancellationToken.ThrowIfCancellationRequested();
        string? catalogWarning = null;
        try
        {
            var catalog = await _seriesCatalogService.GetCatalogByAsinAsync(
                candidate.Asin,
                candidate.Name,
                region,
                forceRefresh: false,
                cancellationToken: cancellationToken);
            if (catalog == null || string.IsNullOrWhiteSpace(catalog.Series.Asin))
            {
                catalogWarning = "The series catalog could not be refreshed; existing catalog data was left intact.";
            }
        }
        catch (Exception exception) when (
            exception is not OperationCanceledException
            && exception is not OutOfMemoryException
            && exception is not StackOverflowException)
        {
            _logger.LogWarning(
                exception,
                "Could not refresh catalog for Audible series {SeriesAsin} before merging memberships",
                candidate.Asin);
            catalogWarning = "The series catalog could not be refreshed; existing catalog data was left intact.";
        }

        cancellationToken.ThrowIfCancellationRequested();
        var merge = await _audiobookRepository.MergeSeriesMembershipsAsync(
            source.MembershipIds,
            candidate.Asin,
            cancellationToken);

        return new SeriesResolutionMergeResult(
            source.Preview.SeriesId,
            candidate.Asin,
            candidate.Name,
            merge.TransferredMembershipCount,
            merge.DeduplicatedMembershipCount,
            merge.MergedBookCount,
            catalogWarning);
    }

    private async Task<(string Name, int BookCount)?> FindAlreadyMergedAsync(
        string seriesId,
        string candidateAsin,
        CancellationToken cancellationToken)
    {
        var audiobooks = await _audiobookRepository.GetAllAsync();
        var membershipsByBook = await _audiobookRepository.GetAllSeriesMembershipsGroupedByAudiobookIdAsync(
            cancellationToken);
        var matchingBooks = audiobooks
            .Where(audiobook => membershipsByBook.TryGetValue(audiobook.Id, out var memberships)
                && memberships.Any(membership =>
                    !string.IsNullOrWhiteSpace(membership.SeriesName)
                    && string.Equals(
                        AudiobookSeriesIdentity.GetId(
                            audiobook,
                            membership.SeriesName!,
                            audiobookshelfSeriesId: membership.AudiobookshelfSeriesId),
                        seriesId,
                        StringComparison.OrdinalIgnoreCase)
                    && string.Equals(
                        NormalizeAsin(membership.SeriesAsin),
                        candidateAsin,
                        StringComparison.Ordinal)))
            .ToList();
        cancellationToken.ThrowIfCancellationRequested();
        if (matchingBooks.Count == 0)
        {
            return null;
        }

        var name = membershipsByBook[matchingBooks[0].Id]
            .First(membership =>
                string.Equals(
                    NormalizeAsin(membership.SeriesAsin),
                    candidateAsin,
                    StringComparison.Ordinal))
            .SeriesName ?? string.Empty;
        return (name, matchingBooks.Count);
    }

    private async Task<List<SeriesAnalysis>> AnalyzeAsync(
        string? seriesId,
        string region,
        CancellationToken cancellationToken)
    {
        var audiobooks = await _audiobookRepository.GetAllAsync();
        cancellationToken.ThrowIfCancellationRequested();
        var membershipsByBook = await _audiobookRepository.GetAllSeriesMembershipsGroupedByAudiobookIdAsync(
            cancellationToken);
        var groups = BuildSourceGroups(audiobooks, membershipsByBook);
        if (!string.IsNullOrWhiteSpace(seriesId))
        {
            groups = groups
                .Where(group => string.Equals(group.Id, seriesId, StringComparison.OrdinalIgnoreCase))
                .ToList();
        }

        if (groups.Count == 0)
        {
            return [];
        }

        var allCanonicalMemberships = membershipsByBook.Values.SelectMany(items => items).ToList();
        var asins = groups
            .SelectMany(group => group.Books)
            .Select(book => book.Asin)
            .Where(asin => !string.IsNullOrWhiteSpace(asin))
            .Select(asin => asin!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        cancellationToken.ThrowIfCancellationRequested();
        List<AudibleBookResponse> metadata = asins.Count == 0
            ? []
            : await _audibleService.GetBooksMetadataByAsinsAsync(asins, region);
        cancellationToken.ThrowIfCancellationRequested();
        var metadataByAsin = metadata
            .Where(book => !string.IsNullOrWhiteSpace(book.Asin))
            .ToDictionary(book => NormalizeAsin(book.Asin), StringComparer.Ordinal);

        return groups.Select(group =>
        {
            var unresolvedBooks = new List<string>();
            var booksWithoutAsin = new List<string>();
            var membershipsByCandidate = new Dictionary<string, CandidateEvidence>(StringComparer.Ordinal);
            var groupAuthors = group.Books
                .SelectMany(book => book.Audiobook.Authors ?? [])
                .Select(NormalizeName)
                .Where(name => name.Length > 0)
                .ToHashSet(StringComparer.Ordinal);

            foreach (var sourceBook in group.Books)
            {
                if (string.IsNullOrWhiteSpace(sourceBook.Asin))
                {
                    booksWithoutAsin.Add(BookLabel(sourceBook.Audiobook));
                    continue;
                }

                if (!metadataByAsin.TryGetValue(NormalizeAsin(sourceBook.Asin), out var audibleBook))
                {
                    unresolvedBooks.Add(BookLabel(sourceBook.Audiobook));
                    continue;
                }

                var seriesEntries = audibleBook.Series ?? [];
                var matched = false;
                foreach (var audibleSeries in seriesEntries)
                {
                    var asin = NormalizeAsin(audibleSeries.Asin);
                    var name = audibleSeries.Name?.Trim();
                    if (asin.Length == 0 || string.IsNullOrWhiteSpace(name))
                    {
                        continue;
                    }

                    var similarity = GetNameSimilarity(group.Name, name);
                    if (similarity == 0)
                    {
                        continue;
                    }

                    matched = true;
                    if (!membershipsByCandidate.TryGetValue(asin, out var evidence))
                    {
                        evidence = new CandidateEvidence(asin, name);
                        membershipsByCandidate.Add(asin, evidence);
                    }

                    evidence.Add(
                        sourceBook,
                        audibleSeries.Position,
                        (audibleBook.Authors ?? [])
                            .Select(author => author.Name)
                            .Where(author => !string.IsNullOrWhiteSpace(author))
                            .Select(author => NormalizeName(author!))
                            .ToHashSet(StringComparer.Ordinal),
                        groupAuthors,
                        similarity);
                }

                if (!matched)
                {
                    unresolvedBooks.Add(BookLabel(sourceBook.Audiobook));
                }
            }

            var candidates = membershipsByCandidate.Values
                .Select(evidence => BuildCandidate(
                    evidence,
                    group,
                    allCanonicalMemberships,
                    membershipsByBook))
                .OrderByDescending(candidate => candidate.Score)
                .ThenBy(candidate => candidate.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();
            var plausibleCount = candidates.Count(candidate => candidate.Score >= 40);
            var conflicts = candidates
                .SelectMany(candidate => candidate.Conflicts)
                .Distinct(StringComparer.Ordinal)
                .ToList();

            var preview = new SeriesResolutionPreview(
                group.Id,
                group.Name,
                group.Books.SelectMany(book => book.Memberships).Select(membership => membership.AudiobookId)
                    .Distinct().Count(),
                booksWithoutAsin,
                unresolvedBooks,
                conflicts,
                plausibleCount > 1,
                candidates);
            return new SeriesAnalysis(preview, group.MembershipIds, candidates);
        }).ToList();
    }

    private static List<SourceGroup> BuildSourceGroups(
        IReadOnlyList<Audiobook> audiobooks,
        IReadOnlyDictionary<int, List<AudiobookSeriesMembership>> membershipsByBook)
    {
        var groups = new Dictionary<string, SourceGroup>(StringComparer.OrdinalIgnoreCase);
        foreach (var audiobook in audiobooks)
        {
            if (!membershipsByBook.TryGetValue(audiobook.Id, out var memberships))
            {
                continue;
            }

            foreach (var membership in memberships.Where(item =>
                         string.IsNullOrWhiteSpace(item.SeriesAsin)
                         && !string.IsNullOrWhiteSpace(item.SeriesName)))
            {
                var name = membership.SeriesName!.Trim();
                var id = AudiobookSeriesIdentity.GetId(
                    audiobook,
                    name,
                    audiobookshelfSeriesId: membership.AudiobookshelfSeriesId);
                if (!groups.TryGetValue(id, out var group))
                {
                    group = new SourceGroup(id, name);
                    groups.Add(id, group);
                }

                group.Add(audiobook, membership);
            }
        }

        return groups.Values.ToList();
    }

    private static SeriesResolutionCandidate BuildCandidate(
        CandidateEvidence evidence,
        SourceGroup source,
        IReadOnlyList<AudiobookSeriesMembership> allMemberships,
        IReadOnlyDictionary<int, List<AudiobookSeriesMembership>> membershipsByBook)
    {
        var sourceBookIds = source.Books.Select(book => book.Audiobook.Id).ToHashSet();
        var canonicalBookIds = allMemberships
            .Where(membership => string.Equals(
                NormalizeAsin(membership.SeriesAsin),
                evidence.Asin,
                StringComparison.Ordinal))
            .Select(membership => membership.AudiobookId)
            .ToHashSet();

        var conflicts = new List<string>(evidence.PositionConflicts);
        var matchingAuthors = evidence.Authors.Intersect(evidence.GroupAuthors, StringComparer.Ordinal).ToList();
        foreach (var sourceBook in source.Books.Where(book => sourceBookIds.Contains(book.Audiobook.Id)))
        {
            if (!membershipsByBook.TryGetValue(sourceBook.Audiobook.Id, out var bookMemberships))
            {
                continue;
            }

            var duplicate = bookMemberships.FirstOrDefault(membership =>
                string.Equals(
                    NormalizeAsin(membership.SeriesAsin),
                    evidence.Asin,
                    StringComparison.Ordinal));
            if (duplicate == null)
            {
                continue;
            }

            var sourcePosition = sourceBook.Memberships
                .Select(membership => membership.SeriesNumber?.Trim())
                .FirstOrDefault(position => !string.IsNullOrWhiteSpace(position));
            if (HasConflictingValue(sourcePosition, duplicate.SeriesNumber))
            {
                conflicts.Add(
                    $"{BookLabel(sourceBook.Audiobook)} already has canonical position '{duplicate.SeriesNumber}' and imported position '{sourcePosition}'.");
            }

            if (!string.IsNullOrWhiteSpace(duplicate.AudiobookshelfSeriesId)
                && sourceBook.Memberships.Any(membership =>
                    !string.IsNullOrWhiteSpace(membership.AudiobookshelfSeriesId)
                    && !string.Equals(
                        membership.AudiobookshelfSeriesId,
                        duplicate.AudiobookshelfSeriesId,
                        StringComparison.Ordinal)))
            {
                conflicts.Add($"{BookLabel(sourceBook.Audiobook)} has conflicting Audiobookshelf source series IDs.");
            }
        }

        var score = evidence.NameSimilarity == 2 ? 45 : 30;
        var evidenceItems = new List<string>
        {
            evidence.NameSimilarity == 2
                ? "Series name matches after normalization."
                : "Series name partially matches after normalization."
        };
        if (canonicalBookIds.Count > 0)
        {
            score += Math.Min(15, canonicalBookIds.Count * 5);
            evidenceItems.Add(
                $"{canonicalBookIds.Count} library book(s) already belong to this Audible series.");
        }
        if (evidence.OverlappingBooks.Count > 1)
        {
            score += 20;
            evidenceItems.Add($"{evidence.OverlappingBooks.Count} library books list this Audible series.");
        }
        else if (evidence.OverlappingBooks.Count == 1)
        {
            score += 10;
            evidenceItems.Add("One library book lists this Audible series.");
        }

        if (matchingAuthors.Count > 0)
        {
            score += 20;
            evidenceItems.Add($"Author match: {string.Join(", ", matchingAuthors)}.");
        }

        if (evidence.PositionMatches > 0)
        {
            score += 15;
            evidenceItems.Add($"{evidence.PositionMatches} book position(s) match Audible.");
        }

        if (evidence.PositionConflicts.Count > 0)
        {
            score -= Math.Min(30, evidence.PositionConflicts.Count * 15);
        }

        if (conflicts.Count > 0)
        {
            evidenceItems.Add("Existing canonical membership conflicts require review.");
        }

        var confidence = score >= 80
            && evidence.OverlappingBooks.Count >= 2
            && evidence.PositionConflicts.Count == 0
                ? "high"
                : score >= 50 ? "medium" : "low";
        var proposedBookCount = canonicalBookIds.Union(sourceBookIds).Count();
        return new SeriesResolutionCandidate(
            evidence.Asin,
            evidence.Name,
            confidence,
            Math.Clamp(score, 0, 100),
            canonicalBookIds.Count,
            proposedBookCount,
            evidence.OverlappingBooks.Count,
            evidenceItems,
            conflicts.Distinct(StringComparer.Ordinal).ToList());
    }

    private static int GetNameSimilarity(string source, string candidate)
    {
        var normalizedSource = NormalizeName(source);
        var normalizedCandidate = NormalizeName(candidate);
        if (normalizedSource.Length == 0 || normalizedCandidate.Length == 0)
        {
            return 0;
        }

        if (string.Equals(normalizedSource, normalizedCandidate, StringComparison.Ordinal))
        {
            return 2;
        }

        return normalizedSource.Contains(normalizedCandidate, StringComparison.Ordinal)
               || normalizedCandidate.Contains(normalizedSource, StringComparison.Ordinal)
            ? 1
            : 0;
    }

    private static bool HasConflictingValue(string? first, string? second) =>
        !string.IsNullOrWhiteSpace(first)
        && !string.IsNullOrWhiteSpace(second)
        && !string.Equals(first.Trim(), second.Trim(), StringComparison.OrdinalIgnoreCase);

    private static string NormalizeAsin(string? asin) =>
        string.IsNullOrWhiteSpace(asin)
            ? string.Empty
            : new string(asin.Where(char.IsLetterOrDigit).ToArray()).ToUpperInvariant();

    private static string NormalizeName(string name) => AudiobookSeriesIdentity.NormalizeName(name);

    private static string BookLabel(Audiobook audiobook) =>
        $"{audiobook.Title ?? "Untitled"} (ID {audiobook.Id})";

}
