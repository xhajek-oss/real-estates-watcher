using RealEstatesWatcher.Models;

namespace RealEstatesWatcher.AdPostsHandlers.Contracts;

public interface IRealEstateAdPostsSnapshotHandler
{
    Task HandleCurrentRealEstateAdPostsAsync(
        IList<RealEstateAdPost> adPosts,
        CancellationToken cancellationToken = default);
}
