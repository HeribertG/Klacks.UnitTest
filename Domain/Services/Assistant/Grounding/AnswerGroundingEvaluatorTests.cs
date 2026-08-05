// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Shadow evaluator truth table: tier decisions (uncovered UUID = tier 1, two significant
/// uncovered numbers = tier 3, one is silence), skip gates (genuine failure skips, confirmation
/// does not), user-stated values count as grounded, agent_self memories never do, Off mode does
/// nothing, and an inner exception lands in the no-verdict counter instead of a fake verdict.
/// </summary>

using Klacks.Api.Domain.Interfaces.Assistant;
using Klacks.Api.Domain.Models.Assistant;
using Klacks.Api.Domain.Models.Assistant.Grounding;
using Klacks.Api.Domain.Services.Assistant.Grounding;
using Klacks.Api.Domain.Services.Assistant.Providers;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;
using Shouldly;

namespace Klacks.UnitTest.Domain.Services.Assistant.Grounding;

[TestFixture]
public class AnswerGroundingEvaluatorTests
{
    private IAnswerGroundingRepository _repository = null!;
    private IAgentMemoryRepository _memoryRepository = null!;
    private AnswerGroundingFinding? _finding;
    private AnswerGroundingDailyCounter? _counter;

    [SetUp]
    public void SetUp()
    {
        _finding = null;
        _counter = null;
        _repository = Substitute.For<IAnswerGroundingRepository>();
        _repository.AddFindingAsync(Arg.Do<AnswerGroundingFinding>(f => _finding = f), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);
        _repository.IncrementDailyAsync(Arg.Do<AnswerGroundingDailyCounter>(c => _counter = c), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);
        _memoryRepository = Substitute.For<IAgentMemoryRepository>();
        _memoryRepository.GetByIdsAsync(Arg.Any<IReadOnlyList<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(new List<AgentMemory>());
    }

    private AnswerGroundingEvaluator Evaluator(string mode = "Shadow")
        => new(new AnswerGroundingOptions(mode), _repository, _memoryRepository,
            NullLogger<AnswerGroundingEvaluator>.Instance);

    private static LLMContext Ctx(string message = "Wie sieht der Plan aus?") => new()
    {
        Message = message,
        Language = "de",
        UserId = Guid.NewGuid().ToString()
    };

    private static LLMFunctionCall DataCall(string dataJson) => new()
    {
        FunctionName = "read_schedule_state",
        Success = true,
        ResultKind = Klacks.Api.Domain.Enums.LLMFunctionResultKind.Data,
        DataJson = [dataJson]
    };

    [Test]
    public async Task OffMode_DoesNothing()
    {
        await Evaluator("Off").EvaluateAsync(Guid.NewGuid(), Ctx(), "Antwort mit 4711 Stunden und 8999 mehr.", []);

        _counter.ShouldBeNull();
        _finding.ShouldBeNull();
    }

    [Test]
    public async Task UncoveredUuid_WritesTierOneFinding()
    {
        var response = "Die Schicht 3f2a0c1d-9b4e-4f6a-8c2d-1234567890ab wurde angepasst.";

        await Evaluator().EvaluateAsync(Guid.NewGuid(), Ctx(), response, [DataCall("""{"Count":1}""")]);

        _finding.ShouldNotBeNull();
        _finding!.Tier.ShouldBe(1);
        _counter!.TurnsWithFindings.ShouldBe(1);
        _finding.EvidenceExcerpt.ShouldContain("read_schedule_state");
    }

    [Test]
    public async Task TwoSignificantUncoveredNumbers_WriteTierThreeFinding()
    {
        var response = "Das Team hat 4711.5 Stunden geleistet und 8999 Franken Spesen erfasst.";

        await Evaluator().EvaluateAsync(Guid.NewGuid(), Ctx(), response, [DataCall("""{"Hours":12.5}""")]);

        _finding.ShouldNotBeNull();
        _finding!.Tier.ShouldBe(3);
        _finding.ClaimsUncovered.ShouldBe(2);
    }

    [Test]
    public async Task SingleUncoveredNumber_StaysSilent()
    {
        await Evaluator().EvaluateAsync(Guid.NewGuid(), Ctx(), "Es sind 4711.5 Stunden.", [DataCall("""{"Hours":12.5}""")]);

        _finding.ShouldBeNull();
        _counter!.TurnsClean.ShouldBe(1);
    }

    [Test]
    public async Task CoveredValues_AreClean()
    {
        var response = "Das Team hat 173.5 Stunden auf 2 Einträge verteilt.";

        await Evaluator().EvaluateAsync(Guid.NewGuid(), Ctx(), response,
            [DataCall("""{"Count":2,"Results":[{"Hours":100.0},{"Hours":73.5}]}""")]);

        _finding.ShouldBeNull();
        _counter!.TurnsClean.ShouldBe(1);
        _counter.ClaimsUncovered.ShouldBe(0);
    }

    [Test]
    public async Task UserStatedNumber_CountsAsGrounded()
    {
        await Evaluator().EvaluateAsync(Guid.NewGuid(),
            Ctx("Stimmt es, dass wir 4711.5 Stunden und 8999 Franken haben?"),
            "Die 4711.5 Stunden und 8999 Franken kann ich nicht prüfen.", []);

        _finding.ShouldBeNull();
        _counter!.TurnsClean.ShouldBe(1);
    }

    [Test]
    public async Task GenuineFailedCall_SkipsTheTurn()
    {
        var failed = new LLMFunctionCall { FunctionName = "x", Success = false };

        await Evaluator().EvaluateAsync(Guid.NewGuid(), Ctx(), "Antwort mit 4711.5 und 8999.", [failed]);

        _counter!.TurnsSkipped.ShouldBe(1);
        _counter.TurnsEvaluated.ShouldBe(0);
        _finding.ShouldBeNull();
    }

    [Test]
    public async Task ConfirmationCall_DoesNotSkipTheTurn()
    {
        var confirmation = new LLMFunctionCall { FunctionName = "x", Success = false, RequiresConfirmation = true };

        await Evaluator().EvaluateAsync(Guid.NewGuid(), Ctx(), "Soll ich das wirklich tun?", [confirmation]);

        _counter!.TurnsEvaluated.ShouldBe(1);
        _counter.TurnsClean.ShouldBe(1);
    }

    [Test]
    public async Task AgentSelfMemory_NeverGrounds_OtherMemoryDoes()
    {
        var memoryIds = new List<Guid> { Guid.NewGuid() };
        _memoryRepository.GetByIdsAsync(Arg.Any<IReadOnlyList<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(new List<AgentMemory>
            {
                new() { Content = "Budget beträgt 4711.5 CHF", Source = "user_stated" },
                new() { Content = "Ich sagte einst 8999", Source = "agent_self" }
            });
        var context = Ctx();
        context.InjectedMemoryIds = memoryIds;

        await Evaluator().EvaluateAsync(Guid.NewGuid(), context, "Es geht um 4711.5 CHF und um 8999 Franken.", []);

        _finding.ShouldBeNull("4711.5 is memory-grounded and a single uncovered number stays silent");
        _counter!.ClaimsUncovered.ShouldBe(1, "8999 from agent_self must stay uncovered");
    }

    [Test]
    public async Task InnerException_LandsInNoVerdict()
    {
        _repository.AddFindingAsync(Arg.Any<AnswerGroundingFinding>(), Arg.Any<CancellationToken>())
            .Returns<Task>(_ => throw new InvalidOperationException("boom"));

        await Evaluator().EvaluateAsync(Guid.NewGuid(), Ctx(),
            "Die Schicht 3f2a0c1d-9b4e-4f6a-8c2d-1234567890ab wurde angepasst.", [DataCall("""{"Count":1}""")]);

        _counter.ShouldNotBeNull();
        _counter!.TurnsNoVerdict.ShouldBe(1);
        _counter.TurnsWithFindings.ShouldBe(0);
    }
}
