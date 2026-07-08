// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Unit tests for BulkAddAbsenceForGroupSkill: a preview (apply=false) never sends the bulk command,
/// an apply creates one absence per member that does not already have it and confirms the count by a
/// database recount, members who already have the absence are skipped, and unknown group or absence-type
/// names are rejected with the real options.
/// </summary>

using Klacks.Api.Application.Commands.Breaks;
using Klacks.Api.Application.DTOs.Schedules;
using Klacks.Api.Application.Interfaces;
using Klacks.Api.Application.Skills;
using Klacks.Api.Domain.Common;
using Klacks.Api.Domain.Models.Assistant;
using Klacks.Api.Domain.Models.Associations;
using Klacks.Api.Domain.Models.Schedules;
using Klacks.Api.Infrastructure.Mediator;

namespace Klacks.UnitTest.Skills;

[TestFixture]
public class BulkAddAbsenceForGroupSkillTests
{
    private IGroupRepository _groupRepository = null!;
    private IAbsenceRepository _absenceRepository = null!;
    private IGetAllClientIdsFromGroupAndSubgroups _memberService = null!;
    private IBreakRepository _breakRepository = null!;
    private IMediator _mediator = null!;
    private BulkAddAbsenceForGroupSkill _skill = null!;

    private static readonly Guid BernGroupId = Guid.NewGuid();
    private static readonly Guid SchulungId = Guid.NewGuid();
    private static readonly Guid C1 = Guid.NewGuid();
    private static readonly Guid C2 = Guid.NewGuid();
    private static readonly Guid C3 = Guid.NewGuid();

    [SetUp]
    public void Setup()
    {
        _groupRepository = Substitute.For<IGroupRepository>();
        _absenceRepository = Substitute.For<IAbsenceRepository>();
        _memberService = Substitute.For<IGetAllClientIdsFromGroupAndSubgroups>();
        _breakRepository = Substitute.For<IBreakRepository>();
        _mediator = Substitute.For<IMediator>();
        _skill = new BulkAddAbsenceForGroupSkill(
            _groupRepository, TestGroupScopeGuard.Unrestricted(), _absenceRepository, _memberService, _breakRepository, _mediator);

        _groupRepository.List().Returns(new List<Group> { new() { Id = BernGroupId, Name = "Bern" } });
        _absenceRepository.List().Returns(new List<Absence>
        {
            new() { Id = SchulungId, Name = new MultiLanguage { De = "Schulung", En = "Training" } }
        });
        _memberService.GetAllClientIdsFromGroupAndSubgroups(BernGroupId)
            .Returns(new List<Guid> { C1, C2, C3 });
        _mediator.Send(Arg.Any<BulkAddBreaksCommand>(), Arg.Any<CancellationToken>())
            .Returns(new BulkBreaksResponse());
    }

    private static SkillExecutionContext Ctx() => new()
    {
        UserId = Guid.NewGuid(),
        TenantId = Guid.NewGuid(),
        UserName = "tester",
        UserPermissions = new List<string> { "CanEditShifts", "CanViewGroups" }
    };

    private static Dictionary<string, object> Params(bool apply, string absenceType = "Schulung") => new()
    {
        ["groupName"] = "Bern",
        ["absenceType"] = absenceType,
        ["date"] = "2026-08-01",
        ["apply"] = apply
    };

    [Test]
    public async Task Preview_DoesNotSendBulkCommand_AndCountsMembers()
    {
        _breakRepository.GetClientIdsWithBreakOnDate(
            Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<DateOnly>(), Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(new List<Guid>());

        var result = await _skill.ExecuteAsync(Ctx(), Params(apply: false));

        Assert.That(result.Success, Is.True);
        Assert.That(result.Message, Does.Contain("Preview"));
        await _mediator.DidNotReceive().Send(Arg.Any<BulkAddBreaksCommand>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task Apply_PlacesAbsenceForAllMembers_AndReportsVerified()
    {
        _breakRepository.GetClientIdsWithBreakOnDate(
            Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<DateOnly>(), Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(new List<Guid>(), new List<Guid> { C1, C2, C3 });

        var result = await _skill.ExecuteAsync(Ctx(), Params(apply: true));

        Assert.That(result.Success, Is.True);
        Assert.That(result.Message, Does.Contain("verified"));
        Assert.That(result.Message, Does.Not.Contain("WARNING"));
        await _mediator.Received(1).Send(
            Arg.Is<BulkAddBreaksCommand>(c => c.Request.Breaks.Count == 3), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task Apply_SkipsMembersWhoAlreadyHaveTheAbsence()
    {
        _breakRepository.GetClientIdsWithBreakOnDate(
            Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<DateOnly>(), Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(new List<Guid> { C1 }, new List<Guid> { C2, C3 });

        var result = await _skill.ExecuteAsync(Ctx(), Params(apply: true));

        Assert.That(result.Success, Is.True);
        Assert.That(result.Message, Does.Contain("already had it"));
        await _mediator.Received(1).Send(
            Arg.Is<BulkAddBreaksCommand>(c => c.Request.Breaks.Count == 2), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task Apply_WarnsWhenRecountConfirmsFewerThanAttempted()
    {
        _breakRepository.GetClientIdsWithBreakOnDate(
            Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<DateOnly>(), Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(new List<Guid>(), new List<Guid> { C1 });

        var result = await _skill.ExecuteAsync(Ctx(), Params(apply: true));

        Assert.That(result.Success, Is.True);
        Assert.That(result.Message, Does.Contain("WARNING"));
    }

    [Test]
    public async Task ReturnsError_WhenMoreThanMaxMembersWouldBeChanged()
    {
        var manyMembers = Enumerable.Range(0, 101).Select(_ => Guid.NewGuid()).ToList();
        _memberService.GetAllClientIdsFromGroupAndSubgroups(BernGroupId).Returns(manyMembers);
        _breakRepository.GetClientIdsWithBreakOnDate(
            Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<DateOnly>(), Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(new List<Guid>());

        var result = await _skill.ExecuteAsync(Ctx(), Params(apply: true));

        Assert.That(result.Success, Is.False);
        Assert.That(result.Message, Does.Contain("101").And.Contain("100"));
        Assert.That(result.Message, Does.Contain("smaller"));
        await _mediator.DidNotReceive().Send(Arg.Any<BulkAddBreaksCommand>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task AllowsApply_WhenExcessMembersAlreadyHaveTheAbsence()
    {
        var manyMembers = Enumerable.Range(0, 150).Select(_ => Guid.NewGuid()).ToList();
        _memberService.GetAllClientIdsFromGroupAndSubgroups(BernGroupId).Returns(manyMembers);
        var alreadyHave = manyMembers.Take(60).ToList();
        _breakRepository.GetClientIdsWithBreakOnDate(
            Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<DateOnly>(), Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(alreadyHave, manyMembers);

        var result = await _skill.ExecuteAsync(Ctx(), Params(apply: true));

        Assert.That(result.Success, Is.True);
        await _mediator.Received(1).Send(
            Arg.Is<BulkAddBreaksCommand>(c => c.Request.Breaks.Count == 90), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task ReturnsError_WhenAbsenceTypeIsUnknown()
    {
        var result = await _skill.ExecuteAsync(Ctx(), Params(apply: false, absenceType: "Nonexistent"));

        Assert.That(result.Success, Is.False);
        Assert.That(result.Message, Does.Contain("not found"));
        Assert.That(result.Message, Does.Contain("Schulung"));
    }

    [Test]
    public async Task ReturnsError_WhenGroupIsUnknown()
    {
        var parameters = new Dictionary<string, object>
        {
            ["groupName"] = "Zürich",
            ["absenceType"] = "Schulung",
            ["date"] = "2026-08-01"
        };

        var result = await _skill.ExecuteAsync(Ctx(), parameters);

        Assert.That(result.Success, Is.False);
        Assert.That(result.Message, Does.Contain("not found"));
        Assert.That(result.Message, Does.Contain("Bern"));
    }
}
