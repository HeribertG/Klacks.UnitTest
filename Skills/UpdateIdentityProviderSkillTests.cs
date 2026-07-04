// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Unit tests for update_identity_provider: the skill merges only supplied fields onto the
/// existing configuration, keeps stored secrets when none are supplied and masks secrets in
/// the returned data.
/// </summary>

using System.Text.Json;
using Klacks.Api.Application.Commands.IdentityProviders;
using Klacks.Api.Application.DTOs.IdentityProviders;
using Klacks.Api.Application.Interfaces;
using Klacks.Api.Application.Queries.IdentityProviders;
using Klacks.Api.Application.Skills;
using Klacks.Api.Domain.Interfaces;
using Klacks.Api.Domain.Models.Authentification;
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

    private static IUnitOfWork UnitOfWorkThatExecutes()
    {
        var unitOfWork = Substitute.For<IUnitOfWork>();
        unitOfWork.ExecuteInTransactionAsync(Arg.Any<Func<Task<Guid>>>())
            .Returns(call => call.Arg<Func<Task<Guid>>>()());
        return unitOfWork;
    }

    private static IIdentityProviderRepository RepositoryReflecting(Func<IdentityProviderResource?> latest)
    {
        var repository = Substitute.For<IIdentityProviderRepository>();
        repository.GetNoTracking(Arg.Any<Guid>()).Returns(_ =>
        {
            var model = latest();
            if (model == null)
            {
                return null;
            }

            return new IdentityProvider
            {
                Id = model.Id,
                Name = model.Name,
                Type = model.Type,
                IsEnabled = model.IsEnabled,
                SortOrder = model.SortOrder,
                UseForAuthentication = model.UseForAuthentication,
                UseForClientImport = model.UseForClientImport,
                Host = model.Host,
                Port = model.Port,
                UseSsl = model.UseSsl,
                BaseDn = model.BaseDn,
                BindDn = model.BindDn,
                BindPassword = model.BindPassword,
                UserFilter = model.UserFilter,
                ClientId = model.ClientId,
                ClientSecret = model.ClientSecret,
                AuthorizationUrl = model.AuthorizationUrl,
                TokenUrl = model.TokenUrl,
                UserInfoUrl = model.UserInfoUrl,
                Scopes = model.Scopes,
                TenantId = model.TenantId
            };
        });
        return repository;
    }

    [Test]
    public async Task PartialUpdate_KeepsStoredSecret_AndMasksResponse()
    {
        var id = Guid.NewGuid();
        IdentityProviderResource? putModel = null;
        var mediator = Substitute.For<IMediator>();
        mediator.Send(Arg.Any<GetQuery>(), Arg.Any<CancellationToken>())
            .Returns(Existing(id));
        mediator.Send(Arg.Any<PutCommand>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                putModel = call.Arg<PutCommand>().Model;
                return putModel;
            });
        var skill = new UpdateIdentityProviderSkill(mediator, RepositoryReflecting(() => putModel), UnitOfWorkThatExecutes());

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
        IdentityProviderResource? putModel = null;
        var mediator = Substitute.For<IMediator>();
        mediator.Send(Arg.Any<GetQuery>(), Arg.Any<CancellationToken>())
            .Returns(Existing(id));
        mediator.Send(Arg.Any<PutCommand>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                putModel = call.Arg<PutCommand>().Model;
                return putModel;
            });
        var skill = new UpdateIdentityProviderSkill(mediator, RepositoryReflecting(() => putModel), UnitOfWorkThatExecutes());

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
        var skill = new UpdateIdentityProviderSkill(mediator, Substitute.For<IIdentityProviderRepository>(), Substitute.For<IUnitOfWork>());

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
        var skill = new UpdateIdentityProviderSkill(mediator, Substitute.For<IIdentityProviderRepository>(), Substitute.For<IUnitOfWork>());

        var result = await skill.ExecuteAsync(Ctx(), new Dictionary<string, object>
        {
            ["id"] = Guid.NewGuid().ToString()
        });

        result.Success.ShouldBeFalse();
        result.Message.ShouldContain("Nothing to update");
        await mediator.DidNotReceive().Send(Arg.Any<PutCommand>(), Arg.Any<CancellationToken>());
    }
}
