using RealEstatesWatcher.AdsPortals.SrealityCz;
using RealEstatesWatcher.Models;

namespace RealEstatesWatcher.Tests;

public class SrealityCzAdsPortalTests : PortalParserTestBase
{
    [Fact]
    public async Task ParsesRepresentativeListing()
    {
        const string html = """
            <html><body>
              <section>
                <a href="/detail/prodej/byt/2+kk/praha/123">
                  <div><img src="https://img.example.test/one.jpg"></div>
                  <p>Prodej bytu 2+kk 55 m²</p>
                  <p>Praha 2</p>
                  <p>5 500 000 Kč</p>
                </a>
              </section>
            </body></html>
            """;
        var portal = new SrealityCzAdsPortal("https://www.sreality.cz/hledani", new StubWebScraper(html));

        var post = Assert.Single(await portal.GetLatestRealEstateAdsAsync());

        AssertPost(post, "Sreality.cz", "Prodej bytu 2+kk 55 m²", 5_500_000m, 55m, Layout.TwoPlusKk, "https://www.sreality.cz/detail/prodej/byt/2+kk/praha/123");
        Assert.Equal("Praha 2", post.Address);
        Assert.Equal(new Uri("https://img.example.test/one.jpg"), post.ImageUrl);
    }

    [Fact]
    public async Task SparseListingUsesFallbackValues()
    {
        const string html = """
            <html><body><a href="/detail/prodej/ostatni/atelier/456"><p>Ateliér</p></a></body></html>
            """;
        var portal = new SrealityCzAdsPortal("https://www.sreality.cz/hledani", new StubWebScraper(html));

        var post = Assert.Single(await portal.GetLatestRealEstateAdsAsync());

        Assert.Equal("Ateliér", post.Title);
        Assert.Equal(string.Empty, post.Address);
        Assert.Equal(decimal.Zero, post.Price);
        Assert.Equal(decimal.Zero, post.FloorArea);
        Assert.Equal(Layout.NotSpecified, post.Layout);
        Assert.Null(post.PriceComment);
        Assert.Null(post.ImageUrl);
    }

    [Fact]
    public async Task IgnoresNonDetailLinks()
    {
        const string html = """
            <html><body>
              <a href="/hledani/prodej/byty"><p>Byty</p></a>
              <a href="/detail/prodej/byt/1+1/pardubice/789"><p>Prodej bytu 1+1 37 m²</p><p>Pardubice</p><p>4 100 000 Kč</p></a>
            </body></html>
            """;
        var portal = new SrealityCzAdsPortal("https://www.sreality.cz/hledani", new StubWebScraper(html));

        var post = Assert.Single(await portal.GetLatestRealEstateAdsAsync());

        Assert.Equal("Prodej bytu 1+1 37 m²", post.Title);
    }
}
