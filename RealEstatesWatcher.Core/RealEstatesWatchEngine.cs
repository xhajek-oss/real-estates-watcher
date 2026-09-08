using Microsoft.Extensions.Logging;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Timers;
using Timer = System.Timers.Timer;

using RealEstatesWatcher.AdPostsFilters.Contracts;
using RealEstatesWatcher.AdPostsHandlers.Contracts;
using RealEstatesWatcher.AdsPortals.Contracts;
using RealEstatesWatcher.Models;

namespace RealEstatesWatcher.Core;

public class RealEstatesWatchEngine(WatchEngineSettings settings,
                                    ILogger<RealEstatesWatchEngine>? logger = null)
{
    private readonly ISet<IRealEstateAdsPortal> _adsPortals = new HashSet<IRealEstateAdsPortal>();
    private readonly ISet<IRealEstateAdPostsHandler> _handlers = new HashSet<IRealEstateAdPostsHandler>();
    private readonly ISet<IRealEstateAdPostsFilter> _filters = new HashSet<IRealEstateAdPostsFilter>();
    private readonly ISet<string> _seenPostKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    private readonly WatchEngineSettings _settings = settings ?? throw new ArgumentNullException(nameof(settings));

    private Timer? _timer;

    public bool IsRunning { get; private set; }

    public void RegisterAdsPortal(IRealEstateAdsPortal adsPortal)
    {
        ArgumentNullException.ThrowIfNull(adsPortal);

        if (!_settings.EnableMultiplePortalInstances && _adsPortals.Any(portal => portal.Name == adsPortal.Name))
        {
            logger?.LogWarning("Trying to register already registered ads portal named '{PortalName}'.", adsPortal.Name);
            return;
        }

        _adsPortals.Add(adsPortal);

        logger?.LogInformation("Ads portal '{PortalName}' successfully registered.{NewLine}[URL = {PortalUrl}]", adsPortal.Name, Environment.NewLine, adsPortal.WatchedUrl);
    }

    public void RegisterAdPostsHandler(IRealEstateAdPostsHandler adPostsHandler)
    {
        ArgumentNullException.ThrowIfNull(adPostsHandler);

        if (_handlers.Any(handler => handler.Equals(adPostsHandler)))
        {
            logger?.LogWarning("Trying to register already registered ads posts handler of type '{HandlerName}'.", adPostsHandler.GetType().FullName);
            return;
        }

        _handlers.Add(adPostsHandler);

        logger?.LogInformation("Ad posts handler of type '{HandlerName}' successfully registered.", adPostsHandler.GetType().FullName);
    }

    public void RegisterAdPostsFilter(IRealEstateAdPostsFilter adPostsFilter)
    {
        ArgumentNullException.ThrowIfNull(adPostsFilter);

        if (_filters.Any(filter => filter.Equals(adPostsFilter)))
        {
            logger?.LogWarning("Trying to register already registered ads posts filter of type '{FilterName}'.", adPostsFilter.GetType().FullName);
            return;
        }

        _filters.Add(adPostsFilter);

        logger?.LogInformation("Ad posts filter of type '{FilterName}' successfully registered.", adPostsFilter.GetType().FullName);
    }

    public async Task StartAsync()
    {
        if (IsRunning)
            throw new RealEstatesWatchEngineException("Watcher is already running.");
        if (_adsPortals.Count is 0)
            throw new RealEstatesWatchEngineException("No Ads portals registered for watching.");
        if (_handlers.Count is 0)
            throw new RealEstatesWatchEngineException("No handlers registered for processing Ad posts.");
        if (_settings.CheckIntervalMinutes < 1)
            throw new RealEstatesWatchEngineException("Invalid check interval value: must be at least 1 minute.");
        if (_settings.CheckIntervalMinutes >= int.MaxValue)
            throw new RealEstatesWatchEngineException("Invalid check interval value: too big number.");

        if (_settings.StartCheckAtSpecificTime is not null && !_settings.PerformCheckOnStartup)
        {
            var checkInterval = CalculateIntervalForNextCheckTime(out var nextCheck);

            logger?.LogInformation(
                "Real estates Watcher has been started without initial ads check with periodic " +
                "checking interval of {CheckInterval} minute(s), next check at {NextCheckTime}.",
                _settings.CheckIntervalMinutes, nextCheck);

            StartTimer(checkInterval);
            IsRunning = true;
            return;
        }

        try
        {
            var persistentStateLoaded = LoadPersistentState();
            var posts = await GetCurrentAdsPortalsSnapshot().ConfigureAwait(false);

            if (posts.Count is 0)
                logger?.LogDebug("No initial posts downloaded.");
            else
                logger?.LogDebug("Downloaded initial {PostsCount} post(s) from {PortalsCount} portal(s).", posts.Count, _adsPortals.Count);

            posts = _filters.Aggregate(posts, (current, filter) => filter.Filter(current).ToList());

            if (posts.Count is 0)
                logger?.LogDebug("All downloaded posts have been filtered based on set filters.");
            else
                logger?.LogDebug("Filtered {Count} post(s) from all downloaded posts.", posts.Count);

            if (persistentStateLoaded)
            {
                var newPosts = posts.Where(TryMarkPostAsSeen).ToList();

                switch (newPosts.Count)
                {
                    case 1:
                        await NotifyHandlers(newPosts[0]).ConfigureAwait(false);
                        break;
                    case > 1:
                        await NotifyHandlers(newPosts).ConfigureAwait(false);
                        break;
                }

                if (newPosts.Count > 0)
                    SavePersistentState();

                logger?.LogInformation("Initial check against persistent state finished - found {Count} new ads.", newPosts.Count);
            }
            else
            {
                foreach (var post in posts)
                    TryMarkPostAsSeen(post);

                if (posts.Count > 0)
                {
                    foreach (var handler in _handlers)
                    {
                        if (!handler.IsEnabled)
                            continue;

                        await handler.HandleInitialRealEstateAdPostsAsync(posts).ConfigureAwait(false);
                    }

                    logger?.LogDebug("Handlers notified about initial posts.");
                }

                SavePersistentState();
                logger?.LogInformation("Established baseline for the current watch configuration with {Count} ads; no new-ad notifications were sent.", posts.Count);
            }
        }
        catch (RealEstateAdsPortalException reapEx)
        {
            throw new RealEstatesWatchEngineException($"Error loading initial Ad posts: {reapEx.Message}", reapEx);
        }
        catch (RealEstateAdPostsHandlerException reaphEx)
        {
            throw new RealEstatesWatchEngineException($"Error notifying Ad post handlers: {reaphEx.Message}", reaphEx);
        }

        StartTimer(CalculateIntervalForNextCheckTime(out var nextCheckTime));
        IsRunning = true;

        logger?.LogInformation(
            "Real estates Watcher has been started with initial ads check and periodic checking interval of " +
            "{CheckInterval} minute(s), next check scheduled at {NextCheckTime}.",
            _settings.CheckIntervalMinutes, nextCheckTime);
    }

    public Task StopAsync()
    {
        if (!IsRunning)
            throw new RealEstatesWatchEngineException("Watcher is not running.");

        StopAndDisposeTimer();
        IsRunning = false;

        logger?.LogInformation("Real estates Watcher has been stopped.");
        logger?.LogInformation("------------------------------------------------");

        return Task.CompletedTask;
    }

    private void StartTimer(double millisecondsInterval)
    {
        if (_timer is not null)
            StopAndDisposeTimer();

        _timer = new Timer
        {
            AutoReset = false,
            Interval = millisecondsInterval
        };
        _timer.Elapsed += Timer_OnElapsed;
        _timer.Start();
    }

    private void StopAndDisposeTimer()
    {
        if (_timer is null)
            return;

        _timer.Stop();
        _timer.Dispose();
        _timer.Elapsed -= Timer_OnElapsed;
        _timer = null;
    }

    private double CalculateIntervalForNextCheckTime(out DateTime nextCheckTime)
    {
        var now = DateTime.UtcNow;

        if (_settings.StartCheckAtSpecificTime is null)
        {
            nextCheckTime = now.AddMinutes(_settings.CheckIntervalMinutes);
            return TimeSpan.FromMinutes(_settings.CheckIntervalMinutes).TotalMilliseconds;
        }

        nextCheckTime = new DateTime(now.Year, now.Month, now.Day,
            _settings.StartCheckAtSpecificTime.Value.Hour,
            _settings.StartCheckAtSpecificTime.Value.Minute,
            _settings.StartCheckAtSpecificTime.Value.Second,
            DateTimeKind.Utc);

        while (nextCheckTime < now)
            nextCheckTime = nextCheckTime.AddMinutes(_settings.CheckIntervalMinutes);

        while ((nextCheckTime - now).TotalMinutes > _settings.CheckIntervalMinutes)
            nextCheckTime = nextCheckTime.AddMinutes(-_settings.CheckIntervalMinutes);

        return (nextCheckTime - now).TotalMilliseconds;
    }

    private async void Timer_OnElapsed(object? sender, ElapsedEventArgs e)
    {
        logger?.LogInformation("Periodic check started.");

        try
        {
            var allPosts = await GetCurrentAdsPortalsSnapshot().ConfigureAwait(false);
            allPosts = _filters.Aggregate(allPosts, (current, filter) => filter.Filter(current).ToList());
            var newPosts = allPosts.Where(TryMarkPostAsSeen).ToList();

            switch (newPosts.Count)
            {
                case 1:
                    await NotifyHandlers(newPosts[0]).ConfigureAwait(false);
                    break;
                case > 1:
                    await NotifyHandlers(newPosts).ConfigureAwait(false);
                    break;
            }

            if (newPosts.Count > 0)
                SavePersistentState();

            logger?.LogInformation("Periodic check finished - found {Count} new ads.", newPosts.Count);
        }
        finally
        {
            StopAndDisposeTimer();
            StartTimer(CalculateIntervalForNextCheckTime(out var nextCheckTime));
            logger?.LogDebug("Next periodic check is scheduled at {NextCheckTime}.", nextCheckTime);
        }
    }

    private async Task NotifyHandlers(RealEstateAdPost adPost)
    {
        foreach (var handler in _handlers)
        {
            if (!handler.IsEnabled)
                continue;

            try
            {
                await handler.HandleNewRealEstateAdPostAsync(adPost).ConfigureAwait(false);
            }
            catch (RealEstateAdPostsHandlerException reaphEx)
            {
                logger?.LogError(reaphEx, "Error notifying Ad posts Handler '{HandlerName}': {ExceptionMessage}", handler.GetType().FullName, reaphEx.Message);
            }
        }
    }

    private async Task NotifyHandlers(IList<RealEstateAdPost> adPosts)
    {
        foreach (var handler in _handlers)
        {
            if (!handler.IsEnabled)
                continue;

            try
            {
                await handler.HandleNewRealEstatesAdPostsAsync(adPosts).ConfigureAwait(false);
            }
            catch (RealEstateAdPostsHandlerException reaphEx)
            {
                logger?.LogError(reaphEx, "Error notifying Ad posts Handler '{HandlerName}': {ExceptionMessage}", handler.GetType().FullName, reaphEx.Message);
            }
        }
    }

    private async Task<IList<RealEstateAdPost>> GetCurrentAdsPortalsSnapshot()
    {
        var posts = new List<RealEstateAdPost>();

        foreach (var adsPortal in _adsPortals)
        {
            try
            {
                posts.AddRange(await adsPortal.GetLatestRealEstateAdsAsync().ConfigureAwait(false));
            }
            catch (RealEstateAdsPortalException reapEx)
            {
                logger?.LogError(reapEx, "Error downloading new Ad posts from '{PortalName}': {ExceptionMessage}", adsPortal.Name, reapEx.Message);
            }
        }

        return posts;
    }

    private bool TryMarkPostAsSeen(RealEstateAdPost post) => _seenPostKeys.Add(GetPostKey(post));

    private static string GetPostKey(RealEstateAdPost post) => post.WebUrl.GetLeftPart(UriPartial.Path);

    private bool LoadPersistentState()
    {
        if (string.IsNullOrWhiteSpace(_settings.StateFilePath))
            return false;

        var stateFilePath = Path.GetFullPath(_settings.StateFilePath);
        if (!File.Exists(stateFilePath))
            return false;

        try
        {
            var json = File.ReadAllText(stateFilePath);
            using var document = JsonDocument.Parse(json);

            if (document.RootElement.ValueKind == JsonValueKind.Array)
            {
                logger?.LogInformation("Legacy persistent state detected at '{StateFilePath}'. Establishing a fresh baseline without notifications.", stateFilePath);
                return false;
            }

            var state = JsonSerializer.Deserialize<PersistentState>(json)
                ?? throw new JsonException("Persistent state is empty.");
            var currentFingerprint = GetConfigurationFingerprint();

            if (!string.Equals(state.ConfigurationFingerprint, currentFingerprint, StringComparison.Ordinal))
            {
                logger?.LogInformation("Watch configuration changed. Establishing a fresh baseline without notifications.");
                return false;
            }

            foreach (var key in state.SeenPostKeys.Where(key => !string.IsNullOrWhiteSpace(key)))
                _seenPostKeys.Add(key);

            logger?.LogInformation("Loaded {Count} seen ad key(s) from persistent state '{StateFilePath}'.", _seenPostKeys.Count, stateFilePath);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            throw new RealEstatesWatchEngineException($"Unable to load persistent state from '{stateFilePath}': {ex.Message}", ex);
        }
    }

    private void SavePersistentState()
    {
        if (string.IsNullOrWhiteSpace(_settings.StateFilePath))
            return;

        var stateFilePath = Path.GetFullPath(_settings.StateFilePath);

        try
        {
            var directory = Path.GetDirectoryName(stateFilePath);
            if (!string.IsNullOrEmpty(directory))
                Directory.CreateDirectory(directory);

            var state = new PersistentState(
                GetConfigurationFingerprint(),
                _seenPostKeys.OrderBy(key => key).ToArray());
            var json = JsonSerializer.Serialize(state, new JsonSerializerOptions { WriteIndented = true });

            var temporaryFilePath = stateFilePath + ".tmp";
            File.WriteAllText(temporaryFilePath, json);
            File.Move(temporaryFilePath, stateFilePath, true);

            logger?.LogDebug("Saved {Count} seen ad key(s) to persistent state '{StateFilePath}'.", _seenPostKeys.Count, stateFilePath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new RealEstatesWatchEngineException($"Unable to save persistent state from '{stateFilePath}': {ex.Message}", ex);
        }
    }

    private string GetConfigurationFingerprint()
    {
        var components = _adsPortals
            .OrderBy(portal => portal.Name, StringComparer.Ordinal)
            .ThenBy(portal => portal.WatchedUrl, StringComparer.Ordinal)
            .Select(portal => $"portal:{portal.Name}|{portal.WatchedUrl}")
            .Concat(_filters
                .OrderBy(filter => filter.GetType().FullName, StringComparer.Ordinal)
                .Select(filter => $"filter:{filter.GetType().FullName}|{filter}"));

        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('\n', components)));
        return Convert.ToHexString(bytes);
    }

    private sealed record PersistentState(string ConfigurationFingerprint, string[] SeenPostKeys);
}
