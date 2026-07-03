using Klacks.Api.Application.Commands.Imports;
using Klacks.Api.Application.Handlers.Imports;
using Klacks.Api.Domain.Exceptions;
using Klacks.Api.Domain.Interfaces.Imports;
using Klacks.Api.Domain.Models.Imports;

namespace Klacks.UnitTest.Application.Handlers.Imports;

[TestFixture]
public class RetryErpOrderFileCommandHandlerTests
{
    private const string BucketPrefix = "erp/orders";

    private IErpDefaultDropPointProvider _defaultDropPointProvider = null!;
    private IObjectStorageService _objectStorageService = null!;
    private RetryErpOrderFileCommandHandler _handler = null!;

    [SetUp]
    public void SetUp()
    {
        _defaultDropPointProvider = Substitute.For<IErpDefaultDropPointProvider>();
        _objectStorageService = Substitute.For<IObjectStorageService>();
        _handler = new RetryErpOrderFileCommandHandler(_defaultDropPointProvider, _objectStorageService);

        _defaultDropPointProvider.GetOrCreateDefaultAsync(Arg.Any<CancellationToken>())
            .Returns(new ErpDropPoint { Id = Guid.NewGuid(), Name = "Default", SourceSystemId = "default", BucketPrefix = BucketPrefix });
    }

    [Test]
    public async Task Handle_ErrorSegmentKey_MovesFileBackToInbox()
    {
        await _handler.Handle(new RetryErpOrderFileCommand("erp/orders/error/abc-order.xml"), CancellationToken.None);

        await _objectStorageService.Received(1)
            .MoveAsync("erp/orders/error/abc-order.xml", "erp/orders/abc-order.xml", Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task Handle_PendingKeyOutsideErrorSegment_Throws()
    {
        await Should.ThrowAsync<InvalidRequestException>(
            () => _handler.Handle(new RetryErpOrderFileCommand("erp/orders/abc-order.xml"), CancellationToken.None));

        await _objectStorageService.DidNotReceive()
            .MoveAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task Handle_ProcessedSegmentKey_Throws()
    {
        await Should.ThrowAsync<InvalidRequestException>(
            () => _handler.Handle(new RetryErpOrderFileCommand("erp/orders/processed/abc-order.xml"), CancellationToken.None));

        await _objectStorageService.DidNotReceive()
            .MoveAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task Handle_ErrorSegmentKeyWithoutFileName_Throws()
    {
        await Should.ThrowAsync<InvalidRequestException>(
            () => _handler.Handle(new RetryErpOrderFileCommand("erp/orders/error/"), CancellationToken.None));

        await _objectStorageService.DidNotReceive()
            .MoveAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task Handle_EmptyKey_Throws()
    {
        await Should.ThrowAsync<InvalidRequestException>(
            () => _handler.Handle(new RetryErpOrderFileCommand(string.Empty), CancellationToken.None));

        await _objectStorageService.DidNotReceive()
            .MoveAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }
}
