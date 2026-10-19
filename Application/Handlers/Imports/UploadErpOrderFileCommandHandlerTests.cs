// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Unit tests for UploadErpOrderFileCommandHandler: the file is stored under the drop point's prefix and
/// registered as a Received ledger row carrying the caller's channel (token push or admin UI upload),
/// uploader, size, hash and duplicate link, committed once; a disabled or unknown drop point and an empty
/// file name are rejected before anything is stored or registered.
/// </summary>

using System.Text;
using Klacks.Api.Application.Commands.Imports;
using Klacks.Api.Application.Handlers.Imports;
using Klacks.Api.Application.Services.Imports;
using Klacks.Api.Domain.Exceptions;
using Klacks.Api.Domain.Interfaces.Imports;
using Klacks.Api.Domain.Models.Imports;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;

namespace Klacks.UnitTest.Application.Handlers.Imports;

[TestFixture]
public class UploadErpOrderFileCommandHandlerTests
{
    private const string BucketPrefix = "customer-1/orders";
    private const string TokenName = "ERP vendor token";
    private const string Sha = "dddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddd";
    private const long SizeBytes = 99;

    private IErpDropPointRepository _dropPointRepository = null!;
    private IObjectStorageService _objectStorageService = null!;
    private IErpImportFileRepository _fileRepository = null!;
    private IUnitOfWork _unitOfWork = null!;
    private UploadErpOrderFileCommandHandler _handler = null!;
    private ErpDropPoint _dropPoint = null!;
    private List<ErpImportFile> _added = null!;

    [SetUp]
    public void SetUp()
    {
        _dropPointRepository = Substitute.For<IErpDropPointRepository>();
        _objectStorageService = Substitute.For<IObjectStorageService>();
        _fileRepository = Substitute.For<IErpImportFileRepository>();
        _unitOfWork = Substitute.For<IUnitOfWork>();
        _dropPoint = new ErpDropPoint { Id = Guid.NewGuid(), SourceSystemId = "erp-1", BucketPrefix = BucketPrefix, IsEnabled = true };
        _dropPointRepository.GetNoTracking(_dropPoint.Id).Returns(_dropPoint);
        _objectStorageService.UploadAsync(Arg.Any<string>(), Arg.Any<Stream>(), Arg.Any<CancellationToken>())
            .Returns(new StoredObjectInfo(SizeBytes, Sha));
        _added = [];
        _fileRepository.Add(Arg.Do<ErpImportFile>(file => _added.Add(file)));

        var registrar = new ErpImportFileRegistrar(
            _fileRepository, _unitOfWork, TimeProvider.System, NullLogger<ErpImportFileRegistrar>.Instance);
        _handler = new UploadErpOrderFileCommandHandler(_dropPointRepository, _objectStorageService, registrar);
    }

    [TestCase(ErpImportChannel.TokenPush, TokenName)]
    [TestCase(ErpImportChannel.UiUpload, "admin")]
    public async Task Handle_StoresTheFile_AndRegistersAReceivedRowWithChannelAndUploader(ErpImportChannel channel, string uploadedBy)
    {
        using var content = Xml();

        var key = await _handler.Handle(
            new UploadErpOrderFileCommand(_dropPoint.Id, "orders.xml", content, channel, uploadedBy), CancellationToken.None);

        key.ShouldStartWith(BucketPrefix + "/");
        key.ShouldEndWith("-orders.xml");
        var file = _added.ShouldHaveSingleItem();
        file.StorageKey.ShouldBe(key);
        file.FileName.ShouldBe("orders.xml");
        file.DropPointId.ShouldBe(_dropPoint.Id);
        file.SourceSystemId.ShouldBe("erp-1");
        file.Channel.ShouldBe(channel);
        file.UploadedBy.ShouldBe(uploadedBy);
        file.SizeBytes.ShouldBe(SizeBytes);
        file.Sha256.ShouldBe(Sha);
        file.Status.ShouldBe(ErpImportFileStatus.Received);
        file.DuplicateOfFileId.ShouldBeNull();
        Received.InOrder(() =>
        {
            _objectStorageService.UploadAsync(key, content, Arg.Any<CancellationToken>());
            _fileRepository.Add(file);
            _unitOfWork.CompleteAsync();
        });
    }

    [Test]
    public async Task Handle_SameHashAsAnEarlierDelivery_LinksTheEarlierRow()
    {
        var earlier = new ErpImportFile { Id = Guid.NewGuid() };
        _fileRepository.FindDuplicateAsync(_dropPoint.Id, Sha, null, Arg.Any<CancellationToken>()).Returns(earlier);
        using var content = Xml();

        await _handler.Handle(
            new UploadErpOrderFileCommand(_dropPoint.Id, "orders.xml", content, ErpImportChannel.TokenPush, TokenName), CancellationToken.None);

        _added.ShouldHaveSingleItem().DuplicateOfFileId.ShouldBe(earlier.Id);
    }

    [Test]
    public async Task Handle_TokenWithoutName_RegistersRowWithoutUploader()
    {
        using var content = Xml();

        await _handler.Handle(
            new UploadErpOrderFileCommand(_dropPoint.Id, "orders.xml", content, ErpImportChannel.TokenPush, null), CancellationToken.None);

        _added.ShouldHaveSingleItem().UploadedBy.ShouldBeNull();
    }

    [Test]
    public async Task Handle_DisabledDropPoint_Throws_AndNeitherStoresNorRegisters()
    {
        _dropPoint.IsEnabled = false;
        using var content = Xml();

        await Should.ThrowAsync<InvalidRequestException>(() => _handler.Handle(
            new UploadErpOrderFileCommand(_dropPoint.Id, "orders.xml", content, ErpImportChannel.TokenPush, TokenName), CancellationToken.None));

        await _objectStorageService.DidNotReceive().UploadAsync(Arg.Any<string>(), Arg.Any<Stream>(), Arg.Any<CancellationToken>());
        _added.ShouldBeEmpty();
        await _unitOfWork.DidNotReceive().CompleteAsync();
    }

    [Test]
    public async Task Handle_EmptyFileName_Throws_AndNeitherStoresNorRegisters()
    {
        using var content = Xml();

        await Should.ThrowAsync<InvalidRequestException>(() => _handler.Handle(
            new UploadErpOrderFileCommand(_dropPoint.Id, "orders/", content, ErpImportChannel.UiUpload, "admin"), CancellationToken.None));

        await _objectStorageService.DidNotReceive().UploadAsync(Arg.Any<string>(), Arg.Any<Stream>(), Arg.Any<CancellationToken>());
        _added.ShouldBeEmpty();
    }

    private static MemoryStream Xml() => new(Encoding.UTF8.GetBytes("<ErpOrderImport />"));
}
