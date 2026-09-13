using RealEstatesWatcher.AdPostsHandlers.Telegram;
using RealEstatesWatcher.Models;

namespace RealEstatesWatcher.Tests;

public sealed class PriceDropTrackingTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"rew-price-drop-{Guid.NewGuid():N}");

    public PriceDropTrackingTests() => Directory.CreateDirectory(_directory);

    [Fact]
    public void LowerPositivePrice_IsDetectedAsPriceDrop()
    {
        var store = CreateStore();
        var original = CreatePost("Sreality.cz", "https://www.sreality.cz/detail/prodej/byt/3+kk/pardubice/123", 6_490_000m);
        store.AddAndSave(store.CreateNew(original, "101"));

        var observation = store.ObservePrice(CreatePost("Sreality.cz", original.WebUrl.ToString(), 5_990_000m));

        Assert.NotNull(observation);
        Assert.True(observation!.IsPriceDrop);
        Assert.Equal(6_490_000m, observation.PreviousPrice);
        Assert.Equal(5_990_000m, observation.CurrentPrice);
    }

    [Fact]
    public void UnknownPriceBecomingKnown_IsBaselineOnly()
    {
        var store = CreateStore();
        var original = CreatePost("Reality.idnes.cz", "https://reality.idnes.cz/detail/ABC123", decimal.Zero);
        store.AddAndSave(store.CreateNew(original, "101"));

        var observation = store.ObservePrice(CreatePost("Reality.idnes.cz", original.WebUrl.ToString(), 5_500_000m));

        Assert.NotNull(observation);
        Assert.False(observation!.IsPriceDrop);
        Assert.Equal(decimal.Zero, observation.PreviousPrice);
        Assert.Equal(5_500_000m, observation.CurrentPrice);
    }

    [Fact]
    public void ZeroCurrentPrice_IsIgnored()
    {
        var store = CreateStore();
        var original = CreatePost("Bazoš.cz", "https://reality.bazos.cz/inzerat/123/dum.php", 5_500_000m);
        store.AddAndSave(store.CreateNew(original, "101"));

        Assert.Null(store.ObservePrice(CreatePost("Bazoš.cz", original.WebUrl.ToString(), decimal.Zero)));
    }

    [Fact]
    public void SamePriceDropCopiedToSecondPortal_DoesNotNotifyTwice()
    {
        var store = CreateStore();
        var sreality = CreatePost("Sreality.cz", "https://www.sreality.cz/detail/prodej/byt/3+kk/pardubice/123", 6_500_000m);
        var property = store.CreateNew(sreality, "101");
        store.AddAndSave(property);

        var idnes = CreatePost("Reality.idnes.cz", "https://reality.idnes.cz/detail/ABC123", 6_500_000m);
        var duplicate = store.FindDuplicate(idnes);
        Assert.NotNull(duplicate);
        store.ReplaceAndSave(duplicate!, store.WithAdditionalSource(duplicate!, idnes));

        var firstDrop = store.ObservePrice(CreatePost("Sreality.cz", sreality.WebUrl.ToString(), 6_000_000m));
        Assert.NotNull(firstDrop);
        Assert.True(firstDrop!.IsPriceDrop);
        store.ReplaceAndSave(firstDrop.ExistingProperty, firstDrop.UpdatedProperty);

        var copiedDrop = store.ObservePrice(CreatePost("Reality.idnes.cz", idnes.WebUrl.ToString(), 6_000_000m));
        Assert.NotNull(copiedDrop);
        Assert.False(copiedDrop!.IsPriceDrop);
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
            Directory.Delete(_directory, recursive: true);
    }

    private CrossPortalPropertyStore CreateStore() =>
        new(Path.Combine(_directory, $"{Guid.NewGuid():N}.json"));

    private static RealEstateAdPost CreatePost(string portal, string url, decimal price) => new()
    {
        AdsPortalName = portal,
        Title = "Prodej bytu 3+kk 74 m²",
        Text = "Prodej bytu 3+kk 74 m²",
        Price = price,
        Address = "Sladkovského, Pardubice - Zelené Předměstí",
        WebUrl = new Uri(url),
        Currency = Currency.CZK,
        Layout = Layout.ThreePlusKk,
        FloorArea = 74m
    };
}
