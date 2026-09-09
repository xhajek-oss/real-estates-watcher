using System.Globalization;
using System.Text;
using RealEstatesWatcher.Models;

namespace RealEstatesWatcher.Core;

public static class CrossPortalPropertyMatcher
{
    public const int DuplicateScoreThreshold = 70;

    private static readonly HashSet<string> NonDiscriminatingLocationTokens = new(StringComparer.Ordinal)
    {
        "pardubice", "i", "cz", "ceska", "republika", "okres", "kraj"
    };

    public static PropertyMatchResult Compare(
        string existingTitle,
        string existingAddress,
        Layout existingLayout,
        decimal? existingFloorArea,
        decimal existingPrice,
        Currency existingCurrency,
        RealEstateAdPost candidate)
    {
        if (existingCurrency != candidate.Currency)
            return PropertyMatchResult.NoMatch;

        var existingAddressTokens = Tokenize(existingAddress);
        var candidateAddressTokens = Tokenize(candidate.Address);
        var exactAddress = Normalize(existingAddress) == Normalize(candidate.Address) &&
                           HasDiscriminatingLocationToken(existingAddressTokens);
        var addressSimilarity = Jaccard(existingAddressTokens, candidateAddressTokens);
        var commonDiscriminatingLocationTokens = existingAddressTokens
            .Intersect(candidateAddressTokens)
            .Count(token => !NonDiscriminatingLocationTokens.Contains(token));

        var titleSimilarity = Jaccard(Tokenize(existingTitle), Tokenize(candidate.Title));
        var strongLocation = exactAddress ||
                             (addressSimilarity >= 0.60 && commonDiscriminatingLocationTokens >= 1) ||
                             (addressSimilarity >= 0.40 && commonDiscriminatingLocationTokens >= 1 && titleSimilarity >= 0.70);

        if (!strongLocation)
            return PropertyMatchResult.NoMatch;

        var layoutComparable = existingLayout != Layout.NotSpecified && candidate.Layout != Layout.NotSpecified;
        if (layoutComparable && existingLayout != candidate.Layout)
            return PropertyMatchResult.NoMatch;

        var areaDifference = GetAbsoluteDifference(existingFloorArea, candidate.FloorArea);
        var areaClose = areaDifference is <= 5m;
        var priceDifferencePercent = GetPercentDifference(existingPrice, candidate.Price);
        var priceClose = priceDifferencePercent <= 5m;

        if (!areaClose && !priceClose)
            return PropertyMatchResult.NoMatch;

        var score = exactAddress ? 45 : addressSimilarity >= 0.60 ? 35 : 30;

        if (layoutComparable)
            score += 20;

        if (areaDifference is <= 2m)
            score += 20;
        else if (areaDifference is <= 5m)
            score += 10;

        if (priceDifferencePercent <= 2m)
            score += 15;
        else if (priceDifferencePercent <= 5m)
            score += 8;

        if (titleSimilarity >= 0.70)
            score += 10;
        else if (titleSimilarity >= 0.50)
            score += 5;

        return new PropertyMatchResult(score >= DuplicateScoreThreshold, score);
    }

    private static decimal? GetAbsoluteDifference(decimal? left, decimal? right)
    {
        if (left is null or <= 0 || right is null or <= 0)
            return null;

        return Math.Abs(left.Value - right.Value);
    }

    private static decimal GetPercentDifference(decimal left, decimal right)
    {
        if (left <= 0 || right <= 0)
            return decimal.MaxValue;

        return Math.Abs(left - right) / Math.Max(left, right) * 100m;
    }

    private static bool HasDiscriminatingLocationToken(IEnumerable<string> tokens) =>
        tokens.Any(token => !NonDiscriminatingLocationTokens.Contains(token));

    private static HashSet<string> Tokenize(string? value) =>
        Normalize(value)
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToHashSet(StringComparer.Ordinal);

    private static double Jaccard(HashSet<string> left, HashSet<string> right)
    {
        if (left.Count == 0 || right.Count == 0)
            return 0;

        var intersection = left.Intersect(right).Count();
        var union = left.Union(right).Count();
        return union == 0 ? 0 : (double)intersection / union;
    }

    private static string Normalize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return string.Empty;

        var decomposed = value.Normalize(NormalizationForm.FormD);
        var builder = new StringBuilder(decomposed.Length);

        foreach (var character in decomposed)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(character) == UnicodeCategory.NonSpacingMark)
                continue;

            builder.Append(char.IsLetterOrDigit(character) ? char.ToLowerInvariant(character) : ' ');
        }

        return string.Join(' ', builder.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries));
    }
}

public readonly record struct PropertyMatchResult(bool IsDuplicate, int Score)
{
    public static PropertyMatchResult NoMatch => new(false, 0);
}
