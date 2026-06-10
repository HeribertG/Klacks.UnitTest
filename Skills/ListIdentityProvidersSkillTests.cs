// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Unit tests for list_identity_providers: the skill sends the identity-provider ListQuery and
/// projects only non-sensitive fields; an empty list yields a zero-count success.
/// </summary>

using Klacks.Api.Application.DTOs.IdentityProviders;
using Klacks.Api.Application.Queries.IdentityProviders;
using Klacks.Api.Application.Skills;
using Klacks.Api.Infrastructure.Mediator;

namespace Klacks.UnitTest.Skills;

[TestFixture]
public class ListIdentityProvidersSkillTests
{
    private static SkillExecutionContext Ctx() => new()
    {
        UserId = Guid.NewGuid(),
        TenantId = Guid.NewGuid(),
        UserName = "tester",
        UserPermissions = new List<string> { "Admin" }
    };

    [Test]
    public async Task List_ReturnsProviders()
    {
        var mediator = Substitute.For<IMediator>();
        mediator.Send(Arg.Any<ListQuery>(), Arg.Any<CancellationToken>())
            .Returns(new List<IdentityProviderListResource>
            {
                new() { Id = Guid.NewGuid(), Name = "Company LDAP", Type = IdentityProviderType.Ldap, IsEnabled = true, SortOrder = 1, UseForAuthentication = true },
                new() { Id = Guid.NewGuid(), Name = "Synology SSO", Type = IdentityProviderType.OAuth2, IsEnabled = false, SortOrder = 2 }
            });
        var skill = new ListIdentityProvidersSkill(mediator);

        var result = await skill.ExecuteAsync(Ctx(), new Dictionary<string, object>());

        result.Success.ShouldBeTrue();
        result.Message.ShouldContain("2 identity providers");
        await mediator.Received(1).Send(Arg.Any<ListQuery>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task List_Empty_ReturnsZeroCount()
    {
        var mediator = Substitute.For<IMediator>();
        mediator.Send(Arg.Any<ListQuery>(), Arg.Any<CancellationToken>())
            .Returns(new List<IdentityProviderListResource>());
        var skill = new ListIdentityProvidersSkill(mediator);

        var result = await skill.ExecuteAsync(Ctx(), new Dictionary<string, object>());

        result.Success.ShouldBeTrue();
        result.Message.ShouldContain("0 identity providers");
    }
}
