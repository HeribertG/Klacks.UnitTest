// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Unit tests for create_identity_provider: the skill validates name/type/port, dispatches
/// PostCommand with the secret values intact and masks secrets in the returned data.
/// </summary>

using System.Text.Json;
using Klacks.Api.Application.Commands.IdentityProviders;
using Klacks.Api.Application.DTOs.IdentityProviders;
using Klacks.Api.Application.Skills;
using Klacks.Api.Infrastructure.Mediator;

namespace Klacks.UnitTest.Skills;

[TestFixture]
public class CreateIdentityProviderSkillTests
{
    private const string BindPasswordValue = "TopSecretBindPw";

    private static SkillExecutionContext Ctx() => new()
    {
        UserId = Guid.NewGuid(),
        TenantId = Guid.NewGuid(),
        UserName = "tester",
        UserPermissions = new List<string> { "Admin" }
    };

    private static IMediator MediatorReturningCreated()
    {
        var mediator = Substitute.For<IMediator>();
        mediator.Send(Arg.Any<PostCommand>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                var model = call.Arg<PostCommand>().Model;
                model.Id = Guid.NewGuid();
                return model;
            });
        return mediator;
    }

    [Test]
    public async Task ExplicitValues_CreatesProvider_AndMasksSecrets()
    {
        var mediator = MediatorReturningCreated();
        var skill = new CreateIdentityProviderSkill(mediator);

        var result = await skill.ExecuteAsync(Ctx(), new Dictionary<string, object>
        {
            ["name"] = "Company LDAP",
            ["type"] = "Ldap",
            ["host"] = "ldap.example.com",
            ["port"] = 636,
            ["useSsl"] = true,
            ["bindDn"] = "cn=reader,dc=example,dc=com",
            ["bindPassword"] = BindPasswordValue
        });

        result.Success.ShouldBeTrue();
        await mediator.Received(1).Send(
            Arg.Is<PostCommand>(c =>
                c.Model.Name == "Company LDAP" &&
                c.Model.Type == IdentityProviderType.Ldap &&
                c.Model.Host == "ldap.example.com" &&
                c.Model.Port == 636 &&
                c.Model.UseSsl &&
                c.Model.BindPassword == BindPasswordValue &&
                !c.Model.IsEnabled),
            Arg.Any<CancellationToken>());

        var serialized = JsonSerializer.Serialize(result.Data);
        serialized.ShouldNotContain(BindPasswordValue);
        serialized.ShouldContain("\"HasBindPassword\":true");
        serialized.ShouldContain("\"HasClientSecret\":false");
    }

    [Test]
    public async Task MissingName_ReturnsErrorWithoutMutation()
    {
        var mediator = Substitute.For<IMediator>();
        var skill = new CreateIdentityProviderSkill(mediator);

        var result = await skill.ExecuteAsync(Ctx(), new Dictionary<string, object>
        {
            ["type"] = "Ldap"
        });

        result.Success.ShouldBeFalse();
        result.Message.ShouldContain("name");
        await mediator.DidNotReceive().Send(Arg.Any<PostCommand>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task InvalidType_ReturnsErrorWithValidValues()
    {
        var mediator = Substitute.For<IMediator>();
        var skill = new CreateIdentityProviderSkill(mediator);

        var result = await skill.ExecuteAsync(Ctx(), new Dictionary<string, object>
        {
            ["name"] = "X",
            ["type"] = "Kerberos"
        });

        result.Success.ShouldBeFalse();
        result.Message.ShouldContain("OpenIdConnect");
        await mediator.DidNotReceive().Send(Arg.Any<PostCommand>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task InvalidPort_ReturnsError()
    {
        var mediator = Substitute.For<IMediator>();
        var skill = new CreateIdentityProviderSkill(mediator);

        var result = await skill.ExecuteAsync(Ctx(), new Dictionary<string, object>
        {
            ["name"] = "X",
            ["type"] = "Ldap",
            ["port"] = 70000
        });

        result.Success.ShouldBeFalse();
        result.Message.ShouldContain("port");
        await mediator.DidNotReceive().Send(Arg.Any<PostCommand>(), Arg.Any<CancellationToken>());
    }
}
