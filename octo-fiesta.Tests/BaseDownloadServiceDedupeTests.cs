using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using octo_fiesta.Models.Domain;
using octo_fiesta.Models.Settings;
using octo_fiesta.Services.Common;
using octo_fiesta.Services.Local;
using octo_fiesta.Services;

namespace octo_fiesta.Tests;

public class BaseDownloadServiceDedupeTests : IDisposable
{
    private readonly string _testDownloadPath;

    public BaseDownloadServiceDedupeTests()
    {
        _testDownloadPath = Path.Combine(Path.GetTempPath(), "octo-fiesta-dedupe-tests-" + Guid.NewGuid());
        Directory.CreateDirectory(_testDownloadPath);
    }

    public void Dispose()
    {
        if (Directory.Exists(_testDownloadPath))
        {
            Directory.Delete(_testDownloadPath, true);
        }
    }

    private FakeDedupeDownloadService BuildService(
        Mock<ILocalLibraryService> localLibMock,
        Mock<IMusicMetadataService> metaMock,
        string? folderTemplate = null,
        DownloadMode? downloadMode = null)
    {
        var settings = new SubsonicSettings
        {
            FolderTemplate = folderTemplate ?? "{artist}/{album}/{track}. {title}",
            StorageMode = StorageMode.Permanent,
            DownloadMode = downloadMode ?? DownloadMode.Track
        };
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Library:DownloadPath"] = _testDownloadPath
            })
            .Build();
        return new FakeDedupeDownloadService(
            new Mock<IHttpClientFactory>().Object,
            config,
            localLibMock.Object,
            metaMock.Object,
            settings,
            new Mock<IServiceProvider>().Object,
            NullLogger.Instance);
    }

    [Fact]
    public async Task DownloadSongAsync_WhenFileExistsOnDiskWithoutMapping_RegistersAndSkipsDownload()
    {
        var album = Path.Combine(_testDownloadPath, "Artist", "Album");
        Directory.CreateDirectory(album);
        var expectedFile = Path.Combine(album, "01. Track.flac");
        await File.WriteAllTextAsync(expectedFile, "existing-file");

        var localLibMock = new Mock<ILocalLibraryService>();
        localLibMock
            .Setup(x => x.GetMappingForExternalSongAsync("fake", "1"))
            .ReturnsAsync((LocalSongMapping?)null);
        localLibMock
            .Setup(x => x.GetLocalPathForExternalSongAsync("fake", "1"))
            .ReturnsAsync((string?)null);
        localLibMock
            .Setup(x => x.FindLocalSongByMetadataAsync(It.IsAny<Song>()))
            .ReturnsAsync((LocalSongMatch?)null);

        var metaMock = new Mock<IMusicMetadataService>();
        metaMock
            .Setup(x => x.GetSongAsync("fake", "1"))
            .ReturnsAsync(new Song
            {
                ExternalId = "1",
                ExternalProvider = "fake",
                Title = "Track",
                Artist = "Artist",
                Album = "Album",
                Track = 1
            });

        var service = BuildService(localLibMock, metaMock);

        var result = await service.DownloadSongAsync("fake", "1");

        Assert.Equal(expectedFile, result);
        Assert.False(service.DownloadTrackCalled);
        localLibMock.Verify(
            x => x.RegisterDownloadedSongAsync(It.IsAny<Song>(), expectedFile, null, null),
            Times.Once);
    }

    [Fact]
    public async Task DownloadSongAsync_WhenNavidromeReturnsMetadataMatch_RegistersAndSkipsDownload()
    {
        // Navidrome reports a path relative to its library root (== DownloadPath) whose synthesized
        // file name ("01-01 - Track") differs from the real file on disk ("01 - Track").
        var albumDir = Path.Combine(_testDownloadPath, "Artist", "Other Album");
        Directory.CreateDirectory(albumDir);
        var realFile = Path.Combine(albumDir, "01 - Track.flac");
        await File.WriteAllTextAsync(realFile, "existing-file");

        var naviPath = "Artist/Other Album/01-01 - Track.flac"; // does not exist verbatim

        var localLibMock = new Mock<ILocalLibraryService>();
        localLibMock
            .Setup(x => x.GetMappingForExternalSongAsync("fake", "2"))
            .ReturnsAsync((LocalSongMapping?)null);
        localLibMock
            .Setup(x => x.GetLocalPathForExternalSongAsync("fake", "2"))
            .ReturnsAsync((string?)null);
        localLibMock
            .Setup(x => x.FindLocalSongByMetadataAsync(It.IsAny<Song>()))
            .ReturnsAsync(new LocalSongMatch("local-id-999", naviPath));

        var metaMock = new Mock<IMusicMetadataService>();
        metaMock
            .Setup(x => x.GetSongAsync("fake", "2"))
            .ReturnsAsync(new Song
            {
                ExternalId = "2",
                ExternalProvider = "fake",
                Title = "Track",
                Artist = "Artist",
                Album = "Album",
                Track = 8
            });

        var service = BuildService(localLibMock, metaMock);

        var result = await service.DownloadSongAsync("fake", "2");

        // The real file is located by title and registered with an absolute path + Navidrome ID.
        Assert.Equal(realFile, result);
        Assert.False(service.DownloadTrackCalled);
        localLibMock.Verify(
            x => x.RegisterDownloadedSongAsync(It.IsAny<Song>(), realFile, null, "local-id-999"),
            Times.Once);
    }

    [Fact]
    public async Task DownloadSongAsync_WhenNavidromeMatchFileMissing_DownloadsInsteadOfRegisteringBrokenPath()
    {
        // Navidrome matches by metadata but the reported path does not resolve to any real local
        // file (different mount, or file deleted). The unverified path must NOT be registered;
        // the service downloads a fresh copy instead.
        var localLibMock = new Mock<ILocalLibraryService>();
        localLibMock
            .Setup(x => x.GetMappingForExternalSongAsync("fake", "2"))
            .ReturnsAsync((LocalSongMapping?)null);
        localLibMock
            .Setup(x => x.GetLocalPathForExternalSongAsync("fake", "2"))
            .ReturnsAsync((string?)null);
        localLibMock
            .Setup(x => x.FindLocalSongByMetadataAsync(It.IsAny<Song>()))
            .ReturnsAsync(new LocalSongMatch("local-id-999", "Artist/Missing Album/01-01 - Track.flac"));

        var metaMock = new Mock<IMusicMetadataService>();
        metaMock
            .Setup(x => x.GetSongAsync("fake", "2"))
            .ReturnsAsync(new Song
            {
                ExternalId = "2",
                ExternalProvider = "fake",
                Title = "Track",
                Artist = "Artist",
                Album = "Album",
                Track = 1
            });

        var service = BuildService(localLibMock, metaMock);

        var result = await service.DownloadSongAsync("fake", "2");

        var expectedDownloadPath = Path.Combine(_testDownloadPath, "Artist", "Album", "01. Track.flac");
        Assert.Equal(expectedDownloadPath, result);
        Assert.True(service.DownloadTrackCalled);
        localLibMock.Verify(
            x => x.RegisterDownloadedSongAsync(
                It.IsAny<Song>(),
                It.Is<string>(p => p.Contains("Missing Album")),
                It.IsAny<string?>(),
                It.IsAny<string?>()),
            Times.Never);
    }

    [Fact]
    public async Task DownloadSongAsync_WhenSameAlbumHasDuplicateTitles_DisambiguatesByTrackNumber()
    {
        // Two different tracks in the same album folder share the same title (e.g. a reprise).
        // The reported track number must select the correct file rather than the first match.
        var albumDir = Path.Combine(_testDownloadPath, "Artist", "Other Album");
        Directory.CreateDirectory(albumDir);
        var track3 = Path.Combine(albumDir, "03 - Track.flac");
        var track9 = Path.Combine(albumDir, "09 - Track.flac");
        await File.WriteAllTextAsync(track3, "reprise");
        await File.WriteAllTextAsync(track9, "the-one-we-want");

        // Navidrome reports disc-track "01-09", whose track number (9) should pick track9.
        var naviPath = "Artist/Other Album/01-09 - Track.flac";

        var localLibMock = new Mock<ILocalLibraryService>();
        localLibMock
            .Setup(x => x.GetMappingForExternalSongAsync("fake", "2"))
            .ReturnsAsync((LocalSongMapping?)null);
        localLibMock
            .Setup(x => x.GetLocalPathForExternalSongAsync("fake", "2"))
            .ReturnsAsync((string?)null);
        localLibMock
            .Setup(x => x.FindLocalSongByMetadataAsync(It.IsAny<Song>()))
            .ReturnsAsync(new LocalSongMatch("local-id-999", naviPath));

        var metaMock = new Mock<IMusicMetadataService>();
        metaMock
            .Setup(x => x.GetSongAsync("fake", "2"))
            .ReturnsAsync(new Song
            {
                ExternalId = "2",
                ExternalProvider = "fake",
                Title = "Track",
                Artist = "Artist",
                Album = "Album",
                Track = 4
            });

        var service = BuildService(localLibMock, metaMock);

        var result = await service.DownloadSongAsync("fake", "2");

        Assert.Equal(track9, result);
        Assert.False(service.DownloadTrackCalled);
    }

    [Fact]
    public async Task SaveDownloadStream_WhenTargetCreatedBetweenProbeAndWrite_Throws()
    {
        var localLibMock = new Mock<ILocalLibraryService>();
        localLibMock
            .Setup(x => x.GetMappingForExternalSongAsync("fake", "3"))
            .ReturnsAsync((LocalSongMapping?)null);
        localLibMock
            .Setup(x => x.GetLocalPathForExternalSongAsync("fake", "3"))
            .ReturnsAsync((string?)null);
        localLibMock
            .Setup(x => x.FindLocalSongByMetadataAsync(It.IsAny<Song>()))
            .ReturnsAsync((LocalSongMatch?)null);

        var metaMock = new Mock<IMusicMetadataService>();
        var song = new Song
        {
            ExternalId = "3",
            ExternalProvider = "fake",
            Title = "RaceTrack",
            Artist = "Artist",
            Album = "Album",
            Track = 1
        };
        metaMock.Setup(x => x.GetSongAsync("fake", "3")).ReturnsAsync(song);

        var service = BuildService(localLibMock, metaMock);
        service.CreateFileBeforeWrite = true;

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.DownloadSongAsync("fake", "3"));
    }

    [Fact]
    public async Task DownloadSongAsync_SingleRelease_DoesNotTriggerAlbumDownload()
    {
        // A single/EP download must stay track-only even in Album mode: the album
        // trigger excludes ReleaseType single/ep (hermes fork).
        var localLibMock = new Mock<ILocalLibraryService>();
        localLibMock.Setup(x => x.GetMappingForExternalSongAsync("fake", "2")).ReturnsAsync((LocalSongMapping?)null);
        localLibMock.Setup(x => x.GetLocalPathForExternalSongAsync("fake", "2")).ReturnsAsync((string?)null);
        localLibMock.Setup(x => x.FindLocalSongByMetadataAsync(It.IsAny<Song>())).ReturnsAsync((LocalSongMatch?)null);

        var metaMock = new Mock<IMusicMetadataService>();
        metaMock.Setup(x => x.GetSongAsync("fake", "2")).ReturnsAsync(new Song
        {
            ExternalId = "2",
            ExternalProvider = "fake",
            Title = "Single Track",
            Artist = "Artist",
            Album = "Single",
            AlbumId = "ext-album-1",
            ReleaseType = "single",
            Track = 1
        });
        // The single's "album" carries remix-pack extras that must NOT be downloaded.
        metaMock.Setup(x => x.GetAlbumAsync("fake", "ext-album-1")).ReturnsAsync(new Album
        {
            Id = "ext-album-1",
            Title = "Single",
            Artist = "Artist",
            Songs = new List<Song>
            {
                new Song { ExternalId = "2", ExternalProvider = "fake", Title = "Single Track", AlbumId = "ext-album-1", ReleaseType = "single" },
                new Song { ExternalId = "3", ExternalProvider = "fake", Title = "Club Mix", AlbumId = "ext-album-1", ReleaseType = "single" },
                new Song { ExternalId = "4", ExternalProvider = "fake", Title = "Extended Mix", AlbumId = "ext-album-1", ReleaseType = "single" }
            }
        });
        metaMock.Setup(x => x.GetSongAsync("fake", "3")).ReturnsAsync(new Song { ExternalId = "3", ExternalProvider = "fake", Title = "Club Mix", AlbumId = "ext-album-1", ReleaseType = "single" });
        metaMock.Setup(x => x.GetSongAsync("fake", "4")).ReturnsAsync(new Song { ExternalId = "4", ExternalProvider = "fake", Title = "Extended Mix", AlbumId = "ext-album-1", ReleaseType = "single" });

        var service = BuildService(localLibMock, metaMock, downloadMode: DownloadMode.Album);

        var result = await service.DownloadSongAsync("fake", "2");

        Assert.NotNull(result);
        // Give a wrongly-fired background album download ample time to run.
        await Task.Delay(1500);
        Assert.Equal(1, service.DownloadTrackCount);
    }

    [Fact]
    public async Task DownloadSongAsync_RealAlbum_TriggersAlbumDownload()
    {
        // Real albums still trigger the background whole-album download in Album mode.
        var localLibMock = new Mock<ILocalLibraryService>();
        localLibMock.Setup(x => x.GetMappingForExternalSongAsync("fake", "2")).ReturnsAsync((LocalSongMapping?)null);
        localLibMock.Setup(x => x.GetLocalPathForExternalSongAsync("fake", "2")).ReturnsAsync((string?)null);
        localLibMock.Setup(x => x.FindLocalSongByMetadataAsync(It.IsAny<Song>())).ReturnsAsync((LocalSongMatch?)null);

        var metaMock = new Mock<IMusicMetadataService>();
        metaMock.Setup(x => x.GetSongAsync("fake", "2")).ReturnsAsync(new Song
        {
            ExternalId = "2",
            ExternalProvider = "fake",
            Title = "Track One",
            Artist = "Artist",
            Album = "Album",
            AlbumId = "ext-album-2",
            ReleaseType = "album",
            Track = 1
        });
        metaMock.Setup(x => x.GetAlbumAsync("fake", "ext-album-2")).ReturnsAsync(new Album
        {
            Id = "ext-album-2",
            Title = "Album",
            Artist = "Artist",
            Songs = new List<Song>
            {
                new Song { ExternalId = "2", ExternalProvider = "fake", Title = "Track One", AlbumId = "ext-album-2", ReleaseType = "album" },
                new Song { ExternalId = "3", ExternalProvider = "fake", Title = "Track Two", AlbumId = "ext-album-2", ReleaseType = "album" },
                new Song { ExternalId = "4", ExternalProvider = "fake", Title = "Track Three", AlbumId = "ext-album-2", ReleaseType = "album" }
            }
        });
        metaMock.Setup(x => x.GetSongAsync("fake", "3")).ReturnsAsync(new Song { ExternalId = "3", ExternalProvider = "fake", Title = "Track Two", AlbumId = "ext-album-2", ReleaseType = "album" });
        metaMock.Setup(x => x.GetSongAsync("fake", "4")).ReturnsAsync(new Song { ExternalId = "4", ExternalProvider = "fake", Title = "Track Three", AlbumId = "ext-album-2", ReleaseType = "album" });

        var service = BuildService(localLibMock, metaMock, downloadMode: DownloadMode.Album);

        var result = await service.DownloadSongAsync("fake", "2");

        Assert.NotNull(result);
        // Poll for the background album download of the remaining two tracks.
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (service.DownloadTrackCount < 3 && DateTime.UtcNow < deadline)
        {
            await Task.Delay(100);
        }
        Assert.Equal(3, service.DownloadTrackCount);
    }

    private sealed class FakeDedupeDownloadService : BaseDownloadService
    {
        public bool DownloadTrackCalled { get; private set; }
        public int DownloadTrackCount { get; private set; }
        public bool CreateFileBeforeWrite { get; set; }

        protected override string ProviderName => "fake";

        public FakeDedupeDownloadService(
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

        protected override string? ExtractExternalIdFromAlbumId(string albumId) => albumId;

        protected override string? GetTargetQuality() => null;

        protected override Task<DownloadResult> DownloadTrackAsync(string trackId, Song song, CancellationToken cancellationToken)
        {
            DownloadTrackCalled = true;
            DownloadTrackCount++;

            if (CreateFileBeforeWrite)
            {
                var racePath = PathHelper.BuildTrackPath(DownloadPath, song, ".flac", SubsonicSettings.FolderTemplate, null);
                Directory.CreateDirectory(Path.GetDirectoryName(racePath)!);
                File.WriteAllText(racePath, "race-written");
            }

            return Task.FromResult(new DownloadResult(
                new MemoryStream(new byte[] { 0x66, 0x4C, 0x61, 0x43 }),
                ".flac",
                "FLAC"));
        }
    }
}
