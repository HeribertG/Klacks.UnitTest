// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Characterization tests for UpdateMembershipSkill.ExecuteAsync, written before its long body was split into
/// per-field steps: the exact refusal, no-op, failure and verification texts, the order of the changed fields and the
/// property order of the serialized result, and that clearValidUntil=true never reads the validUntil string.
/// </summary>

using System.Text.Json;
using Klacks.Api.Application.Commands;
using Klacks.Api.Application.DTOs.Associations;
using Klacks.Api.Application.DTOs.Staffs;
using Klacks.Api.Application.Queries;
using Klacks.Api.Application.Skills;
using Klacks.Api.Infrastructure.Mediator;
using Klacks.UnitTest.TestHelpers;

namespace Klacks.UnitTest.Skills;

[TestFixture]
public class UpdateMembershipSkillCharacterizationTests
{
    private const string VerifiedSuffix =
        " and confirmed in the database (verified). ValidFrom is the plannability boundary in the schedule.";

    private IMediator _mediator = null!;
    private UpdateMembershipSkill _skill = null!;
    private Guid _membershipId;
    private MembershipResource _membership = null!;

    [SetUp]
    public void Setup()
    {
        _membershipId = Guid.NewGuid();
        _membership = new MembershipResource
        {
            Id = _membershipId,
            ClientId = Guid.NewGuid(),
            ValidFrom = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            ValidUntil = new DateTime(2026, 12, 31, 0, 0, 0, DateTimeKind.Utc)
        };

        _mediator = Substitute.For<IMediator>();
        _mediator.Send(Arg.Any<GetQuery<MembershipResource>>(), Arg.Any<CancellationToken>())
            .Returns(_membership);
        _mediator.Send(Arg.Any<PutCommand<MembershipResource>>(), Arg.Any<CancellationToken>())
            .Returns(ci => ((PutCommand<MembershipResource>)ci[0]).Resource);
        _mediator.Send(Arg.Any<GetQuery<ClientResource>>(), Arg.Any<CancellationToken>())
            .Returns(new ClientResource());

        _skill = new UpdateMembershipSkill(
            _mediator,
            Substitute.For<IPendingConfirmationStore>(),
            new FixedCompanyClock(new DateTimeOffset(2026, 6, 28, 8, 0, 0, TimeSpan.Zero)));
    }

    private static SkillExecutionContext Ctx() => new()
    {
        UserId = Guid.NewGuid(),
        TenantId = Guid.NewGuid(),
        UserName = "admin",
        UserPermissions = new List<string> { "CanEditClients" }
    };

    private Task<SkillResult> RunAsync(params (string Key, object Value)[] extra)
    {
        var parameters = new Dictionary<string, object> { ["membershipId"] = _membershipId.ToString() };
        foreach (var (key, value) in extra)
        {
            parameters[key] = value;
        }

        return _skill.ExecuteAsync(Ctx(), parameters);
    }

    private static JsonElement Data(SkillResult result) =>
        JsonDocument.Parse(JsonSerializer.Serialize(result.Data)).RootElement;

    [Test]
    public async Task UnknownMembership_ReturnsExactText()
    {
        _mediator.Send(Arg.Any<GetQuery<MembershipResource>>(), Arg.Any<CancellationToken>())
            .Returns<MembershipResource>(_ => throw new KeyNotFoundException());

        var result = await RunAsync(("validFrom", "2026-03-01"));

        result.Success.ShouldBeFalse();
        result.Message.ShouldBe($"Membership '{_membershipId}' not found.");
    }

    [Test]
    public async Task InvalidValidFrom_ReturnsExactText()
    {
        var result = await RunAsync(("validFrom", "irgendwann"));

        result.Success.ShouldBeFalse();
        result.Message.ShouldBe(SkillDateParser.InvalidDateMessageFor("validFrom", "irgendwann"));
    }

    [Test]
    public async Task InvalidValidUntil_ReturnsExactText_AfterValidFromWasApplied()
    {
        var result = await RunAsync(("validFrom", "2026-03-01"), ("validUntil", "irgendwann"));

        result.Success.ShouldBeFalse();
        result.Message.ShouldBe(SkillDateParser.InvalidDateMessageFor("validUntil", "irgendwann"));
        _membership.ValidFrom.ShouldBe(new DateTime(2026, 3, 1, 0, 0, 0, DateTimeKind.Utc));
        await _mediator.DidNotReceive().Send(Arg.Any<PutCommand<MembershipResource>>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task ClearValidUntil_IgnoresAnUnreadableValidUntilString()
    {
        var result = await RunAsync(("clearValidUntil", true), ("validUntil", "irgendwann"));

        result.Success.ShouldBeTrue(result.Message);
        result.Message.ShouldBe("Membership updated (validUntil)" + VerifiedSuffix);
        _membership.ValidUntil.ShouldBeNull();
    }

    [Test]
    public async Task ClearValidUntil_OnOpenEndedMembership_IsANoOp()
    {
        _membership.ValidUntil = null;

        var result = await RunAsync(("clearValidUntil", true));

        result.Success.ShouldBeTrue();
        result.Message.ShouldBe("No fields supplied for update — membership left unchanged.");
        Data(result).EnumerateObject().Select(p => p.Name).ShouldBe(new[] { "MembershipId", "ChangedFields" });
        await _mediator.DidNotReceive().Send(Arg.Any<PutCommand<MembershipResource>>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task ValidUntilBeforeValidFrom_ReturnsExactText()
    {
        var result = await RunAsync(("validUntil", "2025-12-01"));

        result.Success.ShouldBeFalse();
        result.Message.ShouldBe("validUntil must not be before validFrom.");
    }

    [Test]
    public async Task PutReturningNull_ReturnsExactText()
    {
        _mediator.Send(Arg.Any<PutCommand<MembershipResource>>(), Arg.Any<CancellationToken>())
            .Returns((MembershipResource?)null);

        var result = await RunAsync(("validFrom", "2026-03-01"));

        result.Success.ShouldBeFalse();
        result.Message.ShouldBe($"Updating membership '{_membershipId}' failed.");
    }

    [Test]
    public async Task ReReadMismatch_ReturnsExactText()
    {
        var stale = new MembershipResource
        {
            Id = _membershipId,
            ClientId = _membership.ClientId,
            ValidFrom = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            ValidUntil = _membership.ValidUntil
        };
        _mediator.Send(Arg.Any<GetQuery<MembershipResource>>(), Arg.Any<CancellationToken>())
            .Returns(_membership, stale);

        var result = await RunAsync(("validFrom", "2026-03-01"));

        result.Success.ShouldBeFalse();
        result.Message.ShouldBe(
            $"Database verification failed: membership '{_membershipId}' does not show the requested values after the update. " +
            "Use list_client_memberships to check the current state before retrying.");
    }

    [Test]
    public async Task ReReadMissing_ReturnsExactText()
    {
        var getCalls = 0;
        _mediator.Send(Arg.Any<GetQuery<MembershipResource>>(), Arg.Any<CancellationToken>())
            .Returns(_ => ++getCalls == 1 ? _membership : throw new KeyNotFoundException());

        var result = await RunAsync(("validFrom", "2026-03-01"));

        result.Success.ShouldBeFalse();
        result.Message.ShouldBe(
            $"Database verification failed: membership '{_membershipId}' could not be read back after the update. " +
            "Use list_client_memberships to check the current state before retrying.");
    }

    [Test]
    public async Task BothDates_AreReportedInOrder_WithResultData()
    {
        var result = await RunAsync(("validUntil", "2027-06-30"), ("validFrom", "2026-03-01"));

        result.Success.ShouldBeTrue(result.Message);
        result.Message.ShouldBe("Membership updated (validFrom, validUntil)" + VerifiedSuffix);
        var data = Data(result);
        data.EnumerateObject().Select(p => p.Name).ShouldBe(new[]
        {
            "MembershipId", "ChangedFields", "ClientId", "ValidFrom", "ValidUntil"
        });
        data.GetProperty("ClientId").GetGuid().ShouldBe(_membership.ClientId);
    }
}
