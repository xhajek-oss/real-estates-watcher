using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using RealEstatesWatcher.AdPostsHandlers.Contracts;
using RealEstatesWatcher.Models;

namespace RealEstatesWatcher.AdPostsHandlers.Telegram;

public sealed class TelegramAdPostsHandler : IRealEstateAdPostsHandler
{
    private const int MaxRateLimitRetries = 3;
    private readonly string? _botToken;
    private readonly string? _chatId;
    private readonly NumberFormatInfo _numberFormat;
    private readonly HttpClient _httpClient;

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
    }

    public bool IsEnabled => !string.IsNullOrWhiteSpace(_botToken) && !string.IsNullOrWhiteSpace(_chatId);

    public Task HandleNewRealEstateAdPostAsync(RealEstateAdPost adPost, CancellationToken cancellationToken = default) =>
        SendPostAsync(adPost, cancellationToken);

    public async Task HandleNewRealEstatesAdPostsAsync(IList<RealEstateAdPost> adPosts, CancellationToken cancellationToken = default)
    {
        foreach (var adPost in adPosts)
            await SendPostAsync(adPost, cancellationToken).ConfigureAwait(false);
    }

    public Task HandleInitialRealEstateAdPostsAsync(IList<RealEstateAdPost> adPosts, CancellationToken cancellationToken = default) =>
        Task.CompletedTask;

    private async Task SendPostAsync(RealEstateAdPost adPost, CancellationToken cancellationToken)
    {
        if (!IsEnabled)
            return;

        var message = $"🏠 Nový inzerát\n" +
                      $"{adPost.Title}\n" +
                      $"📍 {adPost.Address}\n" +
                      $"💰 {adPost.Price.ToString("N0", _numberFormat)} {adPost.Currency}\n" +
                      $"🌐 {adPost.AdsPortalName}\n" +
                      $"{adPost.WebUrl}";

        var endpoint = $"https://api.telegram.org/bot{_botToken}/sendMessage";
        var payload = new
        {
            chat_id = _chatId,
            text = message,
            disable_web_page_preview = false
        };

        try
        {
            for (var attempt = 0; ; attempt++)
            {
                using var response = await _httpClient.PostAsJsonAsync(endpoint, payload, cancellationToken).ConfigureAwait(false);
                if (response.IsSuccessStatusCode)
                    return;

                var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                if (response.StatusCode == HttpStatusCode.TooManyRequests && attempt < MaxRateLimitRetries)
                {
                    var retryAfterSeconds = ParseRetryAfterSeconds(body);
                    await Task.Delay(TimeSpan.FromSeconds(retryAfterSeconds), cancellationToken).ConfigureAwait(false);
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
            throw new RealEstateAdPostsHandlerException("Unable to send Telegram notification.", ex);
        }
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
