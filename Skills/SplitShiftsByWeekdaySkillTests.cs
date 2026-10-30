// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// split_shifts_by_weekday over a small fake store that applies the cut operations the way ShiftCutFacade stores them: the
/// preview writes nothing; apply needs the confirmed binding of its server preview (without one, with a foreign kind or with
/// a selection that changed since the preview it cuts nothing and reads nothing); apply cuts a Monday/Wednesday/Friday order into three parts that keep customer, macro, groups with
/// their validity, dates, times, time-range flag, staff count and quantity and differ only in the weekday flag; single-weekday,
/// already split, sporadic and hidden shifts are skipped and reported; a part that does not match what was planned fails the
/// verification, so the run is answered as an error and nothing is reported as cut.
/// </summary>

using Klacks.Api.Application.Commands.Shifts;
using Klacks.Api.Application.Mappers;
using Klacks.Api.Application.Services.Schedules;
using Klacks.Api.Application.Skills;
using Klacks.Api.Domain.Constants;
using Klacks.Api.Domain.Models.Assistant;
using Klacks.UnitTest.TestHelpers;

namespace Klacks.UnitTest.Skills;

[TestFixture]
public class SplitShiftsByWeekdaySkillTests
{
    private const string GroupName = "Spitex Winterthur";

    private static readonly Guid GroupId = Guid.NewGuid();
    private static readonly Guid ClientId = Guid.NewGuid();
    private static readonly Guid MacroId = Guid.NewGuid();
    private static readonly DateTime GroupValidFrom = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    private readonly ScheduleMapper _mapper = new();
    private List<Shift> _store = null!;
    private List<ShiftGroupLink> _links = null!;
    private IShiftCutFacade _facade = null!;
    private IUnitOfWork _unitOfWork = null!;
    private IShiftReader _reader = null!;
    private IGroupItemRepository _groupItems = null!;
    private Action<Shift>? _tamper;
    private ShiftWeekdayCutPlanner _planner = null!;
    private SplitShiftsByWeekdaySkill _skill = null!;

    [SetUp]
    public void SetUp()
    {
        _store = [];
        _links = [];
        _tamper = null;

        var groupRepository = Substitute.For<IGroupRepository>();
        groupRepository.List().Returns(_ => new List<Group> { new() { Id = GroupId, Name = GroupName } });

        _groupItems = Substitute.For<IGroupItemRepository>();
        _groupItems.GetShiftIdsByGroupIds(Arg.Any<List<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(_ => _store.Select(s => s.Id).ToList());

        _reader = Substitute.For<IShiftReader>();
        _reader.ScanRealPlanAsync(Arg.Any<RealPlanShiftScan>(), Arg.Any<CancellationToken>())
            .Returns(ci => (IReadOnlyList<Shift>)Scan(ci.Arg<RealPlanShiftScan>()));
        _reader.GetGroupLinksByShiftIdsAsync(Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(ci => (IReadOnlyList<ShiftGroupLink>)_links
                .Where(link => ci.Arg<IReadOnlyCollection<Guid>>().Contains(link.ShiftId)).ToList());

        _facade = Substitute.For<IShiftCutFacade>();
        _facade.ProcessBatchCutsAsync(Arg.Any<List<CutOperation>>()).Returns(ci => Task.FromResult(Apply(ci.Arg<List<CutOperation>>())));

        _unitOfWork = Substitute.For<IUnitOfWork>();
        _unitOfWork.ExecuteInTransactionAsync(Arg.Any<Func<Task<SplitShiftsByWeekdaySkill.CutOutcome>>>())
            .Returns(ci => ci.Arg<Func<Task<SplitShiftsByWeekdaySkill.CutOutcome>>>()());

        _planner = new ShiftWeekdayCutPlanner(
            groupRepository,
            TestGroupWriteVisibility.UnrestrictedGroups(),
            _groupItems,
            _reader,
            ShiftVisibilityGuards.AllowAll());
        _skill = new SplitShiftsByWeekdaySkill(_planner, _reader, _facade, _unitOfWork);
    }

    private static Shift Plannable(string name, Guid sealedOrderId, bool mo, bool tu, bool we, bool th, bool fr, bool sporadic = false) => new()
    {
        Id = Guid.NewGuid(),
        Name = name,
        Abbreviation = "SP",
        Status = ShiftStatus.OriginalShift,
        ShiftType = ShiftType.IsTask,
        OriginalId = sealedOrderId,
        ClientId = ClientId,
        MacroId = MacroId,
        IsTimeRange = true,
        IsSporadic = sporadic,
        StartShift = new TimeOnly(8, 0),
        EndShift = new TimeOnly(17, 0),
        WorkTime = 0.75m,
        FromDate = new DateOnly(2026, 11, 2),
        SumEmployees = 1,
        Quantity = 1,
        IsMonday = mo,
        IsTuesday = tu,
        IsWednesday = we,
        IsThursday = th,
        IsFriday = fr
    };

    private Shift GivenPlannable(string name, bool mo, bool tu, bool we, bool th, bool fr, bool sporadic = false)
    {
        var shift = Plannable(name, Guid.NewGuid(), mo, tu, we, th, fr, sporadic);
        _store.Add(shift);
        _links.Add(new ShiftGroupLink(shift.Id, GroupId, GroupValidFrom, null));
        return shift;
    }

    private static SkillExecutionContext Ctx() => new()
    {
        UserId = Guid.NewGuid(),
        TenantId = Guid.NewGuid(),
        UserName = "tester",
        UserPermissions = []
    };

    private static Dictionary<string, object> Parameters(bool? apply = null)
    {
        var parameters = new Dictionary<string, object> { ["groupName"] = GroupName };
        if (apply.HasValue)
        {
            parameters["apply"] = apply.Value;
        }

        return parameters;
    }

    private async Task<SkillResult> Run(bool? apply = null)
    {
        var context = apply == true ? Ctx() with { ConfirmedActionBinding = await BindingOfThePreviewAsync() } : Ctx();
        return await _skill.ExecuteAsync(context, Parameters(apply));
    }

    private async Task<ConfirmationBinding> BindingOfThePreviewAsync() =>
        ShiftWeekdayCutBinding.Of(await _planner.PlanAsync(GroupName));

    private List<Shift> Scan(RealPlanShiftScan scan) => _store
        .Where(s => s.Status == scan.Status
            && (scan.ShiftType is null || s.ShiftType == scan.ShiftType)
            && (scan.OnlyIds is null || scan.OnlyIds.Contains(s.Id)))
        .ToList();

    private List<Shift> Apply(List<CutOperation> operations)
    {
        var written = new List<Shift>();
        foreach (var op in operations)
        {
            var entity = _mapper.ToShiftEntity(op.Data);
            entity.Id = op.Data.Id;
            entity.Status = ShiftStatus.SplitShift;
            entity.ParentId = null;
            entity.RootId = entity.Id;
            _store.RemoveAll(s => s.Id == entity.Id);
            _store.Add(entity);
            _links.RemoveAll(link => link.ShiftId == entity.Id);
            _links.AddRange(op.Data.Groups.Select(g => new ShiftGroupLink(entity.Id, g.Id, g.ValidFrom, g.ValidUntil)));
            written.Add(entity);
        }

        if (_tamper is not null)
        {
            _tamper(written[^1]);
        }

        return written;
    }

    [Test]
    public async Task Preview_WritesNothing_AndCountsThePartsAndTheSkipped()
    {
        GivenPlannable("Mueller", mo: true, tu: false, we: true, th: false, fr: true);
        GivenPlannable("Meier", mo: false, tu: true, we: false, th: true, fr: false);
        GivenPlannable("Keller", mo: false, tu: false, we: false, th: true, fr: false);

        var result = await Run();

        result.Success.ShouldBeTrue();
        result.Message.ShouldContain("2 plannable shift(s)");
        result.Message.ShouldContain("5 part(s)");
        result.Message.ShouldContain("1 shift(s) are skipped");
        await _facade.DidNotReceiveWithAnyArgs().ProcessBatchCutsAsync(default!);
        _store.ShouldAllBe(s => s.Status == ShiftStatus.OriginalShift);
    }

    [Test]
    public async Task Apply_CutsAMondayWednesdayFridayShiftIntoThreeSingleWeekdayParts_ThatKeepEverythingElse()
    {
        var source = GivenPlannable("Mueller", mo: true, tu: false, we: true, th: false, fr: true);

        var result = await Run(apply: true);

        result.Success.ShouldBeTrue(result.Message);
        var parts = _store.Where(s => s.Status == ShiftStatus.SplitShift).OrderBy(s => s.IsMonday ? 1 : s.IsWednesday ? 3 : 5).ToList();
        parts.Count.ShouldBe(3);
        parts.Select(p => WeekdayCount(p)).ShouldAllBe(count => count == 1);
        parts[0].IsMonday.ShouldBeTrue();
        parts[1].IsWednesday.ShouldBeTrue();
        parts[2].IsFriday.ShouldBeTrue();
        parts[0].Id.ShouldBe(source.Id);
        parts.ShouldAllBe(p => p.ClientId == ClientId
            && p.MacroId == MacroId
            && p.IsTimeRange
            && p.OriginalId == source.OriginalId
            && p.SumEmployees == 1
            && p.Quantity == 1
            && p.FromDate == source.FromDate
            && p.StartShift == source.StartShift
            && p.EndShift == source.EndShift
            && p.WorkTime == source.WorkTime
            && p.ParentId == null
            && p.RootId == p.Id);
        _store.ShouldNotContain(s => s.Status == ShiftStatus.OriginalShift);
        _links.Where(l => parts.Any(p => p.Id == l.ShiftId)).ShouldAllBe(l => l.GroupId == GroupId && l.ValidFrom == GroupValidFrom);
        _links.Count(l => parts.Any(p => p.Id == l.ShiftId)).ShouldBe(3);
        result.Message.ShouldContain("2 added");
        result.Message.ShouldContain("3 confirmed");
    }

    [Test]
    public async Task Apply_SendsOneUpdateAndTwoCreatesToTheFacade_WithTheSealedOrderAsParent()
    {
        var source = GivenPlannable("Mueller", mo: true, tu: false, we: true, th: false, fr: true);
        List<CutOperation>? sent = null;
        _facade.ProcessBatchCutsAsync(Arg.Do<List<CutOperation>>(ops => sent = ops)).Returns(ci => Task.FromResult(Apply(ci.Arg<List<CutOperation>>())));

        await Run(apply: true);

        sent.ShouldNotBeNull();
        sent.Count.ShouldBe(3);
        sent.Count(op => op.Type == CutOperation.UpdateType).ShouldBe(1);
        sent.Single(op => op.Type == CutOperation.UpdateType).Data.Id.ShouldBe(source.Id);
        sent.ShouldAllBe(op => op.ParentId == source.OriginalId!.Value.ToString());
        sent.Select(op => op.Data.Id).Distinct().Count().ShouldBe(3);
    }

    [Test]
    public async Task Apply_SkipsSingleWeekdayAlreadySplitAndSporadicShifts_AndReportsThem()
    {
        GivenPlannable("Single", mo: false, tu: false, we: false, th: true, fr: false);
        GivenPlannable("Sporadic", mo: true, tu: false, we: true, th: false, fr: false, sporadic: true);
        var alreadySplit = GivenPlannable("Done", mo: true, tu: false, we: true, th: false, fr: false);
        _store.Add(new Shift
        {
            Id = Guid.NewGuid(), Status = ShiftStatus.SplitShift, ShiftType = ShiftType.IsTask, OriginalId = alreadySplit.OriginalId
        });
        var target = GivenPlannable("Target", mo: true, tu: true, we: false, th: false, fr: false);

        var result = await Run(apply: true);

        result.Success.ShouldBeTrue(result.Message);
        result.Message.ShouldContain("Cut 1 plannable shift(s)");
        result.Message.ShouldContain("3 shift(s) were skipped");
        _store.Single(s => s.Id == target.Id).Status.ShouldBe(ShiftStatus.SplitShift);
        _store.Single(s => s.Id == alreadySplit.Id).Status.ShouldBe(ShiftStatus.OriginalShift);
    }

    [Test]
    public async Task Apply_SkipsAShiftWithPlannedWorks_SoThatNoWorkEndsUpOnAPartOfAnotherWeekday()
    {
        var booked = GivenPlannable("Booked", mo: true, tu: true, we: false, th: false, fr: false);
        var free = GivenPlannable("Free", mo: true, tu: true, we: false, th: false, fr: false);
        _reader.GetShiftIdsWithWorksAsync(Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(new HashSet<Guid> { booked.Id });

        var result = await Run(apply: true);

        result.Success.ShouldBeTrue(result.Message);
        result.Message.ShouldContain("Cut 1 plannable shift(s)");
        _store.Single(s => s.Id == booked.Id).Status.ShouldBe(ShiftStatus.OriginalShift);
        _store.Single(s => s.Id == free.Id).Status.ShouldBe(ShiftStatus.SplitShift);
    }

    [Test]
    public async Task Apply_WithNothingToCut_ChangesNothing()
    {
        GivenPlannable("Single", mo: false, tu: false, we: false, th: true, fr: false);

        var result = await Run(apply: true);

        result.Success.ShouldBeTrue();
        result.Message.ShouldContain("Nothing to cut");
        await _facade.DidNotReceiveWithAnyArgs().ProcessBatchCutsAsync(default!);
    }

    [Test]
    public async Task Apply_WhenAPartDoesNotCarryItsWeekday_FailsTheVerification_AndReportsNoSuccess()
    {
        GivenPlannable("Mueller", mo: true, tu: false, we: true, th: false, fr: true);
        _tamper = part => part.IsTuesday = true;

        var result = await Run(apply: true);

        result.Success.ShouldBeFalse();
        result.Message.ShouldContain("rolled back");
        result.Message.ShouldContain("weekday");
    }

    [Test]
    public async Task Apply_WhenAPartLosesItsGroupLink_FailsTheVerification()
    {
        GivenPlannable("Mueller", mo: true, tu: false, we: true, th: false, fr: true);
        _tamper = part => _links.RemoveAll(link => link.ShiftId == part.Id);

        var result = await Run(apply: true);

        result.Success.ShouldBeFalse();
        result.Message.ShouldContain("groups");
    }

    [Test]
    public async Task Apply_WhenAPartLosesTheTimeRangeFlag_FailsTheVerification()
    {
        GivenPlannable("Mueller", mo: true, tu: false, we: true, th: false, fr: true);
        _tamper = part => part.IsTimeRange = false;

        var result = await Run(apply: true);

        result.Success.ShouldBeFalse();
        result.Message.ShouldContain("time-range flag");
    }

    [Test]
    public async Task Apply_WhenTheFacadeRefuses_AnswersAnErrorInsteadOfThrowing()
    {
        GivenPlannable("Mueller", mo: true, tu: false, we: true, th: false, fr: true);
        _facade.ProcessBatchCutsAsync(Arg.Any<List<CutOperation>>()).Returns<Task<List<Shift>>>(_ => throw new InvalidOperationException("dates outside the allowed range"));

        var result = await Run(apply: true);

        result.Success.ShouldBeFalse();
        result.Message.ShouldContain("nothing was changed");
        result.Message.ShouldContain("dates outside the allowed range");
    }

    [Test]
    public async Task AnUnknownGroup_IsRefusedBeforeAnythingIsRead()
    {
        var parameters = new Dictionary<string, object> { ["groupName"] = "No such group" };

        var result = await _skill.ExecuteAsync(Ctx(), parameters);

        result.Success.ShouldBeFalse();
        await _reader.DidNotReceiveWithAnyArgs().ScanRealPlanAsync(default!);
    }

    [Test]
    public async Task Apply_WithoutAConfirmedBinding_IsRefusedBeforeAnythingIsRead()
    {
        GivenPlannable("Mueller", mo: true, tu: false, we: true, th: false, fr: true);

        var result = await _skill.ExecuteAsync(Ctx(), Parameters(apply: true));

        result.Success.ShouldBeFalse();
        result.Message.ShouldBe(string.Format(ConfirmationBindingMessages.BindingRequired, SplitShiftsByWeekdaySkill.SkillName));
        await _reader.DidNotReceiveWithAnyArgs().ScanRealPlanAsync(default!);
        await _facade.DidNotReceiveWithAnyArgs().ProcessBatchCutsAsync(default!);
    }

    [Test]
    public async Task Apply_WithABindingOfAnotherKind_IsRefused()
    {
        GivenPlannable("Mueller", mo: true, tu: false, we: true, th: false, fr: true);
        var context = Ctx() with { ConfirmedActionBinding = ConfirmationBinding.Of(ConfirmationBindingKinds.ShiftCut, ["x"]) };

        var result = await _skill.ExecuteAsync(context, Parameters(apply: true));

        result.Success.ShouldBeFalse();
        result.Message.ShouldBe(string.Format(ConfirmationBindingMessages.BindingRequired, SplitShiftsByWeekdaySkill.SkillName));
        await _facade.DidNotReceiveWithAnyArgs().ProcessBatchCutsAsync(default!);
    }

    [Test]
    public async Task Apply_WhenAShiftAppearedAfterThePreview_CutsNothing_AndReportsTheChange()
    {
        GivenPlannable("Mueller", mo: true, tu: false, we: true, th: false, fr: true);
        var shown = await BindingOfThePreviewAsync();
        GivenPlannable("Meier", mo: false, tu: true, we: false, th: true, fr: false);
        var context = Ctx() with { ConfirmedActionBinding = shown };

        var result = await _skill.ExecuteAsync(context, Parameters(apply: true));

        result.Success.ShouldBeFalse();
        result.Message.ShouldContain("Nothing was changed");
        await _facade.DidNotReceiveWithAnyArgs().ProcessBatchCutsAsync(default!);
        _store.ShouldAllBe(shift => shift.Status == ShiftStatus.OriginalShift);
    }

    [Test]
    public async Task Apply_WhenAShiftGotPlannedWorkAfterThePreview_CutsNothing()
    {
        var booked = GivenPlannable("Mueller", mo: true, tu: false, we: true, th: false, fr: true);
        GivenPlannable("Meier", mo: false, tu: true, we: false, th: true, fr: false);
        var shown = await BindingOfThePreviewAsync();
        _reader.GetShiftIdsWithWorksAsync(Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(new HashSet<Guid> { booked.Id });
        var context = Ctx() with { ConfirmedActionBinding = shown };

        var result = await _skill.ExecuteAsync(context, Parameters(apply: true));

        result.Success.ShouldBeFalse();
        await _facade.DidNotReceiveWithAnyArgs().ProcessBatchCutsAsync(default!);
    }

    private static int WeekdayCount(Shift shift) =>
        new[] { shift.IsMonday, shift.IsTuesday, shift.IsWednesday, shift.IsThursday, shift.IsFriday, shift.IsSaturday, shift.IsSunday }
            .Count(day => day);
}
