// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Characterization tests for CutShiftSkill.ExecuteAsync, written before its long body was split into private
/// steps: every refusal text, the success text and serialized result data (part count taken from the facade's
/// result), every CutOperation handed to the cut facade, the required-parameter order, and the order in which
/// repository, visibility guard, queries and facade are consulted. Since cut_shift runs only as the confirmed replay of its
/// preview, every call carries a ShiftCut binding; the success cases carry the binding of exactly the cut they expect, and
/// a call without a binding or with the binding of another cut is refused before anything is cut.
/// </summary>

using System.Globalization;
using System.Text.Json;
using Klacks.Api.Application.Commands.Shifts;
using Klacks.Api.Application.Common;
using Klacks.Api.Application.DTOs.Schedules;
using Klacks.Api.Domain.Constants;
using Klacks.Api.Application.Services.Schedules;
using Klacks.Api.Application.Skills;
using Klacks.UnitTest.TestHelpers;

namespace Klacks.UnitTest.Skills;

[TestFixture]
public class CutShiftSkillCharacterizationTests
{
    private static readonly Guid SealedOrderId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid OriginalShiftId = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly Guid ClientId = Guid.Parse("33333333-3333-3333-3333-333333333333");
    private static readonly Guid MacroId = Guid.Parse("44444444-4444-4444-4444-444444444444");
    private static readonly Guid GroupA = Guid.Parse("55555555-5555-5555-5555-555555555555");
    private static readonly Guid GroupB = Guid.Parse("66666666-6666-6666-6666-666666666666");

    private IShiftRepository _shiftRepository = null!;
    private IShiftReader _shiftReader = null!;
    private IShiftCutFacade _shiftCutFacade = null!;
    private IShiftVisibilityGuard _visibilityGuard = null!;
    private CutShiftSkill _skill = null!;
    private List<string> _calls = null!;
    private Shift? _loaded;
    private List<Shift> _queryRows = null!;
    private bool _visible;
    private List<CutOperation>? _operations;
    private List<Shift> _facadeResult = null!;
    private ConfirmationBinding? _binding;

    [SetUp]
    public void Setup()
    {
        _calls = [];
        _loaded = Original();
        _queryRows = [];
        _visible = true;
        _operations = null;
        _facadeResult = [new Shift(), new Shift()];
        _binding = ConfirmationBinding.Of(ConfirmationBindingKinds.ShiftCut, ["characterization"]);

        _shiftRepository = Substitute.For<IShiftRepository>();
        _shiftReader = Substitute.For<IShiftReader>();
        _shiftCutFacade = Substitute.For<IShiftCutFacade>();
        _visibilityGuard = Substitute.For<IShiftVisibilityGuard>();

        _shiftRepository.Get(Arg.Any<Guid>())
            .Returns(_ => { _calls.Add("get"); return _loaded; });
        _visibilityGuard.IsVisibleAsync(Arg.Any<Shift>(), Arg.Any<CancellationToken>())
            .Returns(_ => { _calls.Add("visible"); return _visible; });
        ShiftLineageStub.Serve(_shiftReader, () => _queryRows, () => _calls.Add("query"));
        _shiftRepository.GetGroupsForShift(Arg.Any<Guid>())
            .Returns(_ =>
            {
                _calls.Add("groups");
                return new List<Group> { new() { Id = GroupA, Name = "A" }, new() { Id = GroupB, Name = "B" } };
            });
        _shiftCutFacade.ProcessBatchCutsAsync(Arg.Any<List<CutOperation>>())
            .Returns(ci =>
            {
                _calls.Add("cuts");
                _operations = ci.Arg<List<CutOperation>>();
                return _facadeResult;
            });

        var expenses = Substitute.For<IShiftExpensesRepository>();
        expenses.GetByShiftIdsAsync(Arg.Any<IEnumerable<Guid>>(), Arg.Any<CancellationToken>()).Returns(new List<ShiftExpenses>());
        _skill = new CutShiftSkill(
            new ShiftTimeCutPlanner(_shiftRepository, _shiftReader, _visibilityGuard, expenses), _shiftCutFacade);
    }

    private static Shift Original() => new()
    {
        Id = OriginalShiftId,
        Name = "24h Dienst",
        Abbreviation = "24H",
        Description = "Rund um die Uhr",
        Status = ShiftStatus.OriginalShift,
        ShiftType = ShiftType.IsTask,
        OriginalId = SealedOrderId,
        ClientId = ClientId,
        MacroId = MacroId,
        StartShift = new TimeOnly(7, 0),
        EndShift = new TimeOnly(7, 0),
        FromDate = new DateOnly(2026, 1, 1),
        UntilDate = new DateOnly(2026, 12, 31),
        SumEmployees = 2,
        Quantity = 3,
        IsMonday = true,
        IsTuesday = false,
        IsWednesday = true,
        IsThursday = false,
        IsFriday = true,
        IsSaturday = false,
        IsSunday = true,
        IsHoliday = true,
        IsWeekdayAndHoliday = false,
        IsTimeRange = true,
        IsSporadic = false
    };

    private SkillExecutionContext Ctx() => new()
    {
        UserId = Guid.NewGuid(),
        TenantId = Guid.NewGuid(),
        UserName = "tester",
        UserPermissions = new List<string> { "CanEditShifts" },
        ConfirmedActionBinding = _binding
    };

    private static ConfirmationBinding Bound(params (string Start, string End, string Name)[] parts) =>
        ShiftTimeCutBinding.Of(new ShiftTimeCutPlan(
            null,
            new Shift { Id = OriginalShiftId },
            SealedOrderId,
            [],
            parts.Select((part, index) => new ShiftTimeCutPart(
                Guid.Empty,
                index,
                part.Name,
                TimeOnly.ParseExact(part.Start, DateTimeFormats.HourMinute, CultureInfo.InvariantCulture),
                TimeOnly.ParseExact(part.End, DateTimeFormats.HourMinute, CultureInfo.InvariantCulture),
                0m,
                false,
                false)).ToList(),
            [],
            []));

    private static Dictionary<string, object> Params(Guid shiftId, string parts, string? partNames = null)
    {
        var parameters = new Dictionary<string, object> { ["shiftId"] = shiftId.ToString(), ["parts"] = parts };
        if (partNames != null)
        {
            parameters["partNames"] = partNames;
        }

        return parameters;
    }

    private const string ThreeParts = "07:00-15:00, 15:00-23:00 ,23:00-07:00";

    [Test]
    public async Task PlannableShift_ExactOperationsMessageDataAndCallOrder()
    {
        _binding = Bound(("07:00", "15:00", "Early"), ("15:00", "23:00", "Late Shift"), ("23:00", "07:00", "24h Dienst (23:00-07:00)"));
        var result = await _skill.ExecuteAsync(Ctx(), Params(OriginalShiftId, ThreeParts, " Early , ,Late Shift "));

        result.Success.ShouldBeTrue(result.Message);
        _calls.ShouldBe(["get", "visible", "query", "groups", "cuts"]);
        await _shiftRepository.Received(1).GetGroupsForShift(OriginalShiftId);

        var ops = _operations!;
        ops.Select(o => o.Type).ShouldBe([CutOperation.UpdateType, CutOperation.CreateType, CutOperation.CreateType]);
        ops.ShouldAllBe(o => o.ParentId == SealedOrderId.ToString());
        ops[0].Data.Id.ShouldBe(OriginalShiftId);
        ops[1].Data.Id.ShouldNotBe(Guid.Empty);
        ops[2].Data.Id.ShouldNotBe(Guid.Empty);
        ops[1].Data.Id.ShouldNotBe(ops[2].Data.Id);
        ops.Select(o => o.Data.Name).ShouldBe(["Early", "Late Shift", "24h Dienst (23:00-07:00)"]);
        ops.Select(o => o.Data.Abbreviation).ShouldBe(["EAR", "LS", "2D("]);
        ops.Select(o => o.Data.StartShift).ShouldBe([new TimeOnly(7, 0), new TimeOnly(15, 0), new TimeOnly(23, 0)]);
        ops.Select(o => o.Data.EndShift).ShouldBe([new TimeOnly(15, 0), new TimeOnly(23, 0), new TimeOnly(7, 0)]);
        ops.Select(o => o.Data.WorkTime).ShouldBe([8m, 8m, 8m]);
        ops.Select(o => o.Data.CuttingAfterMidnight).ShouldBe([false, false, true]);
        foreach (var data in ops.Select(o => o.Data))
        {
            data.Description.ShouldBe("Rund um die Uhr");
            data.Status.ShouldBe(ShiftStatus.SplitShift);
            data.ShiftType.ShouldBe(ShiftType.IsTask);
            data.OriginalId.ShouldBe(SealedOrderId);
            data.ClientId.ShouldBe(ClientId);
            data.MacroId.ShouldBe(MacroId);
            data.FromDate.ShouldBe(new DateOnly(2026, 1, 1));
            data.UntilDate.ShouldBe(new DateOnly(2026, 12, 31));
            data.SumEmployees.ShouldBe(2);
            data.Quantity.ShouldBe(3);
            new[] { data.IsMonday, data.IsTuesday, data.IsWednesday, data.IsThursday, data.IsFriday, data.IsSaturday, data.IsSunday }
                .ShouldBe([true, false, true, false, true, false, true]);
            data.IsHoliday.ShouldBeTrue();
            data.IsWeekdayAndHoliday.ShouldBeFalse();
            data.IsTimeRange.ShouldBeTrue();
            data.IsSporadic.ShouldBeFalse();
            data.Groups.Select(g => g.Id).ShouldBe([GroupA, GroupB]);
        }

        result.Message.ShouldBe(
            "Cut the plannable shift '24h Dienst' into 2 part(s): Early, Late Shift, 24h Dienst (23:00-07:00). " +
            "The first part reuses the plannable shift (reduced, status -> SplitShift); " +
            "the order (Bestellung) itself stays unchanged.");
        JsonSerializer.Serialize(result.Data).ShouldBe(
            "{\"SealedOrderId\":\"" + SealedOrderId + "\",\"OrderName\":\"24h Dienst\",\"PartCount\":2,\"GroupCount\":2," +
            "\"Parts\":[" +
            "{\"Id\":\"" + OriginalShiftId + "\",\"Name\":\"Early\",\"StartTime\":\"07:00\",\"EndTime\":\"15:00\"," +
            "\"WorkTime\":8,\"CuttingAfterMidnight\":false,\"Converted\":true}," +
            "{\"Id\":\"" + ops[1].Data.Id + "\",\"Name\":\"Late Shift\",\"StartTime\":\"15:00\",\"EndTime\":\"23:00\"," +
            "\"WorkTime\":8,\"CuttingAfterMidnight\":false,\"Converted\":false}," +
            "{\"Id\":\"" + ops[2].Data.Id + "\",\"Name\":\"24h Dienst (23:00-07:00)\",\"StartTime\":\"23:00\"," +
            "\"EndTime\":\"07:00\",\"WorkTime\":8,\"CuttingAfterMidnight\":true,\"Converted\":false}]}");
    }

    [Test]
    public async Task OrderId_ResolvesThePlannableShiftByQuery()
    {
        _loaded = new Shift { Id = SealedOrderId, Name = "Bestellung 24h", Status = ShiftStatus.SealedOrder };
        _queryRows = [Original()];
        _binding = Bound(("07:00", "19:00", "24h Dienst (07:00-19:00)"), ("19:00", "07:00", "24h Dienst (19:00-07:00)"));

        var result = await _skill.ExecuteAsync(Ctx(), Params(SealedOrderId, "07:00-19:00,19:00-07:00"));

        result.Success.ShouldBeTrue(result.Message);
        _calls.ShouldBe(["get", "visible", "query", "query", "groups", "cuts"]);
        _operations![0].Data.Id.ShouldBe(OriginalShiftId);
        _operations.ShouldAllBe(o => o.ParentId == SealedOrderId.ToString());
        _operations.Select(o => o.Data.Abbreviation).ShouldBe(["2D(", "2D("]);
        result.Message!.ShouldStartWith(
            "Cut the plannable shift '24h Dienst' into 2 part(s): 24h Dienst (07:00-19:00), 24h Dienst (19:00-07:00). ");
    }

    [Test]
    public async Task MissingShiftId_IsReportedBeforeMissingParts()
    {
        var ex = await Should.ThrowAsync<ArgumentException>(() => _skill.ExecuteAsync(Ctx(), new Dictionary<string, object>()));

        ex.Message.ShouldBe("Required parameter 'shiftId' is missing");
    }

    [Test]
    public async Task MissingParts_Throws()
    {
        var ex = await Should.ThrowAsync<ArgumentException>(() => _skill.ExecuteAsync(Ctx(),
            new Dictionary<string, object> { ["shiftId"] = OriginalShiftId.ToString() }));

        ex.Message.ShouldBe("Required parameter 'parts' is missing");
    }

    [TestCase("07:00", "Invalid part '07:00'. Expected format 'HH:mm-HH:mm'.")]
    [TestCase("07:00-15:00,-15:00", "Invalid part '-15:00'. Expected format 'HH:mm-HH:mm'.")]
    [TestCase("07:00-", "Invalid part '07:00-'. Expected format 'HH:mm-HH:mm'.")]
    [TestCase("ab:cd-15:00,15:00-07:00", "Invalid time in part 'ab:cd-15:00'. Expected format 'HH:mm-HH:mm'.")]
    [TestCase("07:00-15:00", "A cut needs between 2 and 12 parts; received 1.")]
    [TestCase("00:00-01:00,01:00-02:00,02:00-03:00,03:00-04:00,04:00-05:00,05:00-06:00,06:00-07:00," +
              "07:00-08:00,08:00-09:00,09:00-10:00,10:00-11:00,11:00-12:00,12:00-00:00",
        "A cut needs between 2 and 12 parts; received 13.")]
    public async Task PartErrors_ExactText_BeforeTheShiftIsLoaded(string parts, string expected)
    {
        var result = await _skill.ExecuteAsync(Ctx(), Params(OriginalShiftId, parts));

        result.Success.ShouldBeFalse();
        result.Message.ShouldBe(expected);
        _calls.ShouldBeEmpty();
    }

    [Test]
    public async Task UnknownShift_ExactText()
    {
        _loaded = null;

        var result = await _skill.ExecuteAsync(Ctx(), Params(OriginalShiftId, ThreeParts));

        result.Message.ShouldBe($"Shift with ID {OriginalShiftId} not found.");
        _calls.ShouldBe(["get"]);
    }

    [Test]
    public async Task HiddenShift_IsAnsweredLikeAnUnknownShift()
    {
        _visible = false;

        var result = await _skill.ExecuteAsync(Ctx(), Params(OriginalShiftId, ThreeParts));

        result.Message.ShouldBe($"Shift with ID {OriginalShiftId} not found.");
        _calls.ShouldBe(["get", "visible"]);
    }

    [TestCase(ShiftStatus.OriginalOrder, true)]
    [TestCase(ShiftStatus.OriginalShift, false)]
    public async Task ShiftWithoutOrderReference_ExactText(ShiftStatus status, bool keepOriginalId)
    {
        _loaded = new Shift
        {
            Id = OriginalShiftId,
            Name = "Lose",
            Status = status,
            OriginalId = keepOriginalId ? SealedOrderId : null
        };

        var result = await _skill.ExecuteAsync(Ctx(), Params(OriginalShiftId, ThreeParts));

        result.Message.ShouldBe(
            $"Shift 'Lose' has status {status} and has no order reference and cannot be cut. " +
            "Create the order first (create_shift), then cut its plannable shift.");
        _calls.ShouldBe(["get", "visible"]);
    }

    [Test]
    public async Task AlreadySplitOrder_ExactText()
    {
        _loaded = new Shift { Id = OriginalShiftId, Name = "Teil 1", Status = ShiftStatus.SplitShift, OriginalId = SealedOrderId };
        _queryRows = [_loaded];

        var result = await _skill.ExecuteAsync(Ctx(), Params(OriginalShiftId, ThreeParts));

        result.Message.ShouldBe(
            "Order 'Teil 1' is already split into parts. Reuse the existing parts or reset the cut first " +
            "instead of cutting again (use find_split_shift_candidates to inspect the existing parts).");
        _calls.ShouldBe(["get", "visible", "query"]);
    }

    [Test]
    public async Task OrderWithoutPlannableShift_ExactText()
    {
        _loaded = new Shift { Id = SealedOrderId, Name = "Bestellung 24h", Status = ShiftStatus.SealedOrder };

        var result = await _skill.ExecuteAsync(Ctx(), Params(SealedOrderId, ThreeParts));

        result.Message.ShouldBe(
            "Order 'Bestellung 24h' has no plannable shift to cut. Create the order first (create_shift).");
        _calls.ShouldBe(["get", "visible", "query", "query"]);
    }

    [Test]
    public async Task PartsNotTilingTheOrder_ExactText_BeforeTheGroupLookup()
    {
        const string parts = "07:00-15:00,16:00-07:00";
        var tilingError = ShiftTilingValidator.Validate(
            new TimeOnly(7, 0), new TimeOnly(7, 0),
            [(new TimeOnly(7, 0), new TimeOnly(15, 0)), (new TimeOnly(16, 0), new TimeOnly(7, 0))]);
        tilingError.ShouldNotBeNull();

        var result = await _skill.ExecuteAsync(Ctx(), Params(OriginalShiftId, parts));

        result.Success.ShouldBeFalse();
        result.Message.ShouldBe(
            $"{tilingError} Adjust the parts so they cover the order span exactly, then call cut_shift again.");
        _calls.ShouldBe(["get", "visible", "query"]);
    }

    [Test]
    public async Task WithoutABinding_IsRefused_BeforeAnythingIsRead()
    {
        _binding = null;

        var result = await _skill.ExecuteAsync(Ctx(), Params(OriginalShiftId, ThreeParts));

        result.Success.ShouldBeFalse();
        result.Message.ShouldBe(string.Format(CultureInfo.InvariantCulture, ConfirmationBindingMessages.BindingRequired, CutShiftSkill.SkillName));
        _calls.ShouldBeEmpty();
    }

    [Test]
    public async Task ABindingOfAnotherKind_IsRefused_BeforeAnythingIsRead()
    {
        _binding = ConfirmationBinding.Of(ConfirmationBindingKinds.Work, ["x"]);

        var result = await _skill.ExecuteAsync(Ctx(), Params(OriginalShiftId, ThreeParts));

        result.Message.ShouldBe(string.Format(CultureInfo.InvariantCulture, ConfirmationBindingMessages.BindingRequired, CutShiftSkill.SkillName));
        _calls.ShouldBeEmpty();
    }

    [Test]
    public async Task ABindingOfAnotherCut_IsRefused_AndNothingIsCut()
    {
        _binding = Bound(("07:00", "15:00", "Early"), ("15:00", "07:00", "Late"));

        var result = await _skill.ExecuteAsync(Ctx(), Params(OriginalShiftId, ThreeParts, "Early,Late Shift"));

        result.Success.ShouldBeFalse();
        result.Message!.ShouldStartWith("What this confirmation was given for has changed since the preview");
        _calls.ShouldNotContain("cuts");
    }

    [Test]
    public async Task Result_NamesTheDefaultExpensesEveryPartCarries()
    {
        _binding = Bound(("07:00", "19:00", "Day"), ("19:00", "07:00", "Night"));
        _shiftCutFacade.ProcessBatchCutsAsync(Arg.Any<List<CutOperation>>())
            .Returns(ci => ci.Arg<List<CutOperation>>().Select(op => new Shift
            {
                Id = op.Data.Id,
                ShiftExpenses = [new ShiftExpenses { Description = "Meal", Amount = 12.50m, Taxable = false }]
            }).ToList());

        var result = await _skill.ExecuteAsync(Ctx(), Params(OriginalShiftId, "07:00-19:00,19:00-07:00", "Day,Night"));

        result.Success.ShouldBeTrue(result.Message);
        result.Message!.ShouldContain("'Day': Meal 12.50 (non-taxable expense)");
        result.Message.ShouldContain("'Night': Meal 12.50 (non-taxable expense)");
    }
}
