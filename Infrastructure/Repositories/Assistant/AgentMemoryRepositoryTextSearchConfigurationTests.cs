// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Regression guard for the PostgreSQL full text search configuration used by AgentMemoryRepository's
/// hybrid and text-only memory search. Klacks memories carry no per-row language column and can be
/// written in any of up to 21 supported languages, so the configuration must stay language-neutral
/// ("simple", no dictionary stemming) instead of being hardcoded to a single language such as "german" —
/// a German stemmer mis-parses non-German content and loses matches. The raw SQL itself runs only
/// against PostgreSQL, so this reflection check locks the constant instead of re-executing the query.
/// </summary>

using System.Reflection;
using Klacks.Api.Infrastructure.Repositories.Assistant;

namespace Klacks.UnitTest.Infrastructure.Repositories.Assistant;

[TestFixture]
public class AgentMemoryRepositoryTextSearchConfigurationTests
{
    private const string ExpectedConfiguration = "simple";

    [Test]
    public void TextSearchConfiguration_IsLanguageNeutral_NotHardcodedToASingleLanguage()
    {
        var field = typeof(AgentMemoryRepository).GetField(
            "TextSearchConfiguration", BindingFlags.NonPublic | BindingFlags.Static);

        Assert.That(field, Is.Not.Null, "AgentMemoryRepository must define a named TextSearchConfiguration constant.");
        Assert.That(field!.IsLiteral, Is.True, "TextSearchConfiguration must be a compile-time constant.");

        var value = field.GetRawConstantValue() as string;

        Assert.That(value, Is.EqualTo(ExpectedConfiguration),
            "The PostgreSQL text search configuration must stay language-neutral ('simple') because " +
            "memories have no per-row language column and can be written in any supported language.");
    }
}
