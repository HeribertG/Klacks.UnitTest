using Klacks.Api.Application.Commands.IdentityProviders;
using Klacks.Api.Application.Constants;
using Klacks.Api.Application.Handlers.IdentityProviders;
using Klacks.Api.Application.Interfaces;
using Klacks.Api.Application.Mappers;
using Klacks.Api.Domain.Interfaces;
using Klacks.Api.Domain.Models.Authentification;
using Microsoft.Extensions.Logging;
using Shouldly;

namespace Klacks.UnitTest.Handlers.IdentityProviders;

[TestFixture]
public class DeleteCommandHandlerTests
{
    private const string BindPasswordValue = "super-secret-bind-password";
    private const string ClientSecretValue = "super-secret-client-secret";

    private IIdentityProviderRepository _repository = null!;
    private IUnitOfWork _unitOfWork = null!;
    private DeleteCommandHandler _handler = null!;

    [SetUp]
    public void SetUp()
    {
        _repository = Substitute.For<IIdentityProviderRepository>();
        _unitOfWork = Substitute.For<IUnitOfWork>();

        _handler = new DeleteCommandHandler(
            _repository,
            new IdentityProviderMapper(),
            _unitOfWork,
            Substitute.For<ILogger<DeleteCommandHandler>>());
    }

    [Test]
    public async Task Handle_WhenProviderIsDeleted_ShouldMaskBindPasswordAndClientSecret()
    {
        var id = Guid.NewGuid();
        _repository.Delete(id).Returns(new IdentityProvider
        {
            Id = id,
            Name = "LDAP",
            BindPassword = BindPasswordValue,
            ClientSecret = ClientSecretValue,
        });

        var result = await _handler.Handle(new DeleteCommand(id), CancellationToken.None);

        result.ShouldNotBeNull();
        result!.BindPassword.ShouldBe(SecretMask.Placeholder,
            "DELETE must not return the stored bind password in clear text");
        result.ClientSecret.ShouldBe(SecretMask.Placeholder,
            "DELETE must not return the stored client secret in clear text");
    }

    [Test]
    public async Task Handle_WhenSecretsAreEmpty_ShouldNotInventAPlaceholder()
    {
        var id = Guid.NewGuid();
        _repository.Delete(id).Returns(new IdentityProvider
        {
            Id = id,
            Name = "OAuth2",
            BindPassword = null,
            ClientSecret = string.Empty,
        });

        var result = await _handler.Handle(new DeleteCommand(id), CancellationToken.None);

        result.ShouldNotBeNull();
        result!.BindPassword.ShouldBeNullOrEmpty();
        result.ClientSecret.ShouldBeNullOrEmpty();
    }

    [Test]
    public async Task Handle_WhenProviderDoesNotExist_ShouldReturnNull()
    {
        var id = Guid.NewGuid();
        _repository.Delete(id).Returns((IdentityProvider?)null);

        var result = await _handler.Handle(new DeleteCommand(id), CancellationToken.None);

        result.ShouldBeNull();
        await _unitOfWork.DidNotReceive().CompleteAsync();
    }
}
