// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// LanguagePluginCode accepts only what can safely become one directory name of the language pack directory.
/// </summary>

using Klacks.Api.Domain.Common;

namespace Klacks.UnitTest.Domain.Common;

[TestFixture]
public class LanguagePluginCodeTests
{
    [TestCase("xx")]
    [TestCase("zh-CN")]
    [TestCase("sr_Latn")]
    [TestCase("A1")]
    public void IsValid_LettersDigitsUnderscoreAndHyphen_ReturnsTrue(string code)
    {
        LanguagePluginCode.IsValid(code).ShouldBeTrue();
    }

    [TestCase(null)]
    [TestCase("")]
    [TestCase(" ")]
    [TestCase("../xx")]
    [TestCase("..")]
    [TestCase("a/b")]
    [TestCase("a\\b")]
    [TestCase("C:\\xx")]
    [TestCase("/etc")]
    [TestCase("xx\n")]
    [TestCase("x x")]
    [TestCase("xx.json")]
    [TestCase("\u00e4\u00f6")]
    public void IsValid_AnythingElse_ReturnsFalse(string? code)
    {
        LanguagePluginCode.IsValid(code).ShouldBeFalse();
    }

    [Test]
    public void IsValid_LongerThanTheMaximum_ReturnsFalse()
    {
        LanguagePluginCode.IsValid(new string('a', LanguagePluginCode.MaxLength)).ShouldBeTrue();
        LanguagePluginCode.IsValid(new string('a', LanguagePluginCode.MaxLength + 1)).ShouldBeFalse();
    }
}
