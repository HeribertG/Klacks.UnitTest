// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Pins the accept rule for scenarios that carry variant keys: no keys means no rights query; the right to edit shifts is
/// required (the planner floor and a caller without any HTTP context are refused); a consumed key refuses even an admin;
/// the rights are checked before the consumed keys, so a refused caller learns nothing about the key state; a key of a
/// group the caller may not see, however it reached the scenario, refuses the accept like a missing scenario, before its state.
/// </summary>

using Klacks.Api.Application.Exceptions;
using Klacks.Api.Application.Services.Groups;
using Klacks.Api.Application.Services.ScenarioVariants;
using Klacks.Api.Domain.Constants;
using Klacks.Api.Domain.Models.Schedules;
using Klacks.Api.Infrastructure.Persistence;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace Klacks.UnitTest.Application.Services.ScenarioVariants;

[TestFixture]
public class ScenarioVariantAcceptGateTests
{
    private static readonly Guid ScenarioId = Guid.NewGuid();
    private static readonly Guid Token = Guid.NewGuid();
    private static readonly Guid VisibleGroupId = Guid.NewGuid();
    private static readonly Guid HiddenGroupId = Guid.NewGuid();

    private IScenarioVariantRepository _variantRepository = null!;
    private IUserService _userService = null!;
    private IGroupVisibilityGuard _groupGuard = null!;
    private ScenarioVariantAcceptGate _sut = null!;
    private CancellationToken _ct;

    [SetUp]
    public void SetUp()
    {
        _variantRepository = Substitute.For<IScenarioVariantRepository>();
        _userService = Substitute.For<IUserService>();
        _groupGuard = Substitute.For<IGroupVisibilityGuard>();
        _groupGuard.IsGroupVisibleAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(call => call.ArgAt<Guid>(0) != HiddenGroupId);
        _sut = new ScenarioVariantAcceptGate(_variantRepository, _userService, _groupGuard);
        _ct = new CancellationTokenSource().Token;
    }

    private static AnalyseScenario Variant(string name, AnalyseScenarioStatus status = AnalyseScenarioStatus.Active) => new()
    {
        Id = Guid.NewGuid(),
        Token = Guid.NewGuid(),
        Name = name,
        GroupId = VisibleGroupId,
        Kind = AnalyseScenarioKind.Variant,
        Status = status,
    };

    private void StubVariants(params AnalyseScenario[] variants)
        => _variantRepository.GetVariantsOfScenarioAsync(Token, _ct).Returns(variants.ToList());

    private static IReadOnlyList<string> RightsOf(string role) => Permissions.ExpandRoles([role]);

    [Test]
    public async Task Ensure_AKeyOfAHiddenGroup_IsAnsweredLikeAMissingScenario()
    {
        var hidden = Variant("Hidden key");
        hidden.GroupId = HiddenGroupId;
        StubVariants(Variant("Visible key"), hidden);
        _userService.GetRights().Returns(RightsOf(Roles.Authorised));

        var ex = await Should.ThrowAsync<KeyNotFoundException>(() => _sut.EnsureAcceptAllowedAsync(ScenarioId, Token, _ct));

        ex.Message.ShouldBe(string.Format(ScenarioGroupVisibility.ScenarioNotFoundFormat, ScenarioId));
        ex.Message.ShouldNotContain(hidden.Id.ToString());
    }

    [Test]
    public async Task Ensure_AHiddenKeyThatIsConsumed_DoesNotRevealItsState()
    {
        var hidden = Variant("Hidden key", AnalyseScenarioStatus.Accepted);
        hidden.GroupId = HiddenGroupId;
        StubVariants(hidden);
        _userService.GetRights().Returns(RightsOf(Roles.Authorised));

        await Should.ThrowAsync<KeyNotFoundException>(() => _sut.EnsureAcceptAllowedAsync(ScenarioId, Token, _ct));
    }

    [Test]
    public async Task Ensure_ScenarioWithoutVariants_DoesNothing_AndAsksNoRights()
    {
        StubVariants();

        await _sut.EnsureAcceptAllowedAsync(ScenarioId, Token, _ct);

        _userService.DidNotReceive().GetRights();
    }

    [Test]
    public async Task Ensure_CallerWithoutCanEditShifts_ThrowsForbiddenWithTheConstantMessage()
    {
        StubVariants(Variant("Winter keys"));
        _userService.GetRights().Returns(Permissions.PlannerFloor);

        var ex = await Should.ThrowAsync<ForbiddenException>(() => _sut.EnsureAcceptAllowedAsync(ScenarioId, Token, _ct));

        ex.Message.ShouldBe(ScenarioAcceptDefaults.VariantAcceptForbiddenMessage);
    }

    [Test]
    public async Task Ensure_NoHttpContext_RealUserServiceYieldsFloorRights_Throws()
    {
        var accessor = Substitute.For<IHttpContextAccessor>();
        accessor.HttpContext.Returns((HttpContext?)null);
        var userService = new UserService(accessor, null!, Substitute.For<ILogger<UserService>>());
        var sut = new ScenarioVariantAcceptGate(_variantRepository, userService, _groupGuard);
        StubVariants(Variant("Winter keys"));

        await Should.ThrowAsync<ForbiddenException>(() => sut.EnsureAcceptAllowedAsync(ScenarioId, Token, _ct));
    }

    [Test]
    public async Task Ensure_SupervisorWithCanEditShifts_ActiveVariants_Passes()
    {
        StubVariants(Variant("Winter keys"), Variant("Summer keys"));
        _userService.GetRights().Returns(RightsOf(Roles.Authorised));

        await Should.NotThrowAsync(() => _sut.EnsureAcceptAllowedAsync(ScenarioId, Token, _ct));
    }

    [Test]
    public async Task Ensure_Admin_ActiveVariants_Passes()
    {
        StubVariants(Variant("Winter keys"));
        _userService.GetRights().Returns(RightsOf(Roles.Admin));

        await Should.NotThrowAsync(() => _sut.EnsureAcceptAllowedAsync(ScenarioId, Token, _ct));
    }

    [Test]
    public async Task Ensure_ConsumedVariant_Throws_EvenForAdmin()
    {
        StubVariants(Variant("Winter keys", AnalyseScenarioStatus.Accepted));
        _userService.GetRights().Returns(RightsOf(Roles.Admin));

        var ex = await Should.ThrowAsync<ScenarioVariantConsumedException>(() => _sut.EnsureAcceptAllowedAsync(ScenarioId, Token, _ct));

        ex.ConflictCode.ShouldBe(ScenarioVariantConsumedException.ErrorCode);
        ex.VariantNames.ShouldBe(new[] { "Winter keys" });
    }

    [TestCase(AnalyseScenarioStatus.Accepted)]
    [TestCase(AnalyseScenarioStatus.Rejected)]
    [TestCase(AnalyseScenarioStatus.Superseded)]
    public async Task Ensure_VariantThatIsNotActive_Throws(AnalyseScenarioStatus status)
    {
        StubVariants(Variant("Winter keys", status));
        _userService.GetRights().Returns(RightsOf(Roles.Admin));

        await Should.ThrowAsync<ScenarioVariantConsumedException>(() => _sut.EnsureAcceptAllowedAsync(ScenarioId, Token, _ct));
    }

    [Test]
    public async Task Ensure_MixedVariants_ListsOnlyTheConsumedNames_InOrder()
    {
        StubVariants(
            Variant("Zeta keys", AnalyseScenarioStatus.Accepted),
            Variant("Mid keys"),
            Variant("Alpha keys", AnalyseScenarioStatus.Superseded));
        _userService.GetRights().Returns(RightsOf(Roles.Authorised));

        var ex = await Should.ThrowAsync<ScenarioVariantConsumedException>(() => _sut.EnsureAcceptAllowedAsync(ScenarioId, Token, _ct));

        ex.VariantNames.ShouldBe(new[] { "Alpha keys", "Zeta keys" });
        ex.Message.ShouldBe(string.Format(ScenarioAcceptDefaults.VariantConsumedMessageFormat, "Alpha keys, Zeta keys"));
    }

    [Test]
    public async Task Ensure_ConsumedVariantAndMissingRight_ThrowsForbidden_NotTheKeyState()
    {
        StubVariants(Variant("Winter keys", AnalyseScenarioStatus.Accepted));
        _userService.GetRights().Returns(Permissions.PlannerFloor);

        await Should.ThrowAsync<ForbiddenException>(() => _sut.EnsureAcceptAllowedAsync(ScenarioId, Token, _ct));
    }

    [Test]
    public async Task Ensure_ReadsTheVariantsOfTheGivenScenarioToken()
    {
        StubVariants(Variant("Winter keys"));
        _userService.GetRights().Returns(RightsOf(Roles.Admin));

        await _sut.EnsureAcceptAllowedAsync(ScenarioId, Token, _ct);

        await _variantRepository.Received(1).GetVariantsOfScenarioAsync(Token, _ct);
    }
}
