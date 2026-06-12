using Klacks.Api.Application.Commands.Authentification;
using Klacks.Api.Application.Handlers.Authentification;
using Klacks.Api.Domain.Constants;
using Klacks.Api.Domain.Exceptions;
using Klacks.Api.Domain.Models.Authentification;
using Klacks.Api.Domain.Security;

namespace Klacks.UnitTest.Authentification;

[TestFixture]
public class CreatePersonalAccessTokenCommandHandlerTests
{
    private const string TestUserId = "7e6f0a44-1111-2222-3333-444455556666";
    private const string TestTokenName = "ci-token";
    private const string WhitespaceName = "   ";
    private const int CustomExpiresInDays = 30;
    private const int ExpiresInDaysAboveMaximum = PatConstants.MaxExpiresInDays + 1;
    private const int NonPositiveExpiresInDays = 0;

    private IPersonalAccessTokenRepository _repository = null!;
    private CreatePersonalAccessTokenCommandHandler _handler = null!;
    private PersonalAccessToken? _captured;

    [SetUp]
    public void SetUp()
    {
        _captured = null;
        _repository = Substitute.For<IPersonalAccessTokenRepository>();
        _repository.AddAsync(Arg.Do<PersonalAccessToken>(token => _captured = token), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);
        _handler = new CreatePersonalAccessTokenCommandHandler(_repository);
    }

    [Test]
    public async Task Handle_WithoutExpiry_UsesDefaultExpiryDays()
    {
        var before = DateTime.UtcNow;

        var result = await _handler.Handle(new CreatePersonalAccessTokenCommand(TestUserId, TestTokenName, null), CancellationToken.None);

        var after = DateTime.UtcNow;
        _captured.ShouldNotBeNull();
        _captured!.ExpiresAt.ShouldNotBeNull();
        _captured.ExpiresAt!.Value.ShouldBeInRange(
            before.AddDays(PatConstants.DefaultExpiresInDays),
            after.AddDays(PatConstants.DefaultExpiresInDays));
        result.ExpiresAt.ShouldBe(_captured.ExpiresAt.Value);
    }

    [Test]
    public async Task Handle_WithCustomExpiry_UsesGivenDays()
    {
        var before = DateTime.UtcNow;

        await _handler.Handle(new CreatePersonalAccessTokenCommand(TestUserId, TestTokenName, CustomExpiresInDays), CancellationToken.None);

        var after = DateTime.UtcNow;
        _captured.ShouldNotBeNull();
        _captured!.ExpiresAt!.Value.ShouldBeInRange(
            before.AddDays(CustomExpiresInDays),
            after.AddDays(CustomExpiresInDays));
    }

    [Test]
    public async Task Handle_ReturnsPlaintextMatchingPersistedHash()
    {
        var result = await _handler.Handle(new CreatePersonalAccessTokenCommand(TestUserId, TestTokenName, CustomExpiresInDays), CancellationToken.None);

        _captured.ShouldNotBeNull();
        result.Token.ShouldStartWith(PatConstants.TokenPrefix);
        _captured!.TokenHash.ShouldBe(PatTokenGenerator.HashToken(result.Token));
        _captured.TokenPrefix.ShouldBe(result.Token[..PatConstants.DisplayPrefixLength]);
        result.TokenPrefix.ShouldBe(_captured.TokenPrefix);
        result.Id.ShouldBe(_captured.Id);
        result.Name.ShouldBe(TestTokenName);
        _captured.UserId.ShouldBe(TestUserId);
        _captured.Name.ShouldBe(TestTokenName);
    }

    [Test]
    public async Task Handle_EmptyName_Throws()
    {
        await Should.ThrowAsync<InvalidRequestException>(
            () => _handler.Handle(new CreatePersonalAccessTokenCommand(TestUserId, WhitespaceName, null), CancellationToken.None));

        await _repository.DidNotReceive().AddAsync(Arg.Any<PersonalAccessToken>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task Handle_ExpiryAboveMaximum_Throws()
    {
        await Should.ThrowAsync<InvalidRequestException>(
            () => _handler.Handle(new CreatePersonalAccessTokenCommand(TestUserId, TestTokenName, ExpiresInDaysAboveMaximum), CancellationToken.None));

        await _repository.DidNotReceive().AddAsync(Arg.Any<PersonalAccessToken>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task Handle_NonPositiveExpiry_Throws()
    {
        await Should.ThrowAsync<InvalidRequestException>(
            () => _handler.Handle(new CreatePersonalAccessTokenCommand(TestUserId, TestTokenName, NonPositiveExpiresInDays), CancellationToken.None));

        await _repository.DidNotReceive().AddAsync(Arg.Any<PersonalAccessToken>(), Arg.Any<CancellationToken>());
    }
}
