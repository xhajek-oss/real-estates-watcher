using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Text;
using RealEstatesWatcher.AdPostsFilters.BasicFilter;
using RealEstatesWatcher.AdPostsHandlers.File;
using RealEstatesWatcher.AdsPortals.SrealityCz;
using RealEstatesWatcher.Core;
using RealEstatesWatcher.Models;
using RealEstatesWatcher.Scrapers;
using RealEstatesWatcher.Scrapers.Contracts;

namespace RealEstatesWatcher.Integration.Tests;

public sealed class WatcherPipelineIntegrationTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(),
        $"real-estates-watcher-integration-{Guid.NewGuid():N}");

    public WatcherPipelineIntegrationTests() => Directory.CreateDirectory(_directory);

    [Fact]
    [Trait("Category", "Integration")]
    public async Task StartupCheck_ParsesFiltersAndWritesListingsThroughRealPipeline()
    {
        const string json = """
            {
              "results": [
                {
                  "hash_id": 101,
                  "advert_name": "Prodej bytu 2+kk 55 m²",
                  "category_type_cb": { "value": 1, "name": "Prodej" },
                  "category_main_cb": { "value": 1, "name": "Byty" },
                  "category_sub_cb": { "value": 47, "name": "2+kk" },
                  "locality": {
                    "city": "Praha",
                    "citypart": "Praha 2",
                    "city_seo_name": "praha",
                    "citypart_seo_name": "praha-2"
                  },
                  "price_czk": 5500000
                },
                {
                  "hash_id": 202,
                  "advert_name": "Prodej bytu 4+1 120 m²",
                  "category_type_cb": { "value": 1, "name": "Prodej" },
                  "category_main_cb": { "value": 1, "name": "Byty" },
                  "category_sub_cb": { "value": 51, "name": "4+1" },
                  "locality": {
                    "city": "Praha",
                    "citypart": "Praha 1",
                    "city_seo_name": "praha",
                    "citypart_seo_name": "praha-1"
                  },
                  "price_czk": 12000000
                }
              ]
            }
            """;
        var scraper = new StubWebScraper(string.Empty);
        var portal = new SrealityCzAdsPortal(
            "https://www.sreality.cz/hledani/prodej",
            scraper,
            new HttpClient(new StubHttpMessageHandler(json)));
        var filter = new BasicParametersAdPostsFilter(new BasicParametersAdPostsFilterSettings
        {
            MaxPrice = 6_000_000m,
            Layouts = new HashSet<Layout> { Layout.TwoPlusKk }
        });
        var outputPath = Path.Combine(_directory, "watched-listings.html");
        var handler = new LocalFileAdPostsHandler(
            new LocalFileAdPostsHandlerSettings
            {
                Enabled = true,
                MainFilePath = outputPath,
                PrintFormat = PrintFormat.Html
            },
            new NumberFormatInfo { NumberDecimalDigits = 0, NumberGroupSeparator = " " });
        var engine = new RealEstatesWatchEngine(new WatchEngineSettings
        {
            PerformCheckOnStartup = true,
            CheckIntervalMinutes = 1
        });
        engine.RegisterAdsPortal(portal);
        engine.RegisterAdPostsFilter(filter);
        engine.RegisterAdPostsHandler(handler);

        await engine.StartAsync();
        await engine.StopAsync();

        var output = await File.ReadAllTextAsync(outputPath);
        Assert.Equal(0, scraper.CallCount);
        Assert.Contains("Prodej bytu 2+kk 55 m²", output);
        Assert.Contains("https://www.sreality.cz/detail/prodej/byt/2+kk/praha-praha-2/101", output);
        Assert.Contains("Sreality.cz", output);
        Assert.DoesNotContain("Prodej bytu 4+1 120 m²", output);
        Assert.DoesNotContain("/202", output);
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
            Directory.Delete(_directory, recursive: true);
    }

    private sealed class StubWebScraper(string html) : IWebScraper
    {
        public int CallCount { get; private set; }

        public Task<string> GetFullWebPageContentAsync(
            string url,
            Encoding? pageEncoding = null,
            CancellationToken cancellationToken = default)
        {
            CallCount++;
            return Task.FromResult(html);
        }

        public Task<string> GetFullWebPageContentAsync(
            Uri uri,
            Encoding? pageEncoding = null,
            CancellationToken cancellationToken = default) =>
            GetFullWebPageContentAsync(uri.AbsoluteUri, pageEncoding, cancellationToken);
    }

    private sealed class StubHttpMessageHandler(string responseJson) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(responseJson, Encoding.UTF8, "application/json")
            });
    }
}

public class SystemProcessRunnerIntegrationTests
{
    [Fact]
    [Trait("Category", "Integration")]
    public async Task Run_CapturesOutputFromARealChildProcess()
    {
        var runner = new SystemProcessRunner();
        var startInfo = new ProcessStartInfo { FileName = GetFixtureExecutablePath() };
        startInfo.ArgumentList.Add("write");
        startInfo.ArgumentList.Add("fixture output");

        var result = await runner.RunAsync(
            startInfo,
            Encoding.UTF8,
            TimeSpan.FromSeconds(10));

        Assert.Equal(0, result.ExitCode);
        Assert.Equal("fixture output", result.StandardOutput);
        Assert.True(string.IsNullOrWhiteSpace(result.StandardError));
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task Run_KillsAChildProcessThatExceedsItsTimeout()
    {
        var runner = new SystemProcessRunner();
        var startInfo = CreateDelayProcess(TimeSpan.FromSeconds(30));

        var exception = await Assert.ThrowsAsync<TimeoutException>(() => runner.RunAsync(
            startInfo,
            Encoding.UTF8,
            TimeSpan.FromMilliseconds(100)));

        Assert.Contains("exceeded the timeout", exception.Message);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task Run_KillsAChildProcessWhenCancelled()
    {
        var runner = new SystemProcessRunner();
        var startInfo = CreateDelayProcess(TimeSpan.FromSeconds(30));
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => runner.RunAsync(
            startInfo,
            Encoding.UTF8,
            TimeSpan.FromSeconds(10),
            cancellation.Token));
    }

    private static ProcessStartInfo CreateDelayProcess(TimeSpan delay)
    {
        var startInfo = new ProcessStartInfo { FileName = GetFixtureExecutablePath() };
        startInfo.ArgumentList.Add("delay");
        startInfo.ArgumentList.Add(((int)delay.TotalMilliseconds).ToString(CultureInfo.InvariantCulture));
        return startInfo;
    }

    private static string GetFixtureExecutablePath() => Path.Combine(
        AppContext.BaseDirectory,
        OperatingSystem.IsWindows()
            ? "RealEstatesWatcher.Integration.ProcessFixture.exe"
            : "RealEstatesWatcher.Integration.ProcessFixture");
}
