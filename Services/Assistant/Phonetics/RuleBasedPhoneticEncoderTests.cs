// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Tests for the data-driven rule-based phonetic encoder.
/// </summary>
namespace Klacks.UnitTest.Services.Assistant.Phonetics;

using Shouldly;
using Klacks.Api.Domain.Models.Assistant;
using Klacks.Api.Domain.Services.Assistant.Phonetics;
using NUnit.Framework;

[TestFixture]
public class RuleBasedPhoneticEncoderTests
{
    private static readonly List<PhoneticRule> GermanRules =
    [
        new() { From = "ck", To = "k" },
        new() { From = "x", To = "ks" },
        new() { From = "y", To = "i" },
    ];

    [Test]
    public void Encode_ShouldGiveSameCode_ForKlacksyAndKlaxi()
    {
        var encoder = new RuleBasedPhoneticEncoder(GermanRules);

        encoder.Encode("Klacksy").ShouldBe(encoder.Encode("Klaxi"));
    }

    [Test]
    public void Encode_ShouldStripNonLettersAndLowercase()
    {
        var encoder = new RuleBasedPhoneticEncoder([]);

        encoder.Encode("Ab-Cd 12").ShouldBe("abcd");
    }

    [Test]
    public void Encode_ShouldCollapseConsecutiveDuplicates()
    {
        var encoder = new RuleBasedPhoneticEncoder([]);

        encoder.Encode("Muller").ShouldBe("muler");
    }
}
