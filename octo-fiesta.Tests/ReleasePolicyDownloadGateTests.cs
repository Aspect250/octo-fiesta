using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using octo_fiesta.Models.Domain;
using octo_fiesta.Models.Settings;
using octo_fiesta.Services;
using octo_fiesta.Services.Common;
using octo_fiesta.Services.Local;

namespace octo_fiesta.Tests;

/// <summary>
/// Album-mode download gate tests (hermes): the resolved album behind a track must pass the
/// content policy before a background whole-album download fires. Editions, compilations and
/// live/remix/junk releases are rejected and re-resolved to a plain studio album when one
/// exists; otherwise the download is skipped and the track stays unresolved.
/// </summary>
public class ReleasePolicyDownloadGateTests : IDisposable
{
    private readonly string _testDownloadPath;

    public ReleasePolicyDownloadGateTests()
    {
        _testDownloadPath = Path.Combine(Path.GetTempPath(), "octo-fiesta-gate-tests-" + Guid.NewGuid());
        Directory.CreateDirectory(_testDownloadPath);
    }

    public void Dispose()
    {
        if (Directory.Exists(_testDownloadPath))
        {
            Directory.Delete(_testDownloadPath, true);
        }
    }

    private FakeGateDownloadService BuildService(
        Mock<ILocalLibraryService> localLibMock,
        Mock<IMusicMetadataService> metaMock,
        List<string>? logSink = null)
    {
        var settings = new SubsonicSettings
        {
            FolderTemplate = "{artist}/{album}/{track}. {title}",
            StorageMode = StorageMode.Permanent,
            DownloadMode = DownloadMode.Album
        };
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Library:DownloadPath"] = _testDownloadPath
            })
            .Build();

        // NOTE: no default SearchAlbumsAsync setup here — Moq's last-registered setup wins,
        // so a default registered here would shadow the specific setups each test registers.
        // Tests that reach the plain-album re-resolution path set their own SearchAlbumsAsync.

        return new FakeGateDownloadService(
            new Mock<IHttpClientFactory>().Object,
            config,
            localLibMock.Object,
            metaMock.Object,
            settings,
            new Mock<IServiceProvider>().Object,
            logSink != null ? new ListLogger(logSink) : NullLogger.Instance);
    }

    private static Mock<ILocalLibraryService> EmptyLocalLibrary()
    {
        var localLibMock = new Mock<ILocalLibraryService>();
        localLibMock.Setup(x => x.GetMappingForExternalSongAsync(It.IsAny<string>(), It.IsAny<string>())).ReturnsAsync((LocalSongMapping?)null);
        localLibMock.Setup(x => x.GetLocalPathForExternalSongAsync(It.IsAny<string>(), It.IsAny<string>())).ReturnsAsync((string?)null);
        localLibMock.Setup(x => x.FindLocalSongByMetadataAsync(It.IsAny<Song>())).ReturnsAsync((LocalSongMatch?)null);
        return localLibMock;
    }

    /// <summary>
    /// (a)+(c) Track on an edition album → re-resolves to the plain studio album found via
    /// the ALBUMS search section (not the songs top-5) and downloads THAT album.
    /// </summary>
    [Fact]
    public async Task DownloadSongAsync_TrackOnEditionAlbum_ReResolvesToPlainAlbum()
    {
        var localLibMock = EmptyLocalLibrary();

        var metaMock = new Mock<IMusicMetadataService>();
        metaMock.Setup(x => x.GetSongAsync("fake", "2")).ReturnsAsync(new Song
        {
            ExternalId = "2", ExternalProvider = "fake", Title = "In the End", Artist = "Linkin Park",
            Album = "Hybrid Theory (Bonus Edition)", AlbumId = "edition-album", Track = 1
        });
        // The resolved album is an edition (ReleaseType null on the song — the search-resolved hole).
        metaMock.Setup(x => x.GetAlbumAsync("fake", "edition-album")).ReturnsAsync(new Album
        {
            Id = "edition-album", Title = "Hybrid Theory (Bonus Edition)", Artist = "Linkin Park",
            ReleaseType = "album", ExternalId = "edition-album",
            Songs = new List<Song> { new Song { ExternalId = "2", ExternalProvider = "fake", Title = "In the End", AlbumId = "edition-album" } }
        });
        // Plain album exists in the ALBUMS section only (not in any songs top-5).
        metaMock.Setup(x => x.SearchAlbumsAsync("Hybrid Theory", 20)).ReturnsAsync(new List<Album>
        {
            new Album { Id = "plain-album", Title = "Hybrid Theory", Artist = "Linkin Park",
                ReleaseType = "album", ExternalId = "plain-album" }
        });
        metaMock.Setup(x => x.GetAlbumAsync("fake", "plain-album")).ReturnsAsync(new Album
        {
            Id = "plain-album", Title = "Hybrid Theory", Artist = "Linkin Park",
            ReleaseType = "album", ExternalId = "plain-album",
            Songs = new List<Song>
            {
                new Song { ExternalId = "3", ExternalProvider = "fake", Title = "Papercut", AlbumId = "plain-album" },
                new Song { ExternalId = "4", ExternalProvider = "fake", Title = "One Step Closer", AlbumId = "plain-album" }
            }
        });
        metaMock.Setup(x => x.GetSongAsync("fake", "3")).ReturnsAsync(new Song { ExternalId = "3", ExternalProvider = "fake", Title = "Papercut", AlbumId = "plain-album" });
        metaMock.Setup(x => x.GetSongAsync("fake", "4")).ReturnsAsync(new Song { ExternalId = "4", ExternalProvider = "fake", Title = "One Step Closer", AlbumId = "plain-album" });

        var logSink = new List<string>();
        var service = BuildService(localLibMock, metaMock, logSink);
        var result = await service.DownloadSongAsync("fake", "2");
        Assert.NotNull(result);

        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (service.DownloadTrackCount < 3 && DateTime.UtcNow < deadline)
        {
            await Task.Delay(100);
        }
        Assert.True(service.DownloadTrackCount == 3,
            $"Expected 3 downloads, got {service.DownloadTrackCount}\nLOGS:\n{string.Join("\n", logSink)}");
    }

    /// <summary>
    /// (b) Track whose only carrier is a compilation → no background download, track stays unresolved.
    /// </summary>
    [Fact]
    public async Task DownloadSongAsync_TrackOnlyOnCompilation_SkipsDownload()
    {
        var localLibMock = EmptyLocalLibrary();

        var metaMock = new Mock<IMusicMetadataService>();
        metaMock.Setup(x => x.GetSongAsync("fake", "2")).ReturnsAsync(new Song
        {
            ExternalId = "2", ExternalProvider = "fake", Title = "Radio Ga Ga", Artist = "Queen",
            Album = "Greatest Hits II", AlbumId = "comp-album", Track = 1
        });
        metaMock.Setup(x => x.GetAlbumAsync("fake", "comp-album")).ReturnsAsync(new Album
        {
            Id = "comp-album", Title = "Greatest Hits II", Artist = "Queen",
            ReleaseType = "compilation", ExternalId = "comp-album",
            Songs = new List<Song> { new Song { ExternalId = "2", ExternalProvider = "fake", Title = "Radio Ga Ga", AlbumId = "comp-album" } }
        });
        // Re-resolution finds only more comps — nothing acceptable.
        metaMock.Setup(x => x.SearchAlbumsAsync("Greatest", 20)).ReturnsAsync(new List<Album>
        {
            new Album { Id = "comp-b", Title = "The Essential Queen", Artist = "Queen", ReleaseType = "compilation", ExternalId = "comp-b" }
        });

        var service = BuildService(localLibMock, metaMock);
        var result = await service.DownloadSongAsync("fake", "2");
        Assert.NotNull(result);

        await Task.Delay(1500); // ample time for any wrongly-fired background download
        Assert.Equal(1, service.DownloadTrackCount); // only the main track, no album download
    }

    /// <summary>
    /// (d) Plain track title on a remix album ("Modjo Remixes") → album-level junk check rejects it.
    /// </summary>
    [Fact]
    public async Task DownloadSongAsync_PlainTrackOnRemixAlbum_Rejected()
    {
        var localLibMock = EmptyLocalLibrary();

        var metaMock = new Mock<IMusicMetadataService>();
        metaMock.Setup(x => x.GetSongAsync("fake", "2")).ReturnsAsync(new Song
        {
            ExternalId = "2", ExternalProvider = "fake", Title = "Lady (Hear Me Tonight)", Artist = "Modjo",
            Album = "Modjo Remixes", AlbumId = "remix-album", Track = 1
        });
        // Song title is plain — only the ALBUM title carries the junk term (plural "Remixes").
        metaMock.Setup(x => x.GetAlbumAsync("fake", "remix-album")).ReturnsAsync(new Album
        {
            Id = "remix-album", Title = "Modjo Remixes", Artist = "Modjo",
            ReleaseType = "album", ExternalId = "remix-album",
            Songs = new List<Song> { new Song { ExternalId = "2", ExternalProvider = "fake", Title = "Lady (Hear Me Tonight)", AlbumId = "remix-album" } }
        });
        // No plain equivalent exists.
        metaMock.Setup(x => x.SearchAlbumsAsync("Modjo", 20)).ReturnsAsync(new List<Album>());

        var service = BuildService(localLibMock, metaMock);
        var result = await service.DownloadSongAsync("fake", "2");
        Assert.NotNull(result);

        await Task.Delay(1500);
        Assert.Equal(1, service.DownloadTrackCount); // rejected: no album download
    }

    /// <summary>
    /// (e2) Song ReleaseType missing (search-resolved) but the album's record_type is "single" →
    /// still track-only (the record_type hole fix).
    /// </summary>
    [Fact]
    public async Task DownloadSongAsync_AlbumRecordTypeSingle_TrackOnly()
    {
        var localLibMock = EmptyLocalLibrary();

        var metaMock = new Mock<IMusicMetadataService>();
        metaMock.Setup(x => x.GetSongAsync("fake", "2")).ReturnsAsync(new Song
        {
            ExternalId = "2", ExternalProvider = "fake", Title = "Just The Way You Are", Artist = "Artist",
            Album = "Just The Way You Are (Remixes)", AlbumId = "single-album", Track = 1
            // ReleaseType intentionally null — the search-resolved case.
        });
        metaMock.Setup(x => x.GetAlbumAsync("fake", "single-album")).ReturnsAsync(new Album
        {
            Id = "single-album", Title = "Just The Way You Are (Remixes)", Artist = "Artist",
            ReleaseType = "single", ExternalId = "single-album",
            Songs = new List<Song> { new Song { ExternalId = "2", ExternalProvider = "fake", Title = "Just The Way You Are", AlbumId = "single-album" } }
        });

        var service = BuildService(localLibMock, metaMock);
        var result = await service.DownloadSongAsync("fake", "2");
        Assert.NotNull(result);

        await Task.Delay(1500);
        Assert.Equal(1, service.DownloadTrackCount); // single: no album download
    }

    /// <summary>
    /// (f) Remastered-only release with no plain equivalent → demote-only fallback downloads it.
    /// </summary>
    [Fact]
    public async Task DownloadSongAsync_RemasteredOnly_NoPlain_FallsBackToRemastered()
    {
        var localLibMock = EmptyLocalLibrary();

        var metaMock = new Mock<IMusicMetadataService>();
        metaMock.Setup(x => x.GetSongAsync("fake", "2")).ReturnsAsync(new Song
        {
            ExternalId = "2", ExternalProvider = "fake", Title = "Track One", Artist = "Artist",
            Album = "Album (Remastered)", AlbumId = "remaster-album", Track = 1
        });
        metaMock.Setup(x => x.GetAlbumAsync("fake", "remaster-album")).ReturnsAsync(new Album
        {
            Id = "remaster-album", Title = "Album (Remastered)", Artist = "Artist",
            ReleaseType = "album", ExternalId = "remaster-album",
            Songs = new List<Song>
            {
                new Song { ExternalId = "2", ExternalProvider = "fake", Title = "Track One", AlbumId = "remaster-album" },
                new Song { ExternalId = "3", ExternalProvider = "fake", Title = "Track Two", AlbumId = "remaster-album" }
            }
        });
        // No plain "Album" exists.
        metaMock.Setup(x => x.SearchAlbumsAsync("Album", 20)).ReturnsAsync(new List<Album>());
        metaMock.Setup(x => x.GetSongAsync("fake", "3")).ReturnsAsync(new Song { ExternalId = "3", ExternalProvider = "fake", Title = "Track Two", AlbumId = "remaster-album" });

        var service = BuildService(localLibMock, metaMock);
        var result = await service.DownloadSongAsync("fake", "2");
        Assert.NotNull(result);

        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (service.DownloadTrackCount < 2 && DateTime.UtcNow < deadline)
        {
            await Task.Delay(100);
        }
        Assert.Equal(2, service.DownloadTrackCount); // main track + remastered album's remaining track
    }

    /// <summary>
    /// (g) A plain octo-sync-style query must NOT leak into the download gate: the edition
    /// album is still rejected even though the query "Linkin Park In the End" contains no junk.
    /// </summary>
    [Fact]
    public async Task DownloadSongAsync_PlainQuery_DoesNotBypassEditionRejection()
    {
        var localLibMock = EmptyLocalLibrary();

        var metaMock = new Mock<IMusicMetadataService>();
        metaMock.Setup(x => x.GetSongAsync("fake", "2")).ReturnsAsync(new Song
        {
            ExternalId = "2", ExternalProvider = "fake", Title = "In the End", Artist = "Linkin Park",
            Album = "Hybrid Theory (Bonus Edition)", AlbumId = "edition-album", Track = 1
        });
        metaMock.Setup(x => x.GetAlbumAsync("fake", "edition-album")).ReturnsAsync(new Album
        {
            Id = "edition-album", Title = "Hybrid Theory (Bonus Edition)", Artist = "Linkin Park",
            ReleaseType = "album", ExternalId = "edition-album",
            Songs = new List<Song> { new Song { ExternalId = "2", ExternalProvider = "fake", Title = "In the End", AlbumId = "edition-album" } }
        });
        // No plain equivalent → edition must NOT be downloaded.
        metaMock.Setup(x => x.SearchAlbumsAsync("Hybrid Theory", 20)).ReturnsAsync(new List<Album>());

        var service = BuildService(localLibMock, metaMock);
        var result = await service.DownloadSongAsync("fake", "2");
        Assert.NotNull(result);

        await Task.Delay(1500);
        Assert.Equal(1, service.DownloadTrackCount); // edition rejected, nothing extra downloaded
    }

    private sealed class ListLogger : Microsoft.Extensions.Logging.ILogger
    {
        private readonly List<string> _sink;

        public ListLogger(List<string> sink) => _sink = sink;

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel logLevel) => true;

        public void Log<TState>(Microsoft.Extensions.Logging.LogLevel logLevel, Microsoft.Extensions.Logging.EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            _sink.Add($"[{logLevel}] {formatter(state, exception)}");
        }
    }

    private sealed class FakeGateDownloadService : BaseDownloadService
    {
        public int DownloadTrackCount { get; private set; }

        public FakeGateDownloadService(
            IHttpClientFactory httpClientFactory,
            IConfiguration configuration,
            ILocalLibraryService localLibraryService,
            IMusicMetadataService metadataService,
            SubsonicSettings subsonicSettings,
            IServiceProvider serviceProvider,
            Microsoft.Extensions.Logging.ILogger logger)
            : base(httpClientFactory, configuration, localLibraryService, metadataService, subsonicSettings, serviceProvider, logger)
        {
        }

        public override Task<bool> IsAvailableAsync() => Task.FromResult(true);

        protected override string ProviderName => "fake";

        protected override string? ExtractExternalIdFromAlbumId(string albumId) => albumId;

        protected override string? GetTargetQuality() => null;

        protected override Task<DownloadResult> DownloadTrackAsync(string trackId, Song song, CancellationToken cancellationToken)
        {
            DownloadTrackCount++;
            return Task.FromResult(new DownloadResult(
                new MemoryStream(new byte[] { 0x66, 0x4C, 0x61, 0x43 }),
                ".flac",
                "FLAC"));
        }
    }
}
