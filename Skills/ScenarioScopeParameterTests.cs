// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// The optional analyseToken skill parameter: omitted or blank selects the main plan; a UUID selects that scenario;
/// garbage and the empty UUID are refused instead of silently falling back to the main plan.
/// </summary>

using Klacks.Api.Application.Skills;

namespace Klacks.UnitTest.Skills;

[TestFixture]
public class ScenarioScopeParameterTests
{
    [Test]
    public void Omitted_IsTheMainPlan()
    {
        ScenarioScopeParameter.TryRead(new Dictionary<string, object>(), out var token, out var error).ShouldBeTrue();
        token.ShouldBeNull();
        error.ShouldBeNull();
    }

    [TestCase("")]
    [TestCase("   ")]
    public void Blank_IsTheMainPlan(string value)
    {
        ScenarioScopeParameter.TryRead(Params(value), out var token, out _).ShouldBeTrue();
        token.ShouldBeNull();
    }

    [Test]
    public void AUuid_IsThatScenario()
    {
        var expected = Guid.NewGuid();

        ScenarioScopeParameter.TryRead(Params(expected.ToString()), out var token, out _).ShouldBeTrue();

        token.ShouldBe(expected);
    }

    [TestCase("not-a-uuid")]
    [TestCase("00000000-0000-0000-0000-000000000000")]
    public void GarbageAndTheEmptyUuid_AreRefused(string value)
    {
        ScenarioScopeParameter.TryRead(Params(value), out var token, out var error).ShouldBeFalse();

        token.ShouldBeNull();
        error.ShouldNotBeNull();
        error.ShouldContain(ScenarioScopeParameter.Name);
    }

    private static Dictionary<string, object> Params(string value)
        => new() { [ScenarioScopeParameter.Name] = value };
}
