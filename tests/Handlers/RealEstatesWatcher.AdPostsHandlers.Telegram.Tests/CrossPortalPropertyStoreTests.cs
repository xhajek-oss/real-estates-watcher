using RealEstatesWatcher.AdPostsHandlers.Telegram;
using RealEstatesWatcher.Models;

namespace RealEstatesWatcher.Tests;

public sealed class CrossPortalPropertyStoreTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"rew-telegram-dedup-{Guid.NewGuid():N}");

    public CrossPortalPropertyStoreTests() => Directory.CreateDirectory(_directory);

    [Fact]
    public void SimilarListingOnDifferentPortal_IsMatchedAsDuplicate()
    {
        var store = CreateStore();
        var sreality = CreatePost("Sreality.cz", "https://www.sreality.cz/detail/prodej/byt/3+kk/pardubice/123456", "Prodej bytu 3+kk 74 m²", "Sladkovského, Pardubice", 6_490_000m, 74m, Layout.ThreePlusKk);
        store.AddAndSave(store.CreateNew(sreality, "101"));

        var idnes = CreatePost("Reality.idnes.cz", "https://reality.idnes.cz/detail/ABC123", "Byt 3+kk 73 m²", "Sladkovského, Pardubice", 6_500_000m, 73m, Layout.ThreePlusKk);

        var duplicate = store.FindDuplicate(idnes);

        Assert.NotNull(duplicate);
        var updated = store.WithAdditionalSource(duplicate!, idnes);
        Assert.Equal(2, updated.Sources.Length);
        Assert.Equal("Sreality.cz", updated.PrimarySource.AdsPortalName);
    }

    [Fact]
    public void DifferentListingOnSamePortal_IsNeverCrossPortalDeduplicated()
    {
        var store = CreateStore();
        var first = CreatePost("Sreality.cz", "https://www.sreality.cz/detail/prodej/byt/3+kk/pardubice/111", "Prodej bytu 3+kk 74 m²", "Sladkovského, Pardubice", 6_490_000m, 74m, Layout.ThreePlusKk);
        store.AddAndSave(store.CreateNew(first, "101"));

        var second = CreatePost("Sreality.cz", "https://www.sreality.cz/detail/prodej/byt/3+kk/pardubice/222", "Prodej bytu 3+kk 74 m²", "Sladkovského, Pardubice", 6_490_000m, 74m, Layout.ThreePlusKk);

        Assert.Null(store.FindDuplicate(second));
    }

    [Fact]
    public void DifferentLayout_IsNotMatchedEvenWithSameAddressAndPrice()
    {
        var store = CreateStore();
        var first = CreatePost("Sreality.cz", "https://www.sreality.cz/detail/prodej/byt/3+kk/pardubice/111", "Prodej bytu 3+kk", "Sladkovského, Pardubice", 6_490_000m, 74m, Layout.ThreePlusKk);
        store.AddAndSave(store.CreateNew(first, "101"));

        var second = CreatePost("Reality.idnes.cz", "https://reality.idnes.cz/detail/222", "Prodej bytu 2+kk", "Sladkovského, Pardubice", 6_490_000m, 74m, Layout.TwoPlusKk);

        Assert.Null(store.FindDuplicate(second));
    }

    [Fact]
    public void HigherPriorityPortalBecomesPrimarySource()
    {
        var store = CreateStore();
        var bazos = CreatePost("Bazoš.cz", "https://reality.bazos.cz/inzerat/111/byt.php", "Prodej bytu 3+kk 74 m²", "Sladkovského, Pardubice", 6_490_000m, 74m, Layout.ThreePlusKk);
        var property = store.CreateNew(bazos, "101");
        store.AddAndSave(property);

        var sreality = CreatePost("Sreality.cz", "https://www.sreality.cz/detail/prodej/byt/3+kk/pardubice/222", "Prodej bytu 3+kk 74 m²", "Sladkovského, Pardubice", 6_490_000m, 74m, Layout.ThreePlusKk);
        var duplicate = store.FindDuplicate(sreality);
        Assert.NotNull(duplicate);

        var updated = store.WithAdditionalSource(duplicate!, sreality);

        Assert.Equal("Sreality.cz", updated.PrimarySource.AdsPortalName);
    }

    [Fact]
    public void StateIsPersistedAndReloaded()
    {
        var path = Path.Combine(_directory, "properties.json");
        var store = new CrossPortalPropertyStore(path);
        var first = CreatePost("Sreality.cz", "https://www.sreality.cz/detail/prodej/byt/3+kk/pardubice/111", "Prodej bytu 3+kk 74 m²", "Sladkovského, Pardubice", 6_490_000m, 74m, Layout.ThreePlusKk);
        store.AddAndSave(store.CreateNew(first, "101"));

        var reloaded = new CrossPortalPropertyStore(path);
        var duplicate = reloaded.FindDuplicate(CreatePost("Reality.idnes.cz", "https://reality.idnes.cz/detail/222", "Byt 3+kk 73 m²", "Sladkovského, Pardubice", 6_500_000m, 73m, Layout.ThreePlusKk));

        Assert.NotNull(duplicate);
        Assert.Equal("101", duplicate!.TelegramMessageId);
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
            Directory.Delete(_directory, recursive: true);
    }

    private CrossPortalPropertyStore CreateStore() =>
        new(Path.Combine(_directory, $"{Guid.NewGuid():N}.json"));

    private static RealEstateAdPost CreatePost(
        string portal,
        string url,
        string title,
        string address,
        decimal price,
        decimal floorArea,
        Layout layout) => new()
    {
        AdsPortalName = portal,
        Title = title,
        Text = title,
        Price = price,
        Address = address,
        WebUrl = new Uri(url),
        Currency = Currency.CZK,
        Layout = layout,
        FloorArea = floorArea
    };
}
