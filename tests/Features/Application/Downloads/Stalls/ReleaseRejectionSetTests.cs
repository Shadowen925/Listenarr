using Listenarr.Application.Downloads.Stalls;
using Listenarr.Tests.Common;

namespace Listenarr.Tests.Features.Application.Downloads.Stalls
{
    [Trait("Name", nameof(ReleaseRejectionSetTests))]
    [Trait("Category", "Application")]
    public class ReleaseRejectionSetTests : BaseTests
    {
        [Fact]
        public void FailedReleaseWithStableId_DoesNotRejectAlternateWithSameTitle()
        {
            var rejectedReleases = new ReleaseRejectionSet();
            rejectedReleases.AddDownload(new Download
            {
                Title = "A Book",
                Metadata = new Dictionary<string, object>
                {
                    ["ReleaseId"] = "release-1"
                }
            });

            Assert.True(rejectedReleases.Matches(new SearchResult
            {
                Id = "release-1",
                Title = "A Book"
            }));
            Assert.False(rejectedReleases.Matches(new SearchResult
            {
                Id = "release-2",
                Title = "A Book"
            }));
        }

        [Fact]
        public void FailedReleaseWithoutStableId_UsesTitleAsFallback()
        {
            var rejectedReleases = new ReleaseRejectionSet();
            rejectedReleases.AddDownload(new Download
            {
                Title = "A Book",
                Metadata = new Dictionary<string, object>
                {
                    ["DownloadType"] = "Usenet"
                }
            });

            Assert.True(rejectedReleases.Matches(new SearchResult { Title = "a book" }));
        }
    }
}
