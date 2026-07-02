using Klacks.Api.Application.Commands.ErpImportTokens;
using Klacks.Api.Application.Handlers.ErpImportTokens;
using Klacks.Api.Domain.Interfaces.Imports;
using Klacks.Api.Domain.Models.Imports;

namespace Klacks.UnitTest.Application.Handlers.ErpImportTokens;

[TestFixture]
public class RevokeErpImportTokenCommandHandlerTests
{
    private IErpImportTokenRepository _repository = null!;
    private RevokeErpImportTokenCommandHandler _handler = null!;

    [SetUp]
    public void SetUp()
    {
        _repository = Substitute.For<IErpImportTokenRepository>();
        _handler = new RevokeErpImportTokenCommandHandler(_repository);
    }

    [Test]
    public async Task Handle_TokenRevoked_ReturnsTrue()
    {
        var tokenId = Guid.NewGuid();
        var dropPointId = Guid.NewGuid();
        _repository.RevokeAsync(tokenId, dropPointId, Arg.Any<CancellationToken>()).Returns(new ErpImportToken { Id = tokenId, DropPointId = dropPointId });

        var result = await _handler.Handle(new RevokeErpImportTokenCommand(tokenId, dropPointId), CancellationToken.None);

        result.ShouldBeTrue();
    }

    [Test]
    public async Task Handle_TokenNotFoundOrWrongDropPoint_ReturnsFalse()
    {
        var tokenId = Guid.NewGuid();
        var dropPointId = Guid.NewGuid();
        _repository.RevokeAsync(tokenId, dropPointId, Arg.Any<CancellationToken>()).Returns((ErpImportToken?)null);

        var result = await _handler.Handle(new RevokeErpImportTokenCommand(tokenId, dropPointId), CancellationToken.None);

        result.ShouldBeFalse();
    }
}
