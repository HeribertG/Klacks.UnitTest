// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Pins cutting and resetting cuts inside a variant (key). The request carries no token, so the stored token of the shift
/// decides: a key shift is cut in its own world (new pieces and their group links take the parent's token, the cut dates
/// are validated against the key's sealed order, the reset reads and rewrites rows of that token only), a scenario clone,
/// a hidden key and a consumed key are refused before anything is staged, and only real trees get their nested-set values
/// recalculated. A real-plan cut behaves as before and never asks the variant guard. A new piece in a key takes the groups
/// of its parent whatever the request carries. That the repository really keeps the token and the groups on updated rows is
/// proven against PostgreSQL in VariantShiftCutSeamTests and VariantSecurityFindingsSeamTests, because it is stubbed here.
/// </summary>

using System.Reflection;
using Klacks.Api.Application.Commands.Shifts;
using Klacks.Api.Application.DTOs.Associations;
using Klacks.Api.Application.DTOs.Schedules;
using Klacks.Api.Application.Exceptions;
using Klacks.Api.Application.Mappers;
using Klacks.Api.Application.Services.ScenarioVariants;
using Klacks.Api.Application.Services.Schedules;
using Klacks.Api.Application.Services.Shifts;
using Klacks.Api.Domain.Models.Associations;
using Klacks.Api.Domain.Services.Shifts;
using Klacks.Api.Presentation.Controllers.UserBackend.Schedules;
using Klacks.Api.Presentation.Filters;
using Klacks.UnitTest.TestHelpers;
using NSubstitute.ExceptionExtensions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Logging;

namespace Klacks.UnitTest.Application.Services.Schedules;

[TestFixture]
public class ShiftCutFacadeVariantTests
{
    private static readonly DateOnly FromDate = new(2026, 1, 1);
    private static readonly DateOnly ResetDate = new(2026, 3, 1);

    private IShiftRepository _shiftRepository = null!;
    private IShiftTreeService _shiftTreeService = null!;
    private IShiftValidator _shiftValidator = null!;
    private IVariantShiftWriteGuard _variantGuard = null!;
    private IUnitOfWork _unitOfWork = null!;
    private ShiftCutFacade _facade = null!;

    private readonly Guid _sealedOrderId = Guid.NewGuid();
    private readonly Guid _originalShiftId = Guid.NewGuid();
    private readonly Guid _pieceId = Guid.NewGuid();
    private readonly Guid _customerId = Guid.NewGuid();
    private readonly Guid _groupId = Guid.NewGuid();
    private readonly Guid _parentGroupId = Guid.NewGuid();
    private readonly Guid _keyToken = Guid.NewGuid();
    private readonly Guid _cloneToken = Guid.NewGuid();
    private readonly Dictionary<Guid, Shift> _stored = new();
    private readonly Dictionary<Guid, Shift> _tracked = new();

    [SetUp]
    public void SetUp()
    {
        _stored.Clear();
        _tracked.Clear();
        _shiftRepository = Substitute.For<IShiftRepository>();
        _shiftTreeService = Substitute.For<IShiftTreeService>();
        _shiftValidator = Substitute.For<IShiftValidator>();
        _variantGuard = Substitute.For<IVariantShiftWriteGuard>();
        _unitOfWork = Substitute.For<IUnitOfWork>();
        var requirements = Substitute.For<IShiftRequiredQualificationRepository>();
        requirements.GetEffectiveByShiftIdsAsync(Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(new List<EffectiveShiftRequirement>());
        var expenses = Substitute.For<IShiftExpensesRepository>();
        expenses.GetByShiftIdsAsync(Arg.Any<IEnumerable<Guid>>(), Arg.Any<CancellationToken>()).Returns(new List<ShiftExpenses>());

        _variantGuard.EnsureOpenIfVariantAsync(_keyToken, Arg.Any<Guid>()).Returns(true);
        _variantGuard.IsReadableVariantAsync(_keyToken).Returns(true);
        _shiftRepository.PutCutUpdate(Arg.Any<Shift>())
            .Returns(call => _stored.GetValueOrDefault(call.Arg<Shift>().Id) ?? call.Arg<Shift>());
        _shiftRepository.Get(Arg.Any<Guid>()).Returns(call => _stored.GetValueOrDefault(call.Arg<Guid>()));
        _shiftRepository.GetTrackedOrFromDb(Arg.Any<Guid>())
            .Returns(call => _tracked.GetValueOrDefault(call.Arg<Guid>()) ?? _stored.GetValueOrDefault(call.Arg<Guid>()));
        _shiftRepository.When(r => r.Add(Arg.Any<Shift>())).Do(call => _tracked[call.Arg<Shift>().Id] = call.Arg<Shift>());

        _facade = new ShiftCutFacade(
            _shiftRepository,
            requirements,
            _shiftTreeService,
            new ShiftResetService(_shiftRepository, Substitute.For<ILogger<ShiftResetService>>()),
            _shiftValidator,
            new ScheduleMapper(),
            _unitOfWork,
            ShiftVisibilityGuards.AllowAll(),
            expenses,
            _variantGuard,
            Substitute.For<ILogger<ShiftCutFacade>>());
    }

    [Test]
    public async Task Create_InAKey_PieceAndGroupLinksTakeTheKeyToken_AndNoScenarioSource()
    {
        Store(Stored(_sealedOrderId, ShiftStatus.SealedOrder, null, _keyToken));
        var piece = Piece(_pieceId);

        await _facade.ProcessBatchCutsAsync([Create(_sealedOrderId, piece)]);

        var added = _tracked[_pieceId];
        added.AnalyseToken.ShouldBe(_keyToken);
        added.Status.ShouldBe(ShiftStatus.SplitShift);
        added.OriginalId.ShouldBe(_sealedOrderId);
        added.ScenarioSourceShiftId.ShouldBeNull();
        added.GroupItems.Count.ShouldBe(1);
        added.GroupItems.ShouldAllBe(item => item.AnalyseToken == _keyToken);
        await _shiftValidator.Received(1).ValidateCutDatesWithinAllowedRange(
            _sealedOrderId, piece.FromDate, piece.UntilDate, _shiftRepository, _keyToken);
        await _shiftTreeService.DidNotReceiveWithAnyArgs().RecalculateAllAffectedTreesAsync(default!);
    }

    [Test]
    public async Task Create_InAKey_IgnoresTheGroupsOfTheRequest_AndCopiesThoseOfTheParent()
    {
        var parent = Stored(_sealedOrderId, ShiftStatus.SealedOrder, null, _keyToken);
        var deletedGroupId = Guid.NewGuid();
        parent.GroupItems.Add(new GroupItem { GroupId = deletedGroupId, AnalyseToken = _keyToken, IsDeleted = true });
        Store(parent);

        await _facade.ProcessBatchCutsAsync([Create(_sealedOrderId, Piece(_pieceId))]);

        var item = _tracked[_pieceId].GroupItems.Single();
        item.GroupId.ShouldBe(_parentGroupId);
        item.AnalyseToken.ShouldBe(_keyToken);
        item.ShiftId.ShouldBe(_pieceId);
        item.Id.ShouldNotBe(parent.GroupItems[0].Id);
        item.ShouldNotBeSameAs(parent.GroupItems[0]);
    }

    [Test]
    public async Task Create_InAKey_FromAParentWithoutLiveGroups_IsRefused_InsteadOfAGrouplessPiece()
    {
        var parent = Stored(_sealedOrderId, ShiftStatus.SealedOrder, null, _keyToken);
        parent.GroupItems = [];
        Store(parent);

        var ex = await Should.ThrowAsync<BadRequestException>(
            () => _facade.ProcessBatchCutsAsync([Create(_sealedOrderId, Piece(_pieceId))]));

        ex.Message.ShouldBe(VariantShiftWriteGuard.GroupRequiredMessage);
        await _shiftRepository.DidNotReceiveWithAnyArgs().Add(default!);
    }

    [Test]
    public async Task Create_InTheRealPlan_KeepsTheGroupsOfTheRequest()
    {
        Store(Stored(_sealedOrderId, ShiftStatus.SealedOrder, null, null));

        await _facade.ProcessBatchCutsAsync([Create(_sealedOrderId, Piece(_pieceId))]);

        _tracked[_pieceId].GroupItems.Single().GroupId.ShouldBe(_groupId);
    }

    [Test]
    public async Task Batch_ChainInAKey_EveryPieceCarriesTheKeyToken_AndTheKeyTreeIsNotRecalculated()
    {
        Store(Stored(_originalShiftId, ShiftStatus.OriginalShift, _sealedOrderId, _keyToken));
        var childId = Guid.NewGuid();
        var grandChildId = Guid.NewGuid();

        var processed = await _facade.ProcessBatchCutsAsync(
        [
            Update(Piece(_originalShiftId)),
            Create(_originalShiftId, Piece(childId)),
            Create(childId, Piece(grandChildId))
        ]);

        processed.Count.ShouldBe(3);
        processed.ShouldAllBe(shift => shift.AnalyseToken == _keyToken);
        _tracked[grandChildId].GroupItems.ShouldAllBe(item => item.AnalyseToken == _keyToken);
        await _shiftValidator.Received(3).ValidateCutDatesWithinAllowedRange(
            _sealedOrderId, Arg.Any<DateOnly>(), Arg.Any<DateOnly?>(), _shiftRepository, _keyToken);
        await _shiftTreeService.DidNotReceiveWithAnyArgs().RecalculateAllAffectedTreesAsync(default!);
        await _variantGuard.Received().EnsureOpenIfVariantAsync(_keyToken, Arg.Any<Guid>());
    }

    [Test]
    public async Task Batch_OfARealAndAKeyShift_RecalculatesOnlyTheRealTree()
    {
        var realId = Guid.NewGuid();
        Store(Stored(realId, ShiftStatus.OriginalShift, Guid.NewGuid(), null));
        Store(Stored(_originalShiftId, ShiftStatus.OriginalShift, _sealedOrderId, _keyToken));

        await _facade.ProcessBatchCutsAsync([Update(Piece(realId)), Update(Piece(_originalShiftId))]);

        await _shiftTreeService.Received(1).RecalculateAllAffectedTreesAsync(
            Arg.Is<List<Shift>>(shifts => shifts.Count == 1 && shifts[0].Id == realId));
    }

    [Test]
    public async Task Create_InTheRealPlan_BehavesAsBefore_AndNeverAsksTheVariantGuard()
    {
        Store(Stored(_sealedOrderId, ShiftStatus.SealedOrder, null, null));
        var piece = Piece(_pieceId);

        await _facade.ProcessBatchCutsAsync([Create(_sealedOrderId, piece)]);

        var added = _tracked[_pieceId];
        added.AnalyseToken.ShouldBeNull();
        added.GroupItems.ShouldAllBe(item => item.AnalyseToken == null);
        await _shiftValidator.Received(1).ValidateCutDatesWithinAllowedRange(
            _sealedOrderId, piece.FromDate, piece.UntilDate, _shiftRepository, null);
        await _shiftTreeService.Received(1).RecalculateAllAffectedTreesAsync(
            Arg.Is<List<Shift>>(shifts => shifts.Count == 1 && shifts[0].Id == _pieceId));
        await _variantGuard.DidNotReceiveWithAnyArgs().EnsureOpenIfVariantAsync(default, default, default);
    }

    [Test]
    public async Task Create_UnderAScenarioCloneToken_IsRefused_BeforeAnythingIsStaged()
    {
        Store(Stored(_sealedOrderId, ShiftStatus.SealedOrder, null, _cloneToken));

        await Should.ThrowAsync<InvalidOperationException>(
            () => _facade.ProcessBatchCutsAsync([Create(_sealedOrderId, Piece(_pieceId))]));

        await AssertNothingStagedAsync();
    }

    [Test]
    public async Task Update_OfAScenarioClone_IsRefused_BeforeAnythingIsStaged()
    {
        Store(Stored(_pieceId, ShiftStatus.SplitShift, _sealedOrderId, _cloneToken));

        await Should.ThrowAsync<InvalidOperationException>(() => _facade.ProcessBatchCutsAsync([Update(Piece(_pieceId))]));

        await AssertNothingStagedAsync();
    }

    [Test]
    public async Task Create_InAHiddenKey_PassesTheNotFoundOfTheGuard_BeforeAnythingIsStaged()
    {
        Store(Stored(_sealedOrderId, ShiftStatus.SealedOrder, null, _keyToken));
        _variantGuard.EnsureOpenIfVariantAsync(_keyToken, Arg.Any<Guid>()).Returns<Task<bool>>(_ => throw new KeyNotFoundException("hidden"));

        await Should.ThrowAsync<KeyNotFoundException>(
            () => _facade.ProcessBatchCutsAsync([Create(_sealedOrderId, Piece(_pieceId))]));

        await AssertNothingStagedAsync();
    }

    [Test]
    public async Task Create_InAConsumedKey_PassesTheConflictOfTheGuard_BeforeAnythingIsStaged()
    {
        Store(Stored(_sealedOrderId, ShiftStatus.SealedOrder, null, _keyToken));
        _variantGuard.EnsureOpenIfVariantAsync(_keyToken, Arg.Any<Guid>()).Returns<Task<bool>>(_ => throw new ConflictException("consumed"));

        await Should.ThrowAsync<ConflictException>(
            () => _facade.ProcessBatchCutsAsync([Create(_sealedOrderId, Piece(_pieceId))]));

        await AssertNothingStagedAsync();
    }

    [Test]
    public async Task Reset_InAKey_ReadsAndRewritesRowsOfTheKeyOnly_AndTheNewPlannableCopyKeepsTheToken()
    {
        var sealedOrder = Stored(_sealedOrderId, ShiftStatus.SealedOrder, null, _keyToken);
        sealedOrder.GroupItems = [new GroupItem { GroupId = _groupId, AnalyseToken = _keyToken }];
        var plannable = Stored(_originalShiftId, ShiftStatus.OriginalShift, _sealedOrderId, _keyToken);
        var cut = Stored(_pieceId, ShiftStatus.SplitShift, _sealedOrderId, _keyToken);
        var freshList = new List<Shift> { plannable, cut };
        _shiftRepository.Get(_sealedOrderId).Returns(sealedOrder);
        _shiftRepository.GetSealedOrder(_sealedOrderId, _keyToken).Returns(sealedOrder);
        _shiftRepository.CutList(_sealedOrderId, ResetDate, true, _keyToken).Returns([plannable, cut]);
        _shiftRepository.CutList(_sealedOrderId, null, false, _keyToken).Returns(freshList);

        var result = await _facade.ResetCutsAsync(_sealedOrderId, ResetDate);

        result.ShouldBeSameAs(freshList);
        cut.UntilDate.ShouldBe(ResetDate.AddDays(-1));
        var added = _tracked.Values.Single();
        added.Status.ShouldBe(ShiftStatus.OriginalShift);
        added.OriginalId.ShouldBe(_sealedOrderId);
        added.FromDate.ShouldBe(ResetDate);
        added.AnalyseToken.ShouldBe(_keyToken);
        added.GroupItems.Count.ShouldBe(1);
        added.GroupItems.ShouldAllBe(item => item.AnalyseToken == _keyToken);
        await _unitOfWork.Received(1).CompleteAsync();
    }

    [Test]
    public async Task Reset_InTheRealPlan_ReadsTheRealWorld_WithoutAnExtraLookupOrTheVariantGuard()
    {
        var sealedOrder = Stored(_sealedOrderId, ShiftStatus.SealedOrder, null, null);
        var plannable = Stored(_originalShiftId, ShiftStatus.OriginalShift, _sealedOrderId, null);
        _shiftRepository.GetSealedOrder(_sealedOrderId, null).Returns(sealedOrder);
        _shiftRepository.CutList(_sealedOrderId, ResetDate, true, null).Returns([plannable]);
        _shiftRepository.CutList(_sealedOrderId, null, false, null).Returns([plannable]);

        var result = await _facade.ResetCutsAsync(_sealedOrderId, ResetDate);

        result.Count.ShouldBe(1);
        result[0].ShouldBeSameAs(plannable);
        _tracked.Values.Single().AnalyseToken.ShouldBeNull();
        await _shiftRepository.DidNotReceive().Get(_sealedOrderId);
        await _variantGuard.DidNotReceiveWithAnyArgs().EnsureOpenIfVariantAsync(default, default, default);
        await _variantGuard.DidNotReceiveWithAnyArgs().IsReadableVariantAsync(default, default);
    }

    [Test]
    public async Task Reset_OfAScenarioClone_IsAnsweredLikeAMissingOrder_AndReadsNoRows()
    {
        _shiftRepository.Get(_sealedOrderId).Returns(Stored(_sealedOrderId, ShiftStatus.SealedOrder, null, _cloneToken));

        await Should.ThrowAsync<InvalidOperationException>(() => _facade.ResetCutsAsync(_sealedOrderId, ResetDate));

        await _shiftRepository.DidNotReceive().GetSealedOrder(_sealedOrderId, _cloneToken);
        await _shiftRepository.DidNotReceiveWithAnyArgs().CutList(default, default, default, default);
        await _shiftRepository.DidNotReceiveWithAnyArgs().Add(default!);
    }

    [Test]
    public async Task Reset_OfAHiddenKey_IsAnsweredLikeAMissingOrder_AndReadsNoRows()
    {
        _shiftRepository.Get(_sealedOrderId).Returns(Stored(_sealedOrderId, ShiftStatus.SealedOrder, null, _keyToken));
        _variantGuard.IsReadableVariantAsync(_keyToken).Returns(false);

        var ex = await Should.ThrowAsync<InvalidOperationException>(() => _facade.ResetCutsAsync(_sealedOrderId, ResetDate));

        ex.Message.ShouldBe(string.Format(ShiftVisibilityMessages.NoSealedOrderFound, _sealedOrderId));
        await _shiftRepository.DidNotReceiveWithAnyArgs().CutList(default, default, default, default);
    }

    [Test]
    public async Task Reset_InAConsumedKey_PassesTheConflictOfTheGuard_AndWritesNothing()
    {
        _shiftRepository.Get(_sealedOrderId).Returns(Stored(_sealedOrderId, ShiftStatus.SealedOrder, null, _keyToken));
        _variantGuard.IsReadableVariantAsync(_keyToken).Returns<Task<bool>>(_ => throw new ConflictException("consumed"));

        await Should.ThrowAsync<ConflictException>(() => _facade.ResetCutsAsync(_sealedOrderId, ResetDate));

        await _shiftRepository.DidNotReceiveWithAnyArgs().CutList(default, default, default, default);
        await _shiftRepository.DidNotReceiveWithAnyArgs().Add(default!);
        await _unitOfWork.DidNotReceive().CompleteAsync();
    }

    [Test]
    public async Task Reset_InAKeyInUse_PassesTheConflictOfTheWriteRule_AndWritesNothing()
    {
        var sealedOrder = Stored(_sealedOrderId, ShiftStatus.SealedOrder, null, _keyToken);
        _shiftRepository.Get(_sealedOrderId).Returns(sealedOrder);
        _shiftRepository.GetSealedOrder(_sealedOrderId, _keyToken).Returns(sealedOrder);
        _variantGuard.EnsureOpenIfVariantAsync(_keyToken, Arg.Any<Guid>()).Returns<Task<bool>>(_ => throw new ConflictException("in use"));

        await Should.ThrowAsync<ConflictException>(() => _facade.ResetCutsAsync(_sealedOrderId, ResetDate));

        await _shiftRepository.DidNotReceiveWithAnyArgs().CutList(default, default, default, default);
        await _shiftRepository.DidNotReceiveWithAnyArgs().Add(default!);
        await _unitOfWork.DidNotReceive().CompleteAsync();
    }

    [Test]
    public void BatchRequestOfAKeyShift_CarriesTheKeyTokenToTheTokenFilter()
    {
        var piece = Piece(_pieceId);
        piece.AnalyseToken = _keyToken;
        var request = new PostBatchCutsRequest { Operations = [Create(_sealedOrderId, piece)] };

        var tokens = ScenarioTokenArgumentScanner.Collect(new Dictionary<string, object?> { ["request"] = request });

        tokens.ShouldContain(_keyToken);
    }

    [Test]
    public async Task Batch_InAKey_AsksThePeriodRuleForEveryRange_BeforeAnythingIsStaged()
    {
        Store(Stored(_sealedOrderId, ShiftStatus.SealedOrder, null, _keyToken));
        var piece = Piece(_pieceId);
        piece.UntilDate = null;
        _variantGuard.EnsureInsideVariantPeriodAsync(
                _keyToken,
                Arg.Is<IReadOnlyCollection<(DateOnly From, DateOnly? Until)>>(ranges => IsSingleRange(ranges, piece.FromDate, null)),
                Arg.Any<CancellationToken>())
            .ThrowsAsync(new BadRequestException(VariantShiftWriteGuard.PeriodOutsideVariantMessage));

        await Should.ThrowAsync<BadRequestException>(() => _facade.ProcessBatchCutsAsync([Create(_sealedOrderId, piece)]));

        await AssertNothingStagedAsync();
    }

    [Test]
    public async Task Batch_InTheRealPlan_NeverAsksThePeriodRule()
    {
        Store(Stored(_sealedOrderId, ShiftStatus.SealedOrder, null, null));

        await _facade.ProcessBatchCutsAsync([Create(_sealedOrderId, Piece(_pieceId))]);

        await _variantGuard.DidNotReceiveWithAnyArgs().EnsureInsideVariantPeriodAsync(default, default!, default);
    }

    [Test]
    public async Task Reset_InAKey_AsksThePeriodRuleForTheNewPlannableRange_AndWritesNothingWhenRefused()
    {
        var sealedOrder = Stored(_sealedOrderId, ShiftStatus.SealedOrder, null, _keyToken);
        sealedOrder.UntilDate = ResetDate.AddDays(10);
        _shiftRepository.Get(_sealedOrderId).Returns(sealedOrder);
        _shiftRepository.GetSealedOrder(_sealedOrderId, _keyToken).Returns(sealedOrder);
        _variantGuard.EnsureInsideVariantPeriodAsync(
                _keyToken,
                Arg.Is<IReadOnlyCollection<(DateOnly From, DateOnly? Until)>>(ranges => IsSingleRange(ranges, ResetDate, ResetDate.AddDays(10))),
                Arg.Any<CancellationToken>())
            .ThrowsAsync(new BadRequestException(VariantShiftWriteGuard.PeriodOutsideVariantMessage));

        await Should.ThrowAsync<BadRequestException>(() => _facade.ResetCutsAsync(_sealedOrderId, ResetDate));

        await AssertNothingStagedAsync();
    }

    [Test]
    public async Task Reset_InTheRealPlan_NeverAsksThePeriodRule()
    {
        var sealedOrder = Stored(_sealedOrderId, ShiftStatus.SealedOrder, null, null);
        _shiftRepository.GetSealedOrder(_sealedOrderId).Returns(sealedOrder);
        _shiftRepository.CutList(_sealedOrderId, ResetDate, true, null).Returns([]);

        await _facade.ResetCutsAsync(_sealedOrderId, ResetDate);

        await _variantGuard.DidNotReceiveWithAnyArgs().EnsureInsideVariantPeriodAsync(default, default!, default);
    }

    [TestCase(nameof(ShiftsController.PostBatchCuts))]
    [TestCase(nameof(ShiftsController.PostResetCuts))]
    public void CutActions_AllowTheVariantToken_AndKeepTheirRoles(string actionName)
    {
        var action = typeof(ShiftsController).GetMethod(actionName)!;

        action.GetCustomAttribute<AllowVariantTokenAttribute>().ShouldNotBeNull();
        action.GetCustomAttributes<AuthorizeAttribute>(inherit: true).Single().Roles
            .ShouldBe($"{Klacks.Api.Domain.Constants.Roles.Admin},{Klacks.Api.Domain.Constants.Roles.Authorised}");
    }

    private static bool IsSingleRange(IReadOnlyCollection<(DateOnly From, DateOnly? Until)> ranges, DateOnly from, DateOnly? until)
        => ranges.Count == 1 && ranges.Single() == (from, until);

    private async Task AssertNothingStagedAsync()
    {
        await _shiftRepository.DidNotReceiveWithAnyArgs().Add(default!);
        await _shiftRepository.DidNotReceiveWithAnyArgs().PutCutUpdate(default!);
        await _unitOfWork.DidNotReceive().CompleteAsync();
    }

    private void Store(Shift shift) => _stored[shift.Id] = shift;

    private Shift Stored(Guid id, ShiftStatus status, Guid? originalId, Guid? token) => new()
    {
        Id = id,
        Name = "Stored",
        Status = status,
        OriginalId = originalId,
        ClientId = _customerId,
        FromDate = FromDate,
        AnalyseToken = token,
        GroupItems = [new GroupItem { Id = Guid.NewGuid(), GroupId = _parentGroupId, ShiftId = id, AnalyseToken = token }]
    };

    private ShiftResource Piece(Guid id) => new()
    {
        Id = id,
        Name = "Piece",
        Status = ShiftStatus.SplitShift,
        FromDate = FromDate,
        Groups = [new SimpleGroupResource { Id = _groupId }]
    };

    private static CutOperation Create(Guid parentId, ShiftResource data) =>
        new() { Type = CutOperation.CreateType, ParentId = parentId.ToString(), Data = data };

    private static CutOperation Update(ShiftResource data) =>
        new() { Type = CutOperation.UpdateType, ParentId = string.Empty, Data = data };
}
