// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Owner decision 2026-10-05: a NEW calendar rule is paid unless the caller says otherwise. The default applies on
/// CREATE only - an update body without isPaid must keep the stored value, otherwise an unpaid rule would silently
/// turn paid (or a paid one unpaid). An explicit value always wins.
/// </summary>

using System.Text.Json;
using Klacks.Api.Application.DTOs.Settings;
using Klacks.Api.Application.Mappers;
using Klacks.Api.Domain.Interfaces.Translation;
using Microsoft.Extensions.Logging;
using CalendarRulePostCommand = Klacks.Api.Application.Commands.Settings.CalendarRules.PostCommand;
using CalendarRulePostHandler = Klacks.Api.Application.Handlers.Settings.CalendarRules.PostCommandHandler;
using CalendarRulePutCommand = Klacks.Api.Application.Commands.PutCommand<Klacks.Api.Application.DTOs.Settings.CalendarRuleResource>;
using CalendarRulePutHandler = Klacks.Api.Application.Handlers.Settings.CalendarRules.PutCommandHandler;

namespace Klacks.UnitTest.Application.Handlers.CalendarRules;

[TestFixture]
public class CalendarRuleIsPaidDefaultTests
{
    private const string RequestWithoutIsPaid = """{ "country": "CH", "state": "ZH", "rule": "12/25", "isMandatory": true }""";
    private const string RequestUnpaid = """{ "country": "CH", "state": "ZH", "rule": "03/19", "isMandatory": true, "isPaid": false }""";

    private static readonly JsonSerializerOptions WebOptions = new(JsonSerializerDefaults.Web);

    private ISettingsRepository _settingsRepository = null!;
    private IMultiLanguageTranslationService _translationService = null!;

    [SetUp]
    public void SetUp()
    {
        _settingsRepository = Substitute.For<ISettingsRepository>();
        _settingsRepository.AddCalendarRule(Arg.Any<CalendarRule>()).Returns(call => call.Arg<CalendarRule>());
        _translationService = Substitute.For<IMultiLanguageTranslationService>();
        _translationService.IsConfiguredAsync().Returns(false);
    }

    [Test]
    public void NewEntity_IsPaid()
    {
        new CalendarRule().IsPaid.ShouldBeTrue();
    }

    [Test]
    public void RequestWithoutIsPaid_LeavesTheResourceValueUnset()
    {
        Deserialize(RequestWithoutIsPaid).IsPaid.ShouldBeNull();
    }

    [Test]
    public async Task Post_RequestWithoutIsPaid_StoresAPaidRule()
    {
        await CreatePostHandler().Handle(new CalendarRulePostCommand(Deserialize(RequestWithoutIsPaid)), CancellationToken.None);

        _settingsRepository.Received(1).AddCalendarRule(Arg.Is<CalendarRule>(rule => rule.IsPaid));
    }

    [Test]
    public async Task Post_ExplicitlyUnpaid_StoresAnUnpaidRule()
    {
        await CreatePostHandler().Handle(new CalendarRulePostCommand(Deserialize(RequestUnpaid)), CancellationToken.None);

        _settingsRepository.Received(1).AddCalendarRule(Arg.Is<CalendarRule>(rule => !rule.IsPaid));
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task Put_RequestWithoutIsPaid_KeepsTheStoredValue(bool storedIsPaid)
    {
        var stored = StoredRule(storedIsPaid);
        var resource = Deserialize(RequestWithoutIsPaid);
        resource.Id = stored.Id;

        var result = await CreatePutHandler().Handle(new CalendarRulePutCommand(resource), CancellationToken.None);

        stored.IsPaid.ShouldBe(storedIsPaid);
        result!.IsPaid.ShouldBe(storedIsPaid);
    }

    [Test]
    public async Task Put_ExplicitlyUnpaid_UpdatesAPaidRule()
    {
        var stored = StoredRule(isPaid: true);
        var resource = Deserialize(RequestUnpaid);
        resource.Id = stored.Id;

        await CreatePutHandler().Handle(new CalendarRulePutCommand(resource), CancellationToken.None);

        stored.IsPaid.ShouldBeFalse();
    }

    private CalendarRule StoredRule(bool isPaid)
    {
        var stored = new CalendarRule { Id = Guid.NewGuid(), Country = "CH", State = "ZH", Rule = "03/19", IsMandatory = true, IsPaid = isPaid };
        _settingsRepository.GetCalendarRule(stored.Id).Returns(stored);
        return stored;
    }

    private static CalendarRuleResource Deserialize(string json) =>
        JsonSerializer.Deserialize<CalendarRuleResource>(json, WebOptions)!;

    private CalendarRulePostHandler CreatePostHandler() => new(
        _settingsRepository, new ScheduleMapper(), Substitute.For<IUnitOfWork>(), _translationService,
        Substitute.For<IHolidayCalculatorCache>(), Substitute.For<ILogger<CalendarRulePostHandler>>());

    private CalendarRulePutHandler CreatePutHandler() => new(
        _settingsRepository, new ScheduleMapper(), Substitute.For<IUnitOfWork>(), _translationService,
        Substitute.For<IHolidayCalculatorCache>(), Substitute.For<ILogger<CalendarRulePutHandler>>());
}
