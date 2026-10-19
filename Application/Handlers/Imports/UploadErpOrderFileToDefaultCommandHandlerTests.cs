using System.Text;
using Klacks.Api.Application.Commands.Imports;
using Klacks.Api.Application.Handlers.Imports;
using Klacks.Api.Application.Services.Imports;
using Klacks.Api.Domain.Exceptions;
using Klacks.Api.Domain.Interfaces.Imports;
using Klacks.Api.Domain.Models.Imports;
using Klacks.Api.Infrastructure.Mediator;
using Microsoft.Extensions.Logging.Abstractions;

namespace Klacks.UnitTest.Application.Handlers.Imports;

[TestFixture]
public class UploadErpOrderFileToDefaultCommandHandlerTests
{
    private const string BucketPrefix = "erp/orders";
    private const string Uploader = "admin";
    private const string Sha = "cccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccc";
    private const long SizeBytes = 18;

    private IErpDefaultDropPointProvider _defaultDropPointProvider = null!;
    private IObjectStorageService _objectStorageService = null!;
    private IErpImportFileRepository _fileRepository = null!;
    private IUnitOfWork _unitOfWork = null!;
    private IMediator _mediator = null!;
    private UploadErpOrderFileToDefaultCommandHandler _handler = null!;
    private ErpDropPoint _dropPoint = null!;
    private List<ErpImportFile> _added = null!;

    [SetUp]
    public void SetUp()
    {
        _defaultDropPointProvider = Substitute.For<IErpDefaultDropPointProvider>();
        _objectStorageService = Substitute.For<IObjectStorageService>();
        _fileRepository = Substitute.For<IErpImportFileRepository>();
        _unitOfWork = Substitute.For<IUnitOfWork>();
        _mediator = Substitute.For<IMediator>();
        _dropPoint = new ErpDropPoint { Id = Guid.NewGuid(), SourceSystemId = "erp-default", BucketPrefix = BucketPrefix };
        _defaultDropPointProvider.GetOrCreateDefaultAsync(Arg.Any<CancellationToken>()).Returns(_dropPoint);
        _objectStorageService.UploadAsync(Arg.Any<string>(), Arg.Any<Stream>(), Arg.Any<CancellationToken>())
            .Returns(new StoredObjectInfo(SizeBytes, Sha));
        _added = [];
        _fileRepository.Add(Arg.Do<ErpImportFile>(file => _added.Add(file)));

        var registrar = new ErpImportFileRegistrar(
            _fileRepository, _unitOfWork, TimeProvider.System, NullLogger<ErpImportFileRegistrar>.Instance);
        _handler = new UploadErpOrderFileToDefaultCommandHandler(_defaultDropPointProvider, _objectStorageService, registrar, _mediator);
    }

    [Test]
    public async Task Handle_UploadsFileUnderDropPointPrefix_AndTriggersImportRun()
    {
        using var content = new MemoryStream(Encoding.UTF8.GetBytes("<ErpOrderImport />"));

        var key = await _handler.Handle(new UploadErpOrderFileToDefaultCommand("orders.xml", content, Uploader), CancellationToken.None);

        key.ShouldStartWith(BucketPrefix + "/");
        key.ShouldEndWith("-orders.xml");
        await _objectStorageService.Received(1).UploadAsync(key, content, Arg.Any<CancellationToken>());
        await _mediator.Received(1).Send(Arg.Any<TriggerErpImportRunCommand>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task Handle_RegistersAReceivedUiUploadRow_AndCommitsItBeforeTriggeringTheRun()
    {
        using var content = new MemoryStream(Encoding.UTF8.GetBytes("<ErpOrderImport />"));

        var key = await _handler.Handle(new UploadErpOrderFileToDefaultCommand("orders.xml", content, Uploader), CancellationToken.None);

        var file = _added.ShouldHaveSingleItem();
        file.StorageKey.ShouldBe(key);
        file.FileName.ShouldBe("orders.xml");
        file.DropPointId.ShouldBe(_dropPoint.Id);
        file.SourceSystemId.ShouldBe("erp-default");
        file.Channel.ShouldBe(ErpImportChannel.UiUpload);
        file.UploadedBy.ShouldBe(Uploader);
        file.SizeBytes.ShouldBe(SizeBytes);
        file.Sha256.ShouldBe(Sha);
        file.Status.ShouldBe(ErpImportFileStatus.Received);
        Received.InOrder(() =>
        {
            _objectStorageService.UploadAsync(key, content, Arg.Any<CancellationToken>());
            _fileRepository.FindDuplicateAsync(_dropPoint.Id, Sha, null, Arg.Any<CancellationToken>());
            _fileRepository.Add(file);
            _unitOfWork.CompleteAsync();
            _mediator.Send(Arg.Any<TriggerErpImportRunCommand>(), Arg.Any<CancellationToken>());
        });
    }

    [Test]
    public async Task Handle_SameHashAsAnEarlierDelivery_LinksTheDuplicate_AndStillImports()
    {
        var earlier = new ErpImportFile { Id = Guid.NewGuid() };
        _fileRepository.FindDuplicateAsync(_dropPoint.Id, Sha, null, Arg.Any<CancellationToken>()).Returns(earlier);
        using var content = new MemoryStream(Encoding.UTF8.GetBytes("<ErpOrderImport />"));

        await _handler.Handle(new UploadErpOrderFileToDefaultCommand("orders.xml", content, Uploader), CancellationToken.None);

        _added.ShouldHaveSingleItem().DuplicateOfFileId.ShouldBe(earlier.Id);
        await _mediator.Received(1).Send(Arg.Any<TriggerErpImportRunCommand>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task Handle_FileNameWithDirectoryPart_UploadsUnderBareFileName()
    {
        using var content = new MemoryStream(Encoding.UTF8.GetBytes("<ErpOrderImport />"));

        var key = await _handler.Handle(new UploadErpOrderFileToDefaultCommand("../evil/orders.xml", content, Uploader), CancellationToken.None);

        key.ShouldStartWith(BucketPrefix + "/");
        key.ShouldEndWith("-orders.xml");
        key.ShouldNotContain("evil");
        _added.ShouldHaveSingleItem().FileName.ShouldBe("orders.xml");
    }

    [Test]
    public async Task Handle_EmptyFileName_Throws_AndDoesNotUploadRegisterOrTrigger()
    {
        using var content = new MemoryStream();

        await Should.ThrowAsync<InvalidRequestException>(
            () => _handler.Handle(new UploadErpOrderFileToDefaultCommand("orders/", content, Uploader), CancellationToken.None));

        await _objectStorageService.DidNotReceive().UploadAsync(Arg.Any<string>(), Arg.Any<Stream>(), Arg.Any<CancellationToken>());
        _added.ShouldBeEmpty();
        await _mediator.DidNotReceive().Send(Arg.Any<TriggerErpImportRunCommand>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task Handle_UploadFails_DoesNotRegisterOrTriggerImportRun()
    {
        using var content = new MemoryStream();
        _objectStorageService.UploadAsync(Arg.Any<string>(), Arg.Any<Stream>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException<StoredObjectInfo>(new IOException("disk full")));

        await Should.ThrowAsync<IOException>(
            () => _handler.Handle(new UploadErpOrderFileToDefaultCommand("orders.xml", content, Uploader), CancellationToken.None));

        _added.ShouldBeEmpty();
        await _unitOfWork.DidNotReceive().CompleteAsync();
        await _mediator.DidNotReceive().Send(Arg.Any<TriggerErpImportRunCommand>(), Arg.Any<CancellationToken>());
    }
}
