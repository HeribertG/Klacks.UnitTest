// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

using Klacks.Api.Domain.Helpers;

namespace Klacks.UnitTest.Domain.Helpers;

[TestFixture]
public class EmailAddressFormatTests
{
    [TestCase("anna.muster@example.com", "anna.muster@example.com")]
    [TestCase("  anna@example.ch  ", "anna@example.ch")]
    [TestCase("jürgen@example.com", "jürgen@example.com")]
    [TestCase("anna@bücher.de", "anna@bücher.de")]
    public void TryNormalize_PlainAddress_IsAcceptedAndTrimmed(string input, string expected)
    {
        EmailAddressFormat.TryNormalize(input, out var normalized).ShouldBeTrue();
        normalized.ShouldBe(expected);
    }

    [TestCase(null)]
    [TestCase("")]
    [TestCase("   ")]
    [TestCase("anna")]
    [TestCase("anna@")]
    [TestCase("Anna Muster <anna@example.com>")]
    [TestCase("anna@example.com, ben@example.com")]
    public void TryNormalize_AnythingButOnePlainAddress_IsRejected(string? input)
    {
        EmailAddressFormat.TryNormalize(input, out var normalized).ShouldBeFalse();
        normalized.ShouldBeEmpty();
    }
}
