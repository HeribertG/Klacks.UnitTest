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
    private const string AbcSha256 = "ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad";
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
    public async Task UploadAsync_ReturnsSizeAndSha256OfTheRawBytes()
    {
        var info = await _service.UploadAsync(DropPointPrefix + "order-1.xml", ToStream("abc"));

        info.SizeBytes.ShouldBe(3);
        info.Sha256.ShouldBe(AbcSha256);
    }

    [Test]
    public async Task GetObjectInfoAsync_AfterUpload_ReturnsTheSameInfoAsTheUpload()
    {
        var key = DropPointPrefix + "order-1.xml";
        var uploaded = await _service.UploadAsync(key, ToStream("<ErpOrderImport />"));

        var info = await _service.GetObjectInfoAsync(key);

        info.ShouldBe(uploaded);
        info!.Sha256.ShouldBe(ErpImportFileHasher.ComputeSha256Hex(ToStream("<ErpOrderImport />")));
    }

    [Test]
    public async Task GetObjectInfoAsync_ReportsTheLastWriteTimeInUtc()
    {
        var key = DropPointPrefix + "order-2.xml";
        await _service.UploadAsync(key, ToStream("<ErpOrderImport />"));
        var writtenAt = new DateTime(2026, 10, 18, 21, 30, 0, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(_service.ResolvePath(key), writtenAt);

        var info = await _service.GetObjectInfoAsync(key);

        info!.LastModifiedUtc.ShouldBe(writtenAt);
        info.LastModifiedUtc!.Value.Kind.ShouldBe(DateTimeKind.Utc);
    }

    [Test]
    public async Task GetObjectInfoAsync_FileDroppedDirectlyIntoTheFolder_HashesItsRawBytes()
    {
        var bytes = new byte[] { 0xEF, 0xBB, 0xBF, 0x3C, 0x61, 0x2F, 0x3E };
        Directory.CreateDirectory(Path.Combine(_rootPath, "erp", "exports"));
        await File.WriteAllBytesAsync(Path.Combine(_rootPath, "erp", "exports", "dropped.xml"), bytes);

        var info = await _service.GetObjectInfoAsync(DropPointPrefix + "dropped.xml");

        info.ShouldNotBeNull();
        info.SizeBytes.ShouldBe(bytes.Length);
        info.Sha256.ShouldBe(ErpImportFileHasher.ComputeSha256Hex(new MemoryStream(bytes)));
    }

    [Test]
    public async Task GetObjectInfoAsync_MissingObject_ReturnsNull()
    {
        await _service.UploadAsync(DropPointPrefix + "order-1.xml", ToStream("a"));

        (await _service.GetObjectInfoAsync(DropPointPrefix + "missing.xml")).ShouldBeNull();
    }

    [Test]
    public async Task GetObjectInfoAsync_MissingDirectory_ReturnsNull()
    {
        (await _service.GetObjectInfoAsync("never/created/order.xml")).ShouldBeNull();
    }

    [Test]
    public async Task GetObjectInfoAsync_KeyEscapingRoot_Throws()
    {
        await Should.ThrowAsync<ArgumentException>(() => _service.GetObjectInfoAsync("../outside.xml"));
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
    public async Task TryMoveAsync_FromLegacySubFolderBackIntoTheInbox_KeepsContentAndWriteTimestamp()
    {
        var sourceKey = DropPointPrefix + "processed/order-1.xml";
        var destinationKey = DropPointPrefix + "0123456789abcdef0123456789abcdef-order-1.xml";
        await _service.UploadAsync(sourceKey, ToStream("content"));
        MarkStable(sourceKey);
        var sourceTimestamp = (await _service.ListWithMetadataAsync(sourceKey)).Single().LastModifiedUtc;

        var moved = await _service.TryMoveAsync(sourceKey, destinationKey);

        moved.ShouldBeTrue();
        (await _service.ListAsync(DropPointPrefix)).ShouldBe(new[] { destinationKey }, "the moved file keeps its age and stays listable");
        (await _service.ListWithMetadataAsync(destinationKey)).Single().LastModifiedUtc.ShouldBe(sourceTimestamp);
        await using var downloaded = await _service.DownloadAsync(destinationKey);
        using var reader = new StreamReader(downloaded);
        (await reader.ReadToEndAsync()).ShouldBe("content");
    }

    [Test]
    public async Task TryMoveAsync_FreeDestination_MovesTheFileAndKeepsItListable()
    {
        var sourceKey = DropPointPrefix + "orders.xml";
        var destinationKey = DropPointPrefix + "0123456789abcdef0123456789abcdef-orders.xml";
        await _service.UploadAsync(sourceKey, ToStream("content"));
        MarkStable(sourceKey);

        var moved = await _service.TryMoveAsync(sourceKey, destinationKey);

        moved.ShouldBeTrue();
        (await _service.ListAsync(DropPointPrefix)).ShouldBe(new[] { destinationKey });
    }

    [Test]
    public async Task TryMoveAsync_OccupiedDestination_ReturnsFalseAndOverwritesNothing()
    {
        var sourceKey = DropPointPrefix + "orders.xml";
        var destinationKey = DropPointPrefix + "taken.xml";
        await _service.UploadAsync(sourceKey, ToStream("new"));
        await _service.UploadAsync(destinationKey, ToStream("existing"));

        var moved = await _service.TryMoveAsync(sourceKey, destinationKey);

        moved.ShouldBeFalse();
        await using var downloaded = await _service.DownloadAsync(destinationKey);
        using var reader = new StreamReader(downloaded);
        (await reader.ReadToEndAsync()).ShouldBe("existing");
        File.Exists(Path.Combine(_rootPath, "erp", "exports", "orders.xml")).ShouldBeTrue();
    }

    [Test]
    public async Task TryMoveAsync_SourceAlreadyMovedByAnotherCaller_ReturnsFalse()
    {
        var moved = await _service.TryMoveAsync(DropPointPrefix + "gone.xml", DropPointPrefix + "target.xml");

        moved.ShouldBeFalse();
    }

    [Test]
    public async Task DeleteAsync_RemovesFile()
    {
        var key = DropPointPrefix + "order-1.xml";
        await _service.UploadAsync(key, ToStream("content"));

        await _service.DeleteAsync(key);

        File.Exists(Path.Combine(_rootPath, "erp", "exports", "order-1.xml")).ShouldBeFalse();
    }

    [Test]
    public async Task DeleteAsync_MissingFile_IsANoOp()
    {
        await _service.UploadAsync(DropPointPrefix + "other.xml", ToStream("content"));

        await Should.NotThrowAsync(() => _service.DeleteAsync(DropPointPrefix + "gone.xml"));
        await Should.NotThrowAsync(() => _service.DeleteAsync("missing-folder/gone.xml"));
    }

    [Test]
    public void DeleteAsync_KeyEscapingRoot_Throws()
    {
        Should.Throw<ArgumentException>(() => _service.DeleteAsync("../outside.xml"));
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

    [Test]
    public void ResolvePath_ReturnsAbsolutePathUnderRoot()
    {
        var resolved = _service.ResolvePath(DropPointPrefix);

        var expected = Path.GetFullPath(Path.Combine(_rootPath, "erp", "exports") + Path.DirectorySeparatorChar);
        resolved.ShouldBe(expected);
    }

    [Test]
    public void ResolvePath_PerformsNoFileSystemIo()
    {
        _service.ResolvePath(DropPointPrefix);

        Directory.Exists(_rootPath).ShouldBeFalse();
    }

    [Test]
    public void ResolvePath_KeyEscapingRoot_Throws()
    {
        Should.Throw<ArgumentException>(() => _service.ResolvePath("../outside.xml"));
    }

    [Test]
    public void ResolvePath_EmptyKey_Throws()
    {
        Should.Throw<ArgumentException>(() => _service.ResolvePath(" "));
    }
}
