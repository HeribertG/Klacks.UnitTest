// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// ScenarioTokenAccessFilter refuses a request that carries the token of a scenario the caller may not see - as a query
/// or route parameter, as a body property, or inside a list element - before the action runs (KeyNotFoundException,
/// answered with 404 by the middleware). Visible tokens, requests without a token and unrestricted callers pass. The
/// controller cases bind arguments exactly as the real actions name them (RecoveryController.Candidates,
/// WorksController.GetWorkSchedule / BulkAdd, BreaksController.BulkAdd).
/// </summary>

using System.Reflection;
using Klacks.Api.Application.DTOs.Filter;
using Klacks.Api.Application.DTOs.Schedules;
using Klacks.Api.Application.Services.Groups;
using Klacks.Api.Presentation.Controllers.UserBackend.Schedules;
using Klacks.Api.Presentation.Filters;
using Klacks.UnitTest.TestHelpers;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Routing;

namespace Klacks.UnitTest.Presentation.Filters;

[TestFixture]
public class ScenarioTokenAccessFilterTests
{
    private readonly Guid _hiddenToken = Guid.NewGuid();
    private readonly Guid _visibleToken = Guid.NewGuid();
    private bool _actionRan;

    [SetUp]
    public void SetUp() => _actionRan = false;

    private static ActionExecutingContext Context(IDictionary<string, object?> arguments)
    {
        var actionContext = new ActionContext(new DefaultHttpContext(), new RouteData(), new ActionDescriptor());
        return new ActionExecutingContext(actionContext, new List<IFilterMetadata>(), arguments, controller: new object());
    }

    private Task<ActionExecutedContext> Next()
    {
        _actionRan = true;
        return Task.FromResult<ActionExecutedContext>(null!);
    }

    private Task RunAsync(IScenarioTokenAccessGuard guard, IDictionary<string, object?> arguments) =>
        new ScenarioTokenAccessFilter(guard).OnActionExecutionAsync(Context(arguments), Next);

    private static string ParameterName(Type controller, string action, Type parameterType) =>
        controller.GetMethod(action, BindingFlags.Public | BindingFlags.Instance)!
            .GetParameters().Single(p => p.ParameterType == parameterType).Name!;

    [Test]
    public async Task QueryParameter_OfHiddenScenario_IsRefusedBeforeTheAction()
    {
        var name = ParameterName(typeof(RecoveryController), nameof(RecoveryController.Candidates), typeof(Guid?));

        var act = () => RunAsync(TestScenarioTokenAccess.Hiding(_hiddenToken), new Dictionary<string, object?> { [name] = (Guid?)_hiddenToken });

        (await Should.ThrowAsync<KeyNotFoundException>(act)).Message.ShouldBe($"Scenario with token {_hiddenToken} not found");
        _actionRan.ShouldBeFalse();
    }

    [Test]
    public async Task BodyProperty_OfHiddenScenario_IsRefused()
    {
        var name = ParameterName(typeof(WorksController), nameof(WorksController.GetWorkSchedule), typeof(WorkScheduleFilter));
        var filter = new WorkScheduleFilter { AnalyseToken = _hiddenToken };

        await Should.ThrowAsync<KeyNotFoundException>(() =>
            RunAsync(TestScenarioTokenAccess.Hiding(_hiddenToken), new Dictionary<string, object?> { [name] = filter }));
        _actionRan.ShouldBeFalse();
    }

    [Test]
    public async Task ListElement_OfHiddenScenario_IsRefused()
    {
        var name = ParameterName(typeof(WorksController), nameof(WorksController.BulkAdd), typeof(BulkAddWorksRequest));
        var request = new BulkAddWorksRequest
        {
            Works = [new BulkWorkItem { AnalyseToken = _visibleToken }, new BulkWorkItem { AnalyseToken = _hiddenToken }]
        };

        await Should.ThrowAsync<KeyNotFoundException>(() =>
            RunAsync(TestScenarioTokenAccess.Hiding(_hiddenToken), new Dictionary<string, object?> { [name] = request }));
        _actionRan.ShouldBeFalse();
    }

    [Test]
    public async Task BreakListElement_OfHiddenScenario_IsRefused()
    {
        var name = ParameterName(typeof(BreaksController), nameof(BreaksController.BulkAdd), typeof(BulkAddBreaksRequest));
        var request = new BulkAddBreaksRequest { Breaks = [new BulkBreakItem { AnalyseToken = _hiddenToken }] };

        await Should.ThrowAsync<KeyNotFoundException>(() =>
            RunAsync(TestScenarioTokenAccess.Hiding(_hiddenToken), new Dictionary<string, object?> { [name] = request }));
    }

    [Test]
    public async Task VisibleTokens_ReachTheAction_EachCheckedOnce()
    {
        var guard = TestScenarioTokenAccess.AllVisible();
        var request = new BulkAddWorksRequest
        {
            Works = [new BulkWorkItem { AnalyseToken = _visibleToken }, new BulkWorkItem { AnalyseToken = _visibleToken }]
        };

        await RunAsync(guard, new Dictionary<string, object?> { ["request"] = request });

        _actionRan.ShouldBeTrue();
        await guard.Received(1).EnsureVisibleAsync(_visibleToken, Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task RequestWithoutToken_DoesNotAskTheGuard()
    {
        var guard = TestScenarioTokenAccess.AllVisible();

        await RunAsync(guard, new Dictionary<string, object?>
        {
            ["id"] = Guid.NewGuid(),
            ["filter"] = new WorkScheduleFilter(),
            ["analyseToken"] = null
        });

        _actionRan.ShouldBeTrue();
        await guard.DidNotReceiveWithAnyArgs().EnsureVisibleAsync(default, default);
    }

    [Test]
    public async Task UnrestrictedCaller_PassesEvenWithAnUnknownToken_WithoutQuery()
    {
        var repository = Substitute.For<IAnalyseScenarioRepository>();
        var guard = new ScenarioTokenAccessGuard(repository, TestGroupWriteVisibility.UnrestrictedGroups());

        await RunAsync(guard, new Dictionary<string, object?> { ["analyseToken"] = (Guid?)Guid.NewGuid() });

        _actionRan.ShouldBeTrue();
        await repository.DidNotReceiveWithAnyArgs().GetByTokenAsync(default, default);
    }

    [Test]
    public void Scanner_FindsTokensAtEveryLevel_AndIgnoresEmptyAndUnrelatedValues()
    {
        var tokens = ScenarioTokenArgumentScanner.Collect(new Dictionary<string, object?>
        {
            ["AnalyseToken"] = _visibleToken,
            ["request"] = new BulkAddWorksRequest { Works = [new BulkWorkItem { AnalyseToken = Guid.Empty }, new BulkWorkItem { AnalyseToken = _hiddenToken }] },
            ["ids"] = new List<Guid> { Guid.NewGuid() },
            ["name"] = "analyseToken"
        });

        tokens.ShouldBe(new[] { _visibleToken, _hiddenToken }, ignoreOrder: true);
    }
}
