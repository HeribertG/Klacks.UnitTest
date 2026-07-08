// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Unit tests for AddBreakSkill self-verification: a main-schedule write is confirmed by a database
/// recount and reported as verified, a recount that does not find the break fails loudly instead of
/// claiming success, and a scenario write (analyseToken set) skips the recount because the recount
/// query only sees main-schedule rows.
/// </summary>

using Klacks.Api.Application.Commands.Breaks;
using Klacks.Api.Application.DTOs.Schedules;
using Klacks.Api.Application.Skills;
using Klacks.Api.Infrastructure.Mediator;

namespace Klacks.UnitTest.Skills;

[TestFixture]
public class AddBreakSkillTests
{
    private IMediator _mediator = null!;
    private IAbsenceRepository _absenceRepository = null!;
    private IClientRepository _clientRepository = null!;
    private IBreakRepository _breakRepository = null!;
    private AddBreakSkill _skill = null!;

    private static readonly Guid ClientId = Guid.NewGuid();
    private static readonly Guid AbsenceId = Guid.NewGuid();

    [SetUp]
    public void Setup()
    {
        _mediator = Substitute.For<IMediator>();
        _absenceRepository = Substitute.For<IAbsenceRepository>();
        _clientRepository = Substitute.For<IClientRepository>();
        _breakRepository = Substitute.For<IBreakRepository>();
        _skill = new AddBreakSkill(_mediator, _absenceRepository, _clientRepository, _breakRepository);

        _clientRepository.Exists(ClientId).Returns(true);
        _absenceRepository.Exists(AbsenceId).Returns(true);
        _mediator.Send(Arg.Any<BulkAddBreaksCommand>(), Arg.Any<CancellationToken>())
            .Returns(new BulkBreaksResponse());
    }

    private static SkillExecutionContext Ctx() => new()
    {
        UserId = Guid.NewGuid(),
        TenantId = Guid.NewGuid(),
        UserName = "tester",
        UserPermissions = new List<string> { "CanEditShifts" }
    };

    private static Dictionary<string, object> Params(Guid? analyseToken = null)
    {
        var p = new Dictionary<string, object>
        {
            ["clientId"] = ClientId.ToString(),
            ["absenceId"] = AbsenceId.ToString(),
            ["date"] = "2026-08-01"
        };
        if (analyseToken is not null)
        {
            p["analyseToken"] = analyseToken.Value.ToString();
        }

        return p;
    }

    [Test]
    public async Task MainSchedule_RecountConfirms_ReportsVerified()
    {
        _breakRepository.GetClientIdsWithBreakOnDate(
            Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<DateOnly>(), AbsenceId, Arg.Any<CancellationToken>())
            .Returns(new List<Guid> { ClientId });

        var result = await _skill.ExecuteAsync(Ctx(), Params());

        Assert.That(result.Success, Is.True);
        Assert.That(result.Message, Does.Contain("verified"));
        await _mediator.Received(1).Send(Arg.Any<BulkAddBreaksCommand>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task MainSchedule_RecountFindsNothing_ReturnsError()
    {
        _breakRepository.GetClientIdsWithBreakOnDate(
            Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<DateOnly>(), AbsenceId, Arg.Any<CancellationToken>())
            .Returns(new List<Guid>());

        var result = await _skill.ExecuteAsync(Ctx(), Params());

        Assert.That(result.Success, Is.False);
        Assert.That(result.Message, Does.Contain("verification failed"));
    }

    [Test]
    public async Task ScenarioWrite_SkipsRecount_AndDoesNotClaimVerified()
    {
        var result = await _skill.ExecuteAsync(Ctx(), Params(analyseToken: Guid.NewGuid()));

        Assert.That(result.Success, Is.True);
        Assert.That(result.Message, Does.Contain("Scenario write"));
        Assert.That(result.Message, Does.Not.Contain("(verified)"));
        await _breakRepository.DidNotReceive().GetClientIdsWithBreakOnDate(
            Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<DateOnly>(), Arg.Any<Guid>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task UnknownClient_ReturnsError_WithoutSendingCommand()
    {
        _clientRepository.Exists(ClientId).Returns(false);

        var result = await _skill.ExecuteAsync(Ctx(), Params());

        Assert.That(result.Success, Is.False);
        await _mediator.DidNotReceive().Send(Arg.Any<BulkAddBreaksCommand>(), Arg.Any<CancellationToken>());
    }
}
