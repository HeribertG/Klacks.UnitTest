using Klacks.Api.Application.Commands.Authentification;
using Klacks.Api.Application.Handlers.Authentification;
using Klacks.Api.Domain.Models.Authentification;

namespace Klacks.UnitTest.Authentification;

[TestFixture]
public class RevokePersonalAccessTokenCommandHandlerTests
{
    private const string TestUserId = "7e6f0a44-1111-2222-3333-444455556666";

    private IPersonalAccessTokenRepository _repository = null!;
    private RevokePersonalAccessTokenCommandHandler _handler = null!;

    [SetUp]
    public void SetUp()
    {
        _repository = Substitute.For<IPersonalAccessTokenRepository>();
        _handler = new RevokePersonalAccessTokenCommandHandler(_repository);
    }

    [Test]
    public async Task Handle_OwnToken_ReturnsTrue()
    {
        var tokenId = Guid.NewGuid();
        _repository.RevokeAsync(tokenId, TestUserId, Arg.Any<CancellationToken>())
            .Returns(new PersonalAccessToken { Id = tokenId, UserId = TestUserId });

        var result = await _handler.Handle(new RevokePersonalAccessTokenCommand(tokenId, TestUserId), CancellationToken.None);

        result.ShouldBeTrue();
    }

    [Test]
    public async Task Handle_UnknownOrForeignToken_ReturnsFalse()
    {
        var tokenId = Guid.NewGuid();
        _repository.RevokeAsync(tokenId, TestUserId, Arg.Any<CancellationToken>())
            .Returns((PersonalAccessToken?)null);

        var result = await _handler.Handle(new RevokePersonalAccessTokenCommand(tokenId, TestUserId), CancellationToken.None);

        result.ShouldBeFalse();
    }

    [Test]
    public async Task Handle_PassesOwnerScopedArgumentsToRepository()
    {
        var tokenId = Guid.NewGuid();

        await _handler.Handle(new RevokePersonalAccessTokenCommand(tokenId, TestUserId), CancellationToken.None);

        await _repository.Received(1).RevokeAsync(tokenId, TestUserId, Arg.Any<CancellationToken>());
    }
}
