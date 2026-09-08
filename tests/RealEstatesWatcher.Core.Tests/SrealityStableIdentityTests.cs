using RealEstatesWatcher.AdPostsHandlers.Contracts;
using RealEstatesWatcher.AdsPortals.Contracts;
using RealEstatesWatcher.Core;
using RealEstatesWatcher.Models;

namespace RealEstatesWatcher.Tests;

public class SrealityStableIdentityTests
{
    [Fact]
    public async Task ChangedSrealitySlug_WithSameHashId_IsNotReportedAsNew()
    {
        var tempDirectory = Path.Combine(Path.GetTempPath(), $"rew-sreality-state-{Guid.NewGuid():N}");
        var stateFilePath = Path.Combine(tempDirectory, "seen-posts.json");

        try
        {
            var baselineHandler = new RecordingHandler();
            var baselineEngine = CreateEngine(
                stateFilePath,
                [CreateSrealityPost("pardubice-stare-mesto", "123456789")],
                baselineHandler);

            await baselineEngine.StartAsync();
            await baselineEngine.StopAsync();

            Assert.Empty(baselineHandler.NewPosts);
            Assert.Contains("sreality:123456789", await File.ReadAllTextAsync(stateFilePath));

            var changedSlugHandler = new RecordingHandler();
            var changedSlugEngine = CreateEngine(
                stateFilePath,
                [CreateSrealityPost("pardubice-zelene-predmesti", "123456789")],
                changedSlugHandler);

            await changedSlugEngine.StartAsync();
            await changedSlugEngine.StopAsync();

            Assert.Empty(changedSlugHandler.NewPosts);

            var newListingHandler = new RecordingHandler();
            var newListingEngine = CreateEngine(
                stateFilePath,
                [
                    CreateSrealityPost("pardubice-jina-adresa", "123456789"),
                    CreateSrealityPost("pardubice-novy-inzerat", "987654321")
                ],
                newListingHandler);

            await newListingEngine.StartAsync();
            await newListingEngine.StopAsync();

            var newPost = Assert.Single(newListingHandler.NewPosts);
            Assert.EndsWith("/987654321", newPost.WebUrl.AbsolutePath);
        }
        finally
        {
            if (Directory.Exists(tempDirectory))
                Directory.Delete(tempDirectory, recursive: true);
        }
    }

    private static RealEstatesWatchEngine CreateEngine(
        string stateFilePath,
        IList<RealEstateAdPost> posts,
        RecordingHandler handler)
    {
        var engine = new RealEstatesWatchEngine(new WatchEngineSettings
        {
            CheckIntervalMinutes = 1,
            PerformCheckOnStartup = true,
            StateFilePath = stateFilePath
        });

        engine.RegisterAdsPortal(new StubSrealityPortal(posts));
        engine.RegisterAdPostsHandler(handler);
        return engine;
    }

    private static RealEstateAdPost CreateSrealityPost(string localitySlug, string hashId) => new()
    {
        AdsPortalName = "Sreality.cz",
        Title = "Listing",
        Text = string.Empty,
        Price = 1_000_000m,
        Address = "Pardubice",
        WebUrl = new Uri($"https://www.sreality.cz/detail/prodej/byt/3+kk/{localitySlug}/{hashId}"),
        Currency = Currency.CZK,
        Layout = Layout.NotSpecified
    };

    private sealed class StubSrealityPortal(IList<RealEstateAdPost> posts) : IRealEstateAdsPortal
    {
        public string Name => "Sreality.cz";
        public string WatchedUrl => "https://www.sreality.cz/hledani/prodej?region=pardubice";
        public Task<IList<RealEstateAdPost>> GetLatestRealEstateAdsAsync() => Task.FromResult(posts);
    }

    private sealed class RecordingHandler : IRealEstateAdPostsHandler
    {
        public bool IsEnabled => true;
        public List<RealEstateAdPost> NewPosts { get; } = [];

        public Task HandleNewRealEstateAdPostAsync(RealEstateAdPost adPost, CancellationToken cancellationToken = default)
        {
            NewPosts.Add(adPost);
            return Task.CompletedTask;
        }

        public Task HandleNewRealEstatesAdPostsAsync(IList<RealEstateAdPost> adPosts, CancellationToken cancellationToken = default)
        {
            NewPosts.AddRange(adPosts);
            return Task.CompletedTask;
        }

        public Task HandleInitialRealEstateAdPostsAsync(IList<RealEstateAdPost> adPosts, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}
