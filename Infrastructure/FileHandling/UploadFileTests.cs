// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

using Klacks.Api.Application.Services.FileHandling;
using Klacks.Api.Infrastructure.FileHandling;
using Klacks.Api.Infrastructure.Hosting;
using Microsoft.Extensions.Configuration;

namespace Klacks.UnitTest.Infrastructure.FileHandling;

[TestFixture]
public class UploadFileTests
{
    private static readonly Guid OwnerId = Guid.Parse("11111111-2222-3333-4444-555555555555");

    private string _directory = null!;
    private UploadFile _service = null!;

    [SetUp]
    public void SetUp()
    {
        _directory = Path.Combine(Path.GetTempPath(), "klacks-upload-tests-" + Guid.NewGuid().ToString("N"));
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { [DataRootPaths.ImagesKey] = _directory })
            .Build();
        _service = new UploadFile(configuration);
    }

    [TearDown]
    public void TearDown()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    [Test]
    public async Task StoreImageAsync_Profile_WritesCanonicalNameAndRemovesOtherVariants()
    {
        Directory.CreateDirectory(_directory);
        var stalePng = Path.Combine(_directory, $"{OwnerId}profile.png");
        await File.WriteAllBytesAsync(stalePng, [1]);
        var target = ImageFileNamePolicy.ResolveUpload($"{OwnerId}profile.jpg")!;

        await _service.StoreImageAsync(target, new MemoryStream([7, 8, 9]), CancellationToken.None);

        File.Exists(stalePng).ShouldBeFalse();
        (await File.ReadAllBytesAsync(Path.Combine(_directory, $"{OwnerId}profile.jpg"))).ShouldBe(new byte[] { 7, 8, 9 });
        (await _service.ReadImageAsync(ImageFileNamePolicy.ResolveStored($"{OwnerId}profile")!, CancellationToken.None))
            .ShouldBe(new byte[] { 7, 8, 9 });
    }

    [Test]
    public async Task StoreImageAsync_FileNameOutsideStoredNames_Throws()
    {
        var target = new ImageFileTarget(ImageFileSlot.Logo, null, "../evil.png", ["own-logo.png"], null);

        await Should.ThrowAsync<InvalidOperationException>(
            () => _service.StoreImageAsync(target, new MemoryStream([1]), CancellationToken.None));
    }

    [Test]
    public async Task StoreImageAsync_FileNameWithDirectoryPart_Throws()
    {
        var target = new ImageFileTarget(ImageFileSlot.Logo, null, "../evil.png", ["../evil.png"], null);

        await Should.ThrowAsync<InvalidOperationException>(
            () => _service.StoreImageAsync(target, new MemoryStream([1]), CancellationToken.None));
    }

    [Test]
    public void DeleteImage_Profile_DeletesEveryVariant()
    {
        Directory.CreateDirectory(_directory);
        var png = Path.Combine(_directory, $"{OwnerId}profile.png");
        var gif = Path.Combine(_directory, $"{OwnerId}profile.gif");
        File.WriteAllBytes(png, [1]);
        File.WriteAllBytes(gif, [1]);

        _service.DeleteImage(ImageFileNamePolicy.ResolveStored($"{OwnerId}profile")!);

        File.Exists(png).ShouldBeFalse();
        File.Exists(gif).ShouldBeFalse();
    }

    [Test]
    public async Task ReadImageAsync_MissingDirectory_ReturnsNull()
    {
        var result = await _service.ReadImageAsync(ImageFileNamePolicy.ResolveStored("own-logo.png")!, CancellationToken.None);

        result.ShouldBeNull();
    }
}
