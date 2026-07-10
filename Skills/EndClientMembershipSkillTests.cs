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

namespace Klacks.UnitTest.Skills;

[TestFixture]
public class EndClientMembershipSkillTests
{
    private IMembershipRepository _membershipRepository = null!;
    private IClientRepository _clientRepository = null!;
    private IUnitOfWork _unitOfWork = null!;
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
        _unitOfWork = Substitute.For<IUnitOfWork>();
        _companyClock = Substitute.For<ICompanyClock>();

        _clientRepository.Exists(ClientId).Returns(true);
        _companyClock.GetTodayAsync(Arg.Any<CancellationToken>()).Returns(Today);
        _unitOfWork.ExecuteInTransactionAsync(Arg.Any<Func<Task<bool>>>())
            .Returns(ci => ci.Arg<Func<Task<bool>>>()());

        _skill = new EndClientMembershipSkill(_membershipRepository, _clientRepository, _unitOfWork, _companyClock);
    }

    private static SkillExecutionContext Context() => new()
    {
        UserId = Guid.NewGuid(),
        TenantId = Guid.Empty,
        UserName = "tester",
        UserPermissions = []
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

    [Test]
    public async Task SingleActiveMembership_SetsValidUntil_AndReportsVerified()
    {
        var active = Membership();
        var historical = Membership(validUntil: new DateTime(2025, 12, 31));
        _membershipRepository.List().Returns(new List<Membership> { historical, active });
        _membershipRepository.GetNoTracking(active.Id).Returns(_ => active);

        var result = await _skill.ExecuteAsync(Context(), Parameters());

        result.Success.ShouldBeTrue(result.Message);
        result.Message.ShouldContain("verified");
        result.Message.ShouldContain("2026-08-01");
        active.ValidUntil.ShouldBe(new DateTime(2026, 7, 31));
        await _membershipRepository.Received(1).Put(active);
        await _unitOfWork.Received(1).CompleteAsync();
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
        result.Message.ShouldContain("verified");
        second.ValidUntil.ShouldBe(new DateTime(2026, 7, 31));
        first.ValidUntil.ShouldBeNull();
        await _membershipRepository.Received(1).Put(second);
    }

    [Test]
    public async Task MembershipIdOverride_ForeignClient_ReturnsError()
    {
        var foreign = Membership(clientId: Guid.NewGuid());
        _membershipRepository.List().Returns(new List<Membership> { foreign });

        var result = await _skill.ExecuteAsync(Context(), Parameters(membershipId: foreign.Id));

        result.Success.ShouldBeFalse();
        result.Message.ShouldContain("does not belong");
        await _unitOfWork.DidNotReceive().CompleteAsync();
    }

    [Test]
    public async Task NoActiveMembership_ReturnsError()
    {
        var expired = Membership(validUntil: new DateTime(2026, 6, 30));
        _membershipRepository.List().Returns(new List<Membership> { expired });

        var result = await _skill.ExecuteAsync(Context(), Parameters());

        result.Success.ShouldBeFalse();
        result.Message.ShouldContain("no active membership");
        await _unitOfWork.DidNotReceive().CompleteAsync();
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
        await _unitOfWork.DidNotReceive().CompleteAsync();
    }

    [Test]
    public async Task ExitDateBeforeValidFrom_ReturnsError()
    {
        var active = Membership();
        _membershipRepository.List().Returns(new List<Membership> { active });

        var result = await _skill.ExecuteAsync(Context(), Parameters(exitDate: "2025-12-15"));

        result.Success.ShouldBeFalse();
        result.Message.ShouldContain("validFrom");
        await _unitOfWork.DidNotReceive().CompleteAsync();
    }

    [Test]
    public async Task VerificationFails_ReturnsErrorAfterRollback()
    {
        var active = Membership();
        _membershipRepository.List().Returns(new List<Membership> { active });
        _membershipRepository.GetNoTracking(active.Id)
            .Returns(_ => new Membership
            {
                Id = active.Id,
                ClientId = ClientId,
                Type = 0,
                ValidFrom = ValidFrom,
                ValidUntil = null
            });

        var result = await _skill.ExecuteAsync(Context(), Parameters());

        result.Success.ShouldBeFalse();
        result.Message.ShouldContain("rolled back");
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
