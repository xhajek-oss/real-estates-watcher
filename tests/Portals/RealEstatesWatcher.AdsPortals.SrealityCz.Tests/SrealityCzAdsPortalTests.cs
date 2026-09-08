using System.Net;
using System.Text;
using RealEstatesWatcher.AdsPortals.SrealityCz;
using RealEstatesWatcher.Models;

namespace RealEstatesWatcher.Tests;

public class SrealityCzAdsPortalTests : PortalParserTestBase
{
    [Fact]
    public async Task ParsesRepresentativeListingFromV1JsonApi()
    {
        const string json = """
            {
              "pagination": { "total": 1, "limit": 1000, "offset": 0 },
              "results": [
                {
                  "hash_id": 123,
                  "advert_name": "Prodej bytu 2+kk 55 m²",
                  "price_czk": 5500000,
                  "category_type_cb": { "value": 1, "name": "Prodej", "seo_name": "prodej" },
                  "category_main_cb": { "value": 1, "name": "Byty" },
                  "category_sub_cb": { "value": 47, "name": "2+kk" },
                  "locality": {
                    "city": "Pardubice",
                    "citypart": "Zelené Předměstí",
                    "street": "Palackého třída",
                    "city_seo_name": "pardubice",
                    "citypart_seo_name": "zelene-predmesti",
                    "street_seo_name": "palackeho-trida"
                  },
                  "advert_images": ["https://img.example.test/one.jpg"]
                }
              ]
            }
            """;

        var handler = new StubHttpMessageHandler(json);
        var portal = new SrealityCzAdsPortal(
            "https://www.sreality.cz/hledani/prodej?region=pardubice",
            new StubWebScraper(string.Empty),
            new HttpClient(handler));

        var post = Assert.Single(await portal.GetLatestRealEstateAdsAsync());

        AssertPost(post, "Sreality.cz", "Prodej bytu 2+kk 55 m²", 5_500_000m, 55m, Layout.TwoPlusKk, "https://www.sreality.cz/detail/prodej/byt/2+kk/pardubice-zelene-predmesti-palackeho-trida/123");
        Assert.Equal("Pardubice - Zelené Předměstí - Palackého třída", post.Address);
        Assert.Equal(new Uri("https://img.example.test/one.jpg"), post.ImageUrl);
        Assert.Equal(5, handler.RequestedUris.Count);
        Assert.All(handler.RequestedUris, uri => Assert.Contains("category_type_cb=1", uri.Query));
        Assert.All(handler.RequestedUris, uri => Assert.Contains("locality_region_id=7", uri.Query));
        Assert.All(handler.RequestedUris, uri => Assert.Contains("limit=1000", uri.Query));
        Assert.All(handler.RequestedUris, uri => Assert.Contains("offset=0", uri.Query));
        foreach (var category in Enumerable.Range(1, 5))
            Assert.Contains(handler.RequestedUris, uri => uri.Query.Contains($"category_main_cb={category}"));
    }

    [Fact]
    public async Task PublicUrlFallsBackToNamesWhenSeoFieldsAreMissing()
    {
        const string json = """
            {
              "results": [
                {
                  "hash_id": 456,
                  "advert_name": "Prodej rodinného domu 120 m²",
                  "price_czk": 7000000,
                  "category_type_cb": { "value": 1, "name": "Prodej" },
                  "category_main_cb": { "value": 2, "name": "Domy" },
                  "category_sub_cb": { "value": 37, "name": "Rodinný" },
                  "locality": {
                    "city": "Pardubice",
                    "citypart": "Bílé Předměstí"
                  }
                }
              ]
            }
            """;

        var portal = new SrealityCzAdsPortal(
            "https://www.sreality.cz/hledani/prodej?region=pardubice",
            new StubWebScraper(string.Empty),
            new HttpClient(new StubHttpMessageHandler(json)));

        var post = Assert.Single(await portal.GetLatestRealEstateAdsAsync());

        Assert.Equal(new Uri("https://www.sreality.cz/detail/prodej/dum/rodinny/pardubice-bile-predmesti/456"), post.WebUrl);
    }

    [Fact]
    public async Task SparseListingUsesFallbackValues()
    {
        const string json = """
            {
              "results": [
                {
                  "hash_id": 789,
                  "advert_name": "Ateliér",
                  "locality": {}
                }
              ]
            }
            """;

        var portal = new SrealityCzAdsPortal(
            "https://www.sreality.cz/hledani/prodej?region=pardubice",
            new StubWebScraper(string.Empty),
            new HttpClient(new StubHttpMessageHandler(json)));

        var post = Assert.Single(await portal.GetLatestRealEstateAdsAsync());

        Assert.Equal("Ateliér", post.Title);
        Assert.Equal(string.Empty, post.Address);
        Assert.Equal(decimal.Zero, post.Price);
        Assert.Equal(decimal.Zero, post.FloorArea);
        Assert.Equal(Layout.NotSpecified, post.Layout);
        Assert.Equal("Cena na vyžádání", post.PriceComment);
        Assert.Null(post.ImageUrl);
        Assert.Equal(new Uri("https://www.sreality.cz/detail/prodej/ostatni/ostatni/ceska-republika/789"), post.WebUrl);
    }

    private sealed class StubHttpMessageHandler(string responseJson) : HttpMessageHandler
    {
        public List<Uri> RequestedUris { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestedUris.Add(request.RequestUri!);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(responseJson, Encoding.UTF8, "application/json")
            });
        }
    }
}
