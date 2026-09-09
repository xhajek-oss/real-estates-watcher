using RealEstatesWatcher.Models;

namespace RealEstatesWatcher.AdPostsHandlers.Contracts;

public interface IUpdatableRealEstatePropertyHandler
{
    string HandlerStateKey { get; }

    Task<string?> HandleNewRealEstatePropertyAsync(
        RealEstatePropertyNotification property,
        CancellationToken cancellationToken = default);

    Task UpdateRealEstatePropertyAsync(
        RealEstatePropertyNotification property,
        string externalMessageId,
        CancellationToken cancellationToken = default);
}
