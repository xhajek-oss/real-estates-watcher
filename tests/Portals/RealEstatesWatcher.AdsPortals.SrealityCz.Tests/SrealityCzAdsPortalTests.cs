using System.Net;
using System.Text;
using RealEstatesWatcher.AdsPortals.SrealityCz;
using RealEstatesWatcher.Models;

namespace RealEstatesWatcher.Tests;

public class SrealityCzAdsPortalTests : PortalParserTestBase
{
    [Fact]
    public async Task ParsesRepresentativeListingFromJsonApi()
    {
        const string json = """
            {
              "_embedded": {
                "estates": [
                  {
                    "hash_id": 123,
                    "name": "Prodej bytu 2+kk 55 m²",
                    "locality": "Pardubice - Zelené Předměstí",
                    "price": 5500000,
                    "_links": {
                      "images": [
                        { "href": "https://img.example.test/one.jpg" }
                      ]
                    }
                  }
                ]
              }
            }
            """;

        var handler = new StubHttpMessageHandler(json);
        var httpClient = new HttpClient(handler);
        var portal = new SrealityCzAdsPortal(
            "https://www.sreality.cz/hledani/prodej?region=pardubice",
            new StubWebScraper(string.Empty),
            httpClient);

        var post = Assert.Single(await portal.GetLatestRealEstateAdsAsync());

        AssertPost(post, "Sreality.cz", "Prodej bytu 2+kk 55 m²", 5_500_000m, 55m, Layout.TwoPlusKk, "https://www.sreality.cz/api/cs/v2/estates/123");
        Assert.Equal("Pardubice - Zelené Předměstí", post.Address);
        Assert.Equal(new Uri("https://img.example.test/one.jpg"), post.ImageUrl);
        Assert.Contains("category_type_cb=1", handler.RequestedUri!.Query);
        Assert.Contains("locality_district_id=32", handler.RequestedUri.Query);
    }

    [Fact]
    public async Task SparseListingUsesFallbackValues()
    {
        const string json = """
            {
              "_embedded": {
                "estates": [
                  {
                    "hash_id": 456,
                    "name": "Ateliér",
                    "locality": ""
                  }
                ]
              }
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
    }

    private sealed class StubHttpMessageHandler(string responseJson) : HttpMessageHandler
    {
        public Uri? RequestedUri { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestedUri = request.RequestUri;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(responseJson, Encoding.UTF8, "application/json")
            });
        }
    }
}
