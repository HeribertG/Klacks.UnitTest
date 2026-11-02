// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Pins the freeze of a variant (key) in use: while an Active scenario carries the key, every write through
/// VariantShiftWriteGuard and ScenarioVariantAccess is refused with the conflict of ScenarioVariantAccess, and every read
/// keeps working. The cut list and the reset range use the read door, which answers a hidden key exactly like a missing id.
/// </summary>

using Klacks.Api.Application.Constants;
using Klacks.Api.Application.DTOs.Associations;
using Klacks.Api.Application.Exceptions;
using Klacks.Api.Application.Handlers.Shifts;
using Klacks.Api.Application.Mappers;
using Klacks.Api.Application.Queries.Shifts;
using Klacks.Api.Application.Services.ScenarioVariants;
using Klacks.Api.Domain.Models.Associations;
using Klacks.Api.Domain.Services.ShiftSchedule;
using Microsoft.Extensions.Logging;

namespace Klacks.UnitTest.Application.Services.ScenarioVariants;

[TestFixture]
public class ScenarioVariantFreezeTests
{
    private IShiftRepository _shiftRepository = null!;
    private IAnalyseScenarioRepository _scenarioRepository = null!;
    private IScenarioVariantRepository _variantRepository = null!;
    private IAnalyseScenarioService _scenarioService = null!;
    private IGroupVisibilityGuard _groupGuard = null!;
    private VariantShiftWriteGuard _sut = null!;
    private AnalyseScenario _variant = null!;
    private Guid _groupId;
    private bool _groupVisible;

    [SetUp]
    public void SetUp()
    {
        _shiftRepository = Substitute.For<IShiftRepository>();
        _scenarioRepository = Substitute.For<IAnalyseScenarioRepository>();
        _variantRepository = Substitute.For<IScenarioVariantRepository>();
        _scenarioService = Substitute.For<IAnalyseScenarioService>();
        _groupGuard = Substitute.For<IGroupVisibilityGuard>();
        _groupId = Guid.NewGuid();
        _groupVisible = true;

        _groupGuard.IsGroupVisibleAsync(_groupId, Arg.Any<CancellationToken>()).Returns(_ => _groupVisible);
        _scenarioService.GetGroupHierarchyIdsAsync(_groupId, Arg.Any<CancellationToken>()).Returns(new List<Guid> { _groupId });

        _variant = new AnalyseScenario
        {
            Id = Guid.NewGuid(),
            Token = Guid.NewGuid(),
            Name = "Large order",
            GroupId = _groupId,
            Kind = AnalyseScenarioKind.Variant,
            Status = AnalyseScenarioStatus.Active,
            FromDate = new DateOnly(2099, 8, 1),
            UntilDate = new DateOnly(2099, 8, 31)
        };
        _scenarioRepository.GetByTokenAsync(_variant.Token, Arg.Any<CancellationToken>()).Returns(_variant);
        _variantRepository.GetVariantAsync(_variant.Id, Arg.Any<CancellationToken>()).Returns(_variant);
        _variantRepository.HasActiveScenarioAsync(_variant.Id, Arg.Any<CancellationToken>()).Returns(true);

        _sut = new VariantShiftWriteGuard(_scenarioRepository, _variantRepository, _scenarioService, _groupGuard);
    }

    [Test]
    public async Task Create_IntoAKeyInUse_IsRefusedWithTheCodedConflict()
    {
        await ShouldBeInUseAsync(() => _sut.EnsureCreatableAsync(_variant.Token, KeyShift()));
    }

    [Test]
    public async Task Edit_OfAKeyInUse_IsRefusedWithTheCodedConflict()
    {
        var stored = KeyShift();

        await ShouldBeInUseAsync(() => _sut.EnsureEditableAsync(stored, KeyShift()));
    }

    [Test]
    public async Task CutOrReset_OfAKeyInUse_IsRefusedWithTheCodedConflict()
    {
        await ShouldBeInUseAsync(() => _sut.EnsureOpenIfVariantAsync(_variant.Token, Guid.NewGuid()));
    }

    [Test]
    public async Task Delete_OfAShiftOfAKeyInUse_IsRefusedWithTheCodedConflict_BeforeTheCloneCheck()
    {
        await ShouldBeInUseAsync(() => _sut.ResolveDeletableFamilyAsync(KeyShift()));

        await _variantRepository.DidNotReceiveWithAnyArgs().HasScenarioClonesAsync(default!, default);
    }

    [Test]
    public async Task LoadEditable_OfAKeyInUse_IsRefusedWithTheCodedConflict()
    {
        await ShouldBeInUseAsync(() => ScenarioVariantAccess.LoadEditableAsync(
            _variantRepository, _groupGuard, _variant.Id, CancellationToken.None));
    }

    [Test]
    public async Task Writes_PassAgain_OnceNoActiveScenarioCarriesTheKey()
    {
        _variantRepository.HasActiveScenarioAsync(_variant.Id, Arg.Any<CancellationToken>()).Returns(false);

        await Should.NotThrowAsync(() => _sut.EnsureCreatableAsync(_variant.Token, KeyShift()));
        (await _sut.EnsureOpenIfVariantAsync(_variant.Token, Guid.NewGuid())).ShouldBeTrue();
    }

    [Test]
    public async Task IsReadable_OfAKeyInUse_IsTrue_AndAsksNothingAboutScenarios()
    {
        (await _sut.IsReadableVariantAsync(_variant.Token)).ShouldBeTrue();

        await _variantRepository.DidNotReceiveWithAnyArgs().HasActiveScenarioAsync(default, default);
    }

    [Test]
    public async Task IsReadable_OfAHiddenKey_IsFalse_WithoutAnException()
    {
        _groupVisible = false;

        (await _sut.IsReadableVariantAsync(_variant.Token)).ShouldBeFalse();
    }

    [Test]
    public async Task IsReadable_OfAScenarioOrAnUnknownToken_IsFalse()
    {
        var scenarioToken = Guid.NewGuid();
        _scenarioRepository.GetByTokenAsync(scenarioToken, Arg.Any<CancellationToken>())
            .Returns(new AnalyseScenario { Token = scenarioToken, Kind = AnalyseScenarioKind.Scenario });

        (await _sut.IsReadableVariantAsync(scenarioToken)).ShouldBeFalse();
        (await _sut.IsReadableVariantAsync(Guid.NewGuid())).ShouldBeFalse();
    }

    [Test]
    public async Task IsReadable_OfAConsumedKey_IsTheCodedConflict()
    {
        _variant.Status = AnalyseScenarioStatus.Accepted;

        var ex = await Should.ThrowAsync<ScenarioVariantConflictException>(() => _sut.IsReadableVariantAsync(_variant.Token));

        ex.ConflictCode.ShouldBe(ScenarioVariantErrorCodes.Consumed);
        ex.Message.ShouldBe(ScenarioVariantAccess.ConsumedMessage);
    }

    [Test]
    public async Task Delete_OfAFamilyAScenarioCloned_CarriesItsCode()
    {
        _variantRepository.HasActiveScenarioAsync(_variant.Id, Arg.Any<CancellationToken>()).Returns(false);
        _variantRepository.GetShiftFamilyIdsAsync(_variant.Token, Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(new List<Guid> { Guid.NewGuid() });
        _variantRepository.HasScenarioClonesAsync(Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>()).Returns(true);

        var ex = await Should.ThrowAsync<ScenarioVariantConflictException>(() => _sut.ResolveDeletableFamilyAsync(KeyShift()));

        ex.ConflictCode.ShouldBe(ScenarioVariantErrorCodes.ShiftCloned);
    }

    [Test]
    public async Task Edit_OfACutRelevantField_WhileTheOrderHasCuts_CarriesItsCode()
    {
        _variantRepository.HasActiveScenarioAsync(_variant.Id, Arg.Any<CancellationToken>()).Returns(false);
        _variantRepository.HasCutsAsync(_variant.Token, Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns(true);
        var stored = KeyShift();
        var incoming = KeyShift();
        incoming.StartShift = stored.StartShift.AddHours(1);

        var ex = await Should.ThrowAsync<ScenarioVariantConflictException>(() => _sut.EnsureEditableAsync(stored, incoming));

        ex.ConflictCode.ShouldBe(ScenarioVariantErrorCodes.ShiftHasCuts);
    }

    [TestCase(nameof(Shift.ClientId))]
    [TestCase(nameof(Shift.FromDate))]
    [TestCase(nameof(Shift.StartShift))]
    public async Task Edit_OfACutRelevantFieldOfACutPiece_CarriesTheCutCode_WithoutAskingForCuts(string field)
    {
        _variantRepository.HasActiveScenarioAsync(_variant.Id, Arg.Any<CancellationToken>()).Returns(false);
        var stored = KeyShift();
        stored.Status = ShiftStatus.SplitShift;
        stored.OriginalId = Guid.NewGuid();
        var incoming = KeyShift();
        incoming.Status = ShiftStatus.SplitShift;
        incoming.ClientId = stored.ClientId;
        ChangeCutRelevantField(incoming, field);

        var ex = await Should.ThrowAsync<ScenarioVariantConflictException>(() => _sut.EnsureEditableAsync(stored, incoming));

        ex.ConflictCode.ShouldBe(ScenarioVariantErrorCodes.ShiftHasCuts);
        await _variantRepository.DidNotReceiveWithAnyArgs().HasCutsAsync(default, default, default);
    }

    [Test]
    public async Task Edit_OfTheNameOfACutPiece_Passes()
    {
        _variantRepository.HasActiveScenarioAsync(_variant.Id, Arg.Any<CancellationToken>()).Returns(false);
        var stored = KeyShift();
        stored.Status = ShiftStatus.SplitShift;
        var incoming = KeyShift();
        incoming.Status = ShiftStatus.SplitShift;
        incoming.ClientId = stored.ClientId;
        incoming.Name = "Renamed piece";

        await Should.NotThrowAsync(() => _sut.EnsureEditableAsync(stored, incoming));
    }

    [Test]
    public async Task CutList_OfAHiddenKey_IsEmpty_LikeAnIdThatDoesNotExist()
    {
        var stored = KeyShift();
        _shiftRepository.Get(stored.Id).Returns(stored);
        _shiftRepository.CutList(stored.Id).Returns(new List<Shift>());
        _groupVisible = false;

        var result = await CutListHandler().Handle(new CutListQuery(stored.Id), CancellationToken.None);

        result.ShouldBeEmpty();
        await _shiftRepository.DidNotReceive().CutList(stored.Id, null, false, _variant.Token);
    }

    [Test]
    public async Task CutList_OfAKeyInUse_StillReadsTheRowsOfTheKey()
    {
        var stored = KeyShift();
        _shiftRepository.Get(stored.Id).Returns(stored);
        _shiftRepository.CutList(stored.Id).Returns(new List<Shift>());
        _shiftRepository.CutList(stored.Id, null, false, _variant.Token).Returns(new List<Shift> { stored });

        var result = await CutListHandler().Handle(new CutListQuery(stored.Id), CancellationToken.None);

        result.Single().Id.ShouldBe(stored.Id);
    }

    private CutListQueryHandler CutListHandler()
    {
        return new CutListQueryHandler(
            _shiftRepository,
            Substitute.For<IShiftGroupFilterService>(),
            new ScheduleMapper(),
            _sut,
            Substitute.For<ILogger<CutListQueryHandler>>());
    }

    private Shift KeyShift()
    {
        var id = Guid.NewGuid();
        return new Shift
        {
            Id = id,
            Name = "Key shift",
            ClientId = Guid.NewGuid(),
            Status = ShiftStatus.SealedOrder,
            AnalyseToken = _variant.Token,
            FromDate = new DateOnly(2099, 8, 1),
            UntilDate = new DateOnly(2099, 8, 31),
            StartShift = new TimeOnly(8, 0),
            EndShift = new TimeOnly(16, 0),
            GroupItems = [new GroupItem { GroupId = _groupId, ShiftId = id }]
        };
    }

    private static void ChangeCutRelevantField(Shift shift, string field)
    {
        switch (field)
        {
            case nameof(Shift.ClientId):
                shift.ClientId = Guid.NewGuid();
                break;
            case nameof(Shift.FromDate):
                shift.FromDate = shift.FromDate.AddDays(1);
                break;
            default:
                shift.StartShift = shift.StartShift.AddHours(1);
                break;
        }
    }

    private static async Task ShouldBeInUseAsync(Func<Task> write)
    {
        var ex = await Should.ThrowAsync<ScenarioVariantConflictException>(write);

        ex.ConflictCode.ShouldBe(ScenarioVariantErrorCodes.InUse);
        ex.Message.ShouldBe(ScenarioVariantAccess.InUseMessage);
    }
}
