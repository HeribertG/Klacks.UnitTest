// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Pins the rules of the handler that copies existing shifts into a variant (key) that come before the copier runs: the
/// request must name exactly one of shifts and source group (an empty id list counts as not named), a missing or hidden
/// variant is not found and a consumed one a conflict, a hidden source group is not found and one outside the variant
/// hierarchy a bad request, a hidden shift is not found, and a refused call stages and commits nothing. A valid call hands
/// the deduplicated ids or the group with the variant hierarchy to the copier, commits once and answers the copier's result.
/// </summary>

using Klacks.Api.Application.Commands.ScenarioVariants;
using Klacks.Api.Application.DTOs.Schedules;
using Klacks.Api.Application.Exceptions;
using Klacks.Api.Application.Handlers.ScenarioVariants;
using Klacks.Api.Application.Services.ScenarioVariants;
using Klacks.Api.Application.Services.Shifts;
using Klacks.Api.Domain.Exceptions;
using Microsoft.Extensions.Logging;

namespace Klacks.UnitTest.Application.Handlers.ScenarioVariants;

[TestFixture]
public class CopyShiftsToVariantCommandHandlerTests
{
    private const int CopiedFamilies = 3;
    private const int SkippedFamilies = 2;

    private IScenarioVariantRepository _variantRepository = null!;
    private IGroupVisibilityGuard _groupGuard = null!;
    private IAnalyseScenarioService _scenarioService = null!;
    private IShiftVisibilityGuard _shiftGuard = null!;
    private IScenarioVariantShiftCopier _copier = null!;
    private IUnitOfWork _unitOfWork = null!;
    private Guid _groupId;
    private Guid _subGroupId;
    private List<Guid> _hierarchy = null!;
    private AnalyseScenario _variant = null!;
    private CopyShiftsToVariantResult _copierResult = null!;

    [SetUp]
    public void SetUp()
    {
        _variantRepository = Substitute.For<IScenarioVariantRepository>();
        _groupGuard = Substitute.For<IGroupVisibilityGuard>();
        _scenarioService = Substitute.For<IAnalyseScenarioService>();
        _shiftGuard = Substitute.For<IShiftVisibilityGuard>();
        _copier = Substitute.For<IScenarioVariantShiftCopier>();
        _unitOfWork = Substitute.For<IUnitOfWork>();
        _groupId = Guid.NewGuid();
        _subGroupId = Guid.NewGuid();
        _hierarchy = [_groupId, _subGroupId];
        _copierResult = new CopyShiftsToVariantResult(CopiedFamilies, SkippedFamilies);

        _groupGuard.IsGroupVisibleAsync(_groupId, Arg.Any<CancellationToken>()).Returns(true);
        _groupGuard.IsGroupVisibleAsync(_subGroupId, Arg.Any<CancellationToken>()).Returns(true);
        _scenarioService.GetGroupHierarchyIdsAsync(_groupId, CancellationToken.None).Returns(_hierarchy);
        _shiftGuard.FindFirstHiddenAsync(Arg.Any<IReadOnlyCollection<Guid>>(), CancellationToken.None).Returns((Guid?)null);
        _variant = new AnalyseScenario
        {
            Id = Guid.NewGuid(),
            Token = Guid.NewGuid(),
            Name = "Large order",
            GroupId = _groupId,
            Kind = AnalyseScenarioKind.Variant,
            Status = AnalyseScenarioStatus.Active
        };
        _variantRepository.GetVariantAsync(_variant.Id, CancellationToken.None).Returns(_variant);
        _copier.StageCopyOfShiftsAsync(_variant, _hierarchy, Arg.Any<IReadOnlyCollection<Guid>>(), CancellationToken.None)
            .Returns(_copierResult);
        _copier.StageCopyOfGroupAsync(_variant, _hierarchy, Arg.Any<Guid>(), CancellationToken.None).Returns(_copierResult);
    }

    [Test]
    public async Task Handle_WithShiftIds_HandsTheDistinctIdsAndTheVariantHierarchyToTheCopierAndCommitsOnce()
    {
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();

        var result = await Handler().Handle(CommandFor(new CopyShiftsToVariantRequest { ShiftIds = [first, second, first] }), CancellationToken.None);

        result.ShouldBe(_copierResult);
        await _copier.Received(1).StageCopyOfShiftsAsync(
            _variant,
            Arg.Is<IReadOnlyCollection<Guid>>(ids => ids.SequenceEqual(_hierarchy)),
            Arg.Is<IReadOnlyCollection<Guid>>(ids => ids.SequenceEqual(new[] { first, second })),
            CancellationToken.None);
        await _copier.DidNotReceiveWithAnyArgs().StageCopyOfGroupAsync(default!, default!, default, default);
        await _unitOfWork.Received(1).CompleteAsync();
    }

    [Test]
    public async Task Handle_WithASourceGroup_HandsGroupAndHierarchyToTheCopierAndCommitsOnce()
    {
        var result = await Handler().Handle(CommandFor(new CopyShiftsToVariantRequest { SourceGroupId = _subGroupId }), CancellationToken.None);

        result.ShouldBe(_copierResult);
        await _copier.Received(1).StageCopyOfGroupAsync(
            _variant,
            Arg.Is<IReadOnlyCollection<Guid>>(ids => ids.SequenceEqual(_hierarchy)),
            _subGroupId,
            CancellationToken.None);
        await _copier.DidNotReceiveWithAnyArgs().StageCopyOfShiftsAsync(default!, default!, default!, default);
        await _unitOfWork.Received(1).CompleteAsync();
    }

    [Test]
    public async Task Handle_WithAnEmptyIdListAndASourceGroup_CopiesTheGroup()
    {
        var result = await Handler().Handle(
            CommandFor(new CopyShiftsToVariantRequest { ShiftIds = [], SourceGroupId = _groupId }), CancellationToken.None);

        result.ShouldBe(_copierResult);
        await _copier.Received(1).StageCopyOfGroupAsync(_variant, Arg.Any<IReadOnlyCollection<Guid>>(), _groupId, CancellationToken.None);
    }

    [TestCaseSource(nameof(InvalidSelections))]
    public async Task Handle_WithoutExactlyOneOfShiftsAndGroup_IsABadRequestAndTouchesNothing(CopyShiftsToVariantRequest request)
    {
        var exception = await Should.ThrowAsync<InvalidRequestException>(
            () => Handler().Handle(CommandFor(request), CancellationToken.None));

        exception.Message.ShouldBe(ScenarioVariantValidation.CopySelectionMessage);
        await NothingWasCopiedAsync();
        await _variantRepository.DidNotReceiveWithAnyArgs().GetVariantAsync(default, default);
    }

    [Test]
    public async Task Handle_ForAMissingVariant_IsNotFound()
    {
        var command = new CopyShiftsToVariantCommand(Guid.NewGuid(), new CopyShiftsToVariantRequest { ShiftIds = [Guid.NewGuid()] });

        await Should.ThrowAsync<KeyNotFoundException>(() => Handler().Handle(command, CancellationToken.None));

        await NothingWasCopiedAsync();
    }

    [Test]
    public async Task Handle_ForAVariantOfAHiddenGroup_IsNotFound()
    {
        _groupGuard.IsGroupVisibleAsync(_groupId, Arg.Any<CancellationToken>()).Returns(false);

        await Should.ThrowAsync<KeyNotFoundException>(() => Handler().Handle(
            CommandFor(new CopyShiftsToVariantRequest { ShiftIds = [Guid.NewGuid()] }), CancellationToken.None));

        await NothingWasCopiedAsync();
    }

    [Test]
    public async Task Handle_ForAConsumedVariant_IsAConflict()
    {
        _variant.Status = AnalyseScenarioStatus.Accepted;

        var exception = await Should.ThrowAsync<ConflictException>(() => Handler().Handle(
            CommandFor(new CopyShiftsToVariantRequest { ShiftIds = [Guid.NewGuid()] }), CancellationToken.None));

        exception.Message.ShouldBe(ScenarioVariantAccess.ConsumedMessage);
        await NothingWasCopiedAsync();
    }

    [Test]
    public async Task Handle_ForAVariantAnActiveScenarioCarries_IsAConflictAndCopiesNothing()
    {
        _variantRepository.HasActiveScenarioAsync(_variant.Id, CancellationToken.None).Returns(true);

        var exception = await Should.ThrowAsync<ConflictException>(() => Handler().Handle(
            CommandFor(new CopyShiftsToVariantRequest { ShiftIds = [Guid.NewGuid()] }), CancellationToken.None));

        exception.Message.ShouldBe(ScenarioVariantAccess.InUseMessage);
        await NothingWasCopiedAsync();
    }

    [Test]
    public async Task Handle_ForAVariantWithoutGroup_IsABadRequest()
    {
        _variant.GroupId = null;
        _groupGuard.IsUnrestrictedAsync(Arg.Any<CancellationToken>()).Returns(true);

        await Should.ThrowAsync<InvalidRequestException>(() => Handler().Handle(
            CommandFor(new CopyShiftsToVariantRequest { ShiftIds = [Guid.NewGuid()] }), CancellationToken.None));

        await NothingWasCopiedAsync();
    }

    [Test]
    public async Task Handle_ForAShiftTheCallerCannotSee_IsNotFoundLikeAMissingShift()
    {
        var visible = Guid.NewGuid();
        var hidden = Guid.NewGuid();
        _shiftGuard.FindFirstHiddenAsync(
                Arg.Is<IReadOnlyCollection<Guid>>(ids => ids.Contains(hidden)), CancellationToken.None)
            .Returns(hidden);

        var exception = await Should.ThrowAsync<KeyNotFoundException>(() => Handler().Handle(
            CommandFor(new CopyShiftsToVariantRequest { ShiftIds = [visible, hidden] }), CancellationToken.None));

        exception.Message.ShouldBe(string.Format(ShiftVisibilityMessages.ShiftNotFound, hidden));
        await NothingWasCopiedAsync();
    }

    [Test]
    public async Task Handle_ForASourceGroupTheCallerCannotSee_IsNotFoundLikeAMissingGroup()
    {
        var hiddenGroupId = Guid.NewGuid();
        _hierarchy.Add(hiddenGroupId);
        _groupGuard.IsGroupVisibleAsync(hiddenGroupId, Arg.Any<CancellationToken>()).Returns(false);

        var exception = await Should.ThrowAsync<KeyNotFoundException>(() => Handler().Handle(
            CommandFor(new CopyShiftsToVariantRequest { SourceGroupId = hiddenGroupId }), CancellationToken.None));

        exception.Message.ShouldBe(string.Format(ShiftVisibilityMessages.GroupNotFound, hiddenGroupId));
        await NothingWasCopiedAsync();
    }

    [Test]
    public async Task Handle_ForASourceGroupOutsideTheVariantHierarchy_IsABadRequest()
    {
        var outsideGroupId = Guid.NewGuid();
        _groupGuard.IsGroupVisibleAsync(outsideGroupId, Arg.Any<CancellationToken>()).Returns(true);

        var exception = await Should.ThrowAsync<InvalidRequestException>(() => Handler().Handle(
            CommandFor(new CopyShiftsToVariantRequest { SourceGroupId = outsideGroupId }), CancellationToken.None));

        exception.Message.ShouldBe(ScenarioVariantValidation.SourceGroupOutsideVariantMessage);
        await NothingWasCopiedAsync();
    }

    [Test]
    public async Task Handle_WhenTheCopierRefuses_CommitsNothing()
    {
        _copier.StageCopyOfShiftsAsync(_variant, _hierarchy, Arg.Any<IReadOnlyCollection<Guid>>(), CancellationToken.None)
            .Returns<CopyShiftsToVariantResult>(_ => throw new InvalidRequestException("refused"));

        await Should.ThrowAsync<InvalidRequestException>(() => Handler().Handle(
            CommandFor(new CopyShiftsToVariantRequest { ShiftIds = [Guid.NewGuid()] }), CancellationToken.None));

        await _unitOfWork.DidNotReceive().CompleteAsync();
    }

    private static IEnumerable<TestCaseData> InvalidSelections()
    {
        yield return new TestCaseData(new CopyShiftsToVariantRequest()).SetName("Neither");
        yield return new TestCaseData(new CopyShiftsToVariantRequest { ShiftIds = [] }).SetName("EmptyIdListOnly");
        yield return new TestCaseData(
            new CopyShiftsToVariantRequest { ShiftIds = [Guid.NewGuid()], SourceGroupId = Guid.NewGuid() }).SetName("Both");
    }

    private CopyShiftsToVariantCommand CommandFor(CopyShiftsToVariantRequest request)
        => new(_variant.Id, request);

    private CopyShiftsToVariantCommandHandler Handler()
    {
        return new CopyShiftsToVariantCommandHandler(
            _variantRepository,
            _groupGuard,
            _scenarioService,
            _shiftGuard,
            _copier,
            _unitOfWork,
            Substitute.For<ILogger<CopyShiftsToVariantCommandHandler>>());
    }

    private async Task NothingWasCopiedAsync()
    {
        await _copier.DidNotReceiveWithAnyArgs().StageCopyOfShiftsAsync(default!, default!, default!, default);
        await _copier.DidNotReceiveWithAnyArgs().StageCopyOfGroupAsync(default!, default!, default, default);
        await _unitOfWork.DidNotReceive().CompleteAsync();
    }
}
