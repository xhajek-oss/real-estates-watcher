using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

using Microsoft.Extensions.Logging;

using RealEstatesWatcher.AdPostsFilters.Contracts;
using RealEstatesWatcher.Models;

namespace RealEstatesWatcher.AdPostsFilters.BasicFilter;

public class BasicParametersAdPostsFilter(BasicParametersAdPostsFilterSettings settings,
                                          ILogger<BasicParametersAdPostsFilter>? logger = default) : IRealEstateAdPostsFilter
{
    private readonly ILogger<BasicParametersAdPostsFilter>? _logger = logger;
    private readonly BasicParametersAdPostsFilterSettings _settings = settings ?? throw new ArgumentNullException(nameof(settings));

    public IEnumerable<RealEstateAdPost> Filter(IEnumerable<RealEstateAdPost> adPosts)
    {
        ArgumentNullException.ThrowIfNull(adPosts);

        return adPosts.Where(post =>
        {
            if (_settings.MinPrice is not null && post.Price != decimal.Zero && post.Price < _settings.MinPrice)
                return false;
            if (_settings.MaxPrice is not null && post.Price != decimal.Zero && post.Price > _settings.MaxPrice)
                return false;
            if (_settings.Layouts.Count > 0 && post.Layout != Layout.NotSpecified &&
                !_settings.Layouts.Contains(post.Layout))
                return false;
            if (_settings.MinFloorArea is not null && post.FloorArea != decimal.Zero &&
                post.FloorArea < _settings.MinFloorArea)
                return false;
            if (_settings.MaxFloorArea is not null && post.FloorArea != decimal.Zero &&
                post.FloorArea > _settings.MaxFloorArea)
                return false;

            // Location filters rely on structured-ish location fields only. Free-form ad text
            // can mention unrelated places and must not be accepted as location evidence.
            var searchableLocation = NormalizeLocation($"{post.Address} {post.Title}");

            if (!string.IsNullOrWhiteSpace(_settings.City) &&
                !ContainsLocationPhrase(searchableLocation, NormalizeLocation(_settings.City)))
                return false;

            if (!string.IsNullOrWhiteSpace(_settings.Street) &&
                !ContainsLocationPhrase(searchableLocation, NormalizeLocation(_settings.Street)))
                return false;

            if (!string.IsNullOrWhiteSpace(_settings.LocationAny))
            {
                var locationAliases = _settings.LocationAny
                    .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .Select(NormalizeLocation)
                    .Where(alias => alias.Length > 0);

                if (!locationAliases.Any(alias => ContainsLocationPhrase(searchableLocation, alias)))
                    return false;
            }

            // Postal code is a guard rail, not positive proof of location: Czech postal codes can
            // cover more than one municipality. If a listing exposes a postal code, reject it when
            // it is outside the configured allow-list; listings without a postal code still depend
            // on the city/location evidence above.
            if (!string.IsNullOrWhiteSpace(_settings.PostalCodes))
            {
                var allowedPostalCodes = _settings.PostalCodes
                    .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .Select(NormalizePostalCode)
                    .Where(code => code.Length == 5)
                    .ToHashSet(StringComparer.Ordinal);

                var postalCodes = ExtractPostalCodes($"{post.Address} {post.Title}");
                if (postalCodes.Count > 0 && !postalCodes.Any(allowedPostalCodes.Contains))
                    return false;
            }

            return true;
        });
    }

    public override string ToString()
    {
        var layouts = string.Join(',', _settings.Layouts.OrderBy(layout => layout));
        return string.Join('|',
            _settings.MinPrice?.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
            _settings.MaxPrice?.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
            layouts,
            _settings.MinFloorArea?.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
            _settings.MaxFloorArea?.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
            NormalizeLocation(_settings.City ?? string.Empty),
            NormalizeLocation(_settings.Street ?? string.Empty),
            NormalizeLocation(_settings.LocationAny ?? string.Empty),
            NormalizeLocation(_settings.PostalCodes ?? string.Empty));
    }

    private static bool ContainsLocationPhrase(string searchableLocation, string phrase)
    {
        if (phrase.Length == 0)
            return false;

        // Normalization makes words space-separated. Adding spaces around both values prevents
        // aliases such as "Pardubice I" from matching "Pardubice ID" in a title suffix.
        return $" {searchableLocation} ".Contains($" {phrase} ", StringComparison.Ordinal);
    }

    private static IReadOnlyCollection<string> ExtractPostalCodes(string value) =>
        Regex.Matches(value, @"(?<!\d)(\d{3})\s?(\d{2})(?!\d)")
            .Select(match => match.Groups[1].Value + match.Groups[2].Value)
            .Distinct(StringComparer.Ordinal)
            .ToArray();

    private static string NormalizePostalCode(string value) =>
        new(value.Where(char.IsDigit).ToArray());

    private static string NormalizeLocation(string value)
    {
        var normalized = value.Normalize(NormalizationForm.FormD);
        var builder = new StringBuilder(normalized.Length);

        foreach (var character in normalized)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(character) == UnicodeCategory.NonSpacingMark)
                continue;

            if (char.IsLetterOrDigit(character))
                builder.Append(char.ToLowerInvariant(character));
            else
                builder.Append(' ');
        }

        return string.Join(' ', builder.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries));
    }
}