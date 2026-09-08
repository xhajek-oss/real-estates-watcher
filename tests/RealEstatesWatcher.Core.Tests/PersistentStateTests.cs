using RealEstatesWatcher.AdPostsFilters.Contracts;
using RealEstatesWatcher.AdPostsHandlers.Contracts;
using RealEstatesWatcher.AdsPortals.Contracts;
using RealEstatesWatcher.Core;
using RealEstatesWatcher.Models;

namespace RealEstatesWatcher.Tests;

public class PersistentStateTests
{
    [Fact]
    public async Task FirstRun_CreatesBaseline_AndSecondRunOnlyNotifiesNewPosts()
    {
        var tempDirectory = Path.Combine(Path.GetTempPath(), $"rew-state-{Guid.NewGuid():N}");
        var stateFilePath = Path.Combine(tempDirectory, "seen-posts.json");

        try
        {
            var firstHandler = new RecordingHandler();
            var firstEngine = CreateEngine(stateFilePath, [CreatePost("first")], firstHandler, "scope-a");

            await firstEngine.StartAsync();
            await firstEngine.StopAsync();

            Assert.True(File.Exists(stateFilePath));
            Assert.Single(firstHandler.InitialBatches);
            Assert.Empty(firstHandler.NewPosts);
            var stateJson = await File.ReadAllTextAsync(stateFilePath);
            Assert.Contains("ConfigurationFingerprint", stateJson);
            Assert.Contains("https://example.test/listing/first", stateJson);

            var secondHandler = new RecordingHandler();
            var secondEngine = CreateEngine(stateFilePath, [CreatePost("first"), CreatePost("second")], secondHandler, "scope-a");

            await secondEngine.StartAsync();
            await secondEngine.StopAsync();

            Assert.Empty(secondHandler.InitialBatches);
            var newPost = Assert.Single(secondHandler.NewPosts);
            Assert.EndsWith("/second", newPost.WebUrl.AbsolutePath);
        }
        finally
        {
            if (Directory.Exists(tempDirectory))
                Directory.Delete(tempDirectory, recursive: true);
        }
    }

    [Fact]
    public async Task ChangedFilterConfiguration_EstablishesNewBaselineWithoutNewPostNotifications()
    {
        var tempDirectory = Path.Combine(Path.GetTempPath(), $"rew-state-{Guid.NewGuid():N}");
        var stateFilePath = Path.Combine(tempDirectory, "seen-posts.json");

        try
        {
            var firstHandler = new RecordingHandler();
            var firstEngine = CreateEngine(stateFilePath, [CreatePost("first")], firstHandler, "pardubice-i");
            await firstEngine.StartAsync();
            await firstEngine.StopAsync();

            var changedHandler = new RecordingHandler();
            var changedEngine = CreateEngine(
                stateFilePath,
                [CreatePost("first"), CreatePost("second"), CreatePost("third")],
                changedHandler,
                "pardubice-ii");

            await changedEngine.StartAsync();
            await changedEngine.StopAsync();

            Assert.Single(changedHandler.InitialBatches);
            Assert.Equal(3, changedHandler.InitialBatches[0].Count);
            Assert.Empty(changedHandler.NewPosts);
        }
        finally
        {
            if (Directory.Exists(tempDirectory))
                Directory.Delete(tempDirectory, recursive: true);
        }
    }

    [Fact]
    public async Task LegacyArrayState_IsRebaselinedWithoutNotifications()
    {
        var tempDirectory = Path.Combine(Path.GetTempPath(), $"rew-state-{Guid.NewGuid():N}");
        var stateFilePath = Path.Combine(tempDirectory, "seen-posts.json");
        Directory.CreateDirectory(tempDirectory);
        await File.WriteAllTextAsync(stateFilePath, "[\"https://example.test/listing/first\"]");

        try
        {
            var handler = new RecordingHandler();
            var engine = CreateEngine(
                stateFilePath,
                [CreatePost("first"), CreatePost("second")],
                handler,
                "scope-a");

            await engine.StartAsync();
            await engine.StopAsync();

            Assert.Single(handler.InitialBatches);
            Assert.Empty(handler.NewPosts);
            Assert.Contains("ConfigurationFingerprint", await File.ReadAllTextAsync(stateFilePath));
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
        RecordingHandler handler,
        string filterScope)
    {
        var engine = new RealEstatesWatchEngine(new WatchEngineSettings
        {
            CheckIntervalMinutes = 1,
            PerformCheckOnStartup = true,
            StateFilePath = stateFilePath
        });

        engine.RegisterAdsPortal(new StubPortal(posts));
        engine.RegisterAdPostsFilter(new ScopeFilter(filterScope));
        engine.RegisterAdPostsHandler(handler);
        return engine;
    }

    private static RealEstateAdPost CreatePost(string suffix) => new()
    {
        AdsPortalName = "Test",
        Title = "Listing",
        Text = string.Empty,
        Price = 1_000_000m,
        Address = "Test street",
        WebUrl = new Uri($"https://example.test/listing/{suffix}"),
        Currency = Currency.CZK,
        Layout = Layout.NotSpecified
    };

    private sealed class StubPortal(IList<RealEstateAdPost> posts) : IRealEstateAdsPortal
    {
        public string Name => "Test";
        public string WatchedUrl => "https://example.test";
        public Task<IList<RealEstateAdPost>> GetLatestRealEstateAdsAsync() => Task.FromResult(posts);
    }

    private sealed class ScopeFilter(string scope) : IRealEstateAdPostsFilter
    {
        public IEnumerable<RealEstateAdPost> Filter(IEnumerable<RealEstateAdPost> adPosts) => adPosts;
        public override string ToString() => scope;
    }

    private sealed class RecordingHandler : IRealEstateAdPostsHandler
    {
        public bool IsEnabled => true;
        public List<IList<RealEstateAdPost>> InitialBatches { get; } = [];
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

        public Task HandleInitialRealEstateAdPostsAsync(IList<RealEstateAdPost> adPosts, CancellationToken cancellationToken = default)
        {
            InitialBatches.Add(adPosts);
            return Task.CompletedTask;
        }
    }
}
