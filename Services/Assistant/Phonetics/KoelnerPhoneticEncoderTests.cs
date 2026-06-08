// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Tests for the Cologne phonetics (Kölner Phonetik) encoder.
/// </summary>
namespace Klacks.UnitTest.Services.Assistant.Phonetics;

using Shouldly;
using Klacks.Api.Domain.Services.Assistant.Phonetics;
using NUnit.Framework;

[TestFixture]
public class KoelnerPhoneticEncoderTests
{
    private KoelnerPhoneticEncoder _encoder;

    [SetUp]
    public void SetUp()
    {
        _encoder = new KoelnerPhoneticEncoder();
    }

    [Test]
    public void Encode_ShouldGiveSameCode_ForMeierAndMeyer()
    {
        _encoder.Encode("Meier").ShouldBe(_encoder.Encode("Meyer"));
    }

    [Test]
    public void Encode_Mueller_ShouldBe657()
    {
        _encoder.Encode("Müller").ShouldBe("657");
    }

    [Test]
    public void Encode_ShouldGiveSameCode_ForKlacksyAndKlaxi()
    {
        var klacksy = _encoder.Encode("Klacksy");
        var klaxi = _encoder.Encode("Klaxi");

        klacksy.ShouldBe(klaxi);
        klacksy.ShouldBe("4548");
    }

    [Test]
    public void Encode_ShouldReturnEmpty_ForEmptyInput()
    {
        _encoder.Encode("").ShouldBe(string.Empty);
    }
}
