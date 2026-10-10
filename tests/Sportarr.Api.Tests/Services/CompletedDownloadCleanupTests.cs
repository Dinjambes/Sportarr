using Sportarr.Api.Models;
using Sportarr.Api.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Sportarr.Api.Tests.Services;

public sealed class CompletedDownloadCleanupTests
{
    [Fact]
    public async Task RefusesSharedMediaRoot()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            PackMemberLifecycleHarness.CreateAsync(mediaRoot: "/data/e2e-lib"));
    }

    [Fact]
    public void MissingClientStatusDoesNotSatisfySeedingGoal()
    {
        var indexer = new Indexer { Name = "Fixture", Url = "http://localhost", SeedTime = 60 };

        Assert.False(CompletedDownloadCleanup.HasReachedSeedLimit(null, indexer));
    }

    [Fact]
    public void BothConfiguredSeedGoalsMustBeMet()
    {
        var indexer = new Indexer { Name = "Fixture", Url = "http://localhost", SeedRatio = 0.5, SeedTime = 60 };
        var status = new DownloadClientStatus { Status = "Seeding", Ratio = 0.6, CompletedAt = DateTime.UtcNow.AddMinutes(-10) };

        Assert.False(CompletedDownloadCleanup.HasReachedSeedLimit(status, indexer));
    }

    [Fact]
    public void PackSeedTimeOverridesSingleEventSeedTime()
    {
        var indexer = new Indexer
        {
            Name = "Fixture", Url = "http://localhost", SeedTime = 30, SeasonPackSeedTime = 120
        };
        var status = new DownloadClientStatus
        {
            Status = "Seeding", CompletedAt = DateTime.UtcNow.AddMinutes(-40)
        };

        Assert.True(CompletedDownloadCleanup.HasReachedSeedLimit(status, indexer, isPack: false));
        Assert.False(CompletedDownloadCleanup.HasReachedSeedLimit(status, indexer, isPack: true));
        Assert.True(CompletedDownloadCleanup.HasConfiguredSeedGoal(indexer, isPack: true));

        indexer.SeedTime = null;
        Assert.True(CompletedDownloadCleanup.HasConfiguredSeedGoal(indexer, isPack: true));
        Assert.False(CompletedDownloadCleanup.HasReachedSeedLimit(status, indexer, isPack: true));
    }

    [Fact]
    public async Task MissingTorrentStatusDoesNotRemoveCompletedClientJob()
    {
        await using var rig = await PackMemberLifecycleHarness.CreateAsync();
        var client = await rig.Db.DownloadClients.SingleAsync();
        client.RemoveCompletedDownloads = true;
        var indexer = new Indexer { Name = "Fixture", Url = "http://localhost", SeedTime = 60 };
        rig.Db.Indexers.Add(indexer);
        await rig.Db.SaveChangesAsync();

        var download = new DownloadQueueItem
        {
            Title = "Fixture release",
            DownloadId = "owned-pack-job",
            DownloadClient = client,
            DownloadClientId = client.Id,
            IndexerId = indexer.Id,
            Protocol = "Torrent",
            Status = DownloadStatus.Imported
        };
        rig.Transport.HideJobFromStatus = true;

        await new CompletedDownloadCleanup(rig.Services, NullLogger.Instance).RunAsync(
            download, rig.Services.GetRequiredService<DownloadClientService>(), rig.Db);

        Assert.DoesNotContain(rig.Transport.Requests, request => request.Path == "/api/v2/torrents/delete");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("torrent")]
    [InlineData("Usenet")]
    public async Task TorrentWithoutSeedGoalDoesNotRemoveCompletedClientJob(string? protocol)
    {
        await using var rig = await PackMemberLifecycleHarness.CreateAsync();
        var client = await rig.Db.DownloadClients.SingleAsync();
        client.RemoveCompletedDownloads = true;
        var indexer = new Indexer { Name = "Fixture", Url = "http://localhost" };
        rig.Db.Indexers.Add(indexer);
        await rig.Db.SaveChangesAsync();

        var download = new DownloadQueueItem
        {
            Title = "Fixture release",
            DownloadId = "owned-pack-job",
            DownloadClient = client,
            DownloadClientId = client.Id,
            IndexerId = indexer.Id,
            Protocol = protocol,
            Status = DownloadStatus.Imported
        };

        await new CompletedDownloadCleanup(rig.Services, NullLogger.Instance).RunAsync(
            download, rig.Services.GetRequiredService<DownloadClientService>(), rig.Db);

        Assert.DoesNotContain(rig.Transport.Requests, request => request.Path == "/api/v2/torrents/delete");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("imported")]
    public async Task TorrentWithReachedSeedGoalRemovesCompletedClientJob(string? postImportCategory)
    {
        await using var rig = await PackMemberLifecycleHarness.CreateAsync();
        var client = await rig.Db.DownloadClients.SingleAsync();
        client.RemoveCompletedDownloads = true;
        client.PostImportCategory = postImportCategory;
        var indexer = new Indexer { Name = "Fixture", Url = "http://localhost", SeedRatio = 0.5 };
        rig.Transport.JobPath = "/nonexistent/sportarr-issue328";
        rig.Db.Indexers.Add(indexer);
        await rig.Db.SaveChangesAsync();

        var download = new DownloadQueueItem
        {
            Title = "Fixture release",
            DownloadId = "owned-pack-job",
            DownloadClient = client,
            DownloadClientId = client.Id,
            IndexerId = indexer.Id,
            GrabCategory = "sportarr",
            Protocol = "Torrent",
            Status = DownloadStatus.Imported
        };

        var downloadClientService = rig.Services.GetRequiredService<DownloadClientService>();
        var status = await downloadClientService.GetDownloadStatusAsync(client, download.DownloadId);
        Assert.NotNull(status);
        Assert.True(CompletedDownloadCleanup.HasReachedSeedLimit(status, indexer),
            $"ratio={status.Ratio}, seedRatio={indexer.SeedRatio}, seedTime={indexer.SeedTime}");

        await new CompletedDownloadCleanup(rig.Services, NullLogger.Instance).RunAsync(
            download, downloadClientService, rig.Db);

        Assert.Contains(rig.Transport.Requests, request =>
            request.Path == "/api/v2/torrents/delete" && request.Form.GetValueOrDefault("deleteFiles") == "true");
    }

    [Fact]
    public void UsenetClientWithMissingQueueProtocolIsNotTorrent()
    {
        var client = new DownloadClient
        {
            Name = "Fixture",
            Type = DownloadClientType.NzbGet,
            Host = "localhost"
        };

        Assert.False(DownloadClientService.IsTorrentDownload(new DownloadQueueItem
        {
            Title = "Fixture release",
            DownloadId = "fixture-id",
            DownloadClient = client,
            Protocol = null
        }));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task HardlinkImportKeepsTorrentDataWithoutSeedGoal(bool statusUnavailable)
    {
        var mediaRoot = Environment.GetEnvironmentVariable("SPORTARR_ISSUE328_E2E_ROOT");
        if (mediaRoot != null)
            mediaRoot += statusUnavailable ? "-missing" : "-present";
        await using var rig = await PackMemberLifecycleHarness.CreateAsync(mediaRoot: mediaRoot);
        var client = await rig.Db.DownloadClients.SingleAsync();
        client.RemoveCompletedDownloads = true;
        client.PostImportMode = PostImportMode.Auto;
        rig.Settings.CopyFiles = false;
        var configService = rig.Services.GetRequiredService<ConfigService>();
        var config = await configService.GetConfigAsync();
        config.UseHardlinks = true;
        await configService.SaveConfigAsync(config);
        var indexer = new Indexer { Name = "Fixture", Url = "http://localhost" };
        rig.Db.Indexers.Add(indexer);
        await rig.Db.SaveChangesAsync();

        var root = await rig.Db.RootFolders.SingleAsync();
        var incoming = Path.Combine(Path.GetDirectoryName(root.Path)!, "incoming");
        Directory.CreateDirectory(incoming);
        var source = Path.Combine(incoming, "UFC.9999.720p.WEB-DL.H264.issue328.mkv");
        await File.WriteAllBytesAsync(source, new byte[] { 1, 2, 3, 4 });
        var download = new DownloadQueueItem
        {
            EventId = rig.Event.Id,
            Event = rig.Event,
            Title = "UFC 9999",
            DownloadId = "owned-pack-job",
            DownloadClient = client,
            DownloadClientId = client.Id,
            IndexerId = indexer.Id,
            Protocol = null,
            Quality = "WEBDL-720p",
            Status = DownloadStatus.Completed
        };
        rig.Db.DownloadQueue.Add(download);
        await rig.Db.SaveChangesAsync();
        var queueId = download.Id;
        rig.Db.ChangeTracker.Clear();
        download = await rig.Db.DownloadQueue.AsNoTracking().SingleAsync(item => item.Id == queueId);
        Assert.Null(download.DownloadClient);
        Assert.Equal(client.Id, download.DownloadClientId);
        rig.Transport.JobPath = source;
        rig.Transport.HideJobFromStatus = statusUnavailable;
        if (!statusUnavailable)
            Assert.NotNull(await rig.Services.GetRequiredService<DownloadClientService>()
                .GetDownloadStatusAsync(client, download.DownloadId));

        var imported = await rig.Services.GetRequiredService<FileImportService>()
            .ImportDownloadAsync(download, source);
        Assert.NotNull(imported);
        Assert.NotNull(download.DownloadClient);
        Assert.True(DownloadClientService.IsTorrentDownload(download));
        Assert.True(File.Exists(source));
        Assert.True(File.Exists(imported.DestinationPath));
        Assert.Null(new FileInfo(imported.DestinationPath).LinkTarget);
        await using (var stream = new FileStream(source, FileMode.Open, FileAccess.Write))
            stream.WriteByte(9);
        Assert.Equal(9, (await File.ReadAllBytesAsync(imported.DestinationPath))[0]);

        await new CompletedDownloadCleanup(rig.Services, NullLogger.Instance).RunAsync(
            download, rig.Services.GetRequiredService<DownloadClientService>(), rig.Db);

        Assert.True(File.Exists(source));
        Assert.True(File.Exists(imported.DestinationPath));
        Assert.DoesNotContain(rig.Transport.Requests, request => request.Path == "/api/v2/torrents/delete");
    }
}
