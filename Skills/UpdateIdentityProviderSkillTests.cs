// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Unit tests for update_identity_provider: the skill merges only supplied fields onto the
/// existing configuration, keeps stored secrets when none are supplied and masks secrets in
/// the returned data.
/// </summary>

using System.Text.Json;
using Klacks.Api.Application.Commands.IdentityProviders;
using Klacks.Api.Application.DTOs.IdentityProviders;
using Klacks.Api.Application.Queries.IdentityProviders;
using Klacks.Api.Application.Skills;
using Klacks.Api.Infrastructure.Mediator;

namespace Klacks.UnitTest.Skills;

[TestFixture]
public class UpdateIdentityProviderSkillTests
{
    private const string StoredBindPassword = "StoredBindSecret";

    private static SkillExecutionContext Ctx() => new()
    {
        UserId = Guid.NewGuid(),
        TenantId = Guid.NewGuid(),
        UserName = "tester",
        UserPermissions = new List<string> { "Admin" }
    };

    private static IdentityProviderResource Existing(Guid id) => new()
    {
        Id = id,
        Name = "Company LDAP",
        Type = IdentityProviderType.Ldap,
        IsEnabled = true,
        SortOrder = 1,
        UseForAuthentication = true,
        Host = "old.example.com",
        Port = 389,
        UseSsl = false,
        BindDn = "cn=reader,dc=example,dc=com",
        BindPassword = StoredBindPassword
    };

    [Test]
    public async Task PartialUpdate_KeepsStoredSecret_AndMasksResponse()
    {
        var id = Guid.NewGuid();
        var mediator = Substitute.For<IMediator>();
        mediator.Send(Arg.Any<GetQuery>(), Arg.Any<CancellationToken>())
            .Returns(Existing(id));
        mediator.Send(Arg.Any<PutCommand>(), Arg.Any<CancellationToken>())
            .Returns(call => call.Arg<PutCommand>().Model);
        var skill = new UpdateIdentityProviderSkill(mediator);

        var result = await skill.ExecuteAsync(Ctx(), new Dictionary<string, object>
        {
            ["id"] = id.ToString(),
            ["host"] = "new.example.com",
            ["useSsl"] = true,
            ["port"] = 636
        });

        result.Success.ShouldBeTrue();
        await mediator.Received(1).Send(
            Arg.Is<PutCommand>(c =>
                c.Model.Id == id &&
                c.Model.Host == "new.example.com" &&
                c.Model.Port == 636 &&
                c.Model.UseSsl &&
                c.Model.Name == "Company LDAP" &&
                c.Model.BindPassword == StoredBindPassword),
            Arg.Any<CancellationToken>());

        var serialized = JsonSerializer.Serialize(result.Data);
        serialized.ShouldNotContain(StoredBindPassword);
        serialized.ShouldContain("\"HasBindPassword\":true");
    }

    [Test]
    public async Task NewSecret_ReplacesStoredSecret()
    {
        var id = Guid.NewGuid();
        var mediator = Substitute.For<IMediator>();
        mediator.Send(Arg.Any<GetQuery>(), Arg.Any<CancellationToken>())
            .Returns(Existing(id));
        mediator.Send(Arg.Any<PutCommand>(), Arg.Any<CancellationToken>())
            .Returns(call => call.Arg<PutCommand>().Model);
        var skill = new UpdateIdentityProviderSkill(mediator);

        var result = await skill.ExecuteAsync(Ctx(), new Dictionary<string, object>
        {
            ["id"] = id.ToString(),
            ["bindPassword"] = "NewBindSecret"
        });

        result.Success.ShouldBeTrue();
        await mediator.Received(1).Send(
            Arg.Is<PutCommand>(c => c.Model.BindPassword == "NewBindSecret"),
            Arg.Any<CancellationToken>());
        JsonSerializer.Serialize(result.Data).ShouldNotContain("NewBindSecret");
    }

    [Test]
    public async Task UnknownId_ReturnsErrorWithoutMutation()
    {
        var mediator = Substitute.For<IMediator>();
        mediator.Send(Arg.Any<GetQuery>(), Arg.Any<CancellationToken>())
            .Returns((IdentityProviderResource?)null);
        var skill = new UpdateIdentityProviderSkill(mediator);

        var result = await skill.ExecuteAsync(Ctx(), new Dictionary<string, object>
        {
            ["id"] = Guid.NewGuid().ToString(),
            ["host"] = "new.example.com"
        });

        result.Success.ShouldBeFalse();
        result.Message.ShouldContain("not found");
        await mediator.DidNotReceive().Send(Arg.Any<PutCommand>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task NoFields_ReturnsNothingToUpdate()
    {
        var mediator = Substitute.For<IMediator>();
        var skill = new UpdateIdentityProviderSkill(mediator);

        var result = await skill.ExecuteAsync(Ctx(), new Dictionary<string, object>
        {
            ["id"] = Guid.NewGuid().ToString()
        });

        result.Success.ShouldBeFalse();
        result.Message.ShouldContain("Nothing to update");
        await mediator.DidNotReceive().Send(Arg.Any<PutCommand>(), Arg.Any<CancellationToken>());
    }
}
