// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Characterization tests for CreateShiftSkill.ExecuteAsync, written before its long body was split into
/// private steps: every refusal text, the success and reuse texts and serialized result data, the order
/// in which repositories, guards and the mediator are consulted, and the shift handed to the repository. Since the owner
/// decision of 2026-10-24 the created order is always an open order (OriginalOrder); asDraft is ignored.
/// </summary>

using System.Text.Json;
using Klacks.UnitTest.TestHelpers;
using Klacks.Api.Application.DTOs.Settings;
using Klacks.Api.Application.Queries.Settings.Macros;
using Klacks.Api.Application.Skills;
using Klacks.Api.Infrastructure.Mediator;

namespace Klacks.UnitTest.Skills;

[TestFixture]
public class CreateShiftSkillCharacterizationTests
{
    private static readonly Guid ClientId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid DefaultMacroId = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly Guid PlannableShiftId = Guid.Parse("33333333-3333-3333-3333-333333333333");
    private static readonly Guid GroupId = Guid.Parse("44444444-4444-4444-4444-444444444444");
    private static readonly Guid ReusableShiftId = Guid.Parse("55555555-5555-5555-5555-555555555555");
    private static readonly Guid ReusableSealedId = Guid.Parse("66666666-6666-6666-6666-666666666666");

    private IShiftRepository _shiftRepository = null!;
    private IGroupRepository _groupRepository = null!;
    private IClientRepository _clientRepository = null!;
    private IClientVisibilityGuard _clientVisibilityGuard = null!;
    private IGroupVisibilityGuard _groupVisibilityGuard = null!;
    private IMediator _mediator = null!;
    private IUnitOfWork _unitOfWork = null!;
    private IDefaultShiftMacroResolver _defaultShiftMacroResolver = null!;
    private CreateShiftSkill _skill = null!;
    private List<string> _calls = null!;
    private Client _client = null!;
    private Shift? _added;

    [SetUp]
    public void Setup()
    {
        _calls = [];
        _added = null;
        _client = new Client { Id = ClientId, Company = "Mueller AG", Type = EntityTypeEnum.Customer };

        _shiftRepository = Substitute.For<IShiftRepository>();
        _groupRepository = Substitute.For<IGroupRepository>();
        _clientRepository = Substitute.For<IClientRepository>();
        _clientVisibilityGuard = Substitute.For<IClientVisibilityGuard>();
        _groupVisibilityGuard = Substitute.For<IGroupVisibilityGuard>();
        _mediator = Substitute.For<IMediator>();
        _unitOfWork = Substitute.For<IUnitOfWork>();
        _defaultShiftMacroResolver = Substitute.For<IDefaultShiftMacroResolver>();

        _clientVisibilityGuard.IsVisibleAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(_ => { _calls.Add("client-visible"); return true; });
        _clientRepository.GetByIdsAsync(Arg.Any<IEnumerable<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(_ => { _calls.Add("client-load"); return new List<Client> { _client }; });
        _mediator.Send(Arg.Any<ListQuery>(), Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                _calls.Add("macros");
                return new List<MacroResource> { new() { Id = DefaultMacroId, Name = "Standard", Origin = MacroOrigin.Seed } }
                    .AsEnumerable();
            });
        _defaultShiftMacroResolver.ResolveDefaultMacroIdAsync(Arg.Any<CancellationToken>())
            .Returns(_ => { _calls.Add("default-macro"); return (Guid?)DefaultMacroId; });
        _groupRepository.Get(Arg.Any<Guid>())
            .Returns(ci => { _calls.Add("group-get"); return new Group { Id = ci.Arg<Guid>(), Name = "Bern" }; });
        _groupVisibilityGuard.IsGroupVisibleAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(_ => { _calls.Add("group-visible"); return true; });
        _shiftRepository.FindReusableUncutOrderAsync(Arg.Any<Shift>(), Arg.Any<CancellationToken>())
            .Returns(_ => { _calls.Add("reuse-lookup"); return (Shift?)null; });
        _shiftRepository.AddWithSealedOrderHandling(Arg.Any<Shift>())
            .Returns(ci =>
            {
                _calls.Add("add");
                _added = ci.Arg<Shift>();
                return new Shift { Id = PlannableShiftId, Name = _added.Name };
            });
        _unitOfWork.CompleteAsync().Returns(_ => { _calls.Add("complete"); return Task.CompletedTask; });

        _skill = new CreateShiftSkill(
            _shiftRepository, _groupRepository, _clientRepository, _clientVisibilityGuard, _mediator, _unitOfWork,
            _defaultShiftMacroResolver, _groupVisibilityGuard);
    }

    private static SkillExecutionContext Ctx() => new()
    {
        UserId = Guid.NewGuid(),
        TenantId = Guid.NewGuid(),
        UserName = "tester",
        UserPermissions = new List<string> { "CanEditShifts" }
    };

    private static Dictionary<string, object> Params() => new()
    {
        ["name"] = "Reinigung Mueller",
        ["clientId"] = ClientId.ToString(),
        ["startTime"] = "07:00",
        ["endTime"] = "15:00",
        ["fromDate"] = "2026-06-01"
    };

    private static Dictionary<string, object> With(params (string Key, object Value)[] overrides)
    {
        var parameters = Params();
        foreach (var (key, value) in overrides)
        {
            parameters[key] = value;
        }

        return parameters;
    }

    private static Dictionary<string, object> Without(string key)
    {
        var parameters = Params();
        parameters.Remove(key);
        return parameters;
    }

    private const string MissingClientMessage =
        "Cannot create the order yet. Every order (Bestellung) must be billed to a customer. " +
        "Ask the user which customer this order is for — list existing customers with find_customer_candidates, " +
        "or create a new one with create_employee using entityType=Customer — then call create_shift again with clientId.";

    private const string MissingFromDateMessage =
        "Cannot create the order yet. Ask the user from when the order is valid (ab wann / von wann gilt die Bestellung). " +
        "When you ask, append a date picker so the user can pick it instead of typing: [REPLIES:date \"Ab wann gilt die Bestellung?\"]. " +
        "Resolve casual phrases the user already gave you: \"heute\"/\"sofort\"/\"today\" -> today's date; " +
        "\"morgen\"/\"tomorrow\" -> tomorrow's date; \"1. Juli\"/\"July 1st\" -> the matching YYYY-MM-DD. " +
        "Then call create_shift again with fromDate in YYYY-MM-DD format.";

    private const string NoShiftMacroMessage =
        "Cannot create the order yet. No calculation macro with category 'Shift' is configured, so the " +
        "order would have no way to compute its hours and surcharges. First set up a calculation macro " +
        "with category Shift (function Standard) — inspect the existing macros with list_macros or " +
        "configure one in Settings — then call create_shift again (optionally pass its id as macroId).";

    private static readonly string[] CallsUpToMacro = ["client-visible", "client-load", "macros", "default-macro"];

    [Test]
    public async Task OpenOrder_ExactMessageDataShiftAndCallOrder()
    {
        var result = await _skill.ExecuteAsync(Ctx(), With(
            ("groupIds", $"{GroupId}, not-a-guid"),
            ("weekdays", "mon, wed,sun"),
            ("untilDate", "2026-12-31"),
            ("sumEmployees", 2),
            ("quantity", 3),
            ("description", "Treppenhaus")));

        result.Success.ShouldBeTrue(result.Message);
        result.Message.ShouldBe(
            "Order 'Reinigung Mueller' (07:00-15:00, 8h) created as an open order (status OriginalOrder) for customer " +
            "'Mueller AG' with calculation macro 'Standard', with 1 group(s) — NOT sealed, so it has no plannable shift yet " +
            "and cannot be booked or cut. " +
            "Klacksy never seals an order on create. Once name, abbreviation, fromDate, at least one weekday or holiday, " +
            "quantity>0 and sumEmployees>0 are all set, call seal_shift with " +
            $"shiftId=\"{PlannableShiftId}\": the user confirms its preview (an order without a group is placed in the group " +
            "derived from the customer's address, which the preview names), and only the sealed order gets the plannable shift " +
            "that can be booked - and split with cut_shift if it should be cut into parts.");
        JsonSerializer.Serialize(result.Data).ShouldBe(
            "{\"ShiftId\":\"" + PlannableShiftId + "\",\"Name\":\"Reinigung Mueller\"," +
            "\"Abbreviation\":\"RM\",\"StartTime\":\"07:00\",\"EndTime\":\"15:00\",\"FromDate\":\"2026-06-01\"," +
            "\"UntilDate\":\"2026-12-31\",\"WorkTime\":8,\"SumEmployees\":2,\"GroupCount\":1,\"ClientId\":\"" + ClientId + "\"," +
            "\"ClientName\":\"Mueller AG\",\"MacroId\":\"" + DefaultMacroId + "\",\"MacroName\":\"Standard\"}");
        _calls.ShouldBe(["client-visible", "client-load", "macros", "default-macro", "group-get", "group-visible",
            "reuse-lookup", "add", "complete"]);

        var shift = _added!;
        shift.Id.ShouldNotBe(Guid.Empty);
        shift.Name.ShouldBe("Reinigung Mueller");
        shift.Abbreviation.ShouldBe("RM");
        shift.Description.ShouldBe("Treppenhaus");
        shift.ClientId.ShouldBe(ClientId);
        shift.MacroId.ShouldBe(DefaultMacroId);
        shift.Status.ShouldBe(ShiftStatus.OriginalOrder);
        shift.ShiftType.ShouldBe(ShiftType.IsTask);
        shift.IsTimeRange.ShouldBeTrue();
        shift.StartShift.ShouldBe(new TimeOnly(7, 0));
        shift.EndShift.ShouldBe(new TimeOnly(15, 0));
        shift.FromDate.ShouldBe(new DateOnly(2026, 6, 1));
        shift.UntilDate.ShouldBe(new DateOnly(2026, 12, 31));
        shift.WorkTime.ShouldBe(8m);
        shift.SumEmployees.ShouldBe(2);
        shift.Quantity.ShouldBe(3);
        new[] { shift.IsMonday, shift.IsTuesday, shift.IsWednesday, shift.IsThursday, shift.IsFriday, shift.IsSaturday, shift.IsSunday }
            .ShouldBe([true, false, true, false, false, false, true]);
        shift.GroupItems.Count.ShouldBe(1);
        shift.GroupItems.Single().GroupId.ShouldBe(GroupId);
        shift.GroupItems.Single().ValidFrom.ShouldBe(new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc));
        shift.GroupItems.Single().ValidFrom!.Value.Kind.ShouldBe(DateTimeKind.Utc);
        shift.CurrentUserCreated.ShouldBe("tester");
        await _groupRepository.Received(1).Get(GroupId);
    }

    [Test]
    public async Task OpenOrder_Defaults_AllWeekdays_NoGroups_GivenAbbreviation_PersonClientName_UnknownDefaultMacroName()
    {
        _client = new Client { Id = ClientId, FirstName = "Hans", Name = "Muster", Type = EntityTypeEnum.Customer };
        _defaultShiftMacroResolver.ResolveDefaultMacroIdAsync(Arg.Any<CancellationToken>())
            .Returns(_ => { _calls.Add("default-macro"); return (Guid?)Guid.Parse("77777777-7777-7777-7777-777777777777"); });

        var result = await _skill.ExecuteAsync(Ctx(), With(("abbreviation", "XY"), ("startTime", "22:00"), ("endTime", "06:00")));

        result.Success.ShouldBeTrue(result.Message);
        result.Message!.ShouldStartWith(
            "Order 'Reinigung Mueller' (22:00-06:00, 8h) created as an open order (status OriginalOrder) for customer 'Hans Muster' " +
            "(no default calculation macro configured — set one with category Shift), with 0 group(s) — NOT sealed");
        JsonSerializer.Serialize(result.Data).ShouldContain("\"Abbreviation\":\"XY\"");
        JsonSerializer.Serialize(result.Data).ShouldContain("\"UntilDate\":null");
        JsonSerializer.Serialize(result.Data).ShouldEndWith(
            "\"MacroId\":\"77777777-7777-7777-7777-777777777777\",\"MacroName\":null}");
        var shift = _added!;
        shift.Description.ShouldBe(string.Empty);
        shift.SumEmployees.ShouldBe(1);
        shift.Quantity.ShouldBe(1);
        shift.UntilDate.ShouldBeNull();
        shift.GroupItems.ShouldBeEmpty();
        new[] { shift.IsMonday, shift.IsTuesday, shift.IsWednesday, shift.IsThursday, shift.IsFriday, shift.IsSaturday, shift.IsSunday }
            .ShouldAllBe(flag => flag);
        _calls.ShouldBe(["client-visible", "client-load", "macros", "default-macro", "reuse-lookup", "add", "complete"]);
    }

    [TestCase("weekdays", new[] { true, true, true, true, true, false, false })]
    [TestCase("WEEKEND", new[] { false, false, false, false, false, true, true })]
    [TestCase("", new[] { true, true, true, true, true, true, true })]
    [TestCase("tuesday,friday,xyz", new[] { false, true, false, false, true, false, false })]
    public async Task WeekdaysParameter_SetsTheShiftFlags(string weekdays, bool[] expected)
    {
        await _skill.ExecuteAsync(Ctx(), With(("weekdays", weekdays)));

        var shift = _added!;
        new[] { shift.IsMonday, shift.IsTuesday, shift.IsWednesday, shift.IsThursday, shift.IsFriday, shift.IsSaturday, shift.IsSunday }
            .ShouldBe(expected);
    }

    [Test]
    public async Task AsDraftArgument_IsIgnored_TheOrderIsOpenEitherWay_AndTheReuseLookupRuns()
    {
        var withFalse = await _skill.ExecuteAsync(Ctx(), With(("asDraft", false)));
        var withTrue = await _skill.ExecuteAsync(Ctx(), With(("asDraft", true)));

        withFalse.Message.ShouldBe(withTrue.Message);
        withTrue.Message!.ShouldContain("created as an open order (status OriginalOrder)");
        _added!.Status.ShouldBe(ShiftStatus.OriginalOrder);
        _calls.ShouldBe(["client-visible", "client-load", "macros", "default-macro", "reuse-lookup", "add", "complete",
            "client-visible", "client-load", "macros", "default-macro", "reuse-lookup", "add", "complete"]);
    }

    [Test]
    public async Task ReusableOrder_ExactMessageAndData_NothingPersisted()
    {
        _shiftRepository.FindReusableUncutOrderAsync(Arg.Any<Shift>(), Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                _calls.Add("reuse-lookup");
                return new Shift
                {
                    Id = ReusableShiftId, OriginalId = ReusableSealedId, Name = "Existing", Status = ShiftStatus.OriginalShift
                };
            });

        var result = await _skill.ExecuteAsync(Ctx(), Params());

        result.Success.ShouldBeTrue();
        result.Message.ShouldBe(
            "Order 'Reinigung Mueller' already exists for customer 'Mueller AG' (not yet split) — no new order created. " +
            $"If it should be split into parts, call cut_shift now with shiftId=\"{ReusableShiftId}\" and the requested parts " +
            "(e.g. parts=\"07:00-15:00,15:00-23:00,23:00-07:00\", partNames=\"Frühdienst,Spätdienst,Nachtdienst\"). " +
            "Do NOT navigate to the cut page and do NOT write manual cutting instructions.");
        JsonSerializer.Serialize(result.Data).ShouldBe(
            "{\"ShiftId\":\"" + ReusableShiftId + "\",\"Name\":\"Existing\"," +
            "\"Reused\":true,\"IsSealed\":true,\"ClientId\":\"" + ClientId + "\",\"ClientName\":\"Mueller AG\"}");
        _calls.ShouldBe(["client-visible", "client-load", "macros", "default-macro", "reuse-lookup"]);
        await _shiftRepository.Received(1).FindReusableUncutOrderAsync(
            Arg.Is<Shift>(s => s.Name == "Reinigung Mueller" && s.ClientId == ClientId && s.Status == ShiftStatus.OriginalOrder),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task MissingClientId_IsRefusedBeforeAnyLookup()
    {
        (await _skill.ExecuteAsync(Ctx(), Without("clientId"))).Message.ShouldBe(MissingClientMessage);
        (await _skill.ExecuteAsync(Ctx(), With(("clientId", "not-a-guid")))).Message.ShouldBe(MissingClientMessage);
        _calls.ShouldBeEmpty();
    }

    [Test]
    public async Task UnknownClient_IsRefusedBeforeTheMacroLookup()
    {
        _clientRepository.GetByIdsAsync(Arg.Any<IEnumerable<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(_ => { _calls.Add("client-load"); return new List<Client>(); });

        var result = await _skill.ExecuteAsync(Ctx(), Params());

        result.Success.ShouldBeFalse();
        result.Message.ShouldBe($"Customer with ID {ClientId} not found.");
        _calls.ShouldBe(["client-visible", "client-load"]);
    }

    [Test]
    public async Task NonCustomerClient_IsRefusedWithItsDisplayName()
    {
        _client = new Client { Id = ClientId, FirstName = "Hans", Name = "Muster", Type = EntityTypeEnum.Employee };

        var result = await _skill.ExecuteAsync(Ctx(), Params());

        result.Message.ShouldBe(
            "Client 'Hans Muster' is not a customer (type Employee). An order must be billed to a customer. " +
            "Pick a customer with find_customer_candidates or create one with create_employee using entityType=Customer.");
        _calls.ShouldBe(["client-visible", "client-load"]);
    }

    [Test]
    public async Task MacroErrors_ComeBeforeTimeValidation()
    {
        var unknownMacro = Guid.Parse("88888888-8888-8888-8888-888888888888");

        var notFound = await _skill.ExecuteAsync(Ctx(), With(("macroId", unknownMacro.ToString()), ("startTime", "bad")));
        notFound.Message.ShouldBe(
            $"Calculation macro with ID {unknownMacro} not found. Use list_macros to pick a valid macro.");
        _calls.ShouldBe(["client-visible", "client-load", "macros"]);

        _calls.Clear();
        _defaultShiftMacroResolver.ResolveDefaultMacroIdAsync(Arg.Any<CancellationToken>()).Returns((Guid?)null);
        var noMacro = await _skill.ExecuteAsync(Ctx(), With(("startTime", "bad")));
        noMacro.Message.ShouldBe(NoShiftMacroMessage);
    }

    [Test]
    public async Task InvalidTimes_AreRefusedAfterTheMacroLookup()
    {
        (await _skill.ExecuteAsync(Ctx(), With(("startTime", "abc"), ("endTime", "xyz"))))
            .Message.ShouldBe("Invalid start time format: abc. Expected HH:mm");
        _calls.ShouldBe(CallsUpToMacro);

        (await _skill.ExecuteAsync(Ctx(), With(("endTime", "xyz"))))
            .Message.ShouldBe("Invalid end time format: xyz. Expected HH:mm");
    }

    [Test]
    public async Task DateErrors_HaveTheirExactTexts()
    {
        (await _skill.ExecuteAsync(Ctx(), Without("fromDate"))).Message.ShouldBe(MissingFromDateMessage);
        (await _skill.ExecuteAsync(Ctx(), With(("fromDate", "  ")))).Message.ShouldBe(MissingFromDateMessage);
        (await _skill.ExecuteAsync(Ctx(), With(("fromDate", "someday"))))
            .Message.ShouldBe("Invalid fromDate format: someday. Expected YYYY-MM-DD (e.g. 2026-06-01).");
        (await _skill.ExecuteAsync(Ctx(), With(("untilDate", "2026-05-31"))))
            .Message.ShouldBe("untilDate (2026-05-31) must not be before fromDate (2026-06-01).");
        _calls.ShouldNotContain("add");
    }

    [Test]
    public async Task UnparseableUntilDate_IsSilentlyIgnored()
    {
        var result = await _skill.ExecuteAsync(Ctx(), With(("untilDate", "never")));

        result.Success.ShouldBeTrue(result.Message);
        _added!.UntilDate.ShouldBeNull();
    }

    [Test]
    public async Task CountErrors_HaveTheirExactTexts_AndSumEmployeesIsCheckedFirst()
    {
        (await _skill.ExecuteAsync(Ctx(), With(("sumEmployees", 0), ("quantity", 0))))
            .Message.ShouldBe("sumEmployees (required staff count) must be at least 1.");
        (await _skill.ExecuteAsync(Ctx(), With(("quantity", 0))))
            .Message.ShouldBe("quantity must be at least 1.");
        _calls.ShouldNotContain("group-get");
    }

    [TestCase("fromDate", "someday")]
    [TestCase("untilDate", "2026-05-31")]
    [TestCase("sumEmployees", 0)]
    [TestCase("quantity", 0)]
    [TestCase("endTime", "xyz")]
    public async Task ScheduleAndCountErrors_ComeBeforeTheGroupLookup(string key, object value)
    {
        var result = await _skill.ExecuteAsync(Ctx(), With(("groupIds", GroupId.ToString()), (key, value)));

        result.Success.ShouldBeFalse();
        _calls.ShouldBe(CallsUpToMacro);
    }

    [Test]
    public async Task UnknownGroup_IsRefused_WithoutAskingTheVisibilityGuard()
    {
        _groupRepository.Get(Arg.Any<Guid>()).Returns(_ => { _calls.Add("group-get"); return (Group?)null; });

        var result = await _skill.ExecuteAsync(Ctx(), With(("groupIds", GroupId.ToString())));

        result.Message.ShouldBe($"Group with ID {GroupId} not found.");
        _calls.ShouldBe(["client-visible", "client-load", "macros", "default-macro", "group-get"]);
    }

    [Test]
    public async Task HiddenGroup_IsRefused_AfterTheRepositoryRead()
    {
        _groupVisibilityGuard.IsGroupVisibleAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(_ => { _calls.Add("group-visible"); return false; });

        var result = await _skill.ExecuteAsync(Ctx(), With(("groupIds", GroupId.ToString())));

        result.Message.ShouldBe($"Group with ID {GroupId} not found.");
        _calls.ShouldBe(["client-visible", "client-load", "macros", "default-macro", "group-get", "group-visible"]);
    }

    [Test]
    public async Task MissingRequiredParameters_ThrowInDeclarationOrder()
    {
        var all = new Dictionary<string, object>();
        (await Should.ThrowAsync<ArgumentException>(() => _skill.ExecuteAsync(Ctx(), all)))
            .Message.ShouldBe("Required parameter 'name' is missing");

        var noStart = Without("startTime");
        noStart.Remove("endTime");
        (await Should.ThrowAsync<ArgumentException>(() => _skill.ExecuteAsync(Ctx(), noStart)))
            .Message.ShouldBe("Required parameter 'startTime' is missing");
        _calls.ShouldBeEmpty();
    }
}
