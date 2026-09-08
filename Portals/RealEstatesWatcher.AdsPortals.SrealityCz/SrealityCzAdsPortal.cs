using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using HtmlAgilityPack;
using Microsoft.Extensions.Logging;

using RealEstatesWatcher.AdsPortals.Base;
using RealEstatesWatcher.AdsPortals.Contracts;
using RealEstatesWatcher.Scrapers.Contracts;
using RealEstatesWatcher.Models;

namespace RealEstatesWatcher.AdsPortals.SrealityCz;

public class SrealityCzAdsPortal : RealEstateAdsPortalBase
{
    private const string ApiBaseUrl = "https://www.sreality.cz/api/v1/estates/search";
    private const int PerPage = 1000;
    private const int MaxErrorBodyLength = 4000;
    private static readonly int[] SaleCategoryMainIds = [1, 2, 3, 4, 5];
    private readonly HttpClient _httpClient;

    public SrealityCzAdsPortal(string watchedUrl, IWebScraper webScraper, ILogger<SrealityCzAdsPortal>? logger = null)
        : this(watchedUrl, webScraper, new HttpClient(), logger) { }

    public SrealityCzAdsPortal(string watchedUrl, IWebScraper webScraper, HttpClient httpClient, ILogger<SrealityCzAdsPortal>? logger = null)
        : base(watchedUrl, webScraper, logger)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
    }

    public override string Name => "Sreality.cz";

    public override async Task<IList<RealEstateAdPost>> GetLatestRealEstateAdsAsync()
    {
        try
        {
            var postsByUrl = new Dictionary<string, RealEstateAdPost>(StringComparer.OrdinalIgnoreCase);
            foreach (var categoryMainId in SaleCategoryMainIds)
            {
                for (var offset = 0; ; offset += PerPage)
                {
                    var requestUrl = BuildApiUrl(categoryMainId, offset);
                    using var response = await _httpClient.GetAsync(requestUrl).ConfigureAwait(false);
                    if (!response.IsSuccessStatusCode)
                    {
                        var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                        if (body.Length > MaxErrorBodyLength) body = body[..MaxErrorBodyLength] + "... [truncated]";
                        Logger?.LogError("({Name}): Sreality API request failed. URL={RequestUrl}, Status={StatusCode} ({ReasonPhrase}), Body={ResponseBody}", Name, requestUrl, (int)response.StatusCode, response.ReasonPhrase, body);
                    }
                    response.EnsureSuccessStatusCode();

                    await using var stream = await response.Content.ReadAsStreamAsync().ConfigureAwait(false);
                    using var document = await JsonDocument.ParseAsync(stream).ConfigureAwait(false);
                    var estates = document.RootElement.GetProperty("results");
                    foreach (var estate in estates.EnumerateArray())
                    {
                        var post = ParseEstate(estate);
                        postsByUrl[post.WebUrl.GetLeftPart(UriPartial.Path)] = post;
                    }
                    if (estates.GetArrayLength() < PerPage) break;
                }
            }

            var posts = postsByUrl.Values.ToList();
            Logger?.LogDebug("({Name}): Parsed {PostsCount} unique ads from Sreality v1 JSON API.", Name, posts.Count);
            return posts;
        }
        catch (Exception ex)
        {
            throw new RealEstateAdsPortalException($"({Name}): Error getting latest ads from JSON API: {ex.Message}", ex);
        }
    }

    protected override string GetPathToAdsElements() => string.Empty;
    protected override RealEstateAdPost ParseRealEstateAdPost(HtmlNode node) => throw new NotSupportedException("Sreality uses its JSON API instead of HTML parsing.");

    private string BuildApiUrl(int categoryMainId, int offset)
    {
        var query = new List<string> { "category_type_cb=1", $"category_main_cb={categoryMainId}", $"limit={PerPage}", $"offset={offset}", "lang=cs" };
        var watchedUri = new Uri(WatchedUrl);
        var watchedQuery = System.Web.HttpUtility.ParseQueryString(watchedUri.Query);
        if (string.Equals(watchedQuery["region"], "pardubice", StringComparison.OrdinalIgnoreCase)) query.Add("locality_region_id=7");
        return $"{ApiBaseUrl}?{string.Join("&", query)}";
    }

    private RealEstateAdPost ParseEstate(JsonElement estate)
    {
        var title = GetString(estate, "advert_name");
        var price = estate.TryGetProperty("price_czk", out var priceNode) && priceNode.TryGetDecimal(out var parsedPrice) ? parsedPrice : decimal.Zero;
        var hashId = estate.TryGetProperty("hash_id", out var hashNode) ? hashNode.ToString() : string.Empty;
        if (string.IsNullOrWhiteSpace(hashId)) throw new JsonException("Sreality listing is missing hash_id.");

        return new RealEstateAdPost
        {
            AdsPortalName = Name,
            Title = title,
            Address = ParseAddress(estate),
            Text = string.Empty,
            Price = price,
            Currency = Currency.CZK,
            Layout = ParseLayout(title),
            WebUrl = BuildPublicDetailUrl(estate, hashId),
            FloorArea = ParseFloorArea(title),
            ImageUrl = ParseImageUrl(estate),
            PriceComment = price == decimal.Zero ? "Cena na vyžádání" : null
        };
    }

    private static Uri BuildPublicDetailUrl(JsonElement estate, string hashId)
    {
        var type = GetCategorySeoName(estate, "category_type_cb", "prodej");
        var main = GetMainCategorySeoName(estate);
        var sub = GetCategorySeoName(estate, "category_sub_cb", "ostatni");
        var locality = GetLocalitySeoName(estate);

        return new Uri($"https://www.sreality.cz/detail/{type}/{main}/{sub}/{locality}/{hashId}");
    }

    private static string GetMainCategorySeoName(JsonElement estate)
    {
        if (estate.TryGetProperty("category_main_cb", out var category) && category.ValueKind == JsonValueKind.Object)
        {
            if (category.TryGetProperty("seo_name", out var seoName) && seoName.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(seoName.GetString()))
                return seoName.GetString()!;

            if (category.TryGetProperty("value", out var value) && value.TryGetInt32(out var categoryId))
            {
                return categoryId switch
                {
                    1 => "byt",
                    2 => "dum",
                    3 => "pozemek",
                    4 => "komercni",
                    5 => "ostatni",
                    _ => Slugify(GetString(category, "name"))
                };
            }

            var name = Slugify(GetString(category, "name"));
            if (!string.IsNullOrWhiteSpace(name))
                return name;
        }

        return "ostatni";
    }

    private static string GetCategorySeoName(JsonElement estate, string propertyName, string fallback)
    {
        if (!estate.TryGetProperty(propertyName, out var category) || category.ValueKind != JsonValueKind.Object)
            return fallback;

        var seoName = GetString(category, "seo_name");
        if (!string.IsNullOrWhiteSpace(seoName))
            return seoName;

        var name = Slugify(GetString(category, "name"));
        return string.IsNullOrWhiteSpace(name) ? fallback : name;
    }

    private static string GetLocalitySeoName(JsonElement estate)
    {
        if (!estate.TryGetProperty("locality", out var locality) || locality.ValueKind != JsonValueKind.Object)
            return "ceska-republika";

        var city = FirstNonEmpty(GetString(locality, "city_seo_name"), Slugify(GetString(locality, "city")));
        var cityPart = FirstNonEmpty(GetString(locality, "citypart_seo_name"), Slugify(GetString(locality, "citypart")));
        var street = FirstNonEmpty(GetString(locality, "street_seo_name"), Slugify(GetString(locality, "street")));

        var parts = new[] { city, cityPart, street }
            .Where(part => !string.IsNullOrWhiteSpace(part))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return parts.Length == 0 ? "ceska-republika" : string.Join("-", parts);
    }

    private static string FirstNonEmpty(string preferred, string fallback) =>
        string.IsNullOrWhiteSpace(preferred) ? fallback : preferred;

    private static string Slugify(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return string.Empty;

        var normalized = value.Normalize(NormalizationForm.FormD);
        var builder = new StringBuilder(normalized.Length);
        var previousWasSeparator = false;

        foreach (var character in normalized)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(character) == UnicodeCategory.NonSpacingMark)
                continue;

            var lower = char.ToLowerInvariant(character);
            if (char.IsLetterOrDigit(lower) || lower == '+')
            {
                builder.Append(lower);
                previousWasSeparator = false;
            }
            else if (!previousWasSeparator && builder.Length > 0)
            {
                builder.Append('-');
                previousWasSeparator = true;
            }
        }

        return builder.ToString().Trim('-');
    }

    private static string ParseAddress(JsonElement estate)
    {
        if (!estate.TryGetProperty("locality", out var locality) || locality.ValueKind != JsonValueKind.Object) return string.Empty;
        var parts = new[] { GetString(locality, "city"), GetString(locality, "citypart"), GetString(locality, "street") };
        return string.Join(" - ", parts.Where(x => !string.IsNullOrWhiteSpace(x)).Distinct(StringComparer.OrdinalIgnoreCase));
    }

    private static Uri? ParseImageUrl(JsonElement estate)
    {
        if (!estate.TryGetProperty("advert_images", out var images) || images.ValueKind != JsonValueKind.Array) return null;
        foreach (var image in images.EnumerateArray())
        {
            if (image.ValueKind == JsonValueKind.String && Uri.TryCreate(image.GetString(), UriKind.Absolute, out var uri)) return uri;
        }
        return null;
    }

    private static string GetString(JsonElement element, string name) => element.TryGetProperty(name, out var node) && node.ValueKind == JsonValueKind.String ? node.GetString() ?? string.Empty : string.Empty;

    private static Layout ParseLayout(string title)
    {
        var result = RegexMatchers.Layout().Match(title);
        return result.Success ? LayoutExtensions.ToLayout(result.Groups[1].Value) : Layout.NotSpecified;
    }

    private static decimal ParseFloorArea(string title)
    {
        var normalizedTitle = title;
        var layout = RegexMatchers.Layout().Match(normalizedTitle);
        if (layout.Success) normalizedTitle = normalizedTitle.Replace(layout.Groups[1].Value, string.Empty);
        var result = RegexMatchers.FloorArea().Match(normalizedTitle);
        if (!result.Success) return decimal.Zero;
        var value = result.Groups.Skip<Group>(1).First(group => group.Success).Value;
        return decimal.TryParse(value, out var floorArea) ? floorArea : decimal.Zero;
    }
}
