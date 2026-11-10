// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Pins the setup-code comparison of the anonymous first start: dashes, whitespace and case do not matter, a missing
/// stored code or a missing input never matches, any other difference does not match.
/// </summary>

using Klacks.Api.Domain.Helpers;

namespace Klacks.UnitTest.Domain.Helpers;

[TestFixture]
public class FirstStartSetupCodeTests
{
    private const string StoredCode = "ABCD-EFGH-JK23";

    [TestCase("ABCD-EFGH-JK23")]
    [TestCase("abcd-efgh-jk23")]
    [TestCase(" ABCDEFGHJK23 ")]
    [TestCase("ABCD EFGH JK23")]
    public void Matches_TheStoredCodeInAnyNotation(string supplied)
    {
        FirstStartSetupCode.Matches(StoredCode, supplied).ShouldBeTrue();
    }

    [TestCase("ABCD-EFGH-JK24")]
    [TestCase("ABCD-EFGH-JK2")]
    [TestCase("ABCD-EFGH-JK233")]
    [TestCase("")]
    [TestCase("   ")]
    [TestCase(null)]
    public void DoesNotMatch_AnyOtherInput(string? supplied)
    {
        FirstStartSetupCode.Matches(StoredCode, supplied).ShouldBeFalse();
    }

    [TestCase(null)]
    [TestCase("")]
    [TestCase(" - ")]
    public void NoStoredCode_IsNotConfiguredAndMatchesNothing(string? stored)
    {
        FirstStartSetupCode.IsConfigured(stored).ShouldBeFalse();
        FirstStartSetupCode.Matches(stored, stored).ShouldBeFalse();
        FirstStartSetupCode.Matches(stored, string.Empty).ShouldBeFalse();
    }

    [Test]
    public void OverlongInput_NeverMatches()
    {
        var overlong = StoredCode + new string(' ', FirstStartSetupCode.MaxSuppliedLength);

        FirstStartSetupCode.Matches(StoredCode, overlong).ShouldBeFalse();
    }

    [Test]
    public void StoredCode_IsConfigured()
    {
        FirstStartSetupCode.IsConfigured(StoredCode).ShouldBeTrue();
    }
}
