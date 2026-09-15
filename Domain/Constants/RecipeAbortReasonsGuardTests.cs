// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Guards the abort-reason constants against the column cap. RecipeRunRecorder.Truncate cuts at
/// RecipeRunDefaults.AbortReasonMaxLength with a hard slice and no ellipsis, so a reason longer than the
/// cap is not merely untidy - it is silently truncated into something nobody can filter on later.
/// Reflection over the constants, not a hand-written list, so a new reason is covered by the act of
/// being added.
/// </summary>

using System.Reflection;
using Klacks.Api.Domain.Constants;

namespace Klacks.UnitTest.Domain.Constants;

[TestFixture]
public class RecipeAbortReasonsGuardTests
{
    private static List<(string Name, string? Value)> PublicReasons() =>
        typeof(RecipeAbortReasons)
            .GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(field => field.FieldType == typeof(string))
            .Select(field => (field.Name, field.GetRawConstantValue() as string))
            .ToList();

    [Test]
    public void EveryAbortReason_IsNonEmptyAndFitsTheColumnCap()
    {
        var offenders = PublicReasons()
            .Where(reason => string.IsNullOrWhiteSpace(reason.Value)
                             || reason.Value!.Length > RecipeRunDefaults.AbortReasonMaxLength)
            .ToList();

        offenders.ShouldBeEmpty(
            $"every abort reason must be non-empty and at most {RecipeRunDefaults.AbortReasonMaxLength} " +
            "characters, because RecipeRunRecorder.Truncate cuts with a hard slice and a truncated reason " +
            "is nothing anybody can filter on later. Offenders: " +
            string.Join(", ", offenders.Select(o => $"{o.Name}={o.Value?.Length ?? 0}")));
    }

    /// <summary>
    /// Without this the cap test passes vacantly on an emptied class, which is the failure mode a guard
    /// test must not have.
    /// </summary>
    [Test]
    public void TheReasonCatalogue_IsNotEmpty()
    {
        PublicReasons().ShouldNotBeEmpty();
    }
}
