// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

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
        _skill = new AddBreakSkill(_mediator, _absenceRepository, _clientRepository, SkillClientVisibility.AllVisible(), _breakRepository);

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
            Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<DateOnly>(), AbsenceId, Arg.Any<Guid?>(), Arg.Any<CancellationToken>())
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
            Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<DateOnly>(), AbsenceId, Arg.Any<Guid?>(), Arg.Any<CancellationToken>())
            .Returns(new List<Guid>());

        var result = await _skill.ExecuteAsync(Ctx(), Params());

        Assert.That(result.Success, Is.False);
        Assert.That(result.Message, Does.Contain("verification failed"));
    }

    [Test]
    public async Task ScenarioWrite_RecountsWithToken_AndReportsVerified()
    {
        var token = Guid.NewGuid();
        _breakRepository.GetClientIdsWithBreakOnDate(
            Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<DateOnly>(), AbsenceId, token, Arg.Any<CancellationToken>())
            .Returns(new List<Guid> { ClientId });

        var result = await _skill.ExecuteAsync(Ctx(), Params(analyseToken: token));

        Assert.That(result.Success, Is.True);
        Assert.That(result.Message, Does.Contain("(verified)"));
        await _breakRepository.Received(1).GetClientIdsWithBreakOnDate(
            Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<DateOnly>(), AbsenceId, token, Arg.Any<CancellationToken>());
    }

    [TestCase("not-a-uuid")]
    [TestCase("00000000-0000-0000-0000-000000000000")]
    public async Task InvalidAnalyseToken_ReturnsError_WithoutSendingCommand(string analyseToken)
    {
        var parameters = Params();
        parameters["analyseToken"] = analyseToken;

        var result = await _skill.ExecuteAsync(Ctx(), parameters);

        Assert.That(result.Success, Is.False);
        Assert.That(result.Message, Does.Contain("analyseToken"));
        await _mediator.DidNotReceive().Send(Arg.Any<BulkAddBreaksCommand>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task UnknownClient_ReturnsError_WithoutSendingCommand()
    {
        _clientRepository.Exists(ClientId).Returns(false);

        var result = await _skill.ExecuteAsync(Ctx(), Params());

        Assert.That(result.Success, Is.False);
        await _mediator.DidNotReceive().Send(Arg.Any<BulkAddBreaksCommand>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task HiddenClient_AnswersLikeUnknownId_WithoutSendingCommand()
    {
        var guard = SkillClientVisibility.Hiding(ClientId);
        var skill = new AddBreakSkill(_mediator, _absenceRepository, _clientRepository, guard, _breakRepository);
        var unknownId = Guid.NewGuid();
        var unknownParams = Params();
        unknownParams["clientId"] = unknownId.ToString();

        var hidden = await skill.ExecuteAsync(Ctx(), Params());
        var unknown = await skill.ExecuteAsync(Ctx(), unknownParams);

        Assert.That(hidden.Success, Is.False);
        Assert.That(unknown.Success, Is.False);
        Assert.That(SkillClientVisibility.WithoutId(hidden.Message, ClientId),
            Is.EqualTo(SkillClientVisibility.WithoutId(unknown.Message, unknownId)));
        await guard.Received().IsVisibleAsync(ClientId, Arg.Any<CancellationToken>());
        await _mediator.DidNotReceive().Send(Arg.Any<BulkAddBreaksCommand>(), Arg.Any<CancellationToken>());
        await _breakRepository.DidNotReceive().GetClientIdsWithBreakOnDate(
            Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<DateOnly>(), Arg.Any<Guid>(), Arg.Any<Guid?>(), Arg.Any<CancellationToken>());
    }
}
