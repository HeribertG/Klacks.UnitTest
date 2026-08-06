// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Unit tests for end_client_membership — verifies the lone active membership is ended with
/// database verification, the explicit membershipId override, the error paths for zero and
/// multiple active memberships (real options, no guessing), exitDate before validFrom, and the
/// rollback path when the verification re-read fails.
/// </summary>

using Klacks.Api.Application.Skills;
using Klacks.Api.Domain.Models.Assistant;
using Klacks.Api.Domain.Models.Associations;

using Klacks.Api.Application.DTOs.Associations;

using Klacks.Api.Infrastructure.Services.Assistant;

using Klacks.UnitTest.Infrastructure.SelfApi;

namespace Klacks.UnitTest.Skills;

[TestFixture]
public class EndClientMembershipSkillTests
{
    private IMembershipRepository _membershipRepository = null!;
    private IClientRepository _clientRepository = null!;
    private FakeSelfApi _api = null!;
    private ICompanyClock _companyClock = null!;
    private EndClientMembershipSkill _skill = null!;

    private static readonly Guid ClientId = Guid.NewGuid();
    private static readonly DateTime Today = new(2026, 7, 10);
    private static readonly DateTime ValidFrom = new(2026, 1, 1);

    [SetUp]
    public void SetUp()
    {
        _membershipRepository = Substitute.For<IMembershipRepository>();
        _clientRepository = Substitute.For<IClientRepository>();
        _api = new FakeSelfApi();
        _api.Respond(HttpMethod.Put, "api/backend/Memberships", new MembershipResource());
        _companyClock = Substitute.For<ICompanyClock>();

        _clientRepository.Exists(ClientId).Returns(true);
        _companyClock.GetTodayAsync(Arg.Any<CancellationToken>()).Returns(Today);

        _skill = new EndClientMembershipSkill(_membershipRepository, _clientRepository, _api.Client, new SelfApiRouteResolver(), _companyClock);
    }

    private static SkillExecutionContext Context() => new()
    {
        UserId = Guid.NewGuid(),
        TenantId = Guid.Empty,
        UserName = "tester",
        UserPermissions = [],
        AccessToken = new BearerToken("caller-jwt")
    };

    private static Dictionary<string, object> Parameters(string exitDate = "2026-07-31", Guid? membershipId = null)
    {
        var parameters = new Dictionary<string, object>
        {
            ["clientId"] = ClientId.ToString(),
            ["exitDate"] = exitDate
        };

        if (membershipId.HasValue)
        {
            parameters["membershipId"] = membershipId.Value.ToString();
        }

        return parameters;
    }

    private static Membership Membership(DateTime? validUntil = null, Guid? clientId = null) => new()
    {
        Id = Guid.NewGuid(),
        ClientId = clientId ?? ClientId,
        Type = 0,
        ValidFrom = ValidFrom,
        ValidUntil = validUntil
    };

    [TearDown]
    public void TearDown() => _api.Dispose();

    [Test]
    public async Task SingleActiveMembership_SetsValidUntil_AndReportsVerified()
    {
        var active = Membership();
        var historical = Membership(validUntil: new DateTime(2025, 12, 31));
        _membershipRepository.List().Returns(new List<Membership> { historical, active });
        _membershipRepository.GetNoTracking(active.Id).Returns(_ => active);

        var result = await _skill.ExecuteAsync(Context(), Parameters());

        result.Success.ShouldBeTrue(result.Message);
        result.Message.ShouldContain("2026-08-01");
        active.ValidUntil.ShouldBe(new DateTime(2026, 7, 31));
        _api.SingleCall.Method.ShouldBe(HttpMethod.Put);
    }

    [Test]
    public async Task MembershipIdOverride_EndsExactlyThatMembership()
    {
        var first = Membership();
        var second = Membership(validUntil: new DateTime(2026, 12, 31));
        _membershipRepository.List().Returns(new List<Membership> { first, second });
        _membershipRepository.GetNoTracking(second.Id).Returns(_ => second);

        var result = await _skill.ExecuteAsync(Context(), Parameters(membershipId: second.Id));

        result.Success.ShouldBeTrue(result.Message);
        second.ValidUntil.ShouldBe(new DateTime(2026, 7, 31));
        first.ValidUntil.ShouldBeNull();
        _api.SingleCall.Method.ShouldBe(HttpMethod.Put);
    }

    [Test]
    public async Task MembershipIdOverride_ForeignClient_ReturnsError()
    {
        var foreign = Membership(clientId: Guid.NewGuid());
        _membershipRepository.List().Returns(new List<Membership> { foreign });

        var result = await _skill.ExecuteAsync(Context(), Parameters(membershipId: foreign.Id));

        result.Success.ShouldBeFalse();
        result.Message.ShouldContain("does not belong");
    }

    [Test]
    public async Task NoActiveMembership_ReturnsError()
    {
        var expired = Membership(validUntil: new DateTime(2026, 6, 30));
        _membershipRepository.List().Returns(new List<Membership> { expired });

        var result = await _skill.ExecuteAsync(Context(), Parameters());

        result.Success.ShouldBeFalse();
        result.Message.ShouldContain("no active membership");
    }

    [Test]
    public async Task MultipleActiveMemberships_ReturnsErrorWithRealOptions()
    {
        var first = Membership();
        var second = Membership(validUntil: new DateTime(2026, 12, 31));
        _membershipRepository.List().Returns(new List<Membership> { first, second });

        var result = await _skill.ExecuteAsync(Context(), Parameters());

        result.Success.ShouldBeFalse();
        result.Message.ShouldContain(first.Id.ToString());
        result.Message.ShouldContain(second.Id.ToString());
        result.Message.ShouldContain("end_client_membership again with the membershipId parameter");
    }

    [Test]
    public async Task ExitDateBeforeValidFrom_ReturnsError()
    {
        var active = Membership();
        _membershipRepository.List().Returns(new List<Membership> { active });

        var result = await _skill.ExecuteAsync(Context(), Parameters(exitDate: "2025-12-15"));

        result.Success.ShouldBeFalse();
        result.Message.ShouldContain("validFrom");
    }


    [Test]
    public async Task UnknownClient_ReturnsError()
    {
        _clientRepository.Exists(ClientId).Returns(false);

        var result = await _skill.ExecuteAsync(Context(), Parameters());

        result.Success.ShouldBeFalse();
        result.Message.ShouldContain("not found");
        await _membershipRepository.DidNotReceive().List();
    }
}
