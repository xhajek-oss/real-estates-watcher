namespace RealEstatesWatcher.Models;

public sealed record RealEstatePropertySource(string AdsPortalName, Uri WebUrl);

public sealed record RealEstatePropertyNotification(
    string PropertyId,
    string Title,
    string Address,
    decimal Price,
    Currency Currency,
    Layout Layout,
    decimal? FloorArea,
    RealEstatePropertySource PrimarySource,
    IReadOnlyList<RealEstatePropertySource> Sources);
