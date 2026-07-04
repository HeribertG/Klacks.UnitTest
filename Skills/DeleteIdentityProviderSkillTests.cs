// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Unit tests for delete_identity_provider: the skill dispatches DeleteCommand, warns when the
/// deleted provider was used for authentication and never exposes stored secrets.
/// </summary>

using System.Text.Json;
using Klacks.Api.Application.Commands.IdentityProviders;
using Klacks.Api.Application.DTOs.IdentityProviders;
using Klacks.Api.Application.Interfaces;
using Klacks.Api.Application.Skills;
using Klacks.Api.Domain.Interfaces;
using Klacks.Api.Infrastructure.Mediator;

namespace Klacks.UnitTest.Skills;

[TestFixture]
public class DeleteIdentityProviderSkillTests
{
    private const string StoredClientSecret = "StoredClientSecret";

    private static SkillExecutionContext Ctx() => new()
    {
        UserId = Guid.NewGuid(),
        TenantId = Guid.NewGuid(),
        UserName = "tester",
        UserPermissions = new List<string> { "Admin" }
    };

    private static IUnitOfWork UnitOfWorkThatExecutes()
    {
        var unitOfWork = Substitute.For<IUnitOfWork>();
        unitOfWork.ExecuteInTransactionAsync(Arg.Any<Func<Task<Guid>>>())
            .Returns(call => call.Arg<Func<Task<Guid>>>()());
        return unitOfWork;
    }

    [Test]
    public async Task Delete_AuthProvider_SucceedsWithLockoutWarning()
    {
        var id = Guid.NewGuid();
        var mediator = Substitute.For<IMediator>();
        mediator.Send(Arg.Any<DeleteCommand>(), Arg.Any<CancellationToken>())
            .Returns(new IdentityProviderResource
            {
                Id = id,
                Name = "Synology SSO",
                Type = IdentityProviderType.OAuth2,
                IsEnabled = true,
                UseForAuthentication = true,
                ClientId = "klacks",
                ClientSecret = StoredClientSecret
            });
        // Repository default (unconfigured GetNoTracking) returns null, confirming the soft-delete.
        var skill = new DeleteIdentityProviderSkill(mediator, Substitute.For<IIdentityProviderRepository>(), UnitOfWorkThatExecutes());

        var result = await skill.ExecuteAsync(Ctx(), new Dictionary<string, object>
        {
            ["id"] = id.ToString()
        });

        result.Success.ShouldBeTrue();
        result.Message.ShouldContain("deleted");
        result.Message.ShouldContain("no longer log in");
        JsonSerializer.Serialize(result.Data).ShouldNotContain(StoredClientSecret);
        await mediator.Received(1).Send(
            Arg.Is<DeleteCommand>(c => c.Id == id), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task UnknownId_ReturnsNotFound()
    {
        var mediator = Substitute.For<IMediator>();
        mediator.Send(Arg.Any<DeleteCommand>(), Arg.Any<CancellationToken>())
            .Returns((IdentityProviderResource?)null);
        var skill = new DeleteIdentityProviderSkill(mediator, Substitute.For<IIdentityProviderRepository>(), UnitOfWorkThatExecutes());

        var result = await skill.ExecuteAsync(Ctx(), new Dictionary<string, object>
        {
            ["id"] = Guid.NewGuid().ToString()
        });

        result.Success.ShouldBeFalse();
        result.Message.ShouldContain("not found");
    }

    [Test]
    public async Task InvalidId_ReturnsErrorWithoutMutation()
    {
        var mediator = Substitute.For<IMediator>();
        var skill = new DeleteIdentityProviderSkill(mediator, Substitute.For<IIdentityProviderRepository>(), Substitute.For<IUnitOfWork>());

        var result = await skill.ExecuteAsync(Ctx(), new Dictionary<string, object>
        {
            ["id"] = "not-a-guid"
        });

        result.Success.ShouldBeFalse();
        result.Message.ShouldContain("id");
        await mediator.DidNotReceive().Send(Arg.Any<DeleteCommand>(), Arg.Any<CancellationToken>());
    }
}
