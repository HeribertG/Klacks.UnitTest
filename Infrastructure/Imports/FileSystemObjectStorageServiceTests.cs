using System.Text;
using Klacks.Api.Domain.Services.Imports;
using Klacks.Api.Infrastructure.Services.Imports;
using Microsoft.Extensions.Options;
using Shouldly;

namespace Klacks.UnitTest.Infrastructure.Imports;

[TestFixture]
public class FileSystemObjectStorageServiceTests
{
    private const string DropPointPrefix = "erp/exports/";
    private static readonly DateTime StableTimestampUtc = DateTime.UtcNow.AddMinutes(-5);

    private string _rootPath = null!;
    private FileSystemObjectStorageService _service = null!;

    [SetUp]
    public void SetUp()
    {
        _rootPath = Path.Combine(Path.GetTempPath(), "klacks-fs-storage-tests", Guid.NewGuid().ToString("N"));
        var options = Options.Create(new ErpObjectStorageOptions { RootPath = _rootPath });
        _service = new FileSystemObjectStorageService(options);
    }

    [TearDown]
    public void TearDown()
    {
        if (Directory.Exists(_rootPath))
        {
            Directory.Delete(_rootPath, recursive: true);
        }
    }

    private static Stream ToStream(string content) => new MemoryStream(Encoding.UTF8.GetBytes(content));

    private void MarkStable(string key)
    {
        var path = Path.Combine(_rootPath, key.Replace('/', Path.DirectorySeparatorChar));
        File.SetLastWriteTimeUtc(path, StableTimestampUtc);
    }

    [Test]
    public async Task UploadAsync_ThenDownloadAsync_RoundTripsContent()
    {
        var key = DropPointPrefix + "order-1.xml";

        await _service.UploadAsync(key, ToStream("<ErpOrderImport />"));

        await using var downloaded = await _service.DownloadAsync(key);
        using var reader = new StreamReader(downloaded);
        (await reader.ReadToEndAsync()).ShouldBe("<ErpOrderImport />");
    }

    [Test]
    public async Task UploadAsync_LeavesNoTemporaryFileBehind()
    {
        await _service.UploadAsync(DropPointPrefix + "order-1.xml", ToStream("content"));

        Directory.EnumerateFiles(_rootPath, "*.uploading", SearchOption.AllDirectories).ShouldBeEmpty();
    }

    [Test]
    public async Task ListAsync_ReturnsOnlyKeysUnderPrefix()
    {
        await _service.UploadAsync(DropPointPrefix + "order-1.xml", ToStream("a"));
        await _service.UploadAsync("other/order-2.xml", ToStream("b"));
        MarkStable(DropPointPrefix + "order-1.xml");
        MarkStable("other/order-2.xml");

        var keys = await _service.ListAsync(DropPointPrefix);

        keys.ShouldBe(new[] { DropPointPrefix + "order-1.xml" });
    }

    [Test]
    public async Task ListAsync_SkipsFilesInsideWriteStabilityWindow()
    {
        await _service.UploadAsync(DropPointPrefix + "fresh.xml", ToStream("a"));
        await _service.UploadAsync(DropPointPrefix + "stable.xml", ToStream("b"));
        MarkStable(DropPointPrefix + "stable.xml");

        var keys = await _service.ListAsync(DropPointPrefix);

        keys.ShouldBe(new[] { DropPointPrefix + "stable.xml" });
    }

    [Test]
    public async Task ListAsync_SkipsTemporaryUploadFiles()
    {
        Directory.CreateDirectory(Path.Combine(_rootPath, "erp", "exports"));
        var tempFile = Path.Combine(_rootPath, "erp", "exports", "order-1.xml.uploading");
        await File.WriteAllTextAsync(tempFile, "half written");
        File.SetLastWriteTimeUtc(tempFile, StableTimestampUtc);

        var keys = await _service.ListAsync(DropPointPrefix);

        keys.ShouldBeEmpty();
    }

    [Test]
    public async Task ListAsync_MissingRootDirectory_ReturnsEmpty()
    {
        var keys = await _service.ListAsync(DropPointPrefix);

        keys.ShouldBeEmpty();
    }

    [Test]
    public async Task ListWithMetadataAsync_ReturnsMetadataForKeysUnderPrefix()
    {
        await _service.UploadAsync(DropPointPrefix + "order-1.xml", ToStream("12345"));
        await _service.UploadAsync("other/order-2.xml", ToStream("b"));
        MarkStable(DropPointPrefix + "order-1.xml");
        MarkStable("other/order-2.xml");

        var objects = await _service.ListWithMetadataAsync(DropPointPrefix);

        var entry = objects.ShouldHaveSingleItem();
        entry.Key.ShouldBe(DropPointPrefix + "order-1.xml");
        entry.SizeBytes.ShouldBe(5);
        entry.LastModifiedUtc.ShouldBe(StableTimestampUtc, TimeSpan.FromSeconds(1));
    }

    [Test]
    public async Task ListWithMetadataAsync_SkipsTemporaryUploadFilesButIncludesFilesInsideStabilityWindow()
    {
        await _service.UploadAsync(DropPointPrefix + "fresh.xml", ToStream("fresh"));
        await _service.UploadAsync(DropPointPrefix + "stable.xml", ToStream("stable"));
        MarkStable(DropPointPrefix + "stable.xml");
        var tempFile = Path.Combine(_rootPath, "erp", "exports", "half.xml.uploading");
        await File.WriteAllTextAsync(tempFile, "half written");
        File.SetLastWriteTimeUtc(tempFile, StableTimestampUtc);

        var objects = await _service.ListWithMetadataAsync(DropPointPrefix);

        objects.Select(o => o.Key).ShouldBe(new[]
        {
            DropPointPrefix + "fresh.xml",
            DropPointPrefix + "stable.xml",
        });
    }

    [Test]
    public async Task ListWithMetadataAsync_MissingRootDirectory_ReturnsEmpty()
    {
        var objects = await _service.ListWithMetadataAsync(DropPointPrefix);

        objects.ShouldBeEmpty();
    }

    [Test]
    public async Task MoveAsync_MovesFileToDestinationSegment()
    {
        var sourceKey = DropPointPrefix + "order-1.xml";
        var destinationKey = DropPointPrefix + "processed/order-1.xml";
        await _service.UploadAsync(sourceKey, ToStream("content"));

        await _service.MoveAsync(sourceKey, destinationKey);

        File.Exists(Path.Combine(_rootPath, "erp", "exports", "order-1.xml")).ShouldBeFalse();
        await using var downloaded = await _service.DownloadAsync(destinationKey);
        using var reader = new StreamReader(downloaded);
        (await reader.ReadToEndAsync()).ShouldBe("content");
    }

    [Test]
    public async Task MoveAsync_PreservesWriteTimestamp_SoMovedFilesStayListable()
    {
        var sourceKey = DropPointPrefix + "order-1.xml";
        var destinationKey = DropPointPrefix + "processed/order-1.xml";
        await _service.UploadAsync(sourceKey, ToStream("content"));
        MarkStable(sourceKey);

        await _service.MoveAsync(sourceKey, destinationKey);

        var keys = await _service.ListAsync(DropPointPrefix + "processed/");
        keys.ShouldBe(new[] { destinationKey });
    }

    [Test]
    public void ToSafePath_KeyEscapingRoot_Throws()
    {
        Should.Throw<ArgumentException>(() => _service.DownloadAsync("../outside.xml"));
        Should.Throw<ArgumentException>(() => _service.DownloadAsync("erp/../../outside.xml"));
    }

    [Test]
    public void ToSafePath_AbsoluteKeyOutsideRoot_Throws()
    {
        Should.Throw<ArgumentException>(() => _service.DownloadAsync("/absolute/outside.xml"));
    }

    [Test]
    public void ToSafePath_EmptyKey_Throws()
    {
        Should.Throw<ArgumentException>(() => _service.DownloadAsync(" "));
    }
}
