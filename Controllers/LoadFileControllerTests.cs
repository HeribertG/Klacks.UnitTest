// Copyright (c) Heribert Gasparoli Private. All rights reserved.

using Klacks.Api.Application.Constants;
using Klacks.Api.Presentation.Controllers.UserBackend;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;

namespace Klacks.UnitTest.Controllers;

[TestFixture]
public class LoadFileControllerTests
{
    private const long SmallFileSizeBytes = 1024;

    private IFileUploadService _fileUploadService = null!;
    private LoadFileController _controller = null!;

    [SetUp]
    public void Setup()
    {
        _fileUploadService = Substitute.For<IFileUploadService>();
        var configuration = Substitute.For<IConfiguration>();
        _controller = new LoadFileController(configuration, _fileUploadService);
    }

    [TestCase("image/jpeg")]
    [TestCase("image/png")]
    [TestCase("image/gif")]
    [TestCase("image/x-icon")]
    public async Task Upload_AllowedImageTypeWithinSizeLimit_StoresFileAndReturnsOk(string contentType)
    {
        var file = CreateFile(contentType, SmallFileSizeBytes);

        var result = await _controller.SingleFile(file);

        result.ShouldBeOfType<OkResult>();
        await _fileUploadService.Received(1).StoreFileAsync(file);
    }

    [Test]
    public async Task Upload_FileExactlyAtSizeLimit_StoresFileAndReturnsOk()
    {
        var file = CreateFile("image/png", FileUploadConstants.MaxImageUploadSizeBytes);

        var result = await _controller.SingleFile(file);

        result.ShouldBeOfType<OkResult>();
        await _fileUploadService.Received(1).StoreFileAsync(file);
    }

    [TestCase("application/pdf")]
    [TestCase("image/svg+xml")]
    [TestCase("text/html")]
    [TestCase("application/octet-stream")]
    public async Task Upload_DisallowedContentType_ReturnsBadRequestAndDoesNotStore(string contentType)
    {
        var file = CreateFile(contentType, SmallFileSizeBytes);

        var result = await _controller.SingleFile(file);

        var badRequest = result.ShouldBeOfType<BadRequestObjectResult>();
        badRequest.Value.ShouldBe(FileUploadConstants.InvalidContentTypeMessage);
        await _fileUploadService.DidNotReceive().StoreFileAsync(Arg.Any<IFormFile>());
    }

    [Test]
    public async Task Upload_FileLargerThanSizeLimit_ReturnsBadRequestAndDoesNotStore()
    {
        var file = CreateFile("image/jpeg", FileUploadConstants.MaxImageUploadSizeBytes + 1);

        var result = await _controller.SingleFile(file);

        var badRequest = result.ShouldBeOfType<BadRequestObjectResult>();
        badRequest.Value.ShouldBe(FileUploadConstants.FileTooLargeMessage);
        await _fileUploadService.DidNotReceive().StoreFileAsync(Arg.Any<IFormFile>());
    }

    [Test]
    public async Task Upload_AllowedContentTypeWithDifferentCasing_StoresFileAndReturnsOk()
    {
        var file = CreateFile("IMAGE/JPEG", SmallFileSizeBytes);

        var result = await _controller.SingleFile(file);

        result.ShouldBeOfType<OkResult>();
        await _fileUploadService.Received(1).StoreFileAsync(file);
    }

    private static IFormFile CreateFile(string contentType, long length)
    {
        var file = Substitute.For<IFormFile>();
        file.ContentType.Returns(contentType);
        file.Length.Returns(length);
        file.FileName.Returns("test-upload.png");
        return file;
    }
}
