using System.Text;
using Klacks.Api.Application.Commands.Imports;
using Klacks.Api.Application.Handlers.Imports;
using Klacks.Api.Domain.Exceptions;
using Klacks.Api.Domain.Interfaces.Imports;
using Klacks.Api.Domain.Models.Imports;
using Klacks.Api.Infrastructure.Mediator;

namespace Klacks.UnitTest.Application.Handlers.Imports;

[TestFixture]
public class UploadErpOrderFileToDefaultCommandHandlerTests
{
    private const string BucketPrefix = "erp/orders";

    private IErpDefaultDropPointProvider _defaultDropPointProvider = null!;
    private IObjectStorageService _objectStorageService = null!;
    private IMediator _mediator = null!;
    private UploadErpOrderFileToDefaultCommandHandler _handler = null!;

    [SetUp]
    public void SetUp()
    {
        _defaultDropPointProvider = Substitute.For<IErpDefaultDropPointProvider>();
        _objectStorageService = Substitute.For<IObjectStorageService>();
        _mediator = Substitute.For<IMediator>();
        _defaultDropPointProvider.GetOrCreateDefaultAsync(Arg.Any<CancellationToken>())
            .Returns(new ErpDropPoint { BucketPrefix = BucketPrefix });
        _handler = new UploadErpOrderFileToDefaultCommandHandler(_defaultDropPointProvider, _objectStorageService, _mediator);
    }

    [Test]
    public async Task Handle_UploadsFileUnderDropPointPrefix_AndTriggersImportRun()
    {
        using var content = new MemoryStream(Encoding.UTF8.GetBytes("<ErpOrderImport />"));

        var key = await _handler.Handle(new UploadErpOrderFileToDefaultCommand("orders.xml", content), CancellationToken.None);

        key.ShouldStartWith(BucketPrefix + "/");
        key.ShouldEndWith("-orders.xml");
        await _objectStorageService.Received(1).UploadAsync(key, content, Arg.Any<CancellationToken>());
        await _mediator.Received(1).Send(Arg.Any<TriggerErpImportRunCommand>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task Handle_FileNameWithDirectoryPart_UploadsUnderBareFileName()
    {
        using var content = new MemoryStream(Encoding.UTF8.GetBytes("<ErpOrderImport />"));

        var key = await _handler.Handle(new UploadErpOrderFileToDefaultCommand("../evil/orders.xml", content), CancellationToken.None);

        key.ShouldStartWith(BucketPrefix + "/");
        key.ShouldEndWith("-orders.xml");
        key.ShouldNotContain("evil");
    }

    [Test]
    public async Task Handle_EmptyFileName_Throws_AndDoesNotUploadOrTrigger()
    {
        using var content = new MemoryStream();

        await Should.ThrowAsync<InvalidRequestException>(
            () => _handler.Handle(new UploadErpOrderFileToDefaultCommand("orders/", content), CancellationToken.None));

        await _objectStorageService.DidNotReceive().UploadAsync(Arg.Any<string>(), Arg.Any<Stream>(), Arg.Any<CancellationToken>());
        await _mediator.DidNotReceive().Send(Arg.Any<TriggerErpImportRunCommand>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task Handle_UploadFails_DoesNotTriggerImportRun()
    {
        using var content = new MemoryStream();
        _objectStorageService.UploadAsync(Arg.Any<string>(), Arg.Any<Stream>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException(new IOException("disk full")));

        await Should.ThrowAsync<IOException>(
            () => _handler.Handle(new UploadErpOrderFileToDefaultCommand("orders.xml", content), CancellationToken.None));

        await _mediator.DidNotReceive().Send(Arg.Any<TriggerErpImportRunCommand>(), Arg.Any<CancellationToken>());
    }
}
