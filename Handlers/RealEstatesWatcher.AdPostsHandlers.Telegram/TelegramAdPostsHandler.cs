using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using RealEstatesWatcher.AdPostsHandlers.Contracts;
using RealEstatesWatcher.Models;

namespace RealEstatesWatcher.AdPostsHandlers.Telegram;

public sealed class TelegramAdPostsHandler : IRealEstateAdPostsHandler, IUpdatableRealEstatePropertyHandler
{
    private const int MaxRateLimitRetries = 3;
    private readonly string? _botToken;
    private readonly string? _chatId;
    private readonly NumberFormatInfo _numberFormat;
    private readonly HttpClient _httpClient;
    private readonly CrossPortalPropertyStore _propertyStore;

    public TelegramAdPostsHandler(
        string? botToken,
        string? chatId,
        NumberFormatInfo numberFormat,
        HttpClient? httpClient = null)
    {
        _botToken = botToken;
        _chatId = chatId;
        _numberFormat = numberFormat ?? throw new ArgumentNullException(nameof(numberFormat));
        _httpClient = httpClient ?? new HttpClient();
        _propertyStore = new CrossPortalPropertyStore(ResolvePropertyStatePath());
    }

    public bool IsEnabled => !string.IsNullOrWhiteSpace(_botToken) && !string.IsNullOrWhiteSpace(_chatId);

    public string HandlerStateKey => "telegram";

    public async Task HandleNewRealEstateAdPostAsync(RealEstateAdPost adPost, CancellationToken cancellationToken = default)
    {
        if (!IsEnabled)
            return;

        try
        {
            var existing = _propertyStore.FindDuplicate(adPost);
            if (existing is null)
            {
                var newProperty = _propertyStore.CreateNew(adPost, telegramMessageId: null);
                var messageId = await HandleNewRealEstatePropertyAsync(
                    _propertyStore.ToNotification(newProperty), cancellationToken).ConfigureAwait(false);

                _propertyStore.AddAndSave(newProperty with { TelegramMessageId = messageId });
                return;
            }

            var updated = _propertyStore.WithAdditionalSource(existing, adPost);
            if (updated.Sources.Length == existing.Sources.Length)
                return;

            if (!string.IsNullOrWhiteSpace(existing.TelegramMessageId))
            {
                await UpdateRealEstatePropertyAsync(
                    _propertyStore.ToNotification(updated),
                    existing.TelegramMessageId,
                    cancellationToken).ConfigureAwait(false);
            }

            _propertyStore.ReplaceAndSave(existing, updated);
        }
        catch (RealEstateAdPostsHandlerException ex)
        {
            throw new InvalidOperationException(
                "Telegram delivery failed. The watcher run is intentionally failed so the listing can be retried on the next run.", ex);
        }
    }

    public async Task HandleNewRealEstatesAdPostsAsync(IList<RealEstateAdPost> adPosts, CancellationToken cancellationToken = default)
    {
        foreach (var adPost in adPosts)
            await HandleNewRealEstateAdPostAsync(adPost, cancellationToken).ConfigureAwait(false);
    }

    public Task HandleInitialRealEstateAdPostsAsync(IList<RealEstateAdPost> adPosts, CancellationToken cancellationToken = default)
    {
        if (IsEnabled)
            _propertyStore.EstablishBaseline(adPosts);

        return Task.CompletedTask;
    }

    public async Task<string?> HandleNewRealEstatePropertyAsync(
        RealEstatePropertyNotification property,
        CancellationToken cancellationToken = default)
    {
        if (!IsEnabled)
            return null;

        var endpoint = $"https://api.telegram.org/bot{_botToken}/sendMessage";
        var payload = new
        {
            chat_id = _chatId,
            text = BuildPropertyMessage(property),
            parse_mode = "HTML",
            link_preview_options = new
            {
                is_disabled = false,
                url = property.PrimarySource.WebUrl.ToString()
            }
        };

        var body = await PostWithRetryAsync(endpoint, payload, cancellationToken).ConfigureAwait(false);
        try
        {
            using var document = JsonDocument.Parse(body);
            if (document.RootElement.TryGetProperty("result", out var result) &&
                result.TryGetProperty("message_id", out var messageId) &&
                messageId.TryGetInt64(out var id))
            {
                return id.ToString(CultureInfo.InvariantCulture);
            }
        }
        catch (JsonException ex)
        {
            throw new RealEstateAdPostsHandlerException("Telegram sendMessage returned invalid JSON.", ex);
        }

        throw new RealEstateAdPostsHandlerException("Telegram sendMessage response did not contain result.message_id.");
    }

    public async Task UpdateRealEstatePropertyAsync(
        RealEstatePropertyNotification property,
        string externalMessageId,
        CancellationToken cancellationToken = default)
    {
        if (!IsEnabled)
            return;

        if (!long.TryParse(externalMessageId, NumberStyles.None, CultureInfo.InvariantCulture, out var messageId))
            throw new RealEstateAdPostsHandlerException($"Invalid Telegram message id '{externalMessageId}'.");

        var endpoint = $"https://api.telegram.org/bot{_botToken}/editMessageText";
        var payload = new
        {
            chat_id = _chatId,
            message_id = messageId,
            text = BuildPropertyMessage(property),
            parse_mode = "HTML",
            link_preview_options = new
            {
                is_disabled = false,
                url = property.PrimarySource.WebUrl.ToString()
            }
        };

        await PostWithRetryAsync(endpoint, payload, cancellationToken, acceptMessageNotModified: true).ConfigureAwait(false);
    }

    private string BuildPropertyMessage(RealEstatePropertyNotification property)
    {
        var primary = property.PrimarySource;
        var alternatives = property.Sources
            .Where(source => !Equals(source, primary))
            .OrderBy(source => source.AdsPortalName, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var priceText = property.Price > 0
            ? $"{property.Price.ToString("N0", _numberFormat)} {Html(property.Currency.ToString())}"
            : "Cena na vyžádání";

        var lines = new List<string>
        {
            "🏠 <b>Nový inzerát</b>",
            Html(property.Title),
            $"📍 {Html(property.Address)}",
            $"💰 <b>{priceText}</b>",
            $"🌐 {Link(primary)}"
        };

        if (alternatives.Length > 0)
        {
            lines.Add(string.Empty);
            lines.Add("<b>Také na:</b>");
            lines.AddRange(alternatives.Select(source => $"• {Link(source)}"));
        }

        return string.Join('\n', lines);
    }

    private static string Link(RealEstatePropertySource source) =>
        $"<a href=\"{Html(source.WebUrl.ToString())}\">{Html(source.AdsPortalName)}</a>";

    private static string Html(string? value) => WebUtility.HtmlEncode(value ?? string.Empty);

    private async Task<string> PostWithRetryAsync(
        string endpoint,
        object payload,
        CancellationToken cancellationToken,
        bool acceptMessageNotModified = false)
    {
        try
        {
            for (var attempt = 0; ; attempt++)
            {
                using var response = await _httpClient.PostAsJsonAsync(endpoint, payload, cancellationToken).ConfigureAwait(false);
                var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

                if (response.IsSuccessStatusCode)
                    return body;

                if (acceptMessageNotModified && response.StatusCode == HttpStatusCode.BadRequest &&
                    body.Contains("message is not modified", StringComparison.OrdinalIgnoreCase))
                {
                    return body;
                }

                if (response.StatusCode == HttpStatusCode.TooManyRequests && attempt < MaxRateLimitRetries)
                {
                    await Task.Delay(TimeSpan.FromSeconds(ParseRetryAfterSeconds(body)), cancellationToken).ConfigureAwait(false);
                    continue;
                }

                throw new RealEstateAdPostsHandlerException(
                    $"Telegram API returned {(int)response.StatusCode} ({response.ReasonPhrase}): {body}");
            }
        }
        catch (RealEstateAdPostsHandlerException)
        {
            throw;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            throw new RealEstateAdPostsHandlerException("Unable to communicate with Telegram.", ex);
        }
    }

    private static string ResolvePropertyStatePath()
    {
        var configured = Environment.GetEnvironmentVariable("REW_PROPERTY_STATE_FILE_PATH");
        if (!string.IsNullOrWhiteSpace(configured))
            return configured;

        var watcherState = Environment.GetEnvironmentVariable("REW_STATE_FILE_PATH");
        if (!string.IsNullOrWhiteSpace(watcherState))
        {
            var directory = Path.GetDirectoryName(Path.GetFullPath(watcherState)) ?? Directory.GetCurrentDirectory();
            return Path.Combine(directory, "property-groups.json");
        }

        return Path.Combine(Directory.GetCurrentDirectory(), "state", "property-groups.json");
    }

    private static int ParseRetryAfterSeconds(string responseBody)
    {
        try
        {
            using var document = JsonDocument.Parse(responseBody);
            if (document.RootElement.TryGetProperty("parameters", out var parameters) &&
                parameters.TryGetProperty("retry_after", out var retryAfter) &&
                retryAfter.TryGetInt32(out var seconds))
            {
                return Math.Clamp(seconds + 1, 1, 120);
            }
        }
        catch (JsonException)
        {
        }

        return 5;
    }
}
