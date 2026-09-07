using System.Text.Json;
using System.Text.RegularExpressions;
using HtmlAgilityPack;
using Microsoft.Extensions.Logging;

using RealEstatesWatcher.AdsPortals.Base;
using RealEstatesWatcher.Scrapers.Contracts;
using RealEstatesWatcher.Models;

namespace RealEstatesWatcher.AdsPortals.SrealityCz;

public class SrealityCzAdsPortal : RealEstateAdsPortalBase
{
    private const string ApiBaseUrl = "https://www.sreality.cz/api/cs/v2/estates";
    private const int PerPage = 100;
    private readonly HttpClient _httpClient;

    public SrealityCzAdsPortal(string watchedUrl,
                               IWebScraper webScraper,
                               ILogger<SrealityCzAdsPortal>? logger = null)
        : this(watchedUrl, webScraper, new HttpClient(), logger)
    {
    }

    internal SrealityCzAdsPortal(string watchedUrl,
                                 IWebScraper webScraper,
                                 HttpClient httpClient,
                                 ILogger<SrealityCzAdsPortal>? logger = null)
        : base(watchedUrl, webScraper, logger)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
    }

    public override string Name => "Sreality.cz";

    public override async Task<IList<RealEstateAdPost>> GetLatestRealEstateAdsAsync()
    {
        try
        {
            var posts = new List<RealEstateAdPost>();

            for (var page = 1; ; page++)
            {
                using var response = await _httpClient.GetAsync(BuildApiUrl(page)).ConfigureAwait(false);
                response.EnsureSuccessStatusCode();

                await using var stream = await response.Content.ReadAsStreamAsync().ConfigureAwait(false);
                using var document = await JsonDocument.ParseAsync(stream).ConfigureAwait(false);

                var estates = document.RootElement
                    .GetProperty("_embedded")
                    .GetProperty("estates");

                foreach (var estate in estates.EnumerateArray())
                    posts.Add(ParseEstate(estate));

                if (estates.GetArrayLength() < PerPage)
                    break;
            }

            Logger?.LogDebug("({Name}): Parsed {PostsCount} ads from Sreality JSON API.", Name, posts.Count);
            return posts;
        }
        catch (Exception ex)
        {
            throw new RealEstateAdsPortalException($"({Name}): Error getting latest ads from JSON API: {ex.Message}", ex);
        }
    }

    protected override string GetPathToAdsElements() => string.Empty;

    protected override RealEstateAdPost ParseRealEstateAdPost(HtmlNode node) => throw new NotSupportedException(
        "Sreality uses its JSON API instead of HTML parsing.");

    private string BuildApiUrl(int page)
    {
        var query = new List<string>
        {
            "category_type_cb=1",
            $"page={page}",
            $"per_page={PerPage}"
        };

        var watchedUri = new Uri(WatchedUrl);
        var watchedQuery = System.Web.HttpUtility.ParseQueryString(watchedUri.Query);
        var region = watchedQuery["region"];

        if (string.Equals(region, "pardubice", StringComparison.OrdinalIgnoreCase))
            query.Add("locality_district_id=32");

        return $"{ApiBaseUrl}?{string.Join("&", query)}";
    }

    private RealEstateAdPost ParseEstate(JsonElement estate)
    {
        var title = estate.TryGetProperty("name", out var nameNode)
            ? nameNode.GetString() ?? string.Empty
            : string.Empty;
        var locality = estate.TryGetProperty("locality", out var localityNode)
            ? localityNode.GetString() ?? string.Empty
            : string.Empty;
        var price = estate.TryGetProperty("price", out var priceNode) && priceNode.TryGetDecimal(out var parsedPrice)
            ? parsedPrice
            : decimal.Zero;
        var hashId = estate.TryGetProperty("hash_id", out var hashNode)
            ? hashNode.ToString()
            : string.Empty;

        return new RealEstateAdPost
        {
            AdsPortalName = Name,
            Title = title,
            Address = locality,
            Text = string.Empty,
            Price = price,
            Currency = Currency.CZK,
            Layout = ParseLayout(title),
            WebUrl = new Uri($"{ApiBaseUrl}/{hashId}"),
            FloorArea = ParseFloorArea(title),
            ImageUrl = ParseImageUrl(estate),
            PriceComment = price == decimal.Zero ? "Cena na vyžádání" : null
        };
    }

    private static Uri? ParseImageUrl(JsonElement estate)
    {
        if (!estate.TryGetProperty("_links", out var links) ||
            !links.TryGetProperty("images", out var images) ||
            images.ValueKind != JsonValueKind.Array)
            return null;

        foreach (var image in images.EnumerateArray())
        {
            if (!image.TryGetProperty("href", out var hrefNode))
                continue;

            var href = hrefNode.GetString();
            if (string.IsNullOrWhiteSpace(href))
                continue;

            if (Uri.TryCreate(href, UriKind.Absolute, out var absolute))
                return absolute;

            if (href.StartsWith("//", StringComparison.Ordinal))
                return new Uri($"https:{href}");
        }

        return null;
    }

    private static Layout ParseLayout(string title)
    {
        var result = RegexMatchers.Layout().Match(title);
        return result.Success
            ? LayoutExtensions.ToLayout(result.Groups[1].Value)
            : Layout.NotSpecified;
    }

    private static decimal ParseFloorArea(string title)
    {
        var normalizedTitle = title;
        var layout = RegexMatchers.Layout().Match(normalizedTitle);
        if (layout.Success)
            normalizedTitle = normalizedTitle.Replace(layout.Groups[1].Value, string.Empty);

        var result = RegexMatchers.FloorArea().Match(normalizedTitle);
        if (!result.Success)
            return decimal.Zero;

        var value = result.Groups.Skip<Group>(1).First(group => group.Success).Value;
        return decimal.TryParse(value, out var floorArea)
            ? floorArea
            : decimal.Zero;
    }
}
