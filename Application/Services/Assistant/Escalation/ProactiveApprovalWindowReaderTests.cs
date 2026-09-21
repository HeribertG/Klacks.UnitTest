// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// PROACTIVE_APPROVAL_WINDOW_MINUTES parsing and the deadline arithmetic built on it: a missing,
/// unparsable or non-positive row falls back to the default rather than to a window nobody could
/// answer within, and an empty roster still yields a well-formed one-window deadline.
/// </summary>

using Klacks.Api.Application.Services.Assistant.Escalation;
using Klacks.Api.Domain.Interfaces.Settings;
using Klacks.Api.Domain.Services.Assistant;
using SettingKeys = Klacks.Api.Application.Constants.Settings;
using SettingsEntity = Klacks.Api.Domain.Models.Settings.Settings;

namespace Klacks.UnitTest.Application.Services.Assistant.Escalation;

[TestFixture]
public class ProactiveApprovalWindowReaderTests
{
    private static readonly DateTime NowUtc = new(2026, 9, 21, 22, 0, 0, DateTimeKind.Utc);

    private ISettingsReader _settingsReader = null!;

    [SetUp]
    public void SetUp()
    {
        _settingsReader = Substitute.For<ISettingsReader>();
    }

    [Test]
    public async Task ReadMinutes_NoRow_ReturnsDefault()
    {
        _settingsReader.GetSetting(SettingKeys.PROACTIVE_APPROVAL_WINDOW_MINUTES).Returns((SettingsEntity?)null);

        var minutes = await ProactiveApprovalWindowReader.ReadMinutesAsync(_settingsReader, CancellationToken.None);

        Assert.That(minutes, Is.EqualTo(ProactiveApprovalWindowReader.DefaultWindowMinutes));
    }

    [Test]
    public async Task ReadMinutes_ConfiguredRow_ReturnsConfiguredValue()
    {
        _settingsReader.GetSetting(SettingKeys.PROACTIVE_APPROVAL_WINDOW_MINUTES)
            .Returns(new SettingsEntity { Type = SettingKeys.PROACTIVE_APPROVAL_WINDOW_MINUTES, Value = "45" });

        var minutes = await ProactiveApprovalWindowReader.ReadMinutesAsync(_settingsReader, CancellationToken.None);

        Assert.That(minutes, Is.EqualTo(45));
    }

    [TestCase("0")]
    [TestCase("-5")]
    [TestCase("abc")]
    [TestCase("")]
    public async Task ReadMinutes_InvalidRow_FallsBackToDefault(string raw)
    {
        _settingsReader.GetSetting(SettingKeys.PROACTIVE_APPROVAL_WINDOW_MINUTES)
            .Returns(new SettingsEntity { Type = SettingKeys.PROACTIVE_APPROVAL_WINDOW_MINUTES, Value = raw });

        var minutes = await ProactiveApprovalWindowReader.ReadMinutesAsync(_settingsReader, CancellationToken.None);

        Assert.That(minutes, Is.EqualTo(ProactiveApprovalWindowReader.DefaultWindowMinutes));
    }

    [Test]
    public void Deadline_ThreeStages_IsNowPlusThreeWindows()
    {
        var deadline = ProactiveApprovalDeadline.Compute(NowUtc, 3, 30);

        Assert.That(deadline, Is.EqualTo(NowUtc.AddMinutes(90)));
    }

    [Test]
    public void Deadline_EmptyRoster_StillGetsOneWindow()
    {
        var deadline = ProactiveApprovalDeadline.Compute(NowUtc, 0, 30);

        Assert.That(deadline, Is.EqualTo(NowUtc.AddMinutes(30)));
    }
}
