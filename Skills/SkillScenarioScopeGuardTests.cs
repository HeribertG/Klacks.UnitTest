// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// SkillScenarioScopeGuard refuses only a well-formed analyseToken of a scenario the caller may not see; no token
/// (main plan) and a malformed token (reported by the skill itself) pass through untouched.
/// </summary>

using Klacks.Api.Application.Skills;
using Klacks.UnitTest.TestHelpers;

namespace Klacks.UnitTest.Skills;

[TestFixture]
public class SkillScenarioScopeGuardTests
{
    private readonly Guid _hiddenToken = Guid.NewGuid();

    private SkillScenarioScopeGuard Guard() => new(TestScenarioTokenAccess.Hiding(_hiddenToken));

    [Test]
    public async Task HiddenToken_IsRefusedLikeAnUnknownScenario()
    {
        var result = await Guard().CheckAsync(new Dictionary<string, object> { ["analyseToken"] = _hiddenToken.ToString() });

        result.ShouldNotBeNull();
        result.Success.ShouldBeFalse();
        result.Message.ShouldBe($"No scenario found for analyseToken '{_hiddenToken}'.");
    }

    [Test]
    public async Task VisibleToken_PassesThrough()
    {
        var result = await Guard().CheckAsync(new Dictionary<string, object> { ["analyseToken"] = Guid.NewGuid().ToString() });

        result.ShouldBeNull();
    }

    [Test]
    public async Task NoToken_PassesThrough()
    {
        (await Guard().CheckAsync(new Dictionary<string, object>())).ShouldBeNull();
    }

    [Test]
    public async Task MalformedToken_IsLeftToTheSkill()
    {
        (await Guard().CheckAsync(new Dictionary<string, object> { ["analyseToken"] = "not-a-guid" })).ShouldBeNull();
    }
}
