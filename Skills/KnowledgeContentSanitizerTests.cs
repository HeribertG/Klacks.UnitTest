// Copyright (c) Heribert Gasparoli Private. All rights reserved.

using Klacks.Api.Domain.Services.Assistant;

namespace Klacks.UnitTest.Skills;

[TestFixture]
public class KnowledgeContentSanitizerTests
{
    [Test]
    public void ReplacesInternalNameInProse()
    {
        var result = KnowledgeContentSanitizer.Sanitize("Eine SealedOrder ist unveränderlich.");

        Assert.That(result, Is.EqualTo("Eine versiegelte Bestellung ist unveränderlich."));
    }

    [Test]
    public void PreservesNameInsideInlineCode()
    {
        var input = "Der Status `SealedOrder` ist nur ein interner Anker.";

        var result = KnowledgeContentSanitizer.Sanitize(input);

        Assert.That(result, Is.EqualTo(input));
    }

    [Test]
    public void PreservesNamesInsideCodeFence()
    {
        var input = "Vorher\n```\nupdatedShift.Status == SealedOrder\n```\nNachher.";

        var result = KnowledgeContentSanitizer.Sanitize(input);

        Assert.That(result, Is.EqualTo(input));
    }

    [Test]
    public void SanitizesProseButKeepsBacktickedIdOnSameLine()
    {
        var input = "Der Toggle `schedule-break-placeholder-toggle` betrifft Break-Placeholder.";

        var result = KnowledgeContentSanitizer.Sanitize(input);

        Assert.That(result, Does.Contain("`schedule-break-placeholder-toggle`"));
        Assert.That(result, Does.Contain("vorgeplante Absenz"));
        Assert.That(result, Does.Not.Contain("betrifft Break-Placeholder"));
    }

    [Test]
    public void ReplacesNameOnLineWithUnbalancedBacktick()
    {
        var input = "Hinweis: eine offene `Klammer und dann SealedOrder im Text.";

        var result = KnowledgeContentSanitizer.Sanitize(input);

        Assert.That(result, Does.Contain("versiegelte Bestellung"));
        Assert.That(result, Does.Not.Contain("SealedOrder"));
    }

    [Test]
    public void ReplacesInternalNameRegardlessOfCasing()
    {
        var result = KnowledgeContentSanitizer.Sanitize("Eine sealedorder bleibt eine sealedorder.");

        Assert.That(result, Does.Contain("versiegelte Bestellung"));
        Assert.That(result, Does.Not.Contain("sealedorder"));
    }

    [Test]
    public void ContentWithoutInternalNames_ReturnsSameReference()
    {
        var input = "Eine ganz normale Erklärung ohne interne Namen.";

        var result = KnowledgeContentSanitizer.Sanitize(input);

        Assert.That(result, Is.SameAs(input));
    }

    [Test]
    public void NullOrEmpty_ReturnsEmpty()
    {
        Assert.That(KnowledgeContentSanitizer.Sanitize(null), Is.Empty);
        Assert.That(KnowledgeContentSanitizer.Sanitize(string.Empty), Is.Empty);
    }
}
