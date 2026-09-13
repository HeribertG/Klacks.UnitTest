// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Tests for SkillNameHumanizer: separator handling, whitespace collapsing, capitalisation of the
/// first letter and the empty-input contract.
/// </summary>

using Klacks.Api.Domain.Services.Assistant;
using NUnit.Framework;
using Shouldly;

namespace Klacks.UnitTest.Domain.Services.Assistant;

[TestFixture]
public class SkillNameHumanizerTests
{
    [TestCase("get_page_controls", "Get page controls")]
    [TestCase("create-client", "Create client")]
    [TestCase("  list_clients  ", "List clients")]
    [TestCase("list__clients", "List clients")]
    [TestCase("navigate", "Navigate")]
    public void ToDisplayName_ReplacesSeparatorsAndCapitalisesTheFirstLetter(string input, string expected)
    {
        SkillNameHumanizer.ToDisplayName(input).ShouldBe(expected);
    }

    [TestCase(null)]
    [TestCase("")]
    [TestCase("   ")]
    [TestCase("___")]
    public void ToDisplayName_WithoutAnyUsableCharacter_ReturnsAnEmptyString(string? input)
    {
        SkillNameHumanizer.ToDisplayName(input).ShouldBe(string.Empty);
    }
}
