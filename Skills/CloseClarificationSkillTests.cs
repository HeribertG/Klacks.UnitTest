// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Unit tests for the close_clarification skill: a planner takes over an open clarification by its id
/// or by the employee, the transition is conditional (only from Open) and verified by re-reading, and
/// missing parameters, no open clarification, an already closed one and a lost race are reported as
/// errors in plain words. After a lost race the clarification is re-read so the message names what
/// really happened (expired, taken over, answered) instead of always claiming an expiry.
/// </summary>

using Klacks.Api.Application.Skills;
using Klacks.Api.Domain.Interfaces.Inbound;
using Klacks.Api.Domain.Models.Inbound;
using Klacks.UnitTest.TestHelpers;

namespace Klacks.UnitTest.Skills;

[TestFixture]
public class CloseClarificationSkillTests
{
    private static readonly DateTime NowUtc = new(2026, 9, 23, 7, 0, 0, DateTimeKind.Utc);
    private static readonly Guid ClientId = Guid.NewGuid();

    private IInboundClarificationRepository _repository = null!;
    private CloseClarificationSkill _skill = null!;

    [SetUp]
    public void Setup()
    {
        _repository = Substitute.For<IInboundClarificationRepository>();
        _skill = new CloseClarificationSkill(_repository, new SettableTimeProvider(NowUtc));
    }

    private static SkillExecutionContext Ctx() => new()
    {
        UserId = Guid.NewGuid(),
        TenantId = Guid.NewGuid(),
        UserName = "planner",
        UserPermissions = new List<string> { "CanManageAutomation" }
    };

    private static InboundClarification Clarification(InboundClarificationStatus status, Guid? id = null) => new()
    {
        Id = id ?? Guid.NewGuid(),
        ClientId = ClientId,
        SenderDisplay = "Anna Muster",
        Question = "Heißt das, du kannst heute nicht arbeiten?",
        Status = status
    };

    private void ResolveReturns(Guid id, bool won) =>
        _repository.TryResolveAsync(
                id,
                InboundClarificationStatus.TakenOver,
                answerSourceId: null,
                resultAnalysisId: null,
                resolvedAtUtc: NowUtc,
                cancellationToken: Arg.Any<CancellationToken>())
            .Returns(won);

    [Test]
    public async Task ByEmployee_ClosesTheOpenClarification()
    {
        var open = Clarification(InboundClarificationStatus.Open);
        _repository.GetOpenByClientAsync(ClientId, Arg.Any<CancellationToken>()).Returns(open);
        ResolveReturns(open.Id, true);
        _repository.GetByIdAsync(open.Id, Arg.Any<CancellationToken>())
            .Returns(Clarification(InboundClarificationStatus.TakenOver, open.Id));

        var result = await _skill.ExecuteAsync(Ctx(), new Dictionary<string, object> { ["clientId"] = ClientId.ToString() });

        result.Success.ShouldBeTrue(result.Message);
        result.Message.ShouldContain("Anna Muster");
        await _repository.Received(1).TryResolveAsync(
            open.Id,
            InboundClarificationStatus.TakenOver,
            answerSourceId: null,
            resultAnalysisId: null,
            resolvedAtUtc: NowUtc,
            cancellationToken: Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task ById_ClosesTheOpenClarification()
    {
        var open = Clarification(InboundClarificationStatus.Open);
        var closed = Clarification(InboundClarificationStatus.TakenOver, open.Id);
        _repository.GetByIdAsync(open.Id, Arg.Any<CancellationToken>()).Returns(open, closed);
        ResolveReturns(open.Id, true);

        var result = await _skill.ExecuteAsync(Ctx(), new Dictionary<string, object> { ["clarificationId"] = open.Id.ToString() });

        result.Success.ShouldBeTrue(result.Message);
    }

    [Test]
    public async Task NoParameter_IsAnError()
    {
        var result = await _skill.ExecuteAsync(Ctx(), new Dictionary<string, object>());

        result.Success.ShouldBeFalse();
        await _repository.DidNotReceiveWithAnyArgs().TryResolveAsync(default, default, default, default, default, default);
    }

    [Test]
    public async Task NoOpenClarification_IsAnError()
    {
        _repository.GetOpenByClientAsync(ClientId, Arg.Any<CancellationToken>()).Returns((InboundClarification?)null);

        var result = await _skill.ExecuteAsync(Ctx(), new Dictionary<string, object> { ["clientId"] = ClientId.ToString() });

        result.Success.ShouldBeFalse();
    }

    [Test]
    public async Task AlreadyClosed_IsAnErrorInPlainWords()
    {
        var expired = Clarification(InboundClarificationStatus.Expired);
        _repository.GetByIdAsync(expired.Id, Arg.Any<CancellationToken>()).Returns(expired);

        var result = await _skill.ExecuteAsync(Ctx(), new Dictionary<string, object> { ["clarificationId"] = expired.Id.ToString() });

        result.Success.ShouldBeFalse();
        result.Message.ShouldContain("not answered in time");
        result.Message.ShouldNotContain("Expired");
    }

    [Test]
    public async Task AlreadyTakenOver_IsNotDescribedAsAnswered()
    {
        var taken = Clarification(InboundClarificationStatus.TakenOver);
        _repository.GetByIdAsync(taken.Id, Arg.Any<CancellationToken>()).Returns(taken);

        var result = await _skill.ExecuteAsync(Ctx(), new Dictionary<string, object> { ["clarificationId"] = taken.Id.ToString() });

        result.Success.ShouldBeFalse();
        result.Message.ShouldContain("taken over by a planner");
        result.Message.ShouldNotContain("answered by the employee");
        await _repository.DidNotReceiveWithAnyArgs().TryResolveAsync(default, default, default, default, default, default);
    }

    [Test]
    public async Task OnlySuggested_ExplainsThatNothingWasSent()
    {
        var suggested = Clarification(InboundClarificationStatus.Suggested);
        _repository.GetByIdAsync(suggested.Id, Arg.Any<CancellationToken>()).Returns(suggested);

        var result = await _skill.ExecuteAsync(Ctx(), new Dictionary<string, object> { ["clarificationId"] = suggested.Id.ToString() });

        result.Success.ShouldBeFalse();
        result.Message.ShouldContain("never sent");
        await _repository.DidNotReceiveWithAnyArgs().TryResolveAsync(default, default, default, default, default, default);
    }

    [Test]
    public async Task LostRace_IsAnError()
    {
        var open = Clarification(InboundClarificationStatus.Open);
        _repository.GetOpenByClientAsync(ClientId, Arg.Any<CancellationToken>()).Returns(open);
        ResolveReturns(open.Id, false);

        var result = await _skill.ExecuteAsync(Ctx(), new Dictionary<string, object> { ["clientId"] = ClientId.ToString() });

        result.Success.ShouldBeFalse();
    }

    [Test]
    public async Task LostRace_AgainstTheExpirySweep_SaysNotAnsweredInTime()
    {
        var open = Clarification(InboundClarificationStatus.Open);
        _repository.GetOpenByClientAsync(ClientId, Arg.Any<CancellationToken>()).Returns(open);
        ResolveReturns(open.Id, false);
        _repository.GetByIdAsync(open.Id, Arg.Any<CancellationToken>())
            .Returns(Clarification(InboundClarificationStatus.Expired, open.Id));

        var result = await _skill.ExecuteAsync(Ctx(), new Dictionary<string, object> { ["clientId"] = ClientId.ToString() });

        result.Success.ShouldBeFalse();
        result.Message.ShouldContain("not answered in time");
    }

    [Test]
    public async Task LostRace_AgainstAnotherPlanner_SaysTakenOverAndNeverExpired()
    {
        var open = Clarification(InboundClarificationStatus.Open);
        _repository.GetOpenByClientAsync(ClientId, Arg.Any<CancellationToken>()).Returns(open);
        ResolveReturns(open.Id, false);
        _repository.GetByIdAsync(open.Id, Arg.Any<CancellationToken>())
            .Returns(Clarification(InboundClarificationStatus.TakenOver, open.Id));

        var result = await _skill.ExecuteAsync(Ctx(), new Dictionary<string, object> { ["clientId"] = ClientId.ToString() });

        result.Success.ShouldBeFalse();
        result.Message.ShouldContain("taken over by a planner");
        result.Message.ShouldNotContain("not answered in time");
    }

    [Test]
    public async Task LostRace_AgainstTheEmployeesAnswer_SaysAnswered()
    {
        var open = Clarification(InboundClarificationStatus.Open);
        _repository.GetOpenByClientAsync(ClientId, Arg.Any<CancellationToken>()).Returns(open);
        ResolveReturns(open.Id, false);
        _repository.GetByIdAsync(open.Id, Arg.Any<CancellationToken>())
            .Returns(Clarification(InboundClarificationStatus.Answered, open.Id));

        var result = await _skill.ExecuteAsync(Ctx(), new Dictionary<string, object> { ["clientId"] = ClientId.ToString() });

        result.Success.ShouldBeFalse();
        result.Message.ShouldContain("answered by the employee");
        result.Message.ShouldNotContain("not answered in time");
    }

    [Test]
    public async Task LostRace_WhenTheRowCannotBeReloaded_UsesAGenericText()
    {
        var open = Clarification(InboundClarificationStatus.Open);
        _repository.GetOpenByClientAsync(ClientId, Arg.Any<CancellationToken>()).Returns(open);
        ResolveReturns(open.Id, false);
        _repository.GetByIdAsync(open.Id, Arg.Any<CancellationToken>()).Returns((InboundClarification?)null);

        var result = await _skill.ExecuteAsync(Ctx(), new Dictionary<string, object> { ["clientId"] = ClientId.ToString() });

        result.Success.ShouldBeFalse();
        result.Message.ShouldContain("closed in the meantime");
    }
}
