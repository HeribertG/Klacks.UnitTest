// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Pins the PUT of a shift in a variant (key) that has cuts, with the real VariantShiftWriteGuard over stubbed
/// repositories: a change of time, date, weekdays or staffing is a conflict and reaches no write, whichever row of the
/// pair was addressed (the cuts hang on the sealed order, so the plannable copy is resolved through OriginalId); a rename,
/// a change inside the rounding the edit form applies, and any change of an order without cuts pass. That the mirrored
/// fields really land on both rows is proven against PostgreSQL in VariantShiftFamilySeamTests.
/// </summary>

using Klacks.Api.Application.Commands;
using Klacks.Api.Application.Constants;
using Klacks.Api.Application.DTOs.Associations;
using Klacks.Api.Application.DTOs.Schedules;
using Klacks.Api.Application.Exceptions;
using Klacks.Api.Application.Handlers.Shifts;
using Klacks.Api.Application.Mappers;
using Klacks.Api.Application.Services.ScenarioVariants;
using Klacks.UnitTest.TestHelpers;
using Microsoft.Extensions.Logging;

namespace Klacks.UnitTest.Application.Handlers.Shifts;

[TestFixture]
public class VariantShiftCutConflictTests
{
    private IShiftRepository _shiftRepository = null!;
    private IAnalyseScenarioRepository _scenarioRepository = null!;
    private IScenarioVariantRepository _variantRepository = null!;
    private IAnalyseScenarioService _scenarioService = null!;
    private IGroupVisibilityGuard _groupGuard = null!;
    private IUnitOfWork _unitOfWork = null!;
    private Guid _keyGroupId;
    private AnalyseScenario _variant = null!;

    [SetUp]
    public void SetUp()
    {
        _shiftRepository = Substitute.For<IShiftRepository>();
        _scenarioRepository = Substitute.For<IAnalyseScenarioRepository>();
        _variantRepository = Substitute.For<IScenarioVariantRepository>();
        _scenarioService = Substitute.For<IAnalyseScenarioService>();
        _groupGuard = Substitute.For<IGroupVisibilityGuard>();
        _unitOfWork = Substitute.For<IUnitOfWork>();
        _keyGroupId = Guid.NewGuid();

        _groupGuard.IsGroupVisibleAsync(_keyGroupId, Arg.Any<CancellationToken>()).Returns(true);
        _scenarioService.GetGroupHierarchyIdsAsync(_keyGroupId, Arg.Any<CancellationToken>())
            .Returns(new List<Guid> { _keyGroupId });
        _shiftRepository.PutWithSealedOrderHandling(Arg.Any<Shift>()).Returns(call => call.Arg<Shift>());
        _shiftRepository.Get(Arg.Any<Guid>()).Returns((Shift?)null);

        _variant = new AnalyseScenario
        {
            Id = Guid.NewGuid(),
            Token = Guid.NewGuid(),
            Name = "Large order",
            GroupId = _keyGroupId,
            Kind = AnalyseScenarioKind.Variant,
            Status = AnalyseScenarioStatus.Active,
            FromDate = new DateOnly(2099, 8, 1),
            UntilDate = new DateOnly(2099, 8, 31)
        };
        _scenarioRepository.GetByTokenAsync(_variant.Token, Arg.Any<CancellationToken>()).Returns(_variant);
    }

    [Test]
    public async Task Put_ChangingTheStartTimeOfASealedOrderWithCuts_IsAConflictAndWritesNothing()
    {
        var sealedOrder = StoredSealedOrder();
        CutsExistFor(sealedOrder.Id);
        var resource = ResourceOf(sealedOrder);
        resource.StartShift = new TimeOnly(9, 0);

        var exception = await Should.ThrowAsync<ConflictException>(
            () => PutHandler().Handle(new PutCommand<ShiftResource>(resource), CancellationToken.None));

        exception.Message.ShouldBe(VariantShiftWriteGuard.CutsExistMessage);
        await NothingWasWrittenAsync();
    }

    [Test]
    public async Task Put_ChangingTheWeekdaysOfThePlannableCopyWithCuts_ResolvesTheSealedOrderAndIsAConflict()
    {
        var sealedOrderId = Guid.NewGuid();
        var copy = StoredPlannableCopy(sealedOrderId);
        CutsExistFor(sealedOrderId);
        var resource = ResourceOf(copy);
        resource.IsTuesday = true;

        await Should.ThrowAsync<ConflictException>(
            () => PutHandler().Handle(new PutCommand<ShiftResource>(resource), CancellationToken.None));

        await NothingWasWrittenAsync();
    }

    [Test]
    public async Task Put_ChangingTheStaffingOrTheDateOfAnOrderWithCuts_IsAConflict()
    {
        var sealedOrder = StoredSealedOrder();
        CutsExistFor(sealedOrder.Id);
        var quantity = ResourceOf(sealedOrder);
        quantity.Quantity = sealedOrder.Quantity + 1;
        var date = ResourceOf(sealedOrder);
        date.FromDate = sealedOrder.FromDate.AddDays(1);

        await Should.ThrowAsync<ConflictException>(
            () => PutHandler().Handle(new PutCommand<ShiftResource>(quantity), CancellationToken.None));
        await Should.ThrowAsync<ConflictException>(
            () => PutHandler().Handle(new PutCommand<ShiftResource>(date), CancellationToken.None));

        await NothingWasWrittenAsync();
    }

    [Test]
    public async Task Put_RenamingAnOrderWithCuts_PassesToTheRepository()
    {
        var sealedOrder = StoredSealedOrder();
        CutsExistFor(sealedOrder.Id);
        var resource = ResourceOf(sealedOrder);
        resource.Name = "Renamed";
        resource.Description = "Renamed description";

        await PutHandler().Handle(new PutCommand<ShiftResource>(resource), CancellationToken.None);

        await _shiftRepository.Received(1).PutWithSealedOrderHandling(
            Arg.Is<Shift>(shift => shift.Id == sealedOrder.Id && shift.Name == "Renamed"));
        await _unitOfWork.Received(1).CompleteAsync();
    }

    [Test]
    public async Task Put_ChangingOnlyWhatTheEditFormRounds_PassesEvenWithCuts()
    {
        var sealedOrder = StoredSealedOrder();
        sealedOrder.WorkTime = 8.0333m;
        CutsExistFor(sealedOrder.Id);
        var resource = ResourceOf(sealedOrder);
        resource.WorkTime = 8.03m;
        resource.StartShift = sealedOrder.StartShift.Add(TimeSpan.FromSeconds(40));

        await PutHandler().Handle(new PutCommand<ShiftResource>(resource), CancellationToken.None);

        await _shiftRepository.Received(1).PutWithSealedOrderHandling(Arg.Is<Shift>(shift => shift.Id == sealedOrder.Id));
    }

    [Test]
    public async Task Put_ChangingTheTimeOfAnOrderWithoutCuts_PassesToTheRepository()
    {
        var sealedOrder = StoredSealedOrder();
        var resource = ResourceOf(sealedOrder);
        resource.StartShift = new TimeOnly(9, 0);
        resource.Quantity = sealedOrder.Quantity + 1;

        await PutHandler().Handle(new PutCommand<ShiftResource>(resource), CancellationToken.None);

        await _variantRepository.Received(1).HasCutsAsync(_variant.Token, sealedOrder.Id, CancellationToken.None);
        await _shiftRepository.Received(1).PutWithSealedOrderHandling(Arg.Is<Shift>(shift => shift.Id == sealedOrder.Id));
    }

    [Test]
    public async Task Put_OnlyRenaming_NeverAsksForCuts()
    {
        var sealedOrder = StoredSealedOrder();
        var resource = ResourceOf(sealedOrder);
        resource.Name = "Renamed";

        await PutHandler().Handle(new PutCommand<ShiftResource>(resource), CancellationToken.None);

        await _variantRepository.DidNotReceive().HasCutsAsync(Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task Put_ChangingTheTimeOfACutPiece_IsRefused_WithoutAskingForCuts_TheCutDialogDoesNotComeThisWay()
    {
        var sealedOrderId = Guid.NewGuid();
        var piece = StoredPlannableCopy(sealedOrderId);
        piece.Status = ShiftStatus.SplitShift;
        CutsExistFor(sealedOrderId);
        var resource = ResourceOf(piece);
        resource.StartShift = new TimeOnly(9, 0);

        var ex = await Should.ThrowAsync<ScenarioVariantConflictException>(
            () => PutHandler().Handle(new PutCommand<ShiftResource>(resource), CancellationToken.None));

        ex.ConflictCode.ShouldBe(ScenarioVariantErrorCodes.ShiftHasCuts);
        await _variantRepository.DidNotReceive().HasCutsAsync(Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<CancellationToken>());
        await NothingWasWrittenAsync();
    }

    private void CutsExistFor(Guid sealedOrderId) =>
        _variantRepository.HasCutsAsync(_variant.Token, sealedOrderId, CancellationToken.None).Returns(true);

    private async Task NothingWasWrittenAsync()
    {
        await _shiftRepository.DidNotReceive().PutWithSealedOrderHandling(Arg.Any<Shift>());
        await _unitOfWork.DidNotReceive().CompleteAsync();
    }

    private Shift StoredSealedOrder()
    {
        var stored = new Shift
        {
            Id = Guid.NewGuid(),
            Name = "Key shift",
            ClientId = Guid.NewGuid(),
            Status = ShiftStatus.SealedOrder,
            AnalyseToken = _variant.Token,
            FromDate = new DateOnly(2099, 8, 1),
            UntilDate = new DateOnly(2099, 8, 31),
            StartShift = new TimeOnly(8, 0),
            EndShift = new TimeOnly(16, 0),
            IsMonday = true,
            Quantity = 1,
            WorkTime = 8m
        };
        _shiftRepository.GetNoTracking(stored.Id).Returns(stored);
        return stored;
    }

    private Shift StoredPlannableCopy(Guid sealedOrderId)
    {
        var stored = StoredSealedOrder();
        stored.Status = ShiftStatus.OriginalShift;
        stored.OriginalId = sealedOrderId;
        return stored;
    }

    private ShiftResource ResourceOf(Shift stored)
    {
        var resource = new ScheduleMapper().ToShiftResource(stored);
        resource.AnalyseToken = null;
        resource.Client = null;
        resource.Groups = [new SimpleGroupResource { Id = _keyGroupId }];
        return resource;
    }

    private PutCommandHandler PutHandler() => new(
        _shiftRepository,
        Substitute.For<IShiftRequiredQualificationRepository>(),
        new ScheduleMapper(),
        _unitOfWork,
        ShiftVisibilityGuards.AllowAll(),
        Substitute.For<IClientReferenceGuard>(),
        new VariantShiftWriteGuard(_scenarioRepository, _variantRepository, _scenarioService, _groupGuard),
        Substitute.For<ILogger<PutCommandHandler>>());
}
