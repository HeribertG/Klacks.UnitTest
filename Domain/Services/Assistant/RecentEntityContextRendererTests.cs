// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Tests for RecentEntityContextRenderer: an empty or null list renders no block (empty string), a row
/// with a display name renders a quoted line carrying id and action, a row without a display name renders
/// an unquoted line, and multiple rows keep their given (newest-first) order.
/// </summary>

using Klacks.Api.Domain.Models.Assistant;
using Klacks.Api.Domain.Services.Assistant;
using NUnit.Framework;
using Shouldly;

namespace Klacks.UnitTest.Domain.Services.Assistant;

[TestFixture]
public class RecentEntityContextRendererTests
{
    private static RecentEntityRow Row(string type, Guid id, string? name, string action) => new()
    {
        Id = Guid.NewGuid(),
        UserId = Guid.NewGuid(),
        ConversationId = "c1",
        EntityType = type,
        EntityId = id,
        DisplayName = name,
        Action = action,
        CreatedAtUtc = DateTime.UtcNow
    };

    [Test]
    public void EmptyList_renders_no_block()
    {
        RecentEntityContextRenderer.Render(new List<RecentEntityRow>()).ShouldBe(string.Empty);
    }

    [Test]
    public void NullList_renders_no_block()
    {
        RecentEntityContextRenderer.Render(null).ShouldBe(string.Empty);
    }

    [Test]
    public void RowWithName_renders_quoted_line_with_id_and_action()
    {
        var id = Guid.NewGuid();
        var block = RecentEntityContextRenderer.Render(new[] { Row("shift", id, "Frühdienst", "created") });

        block.ShouldContain("[RECENTLY_TOUCHED]");
        block.ShouldContain($"- shift \"Frühdienst\" (id {id}) — created");
    }

    [Test]
    public void RowWithoutName_renders_unquoted_line()
    {
        var id = Guid.NewGuid();
        var block = RecentEntityContextRenderer.Render(new[] { Row("membership", id, null, "updated") });

        block.ShouldContain($"- membership (id {id}) — updated");
        block.ShouldNotContain("\"\"");
    }

    [Test]
    public void MultipleRows_keep_given_order()
    {
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();
        var block = RecentEntityContextRenderer.Render(new[]
        {
            Row("shift", first, "A", "created"),
            Row("client", second, "B", "updated")
        });

        block.IndexOf(first.ToString(), StringComparison.Ordinal)
            .ShouldBeLessThan(block.IndexOf(second.ToString(), StringComparison.Ordinal));
    }
}
