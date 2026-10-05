// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Guards that every calendar rule write drops all cached holiday calculators after the commit. A rule can be
/// part of many calendar selections, so a change to e.g. IsMandatory must not leave holiday-work warnings and
/// holiday surcharges on the old answer until the next restart.
/// </summary>

using Klacks.Api.Application.DTOs.Settings;
using Klacks.Api.Application.Mappers;
using Klacks.Api.Domain.Interfaces.Translation;
using Microsoft.Extensions.Logging;
using CalendarRuleDeleteCommand = Klacks.Api.Application.Commands.Settings.CalendarRules.DeleteCommand;
using CalendarRuleDeleteHandler = Klacks.Api.Application.Handlers.Settings.CalendarRule.DeleteCommandHandler;
using CalendarRulePostCommand = Klacks.Api.Application.Commands.Settings.CalendarRules.PostCommand;
using CalendarRulePostHandler = Klacks.Api.Application.Handlers.Settings.CalendarRules.PostCommandHandler;
using CalendarRulePutCommand = Klacks.Api.Application.Commands.PutCommand<Klacks.Api.Application.DTOs.Settings.CalendarRuleResource>;
using CalendarRulePutHandler = Klacks.Api.Application.Handlers.Settings.CalendarRules.PutCommandHandler;

namespace Klacks.UnitTest.Application.Handlers.CalendarRules;

[TestFixture]
public class CalendarRuleHolidayCacheInvalidationTests
{
    private ISettingsRepository _settingsRepository = null!;
    private IUnitOfWork _unitOfWork = null!;
    private IMultiLanguageTranslationService _translationService = null!;
    private IHolidayCalculatorCache _holidayCache = null!;

    [SetUp]
    public void SetUp()
    {
        _settingsRepository = Substitute.For<ISettingsRepository>();
        _unitOfWork = Substitute.For<IUnitOfWork>();
        _translationService = Substitute.For<IMultiLanguageTranslationService>();
        _translationService.IsConfiguredAsync().Returns(false);
        _holidayCache = Substitute.For<IHolidayCalculatorCache>();
    }

    [Test]
    public async Task Post_CommitsThenInvalidatesAllHolidayCalculators()
    {
        _settingsRepository.AddCalendarRule(Arg.Any<CalendarRule>()).Returns(call => call.Arg<CalendarRule>());
        var handler = new CalendarRulePostHandler(
            _settingsRepository, new ScheduleMapper(), _unitOfWork, _translationService, _holidayCache,
            Substitute.For<ILogger<CalendarRulePostHandler>>());

        await handler.Handle(new CalendarRulePostCommand(CreateResource(null)), CancellationToken.None);

        Received.InOrder(() =>
        {
            _unitOfWork.CompleteAsync();
            _holidayCache.InvalidateAll();
        });
    }

    [Test]
    public async Task Put_ExistingRule_CommitsThenInvalidatesAllHolidayCalculators()
    {
        var id = Guid.NewGuid();
        _settingsRepository.GetCalendarRule(id).Returns(new CalendarRule { Id = id, Rule = "12.25", IsMandatory = true });
        var handler = CreatePutHandler();

        var result = await handler.Handle(new CalendarRulePutCommand(CreateResource(id)), CancellationToken.None);

        result.ShouldNotBeNull();
        result.IsMandatory.ShouldBeFalse();
        Received.InOrder(() =>
        {
            _unitOfWork.CompleteAsync();
            _holidayCache.InvalidateAll();
        });
    }

    [Test]
    public async Task Put_UnknownRule_DoesNotInvalidate()
    {
        var id = Guid.NewGuid();
        _settingsRepository.GetCalendarRule(id).Returns((CalendarRule)null!);
        var handler = CreatePutHandler();

        var result = await handler.Handle(new CalendarRulePutCommand(CreateResource(id)), CancellationToken.None);

        result.ShouldBeNull();
        _holidayCache.DidNotReceive().InvalidateAll();
    }

    [Test]
    public async Task Put_MissingId_DoesNotInvalidate()
    {
        var handler = CreatePutHandler();

        var result = await handler.Handle(new CalendarRulePutCommand(CreateResource(null)), CancellationToken.None);

        result.ShouldBeNull();
        _holidayCache.DidNotReceive().InvalidateAll();
    }

    [Test]
    public async Task Delete_ExistingRule_CommitsThenInvalidatesAllHolidayCalculators()
    {
        var id = Guid.NewGuid();
        _settingsRepository.DeleteCalendarRule(id).Returns(new CalendarRule { Id = id });
        var handler = CreateDeleteHandler();

        var result = await handler.Handle(new CalendarRuleDeleteCommand(id), CancellationToken.None);

        result.ShouldNotBeNull();
        Received.InOrder(() =>
        {
            _unitOfWork.CompleteAsync();
            _holidayCache.InvalidateAll();
        });
    }

    [Test]
    public async Task Delete_UnknownRule_DoesNotInvalidate()
    {
        var id = Guid.NewGuid();
        _settingsRepository.DeleteCalendarRule(id).Returns((CalendarRule)null!);
        var handler = CreateDeleteHandler();

        await handler.Handle(new CalendarRuleDeleteCommand(id), CancellationToken.None);

        _holidayCache.DidNotReceive().InvalidateAll();
    }

    private CalendarRulePutHandler CreatePutHandler() => new(
        _settingsRepository, new ScheduleMapper(), _unitOfWork, _translationService, _holidayCache,
        Substitute.For<ILogger<CalendarRulePutHandler>>());

    private CalendarRuleDeleteHandler CreateDeleteHandler() => new(
        _settingsRepository, _unitOfWork, _holidayCache,
        Substitute.For<ILogger<CalendarRuleDeleteHandler>>());

    private static CalendarRuleResource CreateResource(Guid? id) => new()
    {
        Id = id,
        Country = "CH",
        State = string.Empty,
        Rule = "12.25",
        SubRule = string.Empty,
        Name = new MultiLanguage { En = "Christmas Day" },
        Description = MultiLanguage.Empty(),
        IsMandatory = false,
        IsPaid = true
    };
}
