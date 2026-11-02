// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Creating a scenario with variants (keys): a group-less scenario, a key of a foreign group, an unknown, hidden or consumed
/// key are refused before anything is staged; a planner without any further right may select keys; every shift of a key goes
/// into the clone with the key token; the links are staged before the single commit; a request without keys clones as before.
/// </summary>

using Klacks.Api.Application.Commands.AnalyseScenarios;
using Klacks.Api.Application.Constants;
using Klacks.Api.Application.DTOs.Schedules;
using Klacks.Api.Application.Exceptions;
using Klacks.Api.Application.Handlers.AnalyseScenarios;
using Klacks.Api.Application.Services.ScenarioVariants;
using Klacks.Api.Domain.Exceptions;
using Klacks.UnitTest.TestHelpers;
using Microsoft.Extensions.Logging;

namespace Klacks.UnitTest.Application.Handlers.AnalyseScenarios;

[TestFixture]
public class CreateScenarioWithVariantsTests
{
    private const string AddStep = "add";
    private const string CloneStep = "clone";
    private const string LinkStep = "link";
    private const string CommitStep = "commit";
    private static readonly DateOnly From = new(2099, 3, 1);
    private static readonly DateOnly Until = new(2099, 3, 31);

    private IAnalyseScenarioRepository _repository = null!;
    private IScenarioVariantRepository _variantRepository = null!;
    private IAnalyseScenarioService _scenarioService = null!;
    private IUnitOfWork _unitOfWork = null!;
    private IGroupVisibilityGuard _guard = null!;
    private Dictionary<Guid, AnalyseScenario> _variants = null!;
    private Dictionary<Guid, List<Guid>> _shiftIdsByToken = null!;
    private Guid _groupId;
    private Guid _subGroupId;
    private Guid _foreignGroupId;
    private Guid _hiddenGroupId;

    [SetUp]
    public void SetUp()
    {
        _groupId = Guid.NewGuid();
        _subGroupId = Guid.NewGuid();
        _foreignGroupId = Guid.NewGuid();
        _hiddenGroupId = Guid.NewGuid();
        _variants = [];
        _shiftIdsByToken = [];

        _repository = Substitute.For<IAnalyseScenarioRepository>();
        _variantRepository = Substitute.For<IScenarioVariantRepository>();
        _scenarioService = Substitute.For<IAnalyseScenarioService>();
        _unitOfWork = Substitute.For<IUnitOfWork>();

        _guard = Substitute.For<IGroupVisibilityGuard>();
        _guard.IsUnrestrictedAsync(Arg.Any<CancellationToken>()).Returns(false);
        _guard.IsGroupVisibleAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(call => call.ArgAt<Guid>(0) != _hiddenGroupId);

        _scenarioService.GetGroupHierarchyIdsAsync(_groupId, Arg.Any<CancellationToken>())
            .Returns(new List<Guid> { _groupId, _subGroupId });
        _variantRepository.GetVariantsByIdsAsync(Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(call => call.ArgAt<IReadOnlyCollection<Guid>>(0)
                .Where(_variants.ContainsKey)
                .Select(id => _variants[id])
                .ToList());
        _variantRepository.GetShiftIdsByVariantTokensAsync(Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(call => call.ArgAt<IReadOnlyCollection<Guid>>(0)
                .SelectMany(token => _shiftIdsByToken[token])
                .ToList());
    }

    [Test]
    public async Task Keys_OnAGrouplessScenario_AreRefused_AndNothingIsStaged()
    {
        var variant = Variant(_groupId);

        var act = () => Handler(TestGroupWriteVisibility.UnrestrictedGroups()).Handle(Command(null, variant.Id), CancellationToken.None);

        (await Should.ThrowAsync<InvalidRequestException>(act)).Message.ShouldBe(ScenarioVariantSelection.GrouplessScenarioMessage);
        await ShouldHaveStagedNothingAsync();
    }

    [Test]
    public async Task AKeyOfAnotherGroup_IsRefused_AndNothingIsStaged()
    {
        var variant = Variant(_foreignGroupId);

        var act = () => Handler(_guard).Handle(Command(_groupId, variant.Id), CancellationToken.None);

        (await Should.ThrowAsync<InvalidRequestException>(act)).Message.ShouldBe(ScenarioVariantSelection.VariantOutsideGroupMessage);
        await ShouldHaveStagedNothingAsync();
    }

    [Test]
    public async Task AConsumedKey_IsAConflict_AndNothingIsStaged()
    {
        var variant = Variant(_groupId, AnalyseScenarioStatus.Accepted);

        var act = () => Handler(_guard).Handle(Command(_groupId, variant.Id), CancellationToken.None);

        var conflict = await Should.ThrowAsync<ScenarioVariantConflictException>(act);
        conflict.Message.ShouldBe(ScenarioVariantSelection.ConsumedMessage);
        conflict.ConflictCode.ShouldBe(ScenarioVariantErrorCodes.Consumed);
        await ShouldHaveStagedNothingAsync();
    }

    [Test]
    public async Task AnUnknownKey_IsAnsweredLikeAMissingScenario_AndNothingIsStaged()
    {
        var unknownId = Guid.NewGuid();

        var act = () => Handler(_guard).Handle(Command(_groupId, unknownId), CancellationToken.None);

        await Should.ThrowAsync<KeyNotFoundException>(act);
        await ShouldHaveStagedNothingAsync();
    }

    [Test]
    public async Task AHiddenConsumedKey_IsAnsweredLikeAMissingKey_SoItsStatusStaysHidden()
    {
        var variant = Variant(_hiddenGroupId, AnalyseScenarioStatus.Accepted);

        var act = () => Handler(_guard).Handle(Command(_groupId, variant.Id), CancellationToken.None);

        await Should.ThrowAsync<KeyNotFoundException>(act);
        await ShouldHaveStagedNothingAsync();
    }

    [Test]
    public async Task APlanner_SeeingTheGroup_MaySelectAKey()
    {
        var variant = Variant(_groupId);

        var result = await Handler(_guard).Handle(Command(_groupId, variant.Id), CancellationToken.None);

        result.VariantIds.ShouldBe(new[] { variant.Id });
        await _unitOfWork.Received(1).CompleteAsync();
    }

    [Test]
    public async Task KeysOfTheGroupAndASubgroup_BringAllTheirShiftsAndTokensIntoTheClone()
    {
        var groupKey = Variant(_groupId);
        var subgroupKey = Variant(_subGroupId);
        var expectedShiftIds = _shiftIdsByToken[groupKey.Token].Concat(_shiftIdsByToken[subgroupKey.Token]).ToHashSet();
        var expectedTokens = new[] { groupKey.Token, subgroupKey.Token }.ToHashSet();

        await Handler(_guard).Handle(Command(_groupId, groupKey.Id, subgroupKey.Id), CancellationToken.None);

        await _scenarioService.Received(1).CloneScenarioDataAsync(
            Arg.Is<Guid?>(groupId => groupId == _groupId),
            From,
            Until,
            Arg.Any<Guid>(),
            Arg.Is<IReadOnlyCollection<Guid>?>(ids => ids != null && ids.Count == expectedShiftIds.Count && expectedShiftIds.SetEquals(ids)),
            Arg.Is<Guid?>(sourceToken => sourceToken == null),
            Arg.Any<CancellationToken>(),
            Arg.Is<IReadOnlySet<Guid>?>(tokens => tokens != null && tokens.SetEquals(expectedTokens)));
    }

    [Test]
    public async Task AnExpiredKey_IsAllowed()
    {
        var variant = Variant(_groupId);
        variant.FromDate = From.AddYears(-1);
        variant.UntilDate = Until.AddYears(-1);

        var result = await Handler(_guard).Handle(Command(_groupId, variant.Id), CancellationToken.None);

        result.VariantIds.ShouldBe(new[] { variant.Id });
    }

    [Test]
    public async Task DuplicateKeyIds_AreLinkedOnce()
    {
        var variant = Variant(_groupId);

        var result = await Handler(_guard).Handle(Command(_groupId, variant.Id, variant.Id), CancellationToken.None);

        result.VariantIds.ShouldBe(new[] { variant.Id });
        await _variantRepository.Received(1).AddLinksAsync(
            result.Token,
            Arg.Is<IReadOnlyCollection<Guid>>(ids => ids.Count == 1 && ids.Contains(variant.Id)),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task TheLinks_AreStagedAfterTheClone_AndBeforeTheOneCommit()
    {
        var variant = Variant(_groupId);
        var order = new List<string>();
        _repository.When(repository => repository.Add(Arg.Any<AnalyseScenario>()))
            .Do(_ => order.Add(AddStep));
        _scenarioService.When(service => service.CloneScenarioDataAsync(
                Arg.Any<Guid?>(),
                Arg.Any<DateOnly>(),
                Arg.Any<DateOnly>(),
                Arg.Any<Guid>(),
                Arg.Any<IReadOnlyCollection<Guid>?>(),
                Arg.Any<Guid?>(),
                Arg.Any<CancellationToken>(),
                Arg.Any<IReadOnlySet<Guid>?>()))
            .Do(_ => order.Add(CloneStep));
        _variantRepository.When(repository => repository.AddLinksAsync(
                Arg.Any<Guid>(), Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>()))
            .Do(_ => order.Add(LinkStep));
        _unitOfWork.When(unitOfWork => unitOfWork.CompleteAsync())
            .Do(_ => order.Add(CommitStep));

        await Handler(_guard).Handle(Command(_groupId, variant.Id), CancellationToken.None);

        order.ShouldBe(new[] { AddStep, CloneStep, LinkStep, CommitStep });
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task WithoutKeys_TheCloneRunsAsBefore_AndNoLinkIsWritten(bool emptyList)
    {
        var request = Request(_groupId);
        request.VariantIds = emptyList ? [] : null;

        var result = await Handler(_guard).Handle(new CreateAnalyseScenarioCommand(request), CancellationToken.None);

        result.VariantIds.ShouldBeEmpty();
        await _scenarioService.Received(1).CloneScenarioDataAsync(
            Arg.Is<Guid?>(groupId => groupId == _groupId),
            From,
            Until,
            Arg.Any<Guid>(),
            Arg.Is<IReadOnlyCollection<Guid>?>(ids => ids == null),
            Arg.Is<Guid?>(sourceToken => sourceToken == null),
            Arg.Any<CancellationToken>(),
            Arg.Is<IReadOnlySet<Guid>?>(tokens => tokens == null));
        await _variantRepository.DidNotReceiveWithAnyArgs().GetVariantsByIdsAsync(default!, default);
        await _variantRepository.DidNotReceiveWithAnyArgs().AddLinksAsync(default, default!, default);
    }

    private async Task ShouldHaveStagedNothingAsync()
    {
        await _repository.DidNotReceiveWithAnyArgs().Add(default!);
        await _scenarioService.DidNotReceiveWithAnyArgs().CloneScenarioDataAsync(default, default, default, default, default, default, default);
        await _variantRepository.DidNotReceiveWithAnyArgs().AddLinksAsync(default, default!, default);
        await _unitOfWork.DidNotReceive().CompleteAsync();
    }

    private AnalyseScenario Variant(Guid groupId, AnalyseScenarioStatus status = AnalyseScenarioStatus.Active)
    {
        var variant = new AnalyseScenario
        {
            Id = Guid.NewGuid(),
            Token = Guid.NewGuid(),
            Name = "Key",
            GroupId = groupId,
            FromDate = From,
            UntilDate = Until,
            Status = status,
            Kind = AnalyseScenarioKind.Variant
        };
        _variants[variant.Id] = variant;
        _shiftIdsByToken[variant.Token] = [Guid.NewGuid(), Guid.NewGuid()];
        return variant;
    }

    private static CreateAnalyseScenarioRequest Request(Guid? groupId, params Guid[] variantIds) => new()
    {
        Name = "Scenario",
        GroupId = groupId,
        FromDate = From,
        UntilDate = Until,
        VariantIds = variantIds
    };

    private static CreateAnalyseScenarioCommand Command(Guid? groupId, params Guid[] variantIds)
        => new(Request(groupId, variantIds));

    private CreateAnalyseScenarioCommandHandler Handler(IGroupVisibilityGuard guard) => new(
        _repository, _variantRepository, _scenarioService, _unitOfWork, guard, Substitute.For<ILogger<CreateAnalyseScenarioCommandHandler>>());
}
