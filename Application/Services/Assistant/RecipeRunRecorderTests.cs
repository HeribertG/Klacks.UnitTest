// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Tests for the best-effort recipe-run recorder (W1.5): a fresh plan opens a Running row, a resumed
/// plan reuses it and appends the new turn id, completion/abort close it, and a failure anywhere in
/// the telemetry path degrades to a null handle/log instead of breaking the turn.
/// </summary>

using Klacks.Api.Application.Interfaces;
using Klacks.Api.Application.Services.Assistant;
using Klacks.Api.Domain.Enums;
using Klacks.Api.Domain.Models.Assistant;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NSubstitute;
using NUnit.Framework;
using Shouldly;

namespace Klacks.UnitTest.Application.Services.Assistant;

[TestFixture]
public class RecipeRunRecorderTests
{
    private IRecipeRunRepository _repository = null!;
    private RecipeRunRecorder _recorder = null!;
    private RecipeRun? _added;
    private RecipeRun? _updated;

    [SetUp]
    public void SetUp()
    {
        _repository = Substitute.For<IRecipeRunRepository>();
        _added = null;
        _updated = null;
        _repository.When(r => r.AddAsync(Arg.Any<RecipeRun>(), Arg.Any<CancellationToken>()))
            .Do(ci => _added = ci.Arg<RecipeRun>());
        _repository.When(r => r.UpdateAsync(Arg.Any<RecipeRun>(), Arg.Any<CancellationToken>()))
            .Do(ci => _updated = ci.Arg<RecipeRun>());
        _repository.ExpireStaleRunsAsync(
                Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<DateTime>(), Arg.Any<CancellationToken>())
            .Returns(0);

        var scope = Substitute.For<IServiceScope>();
        var serviceProvider = Substitute.For<IServiceProvider>();
        serviceProvider.GetService(typeof(IRecipeRunRepository)).Returns(_repository);
        scope.ServiceProvider.Returns(serviceProvider);
        var scopeFactory = Substitute.For<IServiceScopeFactory>();
        scopeFactory.CreateScope().Returns(scope);

        _recorder = new RecipeRunRecorder(scopeFactory, Substitute.For<ILogger<RecipeRunRecorder>>());
    }

    private static readonly Guid UserId = Guid.Parse("0c9f5b51-9c2d-4a2d-8a4f-4b2a2f5f9c2d");

    [Test]
    public async Task FreshPlan_OpensRunningRowWithTurnIdAndStep()
    {
        _repository.FindRunningAsync(
                UserId, "conv-1", "onboard-employee", Arg.Any<CancellationToken>())
            .Returns((RecipeRun?)null);

        var handle = await _recorder.BeginOrResumeAsync("onboard-employee", UserId, "conv-1", TurnId(), 0);

        handle.ShouldNotBeNull();
        _added.ShouldNotBeNull();
        _added!.Status.ShouldBe(RecipeRunStatus.Running);
        _added.RecipeName.ShouldBe("onboard-employee");
        _added.TurnIdsJson.ShouldContain(TurnId().ToString());
        _added.LastStep.ShouldBe(0);
    }

    [Test]
    public async Task ResumedPlan_ReusesRunningRowAndAppendsTurnId()
    {
        var existing = new RecipeRun
        {
            Id = Guid.NewGuid(),
            RecipeName = "onboard-employee",
            UserId = UserId,
            ConversationId = "conv-1",
            Status = RecipeRunStatus.Running,
            LastStep = 2,
            TurnIdsJson = "[\"" + TurnId() + "\"]",
            CreateTime = DateTime.UtcNow,
            UpdateTime = DateTime.UtcNow
        };
        _repository.FindRunningAsync(
                UserId, "conv-1", "onboard-employee", Arg.Any<CancellationToken>())
            .Returns(existing);
        var secondTurn = Guid.NewGuid();

        var handle = await _recorder.BeginOrResumeAsync("onboard-employee", UserId, "conv-1", secondTurn, 3);

        handle!.RunId.ShouldBe(existing.Id);
        existing.TurnIdsJson.ShouldContain(secondTurn.ToString());
        existing.LastStep.ShouldBe(3);
        await _repository.Received(1).UpdateAsync(existing, Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task CompleteAsync_ClosesRunningRow()
    {
        var run = GivenRun(RecipeRunStatus.Running);
        _repository.GetByIdAsync(run.Id, Arg.Any<CancellationToken>()).Returns(run);

        await _recorder.CompleteAsync(Handle(run));

        run.Status.ShouldBe(RecipeRunStatus.Completed);
        await _repository.Received(1).UpdateAsync(run, Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task AbortAsync_ClosesRunningRowWithReason()
    {
        var run = GivenRun(RecipeRunStatus.Running);
        _repository.GetByIdAsync(run.Id, Arg.Any<CancellationToken>()).Returns(run);

        await _recorder.AbortAsync(Handle(run), "autonomy gate hold ended the recipe");

        run.Status.ShouldBe(RecipeRunStatus.Aborted);
        run.AbortReason.ShouldBe("autonomy gate hold ended the recipe");
        await _repository.Received(1).UpdateAsync(run, Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task AbortRunningAsync_ClosesByConversationAndRecipe()
    {
        var run = GivenRun(RecipeRunStatus.Running);
        _repository.FindRunningAsync(
                UserId, "conv-1", "onboard-employee", Arg.Any<CancellationToken>())
            .Returns(run);

        await _recorder.AbortRunningAsync("onboard-employee", UserId, "conv-1", "cancelled during ask step");

        run.Status.ShouldBe(RecipeRunStatus.Aborted);
        run.AbortReason.ShouldBe("cancelled during ask step");
        await _repository.Received(1).UpdateAsync(run, Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task RepositoryFailure_DegradesToNullHandleInsteadOfThrowing()
    {
        _repository.When(r => r.FindRunningAsync(
                Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>()))
            .Do(_ => throw new InvalidOperationException("db down"));

        var handle = await _recorder.BeginOrResumeAsync("onboard-employee", UserId, "conv-1", TurnId(), 0);

        handle.ShouldBeNull();
    }

    private static RecipeRun GivenRun(RecipeRunStatus status) => new()
    {
        Id = Guid.NewGuid(),
        RecipeName = "onboard-employee",
        UserId = UserId,
        ConversationId = "conv-1",
        Status = status,
        LastStep = 0,
        TurnIdsJson = "[]",
        CreateTime = DateTime.UtcNow,
        UpdateTime = DateTime.UtcNow
    };

    private static RecipeRunHandle Handle(RecipeRun run) =>
        new(run.Id, run.RecipeName, run.UserId, run.ConversationId);

    private static Guid TurnId() => Guid.Parse("8a4f5b51-7c2d-4a2d-9b4f-4b2a2f5f9c2e");
}
