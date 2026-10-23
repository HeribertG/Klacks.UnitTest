// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Characterization tests for UpdateIdentityProviderSkill.ExecuteAsync, written before its long body was split into
/// private steps: the invalid-id, nothing-to-update, port and type refusals (port before the load, type after it and
/// before any field is merged), the failed-put and failed-verification texts, the merge rules (trim, blank keeps the
/// stored value), the exact success text, the call order load -> transaction -> put -> re-read, and that the
/// verification compares exactly the requested fields and nothing else.
/// </summary>

using Klacks.Api.Application.Commands.IdentityProviders;
using Klacks.Api.Application.DTOs.IdentityProviders;
using Klacks.Api.Application.Queries.IdentityProviders;
using Klacks.Api.Application.Skills;
using Klacks.Api.Domain.Models.Authentification;
using Klacks.Api.Infrastructure.Mediator;

namespace Klacks.UnitTest.Skills;

[TestFixture]
public class UpdateIdentityProviderSkillCharacterizationTests
{
    private const string Corrupted = "corrupted";

    private static readonly Guid ProviderId = Guid.Parse("11111111-1111-1111-1111-111111111111");

    private IMediator _mediator = null!;
    private IIdentityProviderRepository _repository = null!;
    private IUnitOfWork _unitOfWork = null!;
    private UpdateIdentityProviderSkill _skill = null!;
    private IdentityProviderResource _existing = null!;
    private IdentityProviderResource? _sent;
    private Action<IdentityProvider>? _corrupt;

    [SetUp]
    public void Setup()
    {
        _sent = null;
        _corrupt = null;
        _existing = new IdentityProviderResource
        {
            Id = ProviderId,
            Name = "Company LDAP",
            Type = IdentityProviderType.Ldap,
            IsEnabled = true,
            SortOrder = 1,
            UseForAuthentication = true,
            Host = "old.example.com",
            Port = 389,
            UseSsl = false,
            BindDn = "cn=reader,dc=example,dc=com",
            BindPassword = "StoredBindSecret"
        };

        _mediator = Substitute.For<IMediator>();
        _mediator.Send(Arg.Any<GetQuery>(), Arg.Any<CancellationToken>())
            .Returns(_ => _existing);
        _mediator.Send(Arg.Any<PutCommand>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                _sent = ci.Arg<PutCommand>().Model;
                return _sent;
            });

        _unitOfWork = Substitute.For<IUnitOfWork>();
        _unitOfWork.ExecuteInTransactionAsync(Arg.Any<Func<Task<Guid>>>())
            .Returns(ci => ci.Arg<Func<Task<Guid>>>()());

        _repository = Substitute.For<IIdentityProviderRepository>();
        _repository.GetNoTracking(Arg.Any<Guid>()).Returns(_ => Persisted());

        _skill = new UpdateIdentityProviderSkill(_mediator, _repository, _unitOfWork);
    }

    private IdentityProvider? Persisted()
    {
        if (_sent == null)
        {
            return null;
        }

        var persisted = new IdentityProvider
        {
            Id = _sent.Id,
            Name = _sent.Name,
            Type = _sent.Type,
            IsEnabled = _sent.IsEnabled,
            SortOrder = _sent.SortOrder,
            UseForAuthentication = _sent.UseForAuthentication,
            UseForClientImport = _sent.UseForClientImport,
            Host = _sent.Host,
            Port = _sent.Port,
            UseSsl = _sent.UseSsl,
            BaseDn = _sent.BaseDn,
            BindDn = _sent.BindDn,
            BindPassword = _sent.BindPassword,
            UserFilter = _sent.UserFilter,
            ClientId = _sent.ClientId,
            ClientSecret = _sent.ClientSecret,
            AuthorizationUrl = _sent.AuthorizationUrl,
            TokenUrl = _sent.TokenUrl,
            UserInfoUrl = _sent.UserInfoUrl,
            Scopes = _sent.Scopes,
            TenantId = _sent.TenantId
        };
        _corrupt?.Invoke(persisted);
        return persisted;
    }

    private static SkillExecutionContext Ctx() => new()
    {
        UserId = Guid.NewGuid(),
        TenantId = Guid.NewGuid(),
        UserName = "tester",
        UserPermissions = new List<string> { "Admin" }
    };

    private static Dictionary<string, object> Params(params (string Key, object Value)[] values)
    {
        var parameters = new Dictionary<string, object> { ["id"] = ProviderId.ToString() };
        foreach (var (key, value) in values)
        {
            parameters[key] = value;
        }

        return parameters;
    }

    [Test]
    public async Task MissingId_IsRefused()
    {
        var result = await _skill.ExecuteAsync(Ctx(), new Dictionary<string, object> { ["host"] = "new.example.com" });

        result.Success.ShouldBeFalse();
        result.Message.ShouldBe("Missing or invalid required parameter 'id'. Use list_identity_providers to find the provider id.");
        await _mediator.DidNotReceive().Send(Arg.Any<GetQuery>(), Arg.Any<CancellationToken>());
    }

    [TestCase("  ")]
    [TestCase("not-a-guid")]
    public async Task BlankOrInvalidId_IsRefused(string id)
    {
        var result = await _skill.ExecuteAsync(Ctx(), new Dictionary<string, object>
        {
            ["id"] = id,
            ["host"] = "new.example.com"
        });

        result.Success.ShouldBeFalse();
        result.Message.ShouldBe("Missing or invalid required parameter 'id'. Use list_identity_providers to find the provider id.");
        await _mediator.DidNotReceive().Send(Arg.Any<GetQuery>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task OnlyBlankFields_AreNothingToUpdate_WithoutLoading()
    {
        var result = await _skill.ExecuteAsync(Ctx(), Params(("name", "  "), ("clientSecret", "")));

        result.Success.ShouldBeFalse();
        result.Message.ShouldBe("Nothing to update. Provide at least one field to change.");
        await _mediator.DidNotReceive().Send(Arg.Any<GetQuery>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task PortOutOfRange_IsRefused_BeforeLoading()
    {
        var result = await _skill.ExecuteAsync(Ctx(), Params(("port", 70000)));

        result.Success.ShouldBeFalse();
        result.Message.ShouldBe("Parameter 'port' must be between 1 and 65535.");
        await _mediator.DidNotReceive().Send(Arg.Any<GetQuery>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task UnknownType_IsRefused_AfterLoading_BeforeAnyFieldIsMerged()
    {
        var result = await _skill.ExecuteAsync(Ctx(), Params(("type", "Kerberos"), ("name", "Neu"), ("host", "h")));

        result.Success.ShouldBeFalse();
        result.Message.ShouldBe("'Kerberos' is not a valid identity provider type. Valid values: Ldap, ActiveDirectory, OAuth2, OpenIdConnect.");
        await _mediator.Received(1).Send(Arg.Any<GetQuery>(), Arg.Any<CancellationToken>());
        _existing.Name.ShouldBe("Company LDAP");
        _existing.Host.ShouldBe("old.example.com");
        await _mediator.DidNotReceive().Send(Arg.Any<PutCommand>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task PutWithoutResult_IsReportedAsNotUpdated()
    {
        _mediator.Send(Arg.Any<PutCommand>(), Arg.Any<CancellationToken>())
            .Returns((IdentityProviderResource?)null);

        var result = await _skill.ExecuteAsync(Ctx(), Params(("host", "new.example.com")));

        result.Success.ShouldBeFalse();
        result.Message.ShouldBe($"Identity provider '{ProviderId}' could not be updated.");
        await _repository.DidNotReceive().GetNoTracking(Arg.Any<Guid>());
    }

    [Test]
    public async Task MergedFields_AreTrimmed_BlankKeepsTheStoredValue_AndTheSuccessTextIsExact()
    {
        var result = await _skill.ExecuteAsync(Ctx(), Params(
            ("name", " Neu "),
            ("type", " oauth2 "),
            ("host", "  h.example.com  "),
            ("bindDn", "   "),
            ("isEnabled", false),
            ("sortOrder", 5),
            ("useForClientImport", true),
            ("clientSecret", " s3cret ")));

        result.Success.ShouldBeTrue(result.Message);
        result.Message.ShouldBe(
            $"Identity provider 'Neu' (OAuth2) updated (id {ProviderId}) and confirmed in the database (verified). Secrets are stored but never returned.");
        _sent.ShouldBeSameAs(_existing);
        _existing.Name.ShouldBe("Neu");
        _existing.Type.ShouldBe(IdentityProviderType.OAuth2);
        _existing.Host.ShouldBe("h.example.com");
        _existing.BindDn.ShouldBe("cn=reader,dc=example,dc=com");
        _existing.BindPassword.ShouldBe("StoredBindSecret");
        _existing.IsEnabled.ShouldBeFalse();
        _existing.SortOrder.ShouldBe(5);
        _existing.UseForAuthentication.ShouldBeTrue();
        _existing.UseForClientImport.ShouldBeTrue();
        _existing.Port.ShouldBe(389);
        _existing.ClientSecret.ShouldBe("s3cret");
        Received.InOrder(() =>
        {
            _mediator.Send(Arg.Any<GetQuery>(), Arg.Any<CancellationToken>());
            _unitOfWork.ExecuteInTransactionAsync(Arg.Any<Func<Task<Guid>>>());
            _mediator.Send(Arg.Any<PutCommand>(), Arg.Any<CancellationToken>());
            _repository.GetNoTracking(ProviderId);
        });
    }

    [TestCase("name")]
    [TestCase("type")]
    [TestCase("isEnabled")]
    [TestCase("sortOrder")]
    [TestCase("useForAuthentication")]
    [TestCase("useForClientImport")]
    [TestCase("port")]
    [TestCase("useSsl")]
    [TestCase("host")]
    [TestCase("baseDn")]
    [TestCase("bindDn")]
    [TestCase("bindPassword")]
    [TestCase("userFilter")]
    [TestCase("clientId")]
    [TestCase("clientSecret")]
    [TestCase("authorizationUrl")]
    [TestCase("tokenUrl")]
    [TestCase("userInfoUrl")]
    [TestCase("scopes")]
    [TestCase("tenantId")]
    public async Task RequestedField_DifferingAfterTheWrite_FailsTheVerification(string field)
    {
        var (value, corrupt) = FieldCase(field);
        _corrupt = corrupt;

        var result = await _skill.ExecuteAsync(Ctx(), Params((field, value)));

        result.Success.ShouldBeFalse();
        var name = field == "name" ? "Neu" : "Company LDAP";
        result.Message.ShouldBe(
            $"Database verification failed: the identity provider '{name}' update could not be confirmed in the database "
            + "after the write — the change was rolled back.");
    }

    [Test]
    public async Task FieldsThatWereNotRequested_AreIgnoredByTheVerification()
    {
        _corrupt = persisted =>
        {
            persisted.Name = Corrupted;
            persisted.Type = IdentityProviderType.OpenIdConnect;
            persisted.IsEnabled = !persisted.IsEnabled;
            persisted.UseForAuthentication = !persisted.UseForAuthentication;
            persisted.UseForClientImport = !persisted.UseForClientImport;
            persisted.Port = 1;
            persisted.UseSsl = !persisted.UseSsl;
            persisted.Host = Corrupted;
            persisted.BaseDn = Corrupted;
            persisted.BindDn = Corrupted;
            persisted.BindPassword = Corrupted;
            persisted.UserFilter = Corrupted;
            persisted.ClientId = Corrupted;
            persisted.ClientSecret = Corrupted;
            persisted.AuthorizationUrl = Corrupted;
            persisted.TokenUrl = Corrupted;
            persisted.UserInfoUrl = Corrupted;
            persisted.Scopes = Corrupted;
            persisted.TenantId = Corrupted;
        };

        var result = await _skill.ExecuteAsync(Ctx(), Params(("sortOrder", 7)));

        result.Success.ShouldBeTrue(result.Message);
        result.Message.ShouldBe(
            $"Identity provider 'Company LDAP' (Ldap) updated (id {ProviderId}) and confirmed in the database (verified). Secrets are stored but never returned.");
    }

    private static (object Value, Action<IdentityProvider> Corrupt) FieldCase(string field)
    {
        switch (field)
        {
            case "name":
                return ("Neu", p => p.Name = Corrupted);
            case "type":
                return ("ActiveDirectory", p => p.Type = IdentityProviderType.OpenIdConnect);
            case "isEnabled":
                return (false, p => p.IsEnabled = true);
            case "sortOrder":
                return (9, p => p.SortOrder = 1);
            case "useForAuthentication":
                return (false, p => p.UseForAuthentication = true);
            case "useForClientImport":
                return (true, p => p.UseForClientImport = false);
            case "port":
                return (636, p => p.Port = 389);
            case "useSsl":
                return (true, p => p.UseSsl = false);
            case "host":
                return ("h", p => p.Host = Corrupted);
            case "baseDn":
                return ("dc=x", p => p.BaseDn = Corrupted);
            case "bindDn":
                return ("cn=x", p => p.BindDn = Corrupted);
            case "bindPassword":
                return ("pw", p => p.BindPassword = Corrupted);
            case "userFilter":
                return ("(uid=*)", p => p.UserFilter = Corrupted);
            case "clientId":
                return ("client", p => p.ClientId = Corrupted);
            case "clientSecret":
                return ("secret", p => p.ClientSecret = Corrupted);
            case "authorizationUrl":
                return ("https://a", p => p.AuthorizationUrl = Corrupted);
            case "tokenUrl":
                return ("https://t", p => p.TokenUrl = Corrupted);
            case "userInfoUrl":
                return ("https://u", p => p.UserInfoUrl = Corrupted);
            case "scopes":
                return ("openid", p => p.Scopes = Corrupted);
            case "tenantId":
                return ("tenant", p => p.TenantId = Corrupted);
            default:
                throw new ArgumentOutOfRangeException(nameof(field), field, null);
        }
    }
}
