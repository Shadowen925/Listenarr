using Listenarr.Application.Audiobookshelf;
using Listenarr.Tests.Common;

namespace Listenarr.Tests.Features.Application.Audiobookshelf;

[Trait("Name", nameof(AudiobookshelfPathMapperTests))]
[Trait("Category", "Application")]
public sealed class AudiobookshelfPathMapperTests : BaseTests
{
    [Fact]
    public void TryMapPath_MapsLongestMatchingLibraryRootToListenarrRoot()
    {
        var listenarrRoot = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "listenarr-audiobooks"));

        var mapped = AudiobookshelfPathMapper.TryMapPath(
            "/media/audiobooks/Author/Book/part.m4b",
            ["/media", "/media/audiobooks"],
            listenarrRoot,
            out var mappedPath);

        Assert.True(mapped);
        Assert.Equal(
            Path.Combine(listenarrRoot, "Author", "Book", "part.m4b"),
            mappedPath);
    }

    [Fact]
    public void TryMapPath_MapsRootLibraryAndRejectsTraversal()
    {
        var listenarrRoot = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "listenarr-audiobooks"));

        Assert.True(AudiobookshelfPathMapper.TryMapPath(
            "/Author/Book/book.m4b",
            ["/"],
            listenarrRoot,
            out var mappedPath));
        Assert.Equal(Path.Combine(listenarrRoot, "Author", "Book", "book.m4b"), mappedPath);

        Assert.False(AudiobookshelfPathMapper.TryMapPath(
            "/media/audiobooks/../outside/book.m4b",
            ["/media/audiobooks"],
            listenarrRoot,
            out _));
    }
}
