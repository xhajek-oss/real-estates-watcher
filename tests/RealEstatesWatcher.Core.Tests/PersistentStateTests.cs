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
            var firstEngine = CreateEngine(stateFilePath, [CreatePost("first")], firstHandler);

            await firstEngine.StartAsync();
            await firstEngine.StopAsync();

            Assert.True(File.Exists(stateFilePath));
            Assert.Single(firstHandler.InitialBatches);
            Assert.Empty(firstHandler.NewPosts);
            Assert.Contains("https://example.test/listing/first", await File.ReadAllTextAsync(stateFilePath));

            var secondHandler = new RecordingHandler();
            var secondEngine = CreateEngine(stateFilePath, [CreatePost("first"), CreatePost("second")], secondHandler);

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
    public async Task ExistingState_UsesUrlPathAsStableListingKey()
    {
        var tempDirectory = Path.Combine(Path.GetTempPath(), $"rew-state-{Guid.NewGuid():N}");
        var stateFilePath = Path.Combine(tempDirectory, "seen-posts.json");
        Directory.CreateDirectory(tempDirectory);
        await File.WriteAllTextAsync(stateFilePath, "[\"https://example.test/listing/first\"]");

        try
        {
            var handler = new RecordingHandler();
            var engine = CreateEngine(stateFilePath, [CreatePost("first?tracking=changed")], handler);

            await engine.StartAsync();
            await engine.StopAsync();

            Assert.Empty(handler.InitialBatches);
            Assert.Empty(handler.NewPosts);
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

        engine.RegisterAdsPortal(new StubPortal(posts));
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
