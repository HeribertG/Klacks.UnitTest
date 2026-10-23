// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// The confirmation preview of cut_shift plans with the same planner as the confirmed call: it names the plannable shift,
/// every part with times and name and the default expenses every part carries, and binds exactly that cut (kind ShiftCut).
/// A refused plan or a missing parameter refuses the preview, so the gate issues no token. A changed expense changes the
/// binding, so a confirmation given for the old preview no longer matches.
/// </summary>

using Klacks.Api.Application.Services.Schedules;
using Klacks.Api.Application.Skills;
using Klacks.Api.Domain.Constants;
using Klacks.UnitTest.TestHelpers;

namespace Klacks.UnitTest.Skills;

[TestFixture]
public class CutShiftConfirmationPreviewProviderTests
{
    private static readonly Guid SealedOrderId = Guid.NewGuid();
    private static readonly Guid OriginalShiftId = Guid.NewGuid();

    private IShiftRepository _shiftRepository = null!;
    private List<ShiftExpenses> _expenses = null!;
    private CutShiftConfirmationPreviewProvider _provider = null!;

    [SetUp]
    public void SetUp()
    {
        _expenses =
        [
            new ShiftExpenses { Id = Guid.NewGuid(), ShiftId = OriginalShiftId, Description = "Meal", Amount = 12.50m, Taxable = false },
            new ShiftExpenses { Id = Guid.NewGuid(), ShiftId = OriginalShiftId, Description = "Night allowance", Amount = 20.00m, Taxable = true }
        ];
        _shiftRepository = Substitute.For<IShiftRepository>();
        _shiftRepository.Get(OriginalShiftId).Returns(new Shift
        {
            Id = OriginalShiftId,
            Name = "24h Dienst",
            Status = ShiftStatus.OriginalShift,
            OriginalId = SealedOrderId,
            StartShift = new TimeOnly(7, 0),
            EndShift = new TimeOnly(7, 0),
            FromDate = new DateOnly(2026, 1, 1)
        });
        _shiftRepository.GetGroupsForShift(Arg.Any<Guid>()).Returns(new List<Group>());
        var reader = Substitute.For<IShiftReader>();
        var expensesRepository = Substitute.For<IShiftExpensesRepository>();
        expensesRepository.GetByShiftIdsAsync(Arg.Any<IEnumerable<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(_ => _expenses.ToList());
        _provider = new CutShiftConfirmationPreviewProvider(
            new ShiftTimeCutPlanner(_shiftRepository, reader, ShiftVisibilityGuards.AllowAll(), expensesRepository));
    }

    private static SkillExecutionContext Ctx() => new() { UserId = Guid.NewGuid(), UserName = "tester" };

    private static Dictionary<string, object> Params(Guid shiftId) => new()
    {
        [CutShiftSkill.ShiftIdParameter] = shiftId.ToString(),
        [CutShiftSkill.PartsParameter] = "07:00-15:00,15:00-23:00,23:00-07:00",
        [CutShiftSkill.PartNamesParameter] = "Early,Late,Night"
    };

    [Test]
    public async Task Preview_NamesThePartsAndTheExpensesEveryPartCarries_AndBindsTheCut()
    {
        var preview = await _provider.BuildAsync(CutShiftSkill.SkillName, Ctx(), Params(OriginalShiftId));

        preview.IsRefusal.ShouldBeFalse(preview.Text);
        preview.Text.ShouldContain("Cut the plannable shift '24h Dienst' (07:00-07:00, 2026-01-01 - open-ended) into 3 parts:");
        preview.Text.ShouldContain("- 07:00-15:00 Early (keeps the plannable shift)");
        preview.Text.ShouldContain("- 15:00-23:00 Late");
        preview.Text.ShouldContain("- 23:00-07:00 Night");
        preview.Text.ShouldContain("Meal 12.50 (non-taxable expense), Night allowance 20.00 (taxable allowance)");
        preview.Binding.ShouldNotBeNull();
        preview.Binding!.Kind.ShouldBe(ConfirmationBindingKinds.ShiftCut);
    }

    [Test]
    public async Task Preview_WithoutExpenses_SaysSo()
    {
        _expenses = [];

        var preview = await _provider.BuildAsync(CutShiftSkill.SkillName, Ctx(), Params(OriginalShiftId));

        preview.Text.ShouldContain("Default expenses every part carries (booked with each work on it): no default expenses.");
    }

    [Test]
    public async Task ChangedExpense_ChangesTheBinding()
    {
        var before = await _provider.BuildAsync(CutShiftSkill.SkillName, Ctx(), Params(OriginalShiftId));
        _expenses[0].Amount = 15m;

        var after = await _provider.BuildAsync(CutShiftSkill.SkillName, Ctx(), Params(OriginalShiftId));

        before.Binding!.Matches(after.Binding!).ShouldBeFalse();
    }

    [Test]
    public async Task SamePlanTwice_BindsTheSame_AlthoughNewPartIdsDiffer()
    {
        var first = await _provider.BuildAsync(CutShiftSkill.SkillName, Ctx(), Params(OriginalShiftId));
        var second = await _provider.BuildAsync(CutShiftSkill.SkillName, Ctx(), Params(OriginalShiftId));

        first.Binding!.Matches(second.Binding!).ShouldBeTrue();
    }

    [Test]
    public async Task UnknownShift_RefusesThePreview()
    {
        var unknown = Guid.NewGuid();

        var preview = await _provider.BuildAsync(CutShiftSkill.SkillName, Ctx(), Params(unknown));

        preview.IsRefusal.ShouldBeTrue();
        preview.Text.ShouldBe($"Shift with ID {unknown} not found.");
    }

    [Test]
    public async Task MissingParts_RefusesThePreview()
    {
        var preview = await _provider.BuildAsync(CutShiftSkill.SkillName, Ctx(),
            new Dictionary<string, object> { [CutShiftSkill.ShiftIdParameter] = OriginalShiftId.ToString() });

        preview.IsRefusal.ShouldBeTrue();
        preview.Text.ShouldBe("Required parameter 'parts' is missing.");
    }
}
