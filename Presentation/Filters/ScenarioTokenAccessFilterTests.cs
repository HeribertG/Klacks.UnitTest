// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// ScenarioTokenAccessFilter refuses a request that carries the token of a scenario the caller may not see - as a query
/// or route parameter, as a body property, or inside a list element - before the action runs (KeyNotFoundException,
/// answered with 404 by the middleware). Visible tokens, requests without a token and unrestricted callers pass. The
/// token of a variant (key) is refused for everyone, admins included, unless the action carries AllowVariantToken and the
/// variant's group is visible; the attribute never opens the token of a hidden scenario. The
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

    private static ActionExecutingContext Context(IDictionary<string, object?> arguments, bool allowVariantToken = false)
    {
        var descriptor = new ActionDescriptor();
        if (allowVariantToken)
        {
            descriptor.EndpointMetadata = [new AllowVariantTokenAttribute()];
        }

        var actionContext = new ActionContext(new DefaultHttpContext(), new RouteData(), descriptor);
        return new ActionExecutingContext(actionContext, new List<IFilterMetadata>(), arguments, controller: new object());
    }

    private Task<ActionExecutedContext> Next()
    {
        _actionRan = true;
        return Task.FromResult<ActionExecutedContext>(null!);
    }

    private Task RunAsync(IScenarioTokenAccessGuard guard, IDictionary<string, object?> arguments, bool allowVariantToken = false) =>
        new ScenarioTokenAccessFilter(guard).OnActionExecutionAsync(Context(arguments, allowVariantToken), Next);

    private static ScenarioTokenAccessGuard RealGuard(
        IAnalyseScenarioRepository repository, Guid visibleGroupId, Guid scenarioToken, Guid? groupId, AnalyseScenarioKind kind)
    {
        repository.GetByTokenAsync(scenarioToken, Arg.Any<CancellationToken>())
            .Returns(new AnalyseScenario { Token = scenarioToken, GroupId = groupId, Kind = kind });
        var groups = Substitute.For<IGroupVisibilityGuard>();
        groups.IsUnrestrictedAsync(Arg.Any<CancellationToken>()).Returns(false);
        groups.IsGroupVisibleAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns(ci => ci.ArgAt<Guid>(0) == visibleGroupId);
        return new ScenarioTokenAccessGuard(repository, groups);
    }

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
    public async Task UnrestrictedCaller_PassesWithAnUnknownToken()
    {
        var repository = Substitute.For<IAnalyseScenarioRepository>();
        var guard = new ScenarioTokenAccessGuard(repository, TestGroupWriteVisibility.UnrestrictedGroups());
        var token = Guid.NewGuid();

        await RunAsync(guard, new Dictionary<string, object?> { ["analyseToken"] = (Guid?)token });

        _actionRan.ShouldBeTrue();
        await repository.Received(1).GetByTokenAsync(token, Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task VariantToken_WithoutTheAttribute_IsRefusedEvenForAnUnrestrictedCaller()
    {
        var repository = Substitute.For<IAnalyseScenarioRepository>();
        repository.GetByTokenAsync(_visibleToken, Arg.Any<CancellationToken>())
            .Returns(new AnalyseScenario { Token = _visibleToken, Kind = AnalyseScenarioKind.Variant });
        var guard = new ScenarioTokenAccessGuard(repository, TestGroupWriteVisibility.UnrestrictedGroups());

        var act = () => RunAsync(guard, new Dictionary<string, object?> { ["analyseToken"] = (Guid?)_visibleToken });

        (await Should.ThrowAsync<KeyNotFoundException>(act)).Message.ShouldBe($"Scenario with token {_visibleToken} not found");
        _actionRan.ShouldBeFalse();
    }

    [Test]
    public async Task VariantToken_OfAVisibleGroup_ReachesAnActionWithTheAttribute()
    {
        var groupId = Guid.NewGuid();
        var guard = RealGuard(Substitute.For<IAnalyseScenarioRepository>(), groupId, _visibleToken, groupId, AnalyseScenarioKind.Variant);

        await RunAsync(guard, new Dictionary<string, object?> { ["analyseToken"] = (Guid?)_visibleToken }, allowVariantToken: true);

        _actionRan.ShouldBeTrue();
    }

    [Test]
    public async Task VariantToken_OfAHiddenGroup_IsRefusedEvenWithTheAttribute()
    {
        var guard = RealGuard(
            Substitute.For<IAnalyseScenarioRepository>(), Guid.NewGuid(), _hiddenToken, Guid.NewGuid(), AnalyseScenarioKind.Variant);

        await Should.ThrowAsync<KeyNotFoundException>(() =>
            RunAsync(guard, new Dictionary<string, object?> { ["analyseToken"] = (Guid?)_hiddenToken }, allowVariantToken: true));
        _actionRan.ShouldBeFalse();
    }

    [Test]
    public async Task ScenarioToken_OfAHiddenGroup_IsStillRefusedWithTheAttribute()
    {
        var guard = RealGuard(
            Substitute.For<IAnalyseScenarioRepository>(), Guid.NewGuid(), _hiddenToken, Guid.NewGuid(), AnalyseScenarioKind.Scenario);

        await Should.ThrowAsync<KeyNotFoundException>(() =>
            RunAsync(guard, new Dictionary<string, object?> { ["analyseToken"] = (Guid?)_hiddenToken }, allowVariantToken: true));
        _actionRan.ShouldBeFalse();
    }

    [Test]
    public async Task WithTheAttribute_OnlyAVisibleVariantSkipsTheScenarioCheck()
    {
        var variantToken = Guid.NewGuid();
        var guard = Substitute.For<IScenarioTokenAccessGuard>();
        guard.IsVariantTokenVisibleAsync(variantToken, Arg.Any<CancellationToken>()).Returns(true);
        var request = new BulkAddWorksRequest
        {
            Works = [new BulkWorkItem { AnalyseToken = variantToken }, new BulkWorkItem { AnalyseToken = _visibleToken }]
        };

        await RunAsync(guard, new Dictionary<string, object?> { ["request"] = request }, allowVariantToken: true);

        _actionRan.ShouldBeTrue();
        await guard.Received(1).EnsureVisibleAsync(_visibleToken, Arg.Any<CancellationToken>());
        await guard.DidNotReceive().EnsureVisibleAsync(variantToken, Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task WithoutTheAttribute_TheVariantDoorIsNeverAsked()
    {
        var guard = TestScenarioTokenAccess.AllVisible();

        await RunAsync(guard, new Dictionary<string, object?> { ["analyseToken"] = (Guid?)_visibleToken });

        await guard.DidNotReceiveWithAnyArgs().IsVariantTokenVisibleAsync(default, default);
        await guard.Received(1).EnsureVisibleAsync(_visibleToken, Arg.Any<CancellationToken>());
    }

    [Test]
    public void AllowVariantTokenAttribute_IsRestrictedToMethods()
    {
        var usage = typeof(AllowVariantTokenAttribute).GetCustomAttribute<AttributeUsageAttribute>();

        usage.ShouldNotBeNull();
        usage.ValidOn.ShouldBe(AttributeTargets.Method);
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
