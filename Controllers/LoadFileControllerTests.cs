// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

using System.Security.Claims;
using System.Text;
using Klacks.Api.Application.Constants;
using Klacks.Api.Application.Services.FileHandling;
using Klacks.Api.Domain.Constants;
using Klacks.Api.Presentation.Controllers.UserBackend;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Klacks.UnitTest.Controllers;

[TestFixture]
public class LoadFileControllerTests
{
    private const string PngContentType = "image/png";
    private const string JpegContentType = "image/jpeg";
    private const string GifContentType = "image/gif";
    private const string IconContentType = "image/x-icon";

    private static readonly byte[] PngBytes = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0x00, 0x00];
    private static readonly byte[] JpegBytes = [0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x10, 0x4A, 0x46, 0x49, 0x46];
    private static readonly byte[] GifBytes = Encoding.ASCII.GetBytes("GIF89a0000");
    private static readonly byte[] IcoBytes = [0x00, 0x00, 0x01, 0x00, 0x01, 0x00, 0x10, 0x10, 0x00, 0x00];
    private static readonly byte[] HtmlBytes = Encoding.ASCII.GetBytes("<html><script>alert(1)</script></html>");

    private static readonly Guid CallerId = Guid.Parse("11111111-2222-3333-4444-555555555555");
    private static readonly Guid OtherUserId = Guid.Parse("99999999-8888-7777-6666-555555555555");

    private IFileUploadService _fileUploadService = null!;
    private LoadFileController _controller = null!;

    [SetUp]
    public void Setup()
    {
        _fileUploadService = Substitute.For<IFileUploadService>();
        _controller = new LoadFileController(_fileUploadService, Substitute.For<ILogger<LoadFileController>>());
        SetCaller(CallerId);
    }

    [TestCase(JpegContentType)]
    [TestCase(PngContentType)]
    [TestCase(GifContentType)]
    public async Task Upload_AdminLogoWithMatchingContent_StoresUnderLogoName(string contentType)
    {
        SetCaller(CallerId, Roles.Admin);
        var file = CreateFile(FileUploadConstants.LogoFileName, contentType, BytesFor(contentType));

        var result = await _controller.SingleFile(file);

        result.ShouldBeOfType<OkResult>();
        await _fileUploadService.Received(1).StoreImageAsync(
            Arg.Is<ImageFileTarget>(target => target.Slot == ImageFileSlot.Logo && target.FileName == FileUploadConstants.LogoFileName),
            Arg.Any<Stream>(),
            Arg.Any<CancellationToken>());
    }

    [TestCase(IconContentType)]
    [TestCase(PngContentType)]
    public async Task Upload_AdminIconWithMatchingContent_StoresUnderIconName(string contentType)
    {
        SetCaller(CallerId, Roles.Admin);
        var file = CreateFile(FileUploadConstants.IconFileName, contentType, BytesFor(contentType));

        var result = await _controller.SingleFile(file);

        result.ShouldBeOfType<OkResult>();
        await _fileUploadService.Received(1).StoreImageAsync(
            Arg.Is<ImageFileTarget>(target => target.Slot == ImageFileSlot.Icon && target.FileName == FileUploadConstants.IconFileName),
            Arg.Any<Stream>(),
            Arg.Any<CancellationToken>());
    }

    [TestCase(FileUploadConstants.LogoFileName)]
    [TestCase(FileUploadConstants.IconFileName)]
    [TestCase("report-img-0f8fad5b-d9cb-469f-a165-70867728950e.png")]
    public async Task Upload_NonAdminSettingsImage_Returns403AndDoesNotStore(string fileName)
    {
        SetCaller(CallerId, Roles.Authorised);
        var file = CreateFile(fileName, PngContentType, PngBytes);

        var result = await _controller.SingleFile(file);

        result.ShouldBeOfType<StatusCodeResult>().StatusCode.ShouldBe(StatusCodes.Status403Forbidden);
        await AssertNothingStored();
    }

    [Test]
    public async Task Upload_AdminReportImage_StoresUnderReportImageName()
    {
        SetCaller(CallerId, Roles.Admin);
        const string reportImage = "report-img-0f8fad5b-d9cb-469f-a165-70867728950e.png";
        var file = CreateFile(reportImage, JpegContentType, JpegBytes);

        var result = await _controller.SingleFile(file);

        result.ShouldBeOfType<OkResult>();
        await _fileUploadService.Received(1).StoreImageAsync(
            Arg.Is<ImageFileTarget>(target => target.Slot == ImageFileSlot.ReportImage && target.FileName == reportImage),
            Arg.Any<Stream>(),
            Arg.Any<CancellationToken>());
    }

    [TestCase(".png", PngContentType)]
    [TestCase(".jpg", JpegContentType)]
    [TestCase(".jpeg", JpegContentType)]
    [TestCase(".gif", GifContentType)]
    public async Task Upload_OwnProfilePicture_StoresUnderOwnProfileName(string extension, string contentType)
    {
        var fileName = $"{CallerId}profile{extension}";
        var file = CreateFile(fileName, contentType, BytesFor(contentType));

        var result = await _controller.SingleFile(file);

        result.ShouldBeOfType<OkResult>();
        await _fileUploadService.Received(1).StoreImageAsync(
            Arg.Is<ImageFileTarget>(target =>
                target.Slot == ImageFileSlot.Profile && target.OwnerUserId == CallerId && target.FileName == fileName),
            Arg.Any<Stream>(),
            Arg.Any<CancellationToken>());
    }

    [TestCase("")]
    [TestCase(Roles.Admin)]
    public async Task Upload_OtherUsersProfilePicture_Returns403AndDoesNotStore(string role)
    {
        SetCaller(CallerId, role);
        var file = CreateFile($"{OtherUserId}profile.png", PngContentType, PngBytes);

        var result = await _controller.SingleFile(file);

        result.ShouldBeOfType<StatusCodeResult>().StatusCode.ShouldBe(StatusCodes.Status403Forbidden);
        await AssertNothingStored();
    }

    [TestCase("test-upload.png")]
    [TestCase("evil.html")]
    [TestCase("../own-logo.png")]
    [TestCase("own-logo.png.exe")]
    [TestCase("OWN-LOGO.PNG")]
    [TestCase("11111111-2222-3333-4444-555555555555profile.svg")]
    [TestCase("../11111111-2222-3333-4444-555555555555profile.png")]
    [TestCase("x11111111-2222-3333-4444-555555555555profile.png")]
    [TestCase("report-img-not-a-guid.png")]
    public async Task Upload_ArbitraryFileName_ReturnsBadRequestAndDoesNotStore(string fileName)
    {
        SetCaller(CallerId, Roles.Admin);
        var file = CreateFile(fileName, PngContentType, PngBytes);

        var result = await _controller.SingleFile(file);

        result.ShouldBeOfType<BadRequestObjectResult>().Value.ShouldBe(FileUploadConstants.InvalidFileNameMessage);
        await AssertNothingStored();
    }

    [Test]
    public async Task Upload_SpoofedContentTypeWithHtmlContent_ReturnsBadRequestAndDoesNotStore()
    {
        var file = CreateFile($"{CallerId}profile.png", PngContentType, HtmlBytes);

        var result = await _controller.SingleFile(file);

        result.ShouldBeOfType<BadRequestObjectResult>().Value.ShouldBe(FileUploadConstants.ContentMismatchMessage);
        await AssertNothingStored();
    }

    [Test]
    public async Task Upload_ContentTypeDisagreesWithMagicBytes_ReturnsBadRequest()
    {
        var file = CreateFile($"{CallerId}profile.png", PngContentType, JpegBytes);

        var result = await _controller.SingleFile(file);

        result.ShouldBeOfType<BadRequestObjectResult>().Value.ShouldBe(FileUploadConstants.ContentMismatchMessage);
        await AssertNothingStored();
    }

    [Test]
    public async Task Upload_ProfileExtensionDisagreesWithContent_ReturnsBadRequest()
    {
        var file = CreateFile($"{CallerId}profile.gif", PngContentType, PngBytes);

        var result = await _controller.SingleFile(file);

        result.ShouldBeOfType<BadRequestObjectResult>().Value.ShouldBe(FileUploadConstants.ContentMismatchMessage);
        await AssertNothingStored();
    }

    [Test]
    public async Task Upload_IcoContentForLogo_ReturnsBadRequest()
    {
        SetCaller(CallerId, Roles.Admin);
        var file = CreateFile(FileUploadConstants.LogoFileName, IconContentType, IcoBytes);

        var result = await _controller.SingleFile(file);

        result.ShouldBeOfType<BadRequestObjectResult>().Value.ShouldBe(FileUploadConstants.ContentMismatchMessage);
        await AssertNothingStored();
    }

    [TestCase("application/pdf")]
    [TestCase("image/svg+xml")]
    [TestCase("text/html")]
    [TestCase("application/octet-stream")]
    public async Task Upload_DisallowedContentType_ReturnsBadRequestAndDoesNotStore(string contentType)
    {
        var file = CreateFile($"{CallerId}profile.png", contentType, PngBytes);

        var result = await _controller.SingleFile(file);

        result.ShouldBeOfType<BadRequestObjectResult>().Value.ShouldBe(FileUploadConstants.InvalidContentTypeMessage);
        await AssertNothingStored();
    }

    [Test]
    public async Task Upload_FileExactlyAtSizeLimit_Stores()
    {
        var file = CreateFile($"{CallerId}profile.png", PngContentType, PngBytes, FileUploadConstants.MaxImageUploadSizeBytes);

        var result = await _controller.SingleFile(file);

        result.ShouldBeOfType<OkResult>();
    }

    [Test]
    public async Task Upload_FileLargerThanSizeLimit_ReturnsBadRequestAndDoesNotStore()
    {
        var file = CreateFile($"{CallerId}profile.png", PngContentType, PngBytes, FileUploadConstants.MaxImageUploadSizeBytes + 1);

        var result = await _controller.SingleFile(file);

        result.ShouldBeOfType<BadRequestObjectResult>().Value.ShouldBe(FileUploadConstants.FileTooLargeMessage);
        await AssertNothingStored();
    }

    [Test]
    public async Task Upload_AllowedContentTypeWithDifferentCasing_Stores()
    {
        var file = CreateFile($"{CallerId}profile.jpg", "IMAGE/JPEG", JpegBytes);

        var result = await _controller.SingleFile(file);

        result.ShouldBeOfType<OkResult>();
    }

    [TestCase(FileUploadConstants.LogoFileName)]
    [TestCase(FileUploadConstants.IconFileName)]
    public void Delete_NonAdminSettingsImage_Returns403AndDoesNotDelete(string type)
    {
        SetCaller(CallerId, Roles.Authorised);

        var result = _controller.DeleteFile(type);

        result.ShouldBeOfType<StatusCodeResult>().StatusCode.ShouldBe(StatusCodes.Status403Forbidden);
        _fileUploadService.DidNotReceive().DeleteImage(Arg.Any<ImageFileTarget>());
    }

    [TestCase(FileUploadConstants.LogoFileName)]
    [TestCase(FileUploadConstants.IconFileName)]
    public void Delete_AdminSettingsImage_Deletes(string type)
    {
        SetCaller(CallerId, Roles.Admin);

        var result = _controller.DeleteFile(type);

        result.ShouldBeOfType<OkResult>();
        _fileUploadService.Received(1).DeleteImage(Arg.Is<ImageFileTarget>(target => target.FileName == type));
    }

    [Test]
    public void Delete_OwnProfilePicture_DeletesAllVariants()
    {
        var result = _controller.DeleteFile($"{CallerId}profile");

        result.ShouldBeOfType<OkResult>();
        _fileUploadService.Received(1).DeleteImage(Arg.Is<ImageFileTarget>(target =>
            target.OwnerUserId == CallerId && target.StoredFileNames.Count == FileUploadConstants.ProfileExtensions.Count));
    }

    [TestCase("")]
    [TestCase(Roles.Admin)]
    public void Delete_OtherUsersProfilePicture_Returns403AndDoesNotDelete(string role)
    {
        SetCaller(CallerId, role);

        var result = _controller.DeleteFile($"{OtherUserId}profile");

        result.ShouldBeOfType<StatusCodeResult>().StatusCode.ShouldBe(StatusCodes.Status403Forbidden);
        _fileUploadService.DidNotReceive().DeleteImage(Arg.Any<ImageFileTarget>());
    }

    [TestCase("appsettings.json")]
    [TestCase("anything-profile")]
    public void Delete_ArbitraryName_ReturnsBadRequest(string type)
    {
        SetCaller(CallerId, Roles.Admin);

        var result = _controller.DeleteFile(type);

        result.ShouldBeOfType<BadRequestObjectResult>();
        _fileUploadService.DidNotReceive().DeleteImage(Arg.Any<ImageFileTarget>());
    }

    [Test]
    public async Task Download_ExistingLogo_ReturnsImage()
    {
        _fileUploadService.ReadImageAsync(
                Arg.Is<ImageFileTarget>(target => target.Slot == ImageFileSlot.Logo), Arg.Any<CancellationToken>())
            .Returns(PngBytes);

        var result = await _controller.SingleFile(FileUploadConstants.LogoFileName);

        result.ContentType.ShouldBe(PngContentType);
        result.FileContents.ShouldBe(PngBytes);
    }

    [Test]
    public async Task Download_MissingFile_ReturnsPlainTextNotFoundForUiCompatibility()
    {
        _fileUploadService.ReadImageAsync(Arg.Any<ImageFileTarget>(), Arg.Any<CancellationToken>())
            .Returns((byte[]?)null);

        var result = await _controller.SingleFile(FileUploadConstants.IconFileName);

        result.ContentType.ShouldBe("text/plain");
        Encoding.UTF8.GetString(result.FileContents).ShouldBe(FileUploadConstants.FileNotFoundMessage);
    }

    [Test]
    public async Task Download_ReadFailure_DoesNotLeakExceptionMessage()
    {
        const string secretDetail = "C:\\secret\\path denied";
        _fileUploadService.ReadImageAsync(Arg.Any<ImageFileTarget>(), Arg.Any<CancellationToken>())
            .Returns<byte[]?>(_ => throw new IOException(secretDetail));

        var result = await _controller.SingleFile($"{CallerId}profile");

        var body = Encoding.UTF8.GetString(result.FileContents);
        body.ShouldBe(FileUploadConstants.FileNotFoundMessage);
        body.ShouldNotContain(secretDetail);
    }

    [Test]
    public async Task Download_ArbitraryName_ReturnsNotFoundWithoutReading()
    {
        var result = await _controller.SingleFile("../appsettings.json");

        Encoding.UTF8.GetString(result.FileContents).ShouldBe(FileUploadConstants.FileNotFoundMessage);
        await _fileUploadService.DidNotReceive().ReadImageAsync(Arg.Any<ImageFileTarget>(), Arg.Any<CancellationToken>());
    }

    private async Task AssertNothingStored()
    {
        await _fileUploadService.DidNotReceive().StoreImageAsync(
            Arg.Any<ImageFileTarget>(), Arg.Any<Stream>(), Arg.Any<CancellationToken>());
    }

    private void SetCaller(Guid userId, params string[] roles)
    {
        var claims = new List<Claim> { new(ClaimTypes.NameIdentifier, userId.ToString()) };
        claims.AddRange(roles.Where(role => role.Length > 0).Select(role => new Claim(ClaimTypes.Role, role)));

        _controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity(claims, "TestAuth")) }
        };
    }

    private static byte[] BytesFor(string contentType)
    {
        return contentType switch
        {
            PngContentType => PngBytes,
            JpegContentType => JpegBytes,
            GifContentType => GifBytes,
            IconContentType => IcoBytes,
            _ => throw new ArgumentOutOfRangeException(nameof(contentType)),
        };
    }

    private static IFormFile CreateFile(string fileName, string contentType, byte[] content, long? length = null)
    {
        var file = Substitute.For<IFormFile>();
        file.FileName.Returns(fileName);
        file.ContentType.Returns(contentType);
        file.Length.Returns(length ?? content.Length);
        file.OpenReadStream().Returns(_ => new MemoryStream(content));
        return file;
    }
}
