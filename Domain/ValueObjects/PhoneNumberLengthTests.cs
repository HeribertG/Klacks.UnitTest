// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Pins PhoneNumber.IsValid: a number is valid when its normalized form (separators removed) is 7 to 15
/// characters long, both ends inclusive.
/// </summary>

using Klacks.Api.Domain.ValueObjects;

namespace Klacks.UnitTest.Domain.ValueObjects;

[TestFixture]
public class PhoneNumberLengthTests
{
    [TestCase("123456", false)]
    [TestCase("1234567", true)]
    [TestCase("123456789012345", true)]
    [TestCase("1234567890123456", false)]
    [TestCase("+41 79 123 45 67", true)]
    [TestCase("(044) 123-45.67", true)]
    [TestCase("12 34 56", false)]
    [TestCase("   ", false)]
    public void IsValid_AcceptsSevenToFifteenNormalizedCharacters(string input, bool expected)
    {
        PhoneNumber.IsValid(input).ShouldBe(expected);
    }
}
