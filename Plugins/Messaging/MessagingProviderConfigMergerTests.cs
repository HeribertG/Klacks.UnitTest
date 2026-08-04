// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Tests that updating a messaging provider never silently drops stored credentials.
/// MessagingProviderDto omits ConfigJson on purpose, so a client can only ever send back the
/// fields it managed to fill in. The two real callers both send partial payloads: the row toggle
/// sends an empty string, and the edit dialog sends only the auto-filled webhook URL. Before the
/// merge existed, either one replaced the whole configuration and wiped the bot token.
/// </summary>
using Klacks.Plugin.Messaging.Application.Services;
using NUnit.Framework;
using Shouldly;

namespace Klacks.UnitTest.Plugins.Messaging;

[TestFixture]
public class MessagingProviderConfigMergerTests
{
    private const string StoredSlackConfig =
        """{"BotToken":"xoxb-secret","SigningSecret":"sign-secret","DefaultChannel":"#general"}""";

    [Test]
    public void TryMerge_EmptyIncoming_KeepsStoredConfiguration()
    {
        var succeeded = MessagingProviderConfigMerger.TryMerge(StoredSlackConfig, string.Empty, out var merged);

        succeeded.ShouldBeTrue();
        merged.ShouldBe(StoredSlackConfig);
    }

    [Test]
    public void TryMerge_PartialIncoming_KeepsUnmentionedSecrets()
    {
        const string incoming = """{"WebhookUrl":"https://example.test/api/messaging/webhook/slack"}""";

        var succeeded = MessagingProviderConfigMerger.TryMerge(StoredSlackConfig, incoming, out var merged);

        succeeded.ShouldBeTrue();
        merged.ShouldContain("xoxb-secret");
        merged.ShouldContain("sign-secret");
        merged.ShouldContain("https://example.test/api/messaging/webhook/slack");
    }

    [Test]
    public void TryMerge_IncomingKey_OverwritesStoredValue()
    {
        const string incoming = """{"BotToken":"xoxb-rotated"}""";

        var succeeded = MessagingProviderConfigMerger.TryMerge(StoredSlackConfig, incoming, out var merged);

        succeeded.ShouldBeTrue();
        merged.ShouldContain("xoxb-rotated");
        merged.ShouldNotContain("xoxb-secret");
        merged.ShouldContain("sign-secret");
    }

    [Test]
    public void TryMerge_NoStoredConfiguration_TakesIncomingAsIs()
    {
        const string incoming = """{"BotToken":"xoxb-new"}""";

        var succeeded = MessagingProviderConfigMerger.TryMerge(null, incoming, out var merged);

        succeeded.ShouldBeTrue();
        merged.ShouldContain("xoxb-new");
    }

    [Test]
    public void TryMerge_MalformedIncoming_FailsInsteadOfDiscardingStoredConfiguration()
    {
        var succeeded = MessagingProviderConfigMerger.TryMerge(StoredSlackConfig, "not json", out _);

        succeeded.ShouldBeFalse();
    }

    [Test]
    public void TryMerge_IncomingJsonArray_FailsBecauseConfigurationMustBeAnObject()
    {
        var succeeded = MessagingProviderConfigMerger.TryMerge(StoredSlackConfig, "[1,2,3]", out _);

        succeeded.ShouldBeFalse();
    }

    [Test]
    public void TryMerge_MalformedStoredConfiguration_StillAcceptsIncoming()
    {
        const string incoming = """{"BotToken":"xoxb-new"}""";

        var succeeded = MessagingProviderConfigMerger.TryMerge("not json", incoming, out var merged);

        succeeded.ShouldBeTrue();
        merged.ShouldContain("xoxb-new");
    }
}
