// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Characterization tests for UpdateCalendarSelectionSkill.ExecuteAsync, written before its long body was split
/// into private steps: every refusal text, the success/no-op texts and serialized result data, the order of the
/// merged calendars sent to PutCommand (remove frees a dictionary slot that the next add reuses), the order of the
/// changed-field entries, and the order in which repository, token service, transaction and mediator are consulted.
/// </summary>

using System.Text.Json;
using Klacks.Api.Application.Commands;
using Klacks.Api.Application.DTOs.Schedules;
using Klacks.Api.Application.Skills;
using Klacks.Api.Infrastructure.Mediator;

namespace Klacks.UnitTest.Skills;

[TestFixture]
public class UpdateCalendarSelectionSkillCharacterizationTests
{
    private static readonly Guid SelectionId = Guid.Parse("11111111-1111-1111-1111-111111111111");

    private IMediator _mediator = null!;
    private ICalendarSelectionRepository _calendarSelectionRepository = null!;
    private ISettingsTokenService _settingsTokenService = null!;
    private IUnitOfWork _unitOfWork = null!;
    private UpdateCalendarSelectionSkill _skill = null!;
    private List<string> _calls = null!;
    private CalendarSelection _existing = null!;
    private CalendarSelectionResource? _sent;
    private bool _putReturnsNull;
    private bool _rereadMismatch;

    [SetUp]
    public void Setup()
    {
        _calls = [];
        _sent = null;
        _putReturnsNull = false;
        _rereadMismatch = false;
        _existing = Selection(("CH", "BE", null), ("CH", "CH", false), ("US", "US", null));

        _mediator = Substitute.For<IMediator>();
        _calendarSelectionRepository = Substitute.For<ICalendarSelectionRepository>();
        _settingsTokenService = Substitute.For<ISettingsTokenService>();
        _unitOfWork = Substitute.For<IUnitOfWork>();

        _calendarSelectionRepository.List()
            .Returns(_ => { _calls.Add("list"); return new List<CalendarSelection> { _existing }; });
        _settingsTokenService.GetRuleTokenListAsync(Arg.Any<bool>())
            .Returns(_ => { _calls.Add("tokens"); return Tokens(); });
        _unitOfWork.ExecuteInTransactionAsync(Arg.Any<Func<Task<bool>>>())
            .Returns(ci => { _calls.Add("transaction"); return ci.Arg<Func<Task<bool>>>()(); });
        _mediator.Send(Arg.Any<PutCommand<CalendarSelectionResource>>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                _calls.Add("put");
                _sent = ci.Arg<PutCommand<CalendarSelectionResource>>().Resource;
                return _putReturnsNull ? null : _sent;
            });
        _calendarSelectionRepository.GetNoTrackingWithSelectedCalendars(Arg.Any<Guid>())
            .Returns(ci =>
            {
                _calls.Add("reread");
                return new CalendarSelection
                {
                    Id = ci.Arg<Guid>(),
                    Name = _rereadMismatch ? "Other" : _sent!.Name,
                    SelectedCalendars = _sent!.SelectedCalendars.Select(sc => new SelectedCalendar
                    {
                        Id = Guid.NewGuid(),
                        CalendarSelectionId = ci.Arg<Guid>(),
                        Country = sc.Country,
                        State = sc.State,
                        OfficialOverride = sc.OfficialOverride
                    }).ToList()
                };
            });

        _skill = new UpdateCalendarSelectionSkill(_mediator, _calendarSelectionRepository, _settingsTokenService, _unitOfWork);
    }

    private static List<StateCountryToken> Tokens() =>
    [
        Token("US", "US"),
        Token("CH", "CH"),
        Token("CH", "BE"),
        Token("DE", "DE")
    ];

    private static StateCountryToken Token(string country, string state) => new()
    {
        Id = Guid.NewGuid(),
        Country = country,
        State = state,
        CountryName = MultiLanguage.Empty(),
        StateName = MultiLanguage.Empty()
    };

    private static CalendarSelection Selection(params (string Country, string State, bool? Override)[] calendars) => new()
    {
        Id = SelectionId,
        Name = "Bern",
        SelectedCalendars = calendars.Select(c => new SelectedCalendar
        {
            Id = Guid.NewGuid(),
            CalendarSelectionId = SelectionId,
            Country = c.Country,
            State = c.State,
            OfficialOverride = c.Override
        }).ToList()
    };

    private static SkillExecutionContext Ctx() => new()
    {
        UserId = Guid.NewGuid(),
        TenantId = Guid.NewGuid(),
        UserName = "tester",
        UserPermissions = new List<string> { "CanEditSettings" }
    };

    private static Dictionary<string, object> Params(params (string Key, object Value)[] values)
    {
        var parameters = new Dictionary<string, object> { ["calendarSelectionId"] = SelectionId.ToString() };
        foreach (var (key, value) in values)
        {
            parameters[key] = value;
        }

        return parameters;
    }

    private const string UnknownTokenSuffix =
        "' does not match a known country or subdivision. Use a country code (e.g. 'US') " +
        "or 'COUNTRY-STATE' (e.g. 'CH-BE') — use list_calendars or the calendar rules settings to find valid codes.";

    [Test]
    public async Task FullEdit_ExactMessageDataResourceOrderAndCallOrder()
    {
        var result = await _skill.ExecuteAsync(Ctx(), Params(
            ("newName", " Bern City "),
            ("removeCalendars", new List<string> { "ch-be" }),
            ("addCalendars", new List<string> { "DE", "US" }),
            ("reminderOnlyCalendars", new List<string> { "DE", "US" }),
            ("inheritCalendars", new List<string> { "CH" })));

        result.Success.ShouldBeTrue(result.Message);
        result.Message.ShouldBe(
            "Calendar selection 'Bern City' updated (name, removed CH-BE, added DE, reminder-only DE, reminder-only US, " +
            "inherit CH) and confirmed in the database (verified).");
        JsonSerializer.Serialize(result.Data).ShouldBe(
            "{\"CalendarSelectionId\":\"" + SelectionId + "\",\"ChangedFields\":[\"name\",\"removed CH-BE\",\"added DE\"," +
            "\"reminder-only DE\",\"reminder-only US\",\"inherit CH\"],\"Name\":\"Bern City\",\"Calendars\":[" +
            "{\"Country\":\"DE\",\"State\":\"DE\",\"OfficialOverride\":false}," +
            "{\"Country\":\"CH\",\"State\":\"CH\",\"OfficialOverride\":null}," +
            "{\"Country\":\"US\",\"State\":\"US\",\"OfficialOverride\":false}]}");
        _calls.ShouldBe(["list", "tokens", "transaction", "put", "reread"]);

        _sent!.Id.ShouldBe(SelectionId);
        _sent.Name.ShouldBe("Bern City");
        _sent.SelectedCalendars.Select(sc => (sc.Country, sc.State, sc.OfficialOverride))
            .ShouldBe([("DE", "DE", (bool?)false), ("CH", "CH", (bool?)null), ("US", "US", (bool?)false)]);
        _sent.SelectedCalendars.ShouldAllBe(sc => sc.Id == Guid.Empty && sc.CalendarSelectionId == Guid.Empty);
        await _calendarSelectionRepository.Received(1).GetNoTrackingWithSelectedCalendars(SelectionId);
    }

    [Test]
    public async Task UnchangedValues_AreNoOp_ExactMessageAndData_NoWrite()
    {
        var result = await _skill.ExecuteAsync(Ctx(), Params(
            ("newName", " Bern "),
            ("removeCalendars", new List<string> { "DE" }),
            ("addCalendars", new List<string> { "US" }),
            ("reminderOnlyCalendars", new List<string> { "CH" }),
            ("inheritCalendars", new List<string> { "US" })));

        result.Success.ShouldBeTrue(result.Message);
        result.Message.ShouldBe("No fields supplied for update — calendar selection left unchanged.");
        JsonSerializer.Serialize(result.Data).ShouldBe(
            "{\"CalendarSelectionId\":\"" + SelectionId + "\",\"ChangedFields\":[]}");
        _calls.ShouldBe(["list", "tokens"]);
    }

    [Test]
    public async Task NoOp_IsCheckedBeforeTheLastCalendarRule()
    {
        _existing = Selection();

        var result = await _skill.ExecuteAsync(Ctx(), Params());

        result.Success.ShouldBeTrue(result.Message);
        result.Message.ShouldBe("No fields supplied for update — calendar selection left unchanged.");
    }

    [Test]
    public async Task RemovingTheLastCalendar_NamesTheOldName_EvenWhenRenamed()
    {
        _existing = Selection(("CH", "BE", null));

        var result = await _skill.ExecuteAsync(Ctx(), Params(
            ("newName", "Neu"),
            ("removeCalendars", new List<string> { "CH-BE" })));

        result.Success.ShouldBeFalse();
        result.Message.ShouldBe(
            "Calendar selection 'Bern' must keep at least one calendar — refusing to remove the last one.");
        _calls.ShouldBe(["list", "tokens"]);
    }

    [Test]
    public async Task UnresolvedSelection_StopsBeforeTheTokenService()
    {
        var result = await _skill.ExecuteAsync(Ctx(), new Dictionary<string, object>
        {
            ["calendarSelectionId"] = Guid.Parse("99999999-9999-9999-9999-999999999999").ToString(),
            ["newName"] = "X"
        });

        result.Success.ShouldBeFalse();
        result.Message.ShouldBe("Calendar selection with ID '99999999-9999-9999-9999-999999999999' not found.");
        _calls.ShouldBe(["list"]);
    }

    [Test]
    public async Task RemoveTokenError_WinsOverAddTokenError()
    {
        var result = await _skill.ExecuteAsync(Ctx(), Params(
            ("removeCalendars", new List<string> { "XX" }),
            ("addCalendars", new List<string> { "YY" })));

        result.Success.ShouldBeFalse();
        result.Message.ShouldBe("'XX" + UnknownTokenSuffix);
        _calls.ShouldBe(["list", "tokens"]);
    }

    [Test]
    public async Task AddTokenError_WinsOverReminderTokenError()
    {
        var result = await _skill.ExecuteAsync(Ctx(), Params(
            ("addCalendars", new List<string> { "YY" }),
            ("reminderOnlyCalendars", new List<string> { "ZZ" })));

        result.Message.ShouldBe("'YY" + UnknownTokenSuffix);
    }

    [Test]
    public async Task ReminderTokenError_WinsOverInheritTokenError()
    {
        var result = await _skill.ExecuteAsync(Ctx(), Params(
            ("reminderOnlyCalendars", new List<string> { "ZZ" }),
            ("inheritCalendars", new List<string> { "QQ" })));

        result.Message.ShouldBe("'ZZ" + UnknownTokenSuffix);
    }

    [Test]
    public async Task InheritTokenError_WinsOverReminderNotInSelection()
    {
        _existing = Selection(("CH", "BE", null));

        var result = await _skill.ExecuteAsync(Ctx(), Params(
            ("reminderOnlyCalendars", new List<string> { "US" }),
            ("inheritCalendars", new List<string> { "QQ" })));

        result.Message.ShouldBe("'QQ" + UnknownTokenSuffix);
    }

    [Test]
    public async Task ReminderAndInheritConflict_NamesTheTrimmedRawToken_NotTheFormattedOne()
    {
        var result = await _skill.ExecuteAsync(Ctx(), Params(
            ("reminderOnlyCalendars", new List<string> { "CH-BE" }),
            ("inheritCalendars", new List<string> { " ch-be " })));

        result.Success.ShouldBeFalse();
        result.Message.ShouldBe("'ch-be' is listed in both reminderOnlyCalendars and inheritCalendars — pick one.");
    }

    [Test]
    public async Task ReminderNotInSelection_ExactText_WinsOverInheritNotInSelection()
    {
        _existing = Selection(("CH", "BE", null));

        var result = await _skill.ExecuteAsync(Ctx(), Params(
            ("reminderOnlyCalendars", new List<string> { "US" }),
            ("inheritCalendars", new List<string> { "DE" })));

        result.Success.ShouldBeFalse();
        result.Message.ShouldBe(
            "reminderOnlyCalendars entry 'US' is not part of this calendar selection — add it via addCalendars first.");
        _calls.ShouldBe(["list", "tokens"]);
    }

    [Test]
    public async Task InheritNotInSelection_ExactText()
    {
        _existing = Selection(("CH", "BE", null));

        var result = await _skill.ExecuteAsync(Ctx(), Params(
            ("inheritCalendars", new List<string> { "DE" })));

        result.Success.ShouldBeFalse();
        result.Message.ShouldBe("inheritCalendars entry 'DE' is not part of this calendar selection.");
    }

    [Test]
    public async Task PutReturningNull_ExactText()
    {
        _putReturnsNull = true;

        var result = await _skill.ExecuteAsync(Ctx(), Params(("newName", "Bern City")));

        result.Success.ShouldBeFalse();
        result.Message.ShouldBe("Calendar selection 'Bern City' could not be updated.");
        _calls.ShouldBe(["list", "tokens", "transaction", "put"]);
    }

    [Test]
    public async Task VerificationMismatch_ExactText()
    {
        _rereadMismatch = true;

        var result = await _skill.ExecuteAsync(Ctx(), Params(
            ("newName", "Bern City"),
            ("addCalendars", new List<string> { "DE" })));

        result.Success.ShouldBeFalse();
        result.Message.ShouldBe(
            "Database verification failed: the update (name, added DE) of calendar selection 'Bern City' could not be " +
            "confirmed in the database after the write — the change was rolled back.");
        _calls.ShouldBe(["list", "tokens", "transaction", "put", "reread"]);
    }
}
