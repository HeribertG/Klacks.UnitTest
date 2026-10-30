// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// The confirmation preview of split_shifts_by_weekday plans with the same planner as the confirmed call: it names the group,
/// the shifts to cut with their weekdays, the part count and the skipped count, and binds exactly that selection (kind
/// WeekdayCut). An unknown group, a missing groupName or a group with nothing to cut refuses the preview, so the gate issues
/// no token. apply=true carries the confirmation note, apply=false says that it cuts nothing; a changed selection changes the
/// binding.
/// </summary>

using Klacks.Api.Application.Services.Schedules;
using Klacks.Api.Application.Skills;
using Klacks.Api.Domain.Constants;
using Klacks.UnitTest.TestHelpers;

namespace Klacks.UnitTest.Skills;

[TestFixture]
public class SplitShiftsByWeekdayConfirmationPreviewProviderTests
{
    private const string GroupName = "Spitex Winterthur";

    private static readonly Guid GroupId = Guid.NewGuid();

    private List<Shift> _store = null!;
    private SplitShiftsByWeekdayConfirmationPreviewProvider _provider = null!;

    [SetUp]
    public void SetUp()
    {
        _store = [];
        var groupRepository = Substitute.For<IGroupRepository>();
        groupRepository.List().Returns(_ => new List<Group> { new() { Id = GroupId, Name = GroupName } });
        var groupItems = Substitute.For<IGroupItemRepository>();
        groupItems.GetShiftIdsByGroupIds(Arg.Any<List<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(_ => _store.Select(s => s.Id).ToList());
        var reader = Substitute.For<IShiftReader>();
        reader.ScanRealPlanAsync(Arg.Any<RealPlanShiftScan>(), Arg.Any<CancellationToken>())
            .Returns(call => (IReadOnlyList<Shift>)_store.Where(s => s.Status == call.Arg<RealPlanShiftScan>().Status).ToList());
        _provider = new SplitShiftsByWeekdayConfirmationPreviewProvider(new ShiftWeekdayCutPlanner(
            groupRepository, TestGroupWriteVisibility.UnrestrictedGroups(), groupItems, reader, ShiftVisibilityGuards.AllowAll()));
    }

    private Shift Given(string name, bool mo, bool tu, bool we, bool th, bool fr)
    {
        var shift = new Shift
        {
            Id = Guid.NewGuid(),
            Name = name,
            Status = ShiftStatus.OriginalShift,
            ShiftType = ShiftType.IsTask,
            OriginalId = Guid.NewGuid(),
            IsMonday = mo,
            IsTuesday = tu,
            IsWednesday = we,
            IsThursday = th,
            IsFriday = fr
        };
        _store.Add(shift);
        return shift;
    }

    private static SkillExecutionContext Ctx() => new()
    {
        UserId = Guid.NewGuid(),
        TenantId = Guid.NewGuid(),
        UserName = "tester",
        UserPermissions = []
    };

    private static Dictionary<string, object> Params(bool? apply = null, string groupName = GroupName)
    {
        var parameters = new Dictionary<string, object> { [SplitShiftsByWeekdaySkill.GroupNameParameter] = groupName };
        if (apply.HasValue)
        {
            parameters[PreviewApplySkillCalls.ApplyParameter] = apply.Value;
        }

        return parameters;
    }

    [Test]
    public async Task Apply_NamesTheShiftsWeekdaysAndCounts_AndBindsTheSelection()
    {
        Given("Mueller", mo: true, tu: false, we: true, th: false, fr: true);
        Given("Meier", mo: false, tu: true, we: false, th: true, fr: false);
        Given("Keller", mo: false, tu: false, we: false, th: true, fr: false);

        var preview = await _provider.BuildAsync(SplitShiftsByWeekdaySkill.SkillName, Ctx(), Params(apply: true));

        preview.IsRefusal.ShouldBeFalse(preview.Text);
        preview.Text.ShouldContain($"Cut the plannable shifts of the group '{GroupName}' by weekday now.");
        preview.Text.ShouldContain("2 plannable shift(s) run on several weekdays and would be cut into 5 part(s)");
        preview.Text.ShouldContain("1 shift(s) are skipped");
        preview.Text.ShouldContain("- Mueller (Monday, Wednesday, Friday)");
        preview.Text.ShouldContain("- Meier (Tuesday, Thursday)");
        preview.Text.ShouldNotContain("Keller");
        preview.Text.ShouldContain("bound to the confirmation");
        preview.Binding.ShouldNotBeNull();
        preview.Binding!.Kind.ShouldBe(ConfirmationBindingKinds.WeekdayCut);
    }

    [Test]
    public async Task Preview_SaysThatItCutsNothing_AndCarriesNoConfirmationNote()
    {
        Given("Mueller", mo: true, tu: false, we: true, th: false, fr: false);

        var preview = await _provider.BuildAsync(SplitShiftsByWeekdaySkill.SkillName, Ctx(), Params());

        preview.IsRefusal.ShouldBeFalse(preview.Text);
        preview.Text.ShouldContain("Preview only, this call cuts nothing.");
        preview.Text.ShouldNotContain("bound to the confirmation");
    }

    [Test]
    public async Task AChangedSelection_ChangesTheBinding()
    {
        Given("Mueller", mo: true, tu: false, we: true, th: false, fr: true);
        var first = (await _provider.BuildAsync(SplitShiftsByWeekdaySkill.SkillName, Ctx(), Params(apply: true))).Binding!;
        var same = (await _provider.BuildAsync(SplitShiftsByWeekdaySkill.SkillName, Ctx(), Params(apply: true))).Binding!;
        Given("Meier", mo: false, tu: true, we: false, th: true, fr: false);
        var changed = (await _provider.BuildAsync(SplitShiftsByWeekdaySkill.SkillName, Ctx(), Params(apply: true))).Binding!;

        first.Matches(same).ShouldBeTrue();
        first.Matches(changed).ShouldBeFalse();
    }

    [Test]
    public async Task AGroupWithNothingToCut_RefusesThePreview()
    {
        Given("Keller", mo: false, tu: false, we: false, th: true, fr: false);

        var preview = await _provider.BuildAsync(SplitShiftsByWeekdaySkill.SkillName, Ctx(), Params(apply: true));

        preview.IsRefusal.ShouldBeTrue();
        preview.Text.ShouldContain("Nothing to cut");
        preview.Binding.ShouldBeNull();
    }

    [Test]
    public async Task AnUnknownGroup_RefusesThePreview()
    {
        Given("Mueller", mo: true, tu: false, we: true, th: false, fr: true);

        var preview = await _provider.BuildAsync(SplitShiftsByWeekdaySkill.SkillName, Ctx(), Params(apply: true, groupName: "No such group"));

        preview.IsRefusal.ShouldBeTrue();
        preview.Binding.ShouldBeNull();
    }

    [Test]
    public async Task AMissingGroupName_RefusesThePreview()
    {
        var preview = await _provider.BuildAsync(SplitShiftsByWeekdaySkill.SkillName, Ctx(), new Dictionary<string, object>());

        preview.IsRefusal.ShouldBeTrue();
    }

    [Test]
    public void Supports_OnlySplitShiftsByWeekday()
    {
        _provider.Supports(SplitShiftsByWeekdaySkill.SkillName).ShouldBeTrue();
        _provider.Supports(CutShiftSkill.SkillName).ShouldBeFalse();
    }
}
