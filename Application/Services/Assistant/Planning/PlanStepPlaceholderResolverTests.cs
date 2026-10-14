// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Unit tests for PlanStepPlaceholderResolver, which resolves the "$prev." placeholders of a plan step against the data of
/// the previous step. They pin the behaviour as it is: a member is looked up in a dictionary (with nullable or non-nullable
/// values) by the dictionary's own key comparison, otherwise as a public instance property ignoring case; a dotted path
/// walks nested members; a literal value is kept as it is; a placeholder that cannot be resolved (no previous result, unknown
/// member, null on the way, member value null) leaves the parameter out instead of passing the placeholder text on.
/// </summary>

using Klacks.Api.Application.Services.Assistant.Planning;

namespace Klacks.UnitTest.Application.Services.Assistant.Planning;

[TestFixture]
public class PlanStepPlaceholderResolverTests
{
    private static Dictionary<string, object?> Raw(params (string Key, object? Value)[] entries) =>
        entries.ToDictionary(e => e.Key, e => e.Value);

    private static Dictionary<int, object?> Results(params (int Step, object? Data)[] entries) =>
        entries.ToDictionary(e => e.Step, e => e.Data);

    [Test]
    public void ANullableValueDictionary_ResolvesTheKey()
    {
        var data = new Dictionary<string, object?> { ["clientId"] = "abc" };

        var resolved = PlanStepPlaceholderResolver.ResolveParameters(
            Raw(("client", "$prev.clientId")), Results((1, data)));

        resolved["client"].ShouldBe("abc");
    }

    [Test]
    public void ANonNullableValueDictionary_ResolvesTheKey()
    {
        var data = new Dictionary<string, object> { ["clientId"] = 42 };

        var resolved = PlanStepPlaceholderResolver.ResolveParameters(
            Raw(("client", "$prev.clientId")), Results((1, data)));

        resolved["client"].ShouldBe(42);
    }

    [Test]
    public void ANestedPath_WalksDictionariesAndProperties()
    {
        var id = Guid.NewGuid();
        var data = new Dictionary<string, object?>
        {
            ["client"] = new Dictionary<string, object> { ["address"] = new { City = "Bern", Id = id } }
        };

        var resolved = PlanStepPlaceholderResolver.ResolveParameters(
            Raw(("city", "$prev.client.address.city"), ("addressId", "$prev.client.address.id")), Results((1, data)));

        resolved["city"].ShouldBe("Bern");
        resolved["addressId"].ShouldBe(id);
    }

    [Test]
    public void APropertyOfAnObject_IsFoundIgnoringCase()
    {
        var id = Guid.NewGuid();

        var resolved = PlanStepPlaceholderResolver.ResolveParameters(
            Raw(("clientId", "$prev.CLIENTID"), ("name", "$prev.name")), Results((1, new { ClientId = id, Name = "Max" })));

        resolved["clientId"].ShouldBe(id);
        resolved["name"].ShouldBe("Max");
    }

    [Test]
    public void ADictionaryKey_FollowsTheComparerOfTheDictionary()
    {
        var caseSensitive = new Dictionary<string, object?> { ["clientId"] = "abc" };
        var caseInsensitive = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase) { ["clientId"] = "abc" };

        PlanStepPlaceholderResolver.ResolveParameters(Raw(("c", "$prev.CLIENTID")), Results((1, caseSensitive)))
            .ContainsKey("c").ShouldBeFalse();
        PlanStepPlaceholderResolver.ResolveParameters(Raw(("c", "$prev.CLIENTID")), Results((1, caseInsensitive)))["c"]
            .ShouldBe("abc");
    }

    [Test]
    public void AnUnknownMember_LeavesTheParameterOut_AndKeepsTheOthers()
    {
        var data = new Dictionary<string, object?> { ["clientId"] = "abc" };

        var resolved = PlanStepPlaceholderResolver.ResolveParameters(
            Raw(("missing", "$prev.doesNotExist"), ("client", "$prev.clientId")), Results((1, data)));

        resolved.ContainsKey("missing").ShouldBeFalse();
        resolved["client"].ShouldBe("abc");
    }

    [Test]
    public void ANullOnTheWay_LeavesTheParameterOut()
    {
        var data = new Dictionary<string, object?> { ["client"] = null };

        PlanStepPlaceholderResolver.ResolveParameters(Raw(("id", "$prev.client.id")), Results((1, data)))
            .ContainsKey("id").ShouldBeFalse();
    }

    [Test]
    public void AMemberWhoseValueIsNull_LeavesTheParameterOut()
    {
        var data = new Dictionary<string, object?> { ["clientId"] = null };

        PlanStepPlaceholderResolver.ResolveParameters(Raw(("id", "$prev.clientId")), Results((1, data)))
            .ContainsKey("id").ShouldBeFalse();
    }

    [Test]
    public void WithoutAnyPreviousResult_ThePlaceholderParameterIsLeftOut_NotPassedAsText()
    {
        var resolved = PlanStepPlaceholderResolver.ResolveParameters(
            Raw(("id", "$prev.clientId"), ("note", "keep")), Results());

        resolved.ContainsKey("id").ShouldBeFalse();
        resolved["note"].ShouldBe("keep");
    }

    [Test]
    public void APreviousResultThatIsNull_LeavesThePlaceholderParameterOut()
    {
        PlanStepPlaceholderResolver.ResolveParameters(Raw(("id", "$prev.clientId")), Results((1, null)))
            .ContainsKey("id").ShouldBeFalse();
    }

    [Test]
    public void OnlyTheResultOfTheHighestStepIsUsed()
    {
        var resolved = PlanStepPlaceholderResolver.ResolveParameters(
            Raw(("id", "$prev.id")),
            Results((1, new Dictionary<string, object?> { ["id"] = "first" }),
                (3, new Dictionary<string, object?> { ["id"] = "third" }),
                (2, new Dictionary<string, object?> { ["id"] = "second" })));

        resolved["id"].ShouldBe("third");
    }

    [Test]
    public void LiteralValues_AreKeptAsTheyAre()
    {
        var resolved = PlanStepPlaceholderResolver.ResolveParameters(
            Raw(("text", "plain"), ("count", 3), ("flag", true), ("inner", "x$prev.id"), ("upper", "$PREV.id")),
            Results((1, new Dictionary<string, object?> { ["id"] = "abc" })));

        resolved["text"].ShouldBe("plain");
        resolved["count"].ShouldBe(3);
        resolved["flag"].ShouldBe(true);
        resolved["inner"].ShouldBe("x$prev.id");
        resolved["upper"].ShouldBe("$PREV.id");
    }

    [Test]
    public void ALiteralNull_IsDropped()
    {
        PlanStepPlaceholderResolver.ResolveParameters(Raw(("empty", null)), Results()).ContainsKey("empty").ShouldBeFalse();
    }

    [Test]
    public void NoRawParameters_ResolveToNothing()
    {
        PlanStepPlaceholderResolver.ResolveParameters(Raw(), Results((1, new { Id = 1 }))).ShouldBeEmpty();
    }

    [Test]
    public void TheResolvedParameters_AreLookedUpIgnoringCase()
    {
        var resolved = PlanStepPlaceholderResolver.ResolveParameters(Raw(("ClientId", "x")), Results());

        resolved["clientid"].ShouldBe("x");
    }
}
