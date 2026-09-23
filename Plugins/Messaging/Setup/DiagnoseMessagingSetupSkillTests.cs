// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Tests for DiagnoseMessagingSetupSkill: the skill name matches the seed and the result carries the
/// diagnosis report unchanged with a count of providers that need action.
/// </summary>
using System.Reflection;
using Klacks.Plugin.Contracts.Skills;
using SkillExecutionContext = Klacks.Plugin.Contracts.Skills.SkillExecutionContext;
using Klacks.Plugin.Messaging.Application.Constants;
using Klacks.Plugin.Messaging.Application.Interfaces;
using Klacks.Plugin.Messaging.Domain.Enums;
using Klacks.Plugin.Messaging.Domain.Models.Setup;
using Klacks.Plugin.Messaging.Skills;
using NSubstitute;
using NUnit.Framework;
using Shouldly;

namespace Klacks.UnitTest.Plugins.Messaging.Setup;

[TestFixture]
public class DiagnoseMessagingSetupSkillTests
{
    private IMessagingSetupDiagnosticsService _diagnostics = null!;
    private DiagnoseMessagingSetupSkill _sut = null!;
    private SkillExecutionContext _context = null!;

    [SetUp]
    public void SetUp()
    {
        _diagnostics = Substitute.For<IMessagingSetupDiagnosticsService>();
        _sut = new DiagnoseMessagingSetupSkill(_diagnostics);
        _context = new SkillExecutionContext
        {
            UserId = Guid.NewGuid(),
            TenantId = Guid.NewGuid(),
            UserName = "admin",
            UserPermissions = Array.Empty<string>(),
        };
    }

    [Test]
    public void SkillImplementationAttribute_UsesSeededSkillName()
    {
        var attribute = typeof(DiagnoseMessagingSetupSkill).GetCustomAttribute<SkillImplementationAttribute>();

        attribute.ShouldNotBeNull();
        attribute!.SkillName.ShouldBe("diagnose_messaging_setup");
    }

    [Test]
    public async Task ExecuteAsync_ReturnsReportUnchangedAndCountsProvidersNeedingAction()
    {
        var openStep = new SetupStep(SetupStepCodes.WebhookUrl, SetupStepStatus.Error, nameof(WebhookUrlVerdict.NotPublic));
        var report = new MessagingSetupReport(
            [new SetupStep(SetupStepCodes.ProviderPresent, SetupStepStatus.Ok)],
            [
                new ProviderSetupReport("telegram-main", MessagingConstants.ProviderTelegram, true, [openStep], openStep),
                new ProviderSetupReport("sms-main", MessagingConstants.ProviderSms, true, [], null),
            ]);
        _diagnostics.DiagnoseAsync(Arg.Any<CancellationToken>()).Returns(report);

        var result = await _sut.ExecuteAsync(_context, new Dictionary<string, object>());

        result.Success.ShouldBeTrue();
        result.Data.ShouldBeSameAs(report);
        result.Message.ShouldBe("2 messaging provider(s) diagnosed, 1 need(s) action.");
    }

    [Test]
    public async Task ExecuteAsync_PassesCancellationTokenToDiagnosis()
    {
        using var cts = new CancellationTokenSource();
        _diagnostics.DiagnoseAsync(Arg.Any<CancellationToken>()).Returns(new MessagingSetupReport([], []));

        await _sut.ExecuteAsync(_context, new Dictionary<string, object>(), cts.Token);

        await _diagnostics.Received(1).DiagnoseAsync(cts.Token);
    }
}
