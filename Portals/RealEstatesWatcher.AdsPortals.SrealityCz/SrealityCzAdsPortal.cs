using System.Text.RegularExpressions;
using System.Web;
using HtmlAgilityPack;
using Microsoft.Extensions.Logging;

using RealEstatesWatcher.AdsPortals.Base;
using RealEstatesWatcher.Scrapers.Contracts;
using RealEstatesWatcher.Models;

namespace RealEstatesWatcher.AdsPortals.SrealityCz;

public class SrealityCzAdsPortal(string watchedUrl,
                                 IWebScraper webScraper,
                                 ILogger<SrealityCzAdsPortal>? logger = null) : RealEstateAdsPortalBase(watchedUrl, webScraper, logger)
{
    public override string Name => "Sreality.cz";

    // Sreality no longer exposes the old estate-list-item ids. Listing cards are
    // represented by anchors leading to /detail/... and containing the card text.
    protected override string GetPathToAdsElements() => "//a[starts-with(@href,'/detail/') and .//p]";

    protected override RealEstateAdPost ParseRealEstateAdPost(HtmlNode node) => new()
    {
        AdsPortalName = Name,
        Title = ParseTitle(node),
        Address = ParseAddress(node),
        Text = string.Empty,
        Price = ParsePrice(node),
        Currency = Currency.CZK,
        Layout = ParseLayout(node),
        WebUrl = ParseWebUrl(node, RootHost),
        FloorArea = ParseFloorArea(node),
        ImageUrl = ParseImageUrl(node),
        PriceComment = ParsePriceComment(node)
    };

    private static IReadOnlyList<HtmlNode> GetDescriptionNodes(HtmlNode node) =>
        node.SelectNodes(".//p")?.ToList() ?? [];

    private static string ParseTitle(HtmlNode node)
    {
        var descriptionNodes = GetDescriptionNodes(node);
        return descriptionNodes.Count < 1
            ? string.Empty
            : HttpUtility.HtmlDecode(descriptionNodes[0].InnerText.Trim());
    }

    private static string ParseAddress(HtmlNode node)
    {
        var descriptionNodes = GetDescriptionNodes(node);
        return descriptionNodes.Count < 2
            ? string.Empty
            : HttpUtility.HtmlDecode(descriptionNodes[1].InnerText.Trim());
    }

    private static Uri ParseWebUrl(HtmlNode node, string rootHost)
    {
        var linkNode = node.Name.Equals("a", StringComparison.OrdinalIgnoreCase)
            ? node
            : node.SelectSingleNode(".//a[starts-with(@href,'/detail/')]");
        var path = linkNode?.GetAttributeValue("href", string.Empty) ?? string.Empty;

        if (Uri.TryCreate(path, UriKind.Absolute, out var absoluteUri)
            && (absoluteUri.Scheme == Uri.UriSchemeHttp || absoluteUri.Scheme == Uri.UriSchemeHttps))
        {
            return absoluteUri;
        }

        return new Uri(new Uri(rootHost), path);
    }

    private static Uri? ParseImageUrl(HtmlNode node)
    {
        var imageNodes = node.SelectNodes(".//img");
        if (imageNodes is null || imageNodes.Count < 1)
            return null;

        var selectedImage = imageNodes.Count > 1 ? imageNodes[1] : imageNodes[0];
        var path = selectedImage.GetAttributeValue<string?>("src", null);
        if (string.IsNullOrWhiteSpace(path))
            return null;

        if (Uri.TryCreate(path, UriKind.Absolute, out var absoluteUri)
            && (absoluteUri.Scheme == Uri.UriSchemeHttp || absoluteUri.Scheme == Uri.UriSchemeHttps))
        {
            return absoluteUri;
        }

        return path.StartsWith("//", StringComparison.Ordinal)
            ? new Uri($"https:{path}")
            : null;
    }

    private static decimal ParsePrice(HtmlNode node)
    {
        var descriptionNodes = GetDescriptionNodes(node);
        if (descriptionNodes.Count < 3)
            return decimal.Zero;

        var value = HttpUtility.HtmlDecode(descriptionNodes[2].InnerText);
        value = RegexMatchers.AllNonNumberValues().Replace(value, string.Empty);

        return decimal.TryParse(value, out var price)
            ? price
            : decimal.Zero;
    }

    private static string? ParsePriceComment(HtmlNode node)
    {
        if (ParsePrice(node) is not decimal.Zero)
            return null;

        var descriptionNodes = GetDescriptionNodes(node);
        return descriptionNodes.Count < 3
            ? null
            : HttpUtility.HtmlDecode(descriptionNodes[2].InnerText.Trim());
    }

    private static Layout ParseLayout(HtmlNode node)
    {
        var result = RegexMatchers.Layout().Match(ParseTitle(node));

        return result.Success
            ? LayoutExtensions.ToLayout(result.Groups[1].Value)
            : Layout.NotSpecified;
    }

    private static decimal ParseFloorArea(HtmlNode node)
    {
        var title = ParseTitle(node);

        // workaround for the case like "Prodej bytu 4+1 111 m²" when it parses to "1111 m²"
        var layout = RegexMatchers.Layout().Match(title);
        if (layout.Success)
            title = title.Replace(layout.Groups[1].Value, string.Empty);

        var result = RegexMatchers.FloorArea().Match(title);
        if (!result.Success)
            return decimal.Zero;

        var value = result.Groups.Skip<Group>(1).First(group => group.Success).Value;

        return decimal.TryParse(value, out var floorArea)
            ? floorArea
            : decimal.Zero;
    }
}
