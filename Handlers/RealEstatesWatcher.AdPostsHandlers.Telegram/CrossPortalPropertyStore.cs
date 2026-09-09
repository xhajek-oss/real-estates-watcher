using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using RealEstatesWatcher.Models;

namespace RealEstatesWatcher.AdPostsHandlers.Telegram;

internal sealed class CrossPortalPropertyStore
{
    private const int StateVersion = 1;
    private const int DuplicateScoreThreshold = 70;
    private readonly string _stateFilePath;
    private readonly List<StoredProperty> _properties = [];

    private static readonly HashSet<string> NonDiscriminatingLocationTokens = new(StringComparer.Ordinal)
    {
        "pardubice", "i", "cz", "ceska", "republika", "okres", "kraj"
    };

    public CrossPortalPropertyStore(string stateFilePath)
    {
        _stateFilePath = Path.GetFullPath(stateFilePath);
        Load();
    }

    public bool ExistsOnDisk => File.Exists(_stateFilePath);

    public void EstablishBaseline(IEnumerable<RealEstateAdPost> posts)
    {
        _properties.Clear();
        foreach (var post in posts)
        {
            var duplicate = FindDuplicate(post);
            if (duplicate is null)
                _properties.Add(CreateProperty(post, telegramMessageId: null));
            else
                Replace(duplicate, AddSource(duplicate, post));
        }

        Save();
    }

    public StoredProperty? FindDuplicate(RealEstateAdPost candidate)
    {
        var candidateKey = GetListingKey(candidate);

        foreach (var property in _properties)
        {
            if (property.Sources.Any(source => string.Equals(source.ListingKey, candidateKey, StringComparison.OrdinalIgnoreCase)))
                return property;
        }

        return _properties
            .Where(property => property.Sources.All(source =>
                !string.Equals(source.AdsPortalName, candidate.AdsPortalName, StringComparison.OrdinalIgnoreCase)))
            .Select(property => new { Property = property, Score = Score(property, candidate) })
            .Where(result => result.Score >= DuplicateScoreThreshold)
            .OrderByDescending(result => result.Score)
            .Select(result => result.Property)
            .FirstOrDefault();
    }

    public StoredProperty CreateNew(RealEstateAdPost post, string? telegramMessageId) =>
        CreateProperty(post, telegramMessageId);

    public StoredProperty WithAdditionalSource(StoredProperty property, RealEstateAdPost post) =>
        AddSource(property, post);

    public void AddAndSave(StoredProperty property)
    {
        _properties.Add(property);
        Save();
    }

    public void ReplaceAndSave(StoredProperty existing, StoredProperty updated)
    {
        Replace(existing, updated);
        Save();
    }

    public RealEstatePropertyNotification ToNotification(StoredProperty property)
    {
        var primary = new RealEstatePropertySource(property.PrimarySource.AdsPortalName, new Uri(property.PrimarySource.WebUrl));
        var sources = property.Sources
            .Select(source => new RealEstatePropertySource(source.AdsPortalName, new Uri(source.WebUrl)))
            .ToArray();

        return new RealEstatePropertyNotification(
            property.PropertyId,
            property.Title,
            property.Address,
            property.Price,
            property.Currency,
            property.Layout,
            property.FloorArea,
            primary,
            sources);
    }

    private void Replace(StoredProperty existing, StoredProperty updated)
    {
        var index = _properties.FindIndex(property => property.PropertyId == existing.PropertyId);
        if (index >= 0)
            _properties[index] = updated;
    }

    private static StoredProperty CreateProperty(RealEstateAdPost post, string? telegramMessageId)
    {
        var source = CreateSource(post);
        return new StoredProperty(
            CreatePropertyId(GetListingKey(post)),
            post.Title,
            post.Address,
            post.Price,
            post.Currency,
            post.Layout,
            post.FloorArea,
            source,
            [source],
            telegramMessageId);
    }

    private static StoredProperty AddSource(StoredProperty property, RealEstateAdPost post)
    {
        var source = CreateSource(post);
        if (property.Sources.Any(existing => string.Equals(existing.ListingKey, source.ListingKey, StringComparison.OrdinalIgnoreCase)))
            return property;

        var sources = property.Sources.Append(source).ToArray();
        if (GetPortalPriority(source.AdsPortalName) < GetPortalPriority(property.PrimarySource.AdsPortalName))
        {
            return property with
            {
                Title = post.Title,
                Address = post.Address,
                Price = post.Price,
                Currency = post.Currency,
                Layout = post.Layout,
                FloorArea = post.FloorArea,
                PrimarySource = source,
                Sources = sources
            };
        }

        return property with { Sources = sources };
    }

    private static StoredSource CreateSource(RealEstateAdPost post) =>
        new(GetListingKey(post), post.AdsPortalName, post.WebUrl.ToString());

    private static int Score(StoredProperty existing, RealEstateAdPost candidate)
    {
        if (existing.Currency != candidate.Currency)
            return 0;

        var existingAddressTokens = Tokenize(existing.Address);
        var candidateAddressTokens = Tokenize(candidate.Address);
        var exactAddress = Normalize(existing.Address) == Normalize(candidate.Address) &&
                           HasDiscriminatingLocationToken(existingAddressTokens);
        var addressSimilarity = Jaccard(existingAddressTokens, candidateAddressTokens);
        var commonLocationTokens = existingAddressTokens.Intersect(candidateAddressTokens)
            .Count(token => !NonDiscriminatingLocationTokens.Contains(token));
        var titleSimilarity = Jaccard(Tokenize(existing.Title), Tokenize(candidate.Title));

        var strongLocation = exactAddress ||
                             (addressSimilarity >= 0.60 && commonLocationTokens >= 1) ||
                             (addressSimilarity >= 0.40 && commonLocationTokens >= 1 && titleSimilarity >= 0.70);
        if (!strongLocation)
            return 0;

        var layoutsKnown = existing.Layout != Layout.NotSpecified && candidate.Layout != Layout.NotSpecified;
        if (layoutsKnown && existing.Layout != candidate.Layout)
            return 0;

        var areaDifference = AbsoluteAreaDifference(existing.FloorArea, candidate.FloorArea);
        var priceDifference = PercentDifference(existing.Price, candidate.Price);
        if (areaDifference is not <= 5m && priceDifference > 5m)
            return 0;

        var score = exactAddress ? 45 : addressSimilarity >= 0.60 ? 35 : 30;
        if (layoutsKnown)
            score += 20;
        if (areaDifference is <= 2m)
            score += 20;
        else if (areaDifference is <= 5m)
            score += 10;
        if (priceDifference <= 2m)
            score += 15;
        else if (priceDifference <= 5m)
            score += 8;
        if (titleSimilarity >= 0.70)
            score += 10;
        else if (titleSimilarity >= 0.50)
            score += 5;

        return score;
    }

    private void Load()
    {
        if (!File.Exists(_stateFilePath))
            return;

        try
        {
            var state = JsonSerializer.Deserialize<PropertyStoreState>(File.ReadAllText(_stateFilePath))
                ?? throw new JsonException("Property state is empty.");
            if (state.Version != StateVersion)
                return;
            _properties.AddRange(state.Properties ?? []);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            throw new InvalidOperationException($"Unable to load Telegram property state '{_stateFilePath}'.", ex);
        }
    }

    private void Save()
    {
        try
        {
            var directory = Path.GetDirectoryName(_stateFilePath);
            if (!string.IsNullOrWhiteSpace(directory))
                Directory.CreateDirectory(directory);

            var json = JsonSerializer.Serialize(
                new PropertyStoreState(StateVersion, _properties.ToArray()),
                new JsonSerializerOptions { WriteIndented = true });
            var temporaryPath = _stateFilePath + ".tmp";
            File.WriteAllText(temporaryPath, json);
            File.Move(temporaryPath, _stateFilePath, true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new InvalidOperationException($"Unable to save Telegram property state '{_stateFilePath}'.", ex);
        }
    }

    private static string GetListingKey(RealEstateAdPost post)
    {
        if (string.Equals(post.AdsPortalName, "Sreality.cz", StringComparison.OrdinalIgnoreCase))
        {
            var hashId = post.WebUrl.Segments.LastOrDefault()?.Trim('/');
            if (!string.IsNullOrWhiteSpace(hashId))
                return $"sreality:{hashId}";
        }

        return post.WebUrl.GetLeftPart(UriPartial.Path);
    }

    private static string CreatePropertyId(string listingKey)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(listingKey));
        return $"property:{Convert.ToHexString(hash)[..16].ToLowerInvariant()}";
    }

    private static int GetPortalPriority(string portalName) => portalName.ToLowerInvariant() switch
    {
        "sreality.cz" => 0,
        "bezrealitky.cz" => 1,
        "reality.idnes.cz" => 2,
        "bazoš.cz" => 3,
        "realcity.cz" => 4,
        _ => 100
    };

    private static decimal? AbsoluteAreaDifference(decimal? left, decimal? right) =>
        left is null or <= 0 || right is null or <= 0 ? null : Math.Abs(left.Value - right.Value);

    private static decimal PercentDifference(decimal left, decimal right) =>
        left <= 0 || right <= 0 ? decimal.MaxValue : Math.Abs(left - right) / Math.Max(left, right) * 100m;

    private static bool HasDiscriminatingLocationToken(IEnumerable<string> tokens) =>
        tokens.Any(token => !NonDiscriminatingLocationTokens.Contains(token));

    private static HashSet<string> Tokenize(string? value) => Normalize(value)
        .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .ToHashSet(StringComparer.Ordinal);

    private static double Jaccard(HashSet<string> left, HashSet<string> right)
    {
        if (left.Count == 0 || right.Count == 0)
            return 0;
        var union = left.Union(right).Count();
        return union == 0 ? 0 : (double)left.Intersect(right).Count() / union;
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

    internal sealed record StoredSource(string ListingKey, string AdsPortalName, string WebUrl);

    internal sealed record StoredProperty(
        string PropertyId,
        string Title,
        string Address,
        decimal Price,
        Currency Currency,
        Layout Layout,
        decimal? FloorArea,
        StoredSource PrimarySource,
        StoredSource[] Sources,
        string? TelegramMessageId);

    private sealed record PropertyStoreState(int Version, StoredProperty[]? Properties);
}
