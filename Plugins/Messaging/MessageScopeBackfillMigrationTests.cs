// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Guards the frozen provider-type-to-MessengerType mapping hardcoded in the
/// AddMessageScopeAndClientIdBackfill migration's raw SQL CASE statement. The migration
/// intentionally snapshots the mapping as it stood when historical rows were written - it must
/// not be regenerated from the live enum on every run, or a later rename/renumber would silently
/// stop matching old data. This test is the safety net for that snapshot: if MessengerType is ever
/// renamed or renumbered, this test goes red and forces a conscious decision instead of the
/// migration silently producing wrong results.
/// </summary>
using Klacks.Plugin.Messaging.Domain.Enums;
using NUnit.Framework;
using Shouldly;

namespace Klacks.UnitTest.Plugins.Messaging;

[TestFixture]
public class MessageScopeBackfillMigrationTests
{
    // Mirrors the WHEN 'x' THEN n pairs in
    // Klacks.Api/Infrastructure/Persistence/Migrations/20260818140319_AddMessageScopeAndClientIdBackfill.cs
    // verbatim. Update both together if that migration's CASE statement is ever touched.
    private static readonly (string ProviderType, int ExpectedValue)[] MigrationCasePairs =
    {
        ("telegram", 1),
        ("whatsapp", 2),
        ("signal", 3),
        ("threema", 4),
        ("viber", 5),
        ("line", 6),
        ("kakaotalk", 7),
        ("wechat", 8),
        ("zalo", 9),
        ("microsoftteams", 10),
        ("slack", 11),
        ("sms", 12),
    };

    [TestCaseSource(nameof(MigrationCasePairs))]
    public void MigrationCasePair_MustMatchCurrentMessengerTypeEnum((string ProviderType, int ExpectedValue) pair)
    {
        var parsed = Enum.TryParse<MessengerType>(pair.ProviderType, ignoreCase: true, out var messengerType);

        parsed.ShouldBeTrue(
            $"MessengerType no longer has a member named '{pair.ProviderType}'. The historical backfill " +
            "migration hardcodes this name -> int mapping as a frozen snapshot; if it was renamed, decide " +
            "explicitly whether old data needs a new migration instead of silently breaking the backfill.");

        ((int)messengerType).ShouldBe(
            pair.ExpectedValue,
            $"MessengerType.{messengerType} was renumbered from {pair.ExpectedValue} to {(int)messengerType}. " +
            "The historical backfill migration hardcodes the old number as a frozen snapshot of the data " +
            "written at the time - renumbering the enum does not retroactively change stored messenger_contact.type values.");
    }
}
