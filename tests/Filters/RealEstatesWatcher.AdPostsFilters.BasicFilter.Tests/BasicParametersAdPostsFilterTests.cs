using RealEstatesWatcher.AdPostsFilters.BasicFilter;
using RealEstatesWatcher.Models;

namespace RealEstatesWatcher.Tests;

public class BasicParametersAdPostsFilterTests
{
    [Fact]
    public void Constructor_RejectsNullSettings() =>
        Assert.Throws<ArgumentNullException>(() => new BasicParametersAdPostsFilter(null!));

    [Fact]
    public void Filter_RejectsNullPosts()
    {
        var filter = new BasicParametersAdPostsFilter(new BasicParametersAdPostsFilterSettings());

        Assert.Throws<ArgumentNullException>(() => filter.Filter(null!));
    }

    [Fact]
    public void Filter_AppliesAllConfiguredBounds()
    {
        var filter = new BasicParametersAdPostsFilter(new BasicParametersAdPostsFilterSettings
        {
            MinPrice = 2_000_000m,
            MaxPrice = 4_000_000m,
            MinFloorArea = 50m,
            MaxFloorArea = 80m,
            Layouts = new HashSet<Layout> { Layout.TwoPlusKk }
        });
        var posts = new[]
        {
            TestData.CreatePost("match"),
            TestData.CreatePost("cheap", price: 1_999_999m),
            TestData.CreatePost("expensive", price: 4_000_001m),
            TestData.CreatePost("small", floorArea: 49m),
            TestData.CreatePost("large", floorArea: 81m),
            TestData.CreatePost("layout", layout: Layout.ThreePlusKk)
        };

        var result = filter.Filter(posts).ToList();

        Assert.Collection(result, post => Assert.EndsWith("/match", post.WebUrl.AbsolutePath));
    }

    [Fact]
    public void Filter_IncludesInclusiveBoundaryValues()
    {
        var filter = new BasicParametersAdPostsFilter(new BasicParametersAdPostsFilterSettings
        {
            MinPrice = 2_500_000m,
            MaxPrice = 2_500_000m,
            MinFloorArea = 60m,
            MaxFloorArea = 60m
        });

        Assert.Single(filter.Filter([TestData.CreatePost()]));
    }

    [Fact]
    public void Filter_KeepsListingsWithUnknownPriceAreaOrLayout()
    {
        var filter = new BasicParametersAdPostsFilter(new BasicParametersAdPostsFilterSettings
        {
            MinPrice = 1m,
            MaxPrice = 2m,
            MinFloorArea = 1m,
            MaxFloorArea = 2m,
            Layouts = new HashSet<Layout> { Layout.FivePlusOne }
        });
        var unknown = TestData.CreatePost(
            price: decimal.Zero,
            floorArea: decimal.Zero,
            layout: Layout.NotSpecified);

        Assert.Single(filter.Filter([unknown]));
    }

    [Fact]
    public void Filter_IsDeferredAndDoesNotModifyTheInput()
    {
        var posts = new List<RealEstateAdPost> { TestData.CreatePost("first") };
        var filter = new BasicParametersAdPostsFilter(new BasicParametersAdPostsFilterSettings());
        var result = filter.Filter(posts);
        posts.Add(TestData.CreatePost("second"));

        Assert.Equal(2, result.Count());
        Assert.Equal(2, posts.Count);
    }

    [Fact]
    public void Filter_DoesNotUseFreeFormTextAsLocationEvidence()
    {
        var filter = CreatePardubiceIFilter();
        var chvaletice = CreateLocationPost(
            title: "Prodej, domy/rodinný, 97 m2, V Telčicích 6, 53312 Chvaletice",
            address: "Pardubice 533 12",
            text: "Rodinný dům se zahradou a zámek v okolí.");

        Assert.Empty(filter.Filter([chvaletice]));
    }

    [Fact]
    public void Filter_KeepsPardubiceIAliasFromAddressOrTitle()
    {
        var filter = CreatePardubiceIFilter();
        var pardubice = CreateLocationPost(
            title: "Prodej bytu 2+kk, Pardubice",
            address: "Jiráskova, Pardubice - Zelené Předměstí",
            text: "Bez dalších lokalit v popisu.");

        Assert.Single(filter.Filter([pardubice]));
    }

    private static BasicParametersAdPostsFilter CreatePardubiceIFilter() =>
        new(new BasicParametersAdPostsFilterSettings
        {
            City = "Pardubice",
            LocationAny = "Pardubice I,Pardubice-Staré Město,Staré Město,Zámek,Bílé Předměstí,Zelené Předměstí"
        });

    private static RealEstateAdPost CreateLocationPost(string title, string address, string text) => new()
    {
        AdsPortalName = "Bazoš.cz",
        Title = title,
        Text = text,
        Price = 5_980_000m,
        Address = address,
        WebUrl = new Uri("https://reality.bazos.cz/inzerat/123/test.php"),
        Currency = Currency.CZK,
        Layout = Layout.NotSpecified,
        FloorArea = 97m
    };
}
