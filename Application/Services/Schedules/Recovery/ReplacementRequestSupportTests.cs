// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Tests for the small parts of the replacement request book: which phone number is offered (mobile before
/// fixed line, never the emergency number), that a hidden employee never gets a number, the settings fallbacks
/// (48 h short notice, 730 days retention) and the short-notice rule.
/// </summary>

using System.Text.Json;
using Klacks.Api.Application.DTOs.Schedules;
using Klacks.Api.Application.Services.Schedules.Recovery;
using Klacks.Api.Domain.Services.Schedules;
using SettingKeys = Klacks.Api.Application.Constants.Settings;
using SettingsEntity = Klacks.Api.Domain.Models.Settings.Settings;

namespace Klacks.UnitTest.Application.Services.Schedules.Recovery;

[TestFixture]
public class ReplacementRequestSupportTests
{
    private static readonly Guid VisibleId = Guid.NewGuid();
    private static readonly Guid HiddenId = Guid.NewGuid();

    [Test]
    public void PhoneSelector_PrefersMobileOverFixedLine_AndNeverOffersTheEmergencyNumber()
    {
        var entries = new[]
        {
            Phone(VisibleId, CommunicationTypeEnum.EmergencyPhone, "111"),
            Phone(VisibleId, CommunicationTypeEnum.PrivateFixPhone, "044 000 00 00"),
            Phone(VisibleId, CommunicationTypeEnum.OfficeCellPhone, "079 222 22 22"),
        };

        PreferredPhoneSelector.Select(entries).ShouldBe("079 222 22 22");
        PreferredPhoneSelector.Select([Phone(VisibleId, CommunicationTypeEnum.EmergencyPhone, "111")]).ShouldBeNull();
    }

    [Test]
    public void PhoneSelector_PutsTheStoredPrefixInFrontOnce()
    {
        var withPrefix = Phone(VisibleId, CommunicationTypeEnum.PrivateCellPhone, "79 333 33 33");
        withPrefix.Prefix = "+41";
        var alreadyPrefixed = Phone(VisibleId, CommunicationTypeEnum.PrivateCellPhone, "+41 79 333 33 33");
        alreadyPrefixed.Prefix = "+41";

        PreferredPhoneSelector.Select([withPrefix]).ShouldBe("+41 79 333 33 33");
        PreferredPhoneSelector.Select([alreadyPrefixed]).ShouldBe("+41 79 333 33 33");
    }

    [Test]
    public async Task PhoneResolver_HiddenEmployee_GetsNoNumberAndIsNeverQueried()
    {
        var repository = Substitute.For<ICommunicationRepository>();
        repository.GetPhoneEntriesAsync(Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(ci => ci.ArgAt<IReadOnlyCollection<Guid>>(0)
                .Select(id => Phone(id, CommunicationTypeEnum.PrivateCellPhone, "079 444 44 44"))
                .ToList());
        var guard = Substitute.For<IClientVisibilityGuard>();
        guard.FilterVisibleAsync(Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<Func<Guid, Guid>>(), Arg.Any<CancellationToken>())
            .Returns(ci => ci.ArgAt<IReadOnlyCollection<Guid>>(0).Where(id => id != HiddenId).ToList());

        var phones = await new ReplacementContactPhoneResolver(repository, guard).ResolveAsync([VisibleId, HiddenId]);

        phones.Keys.ShouldBe([VisibleId]);
        await repository.Received(1).GetPhoneEntriesAsync(
            Arg.Is<IReadOnlyCollection<Guid>>(ids => !ids.Contains(HiddenId)), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task SettingsReader_FallsBackTo48HoursAnd730Days_WhenTheSettingIsMissingOrInvalid()
    {
        var reader = Substitute.For<ISettingsReader>();
        reader.GetSetting(SettingKeys.REPLACEMENT_SHORT_NOTICE_HOURS).Returns((SettingsEntity?)null);
        reader.GetSetting(SettingKeys.REPLACEMENT_REQUEST_RETENTION_DAYS)
            .Returns(new SettingsEntity { Type = SettingKeys.REPLACEMENT_REQUEST_RETENTION_DAYS, Value = "-5" });

        (await ReplacementRequestSettingsReader.ReadShortNoticeHoursAsync(reader)).ShouldBe(48);
        (await ReplacementRequestSettingsReader.ReadRetentionDaysAsync(reader)).ShouldBe(730);
    }

    [Test]
    public async Task SettingsReader_ReadsConfiguredValues()
    {
        var reader = Substitute.For<ISettingsReader>();
        reader.GetSetting(SettingKeys.REPLACEMENT_SHORT_NOTICE_HOURS)
            .Returns(new SettingsEntity { Type = SettingKeys.REPLACEMENT_SHORT_NOTICE_HOURS, Value = "12" });
        reader.GetSetting(SettingKeys.REPLACEMENT_REQUEST_RETENTION_DAYS)
            .Returns(new SettingsEntity { Type = SettingKeys.REPLACEMENT_REQUEST_RETENTION_DAYS, Value = "365" });

        (await ReplacementRequestSettingsReader.ReadShortNoticeHoursAsync(reader)).ShouldBe(12);
        (await ReplacementRequestSettingsReader.ReadRetentionDaysAsync(reader)).ShouldBe(365);
    }

    [Test]
    public void ShortNotice_IsStrictlyLessThanTheThreshold()
    {
        var start = new DateTime(2026, 3, 10, 7, 0, 0, DateTimeKind.Utc);

        ReplacementShortNoticePolicy.IsShortNotice(start.AddHours(-47), start, 48).ShouldBeTrue();
        ReplacementShortNoticePolicy.IsShortNotice(start.AddHours(-48), start, 48).ShouldBeFalse();
    }

    [Test]
    public void Dtos_SerializeOutcomeAndSourceAsStrings_ForTheUi()
    {
        var options = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
        var slot = new CoveredSlot(Guid.NewGuid(), new DateOnly(2026, 3, 10), Guid.NewGuid(), "Bob", Outcome: ReplacementRequestOutcome.Proposed);

        JsonSerializer.Serialize(slot, options).ShouldContain("\"outcome\":\"Proposed\"");
        JsonSerializer.Deserialize<SetReplacementOutcomeRequest>("{\"outcome\":\"NotReached\"}", options)!
            .Outcome.ShouldBe(ReplacementRequestOutcome.NotReached);
    }
    private static Communication Phone(Guid clientId, CommunicationTypeEnum type, string value)
        => new() { Id = Guid.NewGuid(), ClientId = clientId, Type = type, Value = value };
}
