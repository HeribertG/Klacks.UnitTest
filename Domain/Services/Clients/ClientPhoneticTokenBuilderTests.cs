// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Unit tests for ClientPhoneticTokenBuilder — verifies that common German surname variants
/// ("Meier"/"Mayer"/"Maier", "Schmidt"/"Schmitt") collapse to the same Kölner Phonetik code,
/// multi-word names yield one code per word, and empty input yields null.
/// </summary>

using Klacks.Api.Domain.Models.Staffs;
using Klacks.Api.Domain.Services.Clients;

namespace Klacks.UnitTest.Domain.Services.Clients;

[TestFixture]
public class ClientPhoneticTokenBuilderTests
{
    [TestCase("Meier", "Mayer")]
    [TestCase("Meier", "Maier")]
    [TestCase("Meier", "Meyer")]
    [TestCase("Schmidt", "Schmitt")]
    [TestCase("Müller", "Mueller")]
    public void Build_SurnameVariants_YieldSameCode(string left, string right)
    {
        var leftCode = ClientPhoneticTokenBuilder.Build(left);
        var rightCode = ClientPhoneticTokenBuilder.Build(right);

        leftCode.ShouldNotBeNull();
        leftCode.ShouldBe(rightCode);
    }

    [Test]
    public void Build_DistinctNames_YieldDistinctCodes()
    {
        ClientPhoneticTokenBuilder.Build("Meier").ShouldNotBe(ClientPhoneticTokenBuilder.Build("Zimmermann"));
    }

    [Test]
    public void Build_MultiWordName_YieldsOneCodePerWord()
    {
        var codes = ClientPhoneticTokenBuilder.Build("Petra", "Meier");

        codes.ShouldNotBeNull();
        codes!.Split(' ').Length.ShouldBe(2);
    }

    [Test]
    public void Build_DuplicateWords_AreDeduplicated()
    {
        var codes = ClientPhoneticTokenBuilder.Build("Meier", "Meier");

        codes.ShouldNotBeNull();
        codes!.Split(' ').Length.ShouldBe(1);
    }

    [TestCase(null, null)]
    [TestCase("", "   ")]
    public void Build_EmptyInput_ReturnsNull(string? first, string? second)
    {
        ClientPhoneticTokenBuilder.Build(first, second).ShouldBeNull();
    }

    [Test]
    public void BuildFor_UsesAllNameFields()
    {
        var client = new Client
        {
            Name = "Meier",
            FirstName = "Petra",
            SecondName = "Luise",
            MaidenName = "Schmidt"
        };

        var codes = ClientPhoneticTokenBuilder.BuildFor(client);

        codes.ShouldNotBeNull();
        codes!.Split(' ').Length.ShouldBe(4);
    }
}
