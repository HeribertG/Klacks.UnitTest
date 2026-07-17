// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Tests for CompensatoryRestEvaluator (K12): open obligations surface as due/overdue feed entries,
/// block-mode escalates to Error with the enforcement tag, and the feature/scenario guards suppress
/// output.
/// </summary>

using Klacks.Api.Application.Interfaces.Schedules;
using Klacks.Api.Domain.Constants;
using Klacks.Api.Domain.Enums;
using Klacks.Api.Domain.Interfaces.Scheduling;
using Klacks.Api.Domain.Interfaces.Settings;
using Klacks.Api.Domain.Models.Scheduling;
using Klacks.Api.Infrastructure.Services.Schedules;
using NSubstitute;
using NUnit.Framework;
using Shouldly;
using SettingsModel = Klacks.Api.Domain.Models.Settings.Settings;

namespace Klacks.UnitTest.Infrastructure.Services.Schedules;

[TestFixture]
public class CompensatoryRestEvaluatorTests
{
    private static readonly DateOnly Today = new(2026, 6, 20);

    private ICompensatoryRestObligationRepository _repository = null!;
    private IComplianceEnforcementResolver _enforcementResolver = null!;
    private ISettingsReader _settingsReader = null!;
    private CompensatoryRestEvaluator _sut = null!;

    [SetUp]
    public void SetUp()
    {
        _repository = Substitute.For<ICompensatoryRestObligationRepository>();
        _repository.GetOpenByClientAsync(Arg.Any<Guid>()).Returns(new List<CompensatoryRestObligation>());

        _enforcementResolver = Substitute.For<IComplianceEnforcementResolver>();
        _enforcementResolver.GetModeAsync(ComplianceRuleNames.CompensatoryRest).Returns(RuleEnforcementMode.Warn);

        _settingsReader = Substitute.For<ISettingsReader>();
        SetEnabled(true);

        _sut = new CompensatoryRestEvaluator(_repository, _enforcementResolver, _settingsReader);
    }

    [Test]
    public async Task Evaluate_OpenObligationWithinDeadline_ReportsDueWarning()
    {
        var clientId = Guid.NewGuid();
        StubOpen(clientId, triggerDate: Today.AddDays(-2), dueDate: Today.AddDays(5), shortfall: 3m);

        var result = await _sut.EvaluateAsync(clientId, "Anna", Today);

        var entry = result.ShouldHaveSingleItem();
        entry.Type.ShouldBe(ScheduleValidationType.Warning);
        entry.Comment.ShouldBe(ScheduleValidationKeys.CompensatoryRestDue);
        entry.Date.ShouldBe(Today.AddDays(-2));
        entry.CommentParams["shortfallHours"].ShouldBe("3.0");
        entry.CommentParams["triggerDate"].ShouldBe("2026-06-18");
        entry.CommentParams["dueDate"].ShouldBe("2026-06-25");
    }

    [Test]
    public async Task Evaluate_OverduePastDeadline_ReportsOverdueKey()
    {
        var clientId = Guid.NewGuid();
        StubOpen(clientId, triggerDate: Today.AddDays(-10), dueDate: Today.AddDays(-1), shortfall: 2m);

        var result = await _sut.EvaluateAsync(clientId, "Anna", Today);

        var entry = result.ShouldHaveSingleItem();
        entry.Comment.ShouldBe(ScheduleValidationKeys.CompensatoryRestOverdue);
    }

    [Test]
    public async Task Evaluate_BlockMode_EscalatesToErrorWithEnforcementTag()
    {
        _enforcementResolver.GetModeAsync(ComplianceRuleNames.CompensatoryRest).Returns(RuleEnforcementMode.Block);
        var clientId = Guid.NewGuid();
        StubOpen(clientId, triggerDate: Today.AddDays(-2), dueDate: Today.AddDays(5), shortfall: 3m);

        var result = await _sut.EvaluateAsync(clientId, "Anna", Today);

        var entry = result.ShouldHaveSingleItem();
        entry.Type.ShouldBe(ScheduleValidationType.Error);
        entry.CommentParams[ComplianceRuleNames.EnforcementRuleParamKey].ShouldBe(ComplianceRuleNames.CompensatoryRest);
    }

    [Test]
    public async Task Evaluate_NoOpenObligations_ReturnsEmpty()
    {
        var result = await _sut.EvaluateAsync(Guid.NewGuid(), "Anna", Today);

        result.ShouldBeEmpty();
    }

    [Test]
    public async Task Evaluate_FeatureDisabled_ReturnsEmpty()
    {
        SetEnabled(false);
        var clientId = Guid.NewGuid();
        StubOpen(clientId, triggerDate: Today.AddDays(-2), dueDate: Today.AddDays(5), shortfall: 3m);

        var result = await _sut.EvaluateAsync(clientId, "Anna", Today);

        result.ShouldBeEmpty();
    }

    [Test]
    public async Task Evaluate_ScenarioToken_ReturnsEmpty()
    {
        var clientId = Guid.NewGuid();
        StubOpen(clientId, triggerDate: Today.AddDays(-2), dueDate: Today.AddDays(5), shortfall: 3m);

        var result = await _sut.EvaluateAsync(clientId, "Anna", Today, Guid.NewGuid());

        result.ShouldBeEmpty();
    }

    private void StubOpen(Guid clientId, DateOnly triggerDate, DateOnly dueDate, decimal shortfall)
    {
        _repository.GetOpenByClientAsync(clientId).Returns(new List<CompensatoryRestObligation>
        {
            new()
            {
                Id = Guid.NewGuid(),
                ClientId = clientId,
                TriggerDate = triggerDate,
                RestGapStart = triggerDate.ToDateTime(new TimeOnly(20, 0)),
                ShortfallHours = shortfall,
                StandardRestHours = 11m,
                DueDate = dueDate,
            },
        });
    }

    private void SetEnabled(bool enabled) =>
        _settingsReader.GetSetting(SettingKeys.ComplianceCompensatoryRestEnabled)
            .Returns(new SettingsModel { Type = SettingKeys.ComplianceCompensatoryRestEnabled, Value = enabled ? "true" : "false" });
}
