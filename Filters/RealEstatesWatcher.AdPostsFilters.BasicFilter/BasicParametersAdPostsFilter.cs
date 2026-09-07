using System.Globalization;
using System.Text;

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

            var searchableLocation = NormalizeLocation($"{post.Address} {post.Title} {post.Text}");

            if (!string.IsNullOrWhiteSpace(_settings.City) &&
                !searchableLocation.Contains(NormalizeLocation(_settings.City), StringComparison.Ordinal))
                return false;

            if (!string.IsNullOrWhiteSpace(_settings.Street) &&
                !searchableLocation.Contains(NormalizeLocation(_settings.Street), StringComparison.Ordinal))
                return false;

            if (!string.IsNullOrWhiteSpace(_settings.LocationAny))
            {
                var locationAliases = _settings.LocationAny
                    .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .Select(NormalizeLocation)
                    .Where(alias => alias.Length > 0);

                if (!locationAliases.Any(alias => searchableLocation.Contains(alias, StringComparison.Ordinal)))
                    return false;
            }

            return true;
        });
    }

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