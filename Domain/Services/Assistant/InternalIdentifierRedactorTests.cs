// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Guards the last user-visible path that bypasses the model: the recipe-step-failed notice appends a
/// raw skill result, which can carry internal snake_case skill names (e.g. "Skill 'x_y' not found" from
/// SkillExecutorService). No prompt rule can reach that text, so the redactor must strip the names while
/// leaving the actionable part of the message (real group names, counts) intact.
/// </summary>

using Klacks.Api.Domain.Constants;
using Klacks.Api.Domain.Services.Assistant;

namespace Klacks.UnitTest.Domain.Services.Assistant;

[TestFixture]
public class InternalIdentifierRedactorTests
{
    [TestCase("Error: Skill 'group_ungrouped_by_city_name' not found")]
    [TestCase("Function 'search_employees' is not available.")]
    [TestCase("Error executing add_client_to_nearest_group: timeout")]
    [TestCase("Use fill_group_by_criteria first, then propose_grouping.")]
    public void Redact_RemovesInternalSkillNames(string rawResult)
    {
        var redacted = InternalIdentifierRedactor.Redact(rawResult);

        redacted.ShouldNotContain("_");
        redacted.ShouldContain(MutationGuardConstants.RedactedInternalIdentifier);
    }

    [Test]
    public void Redact_KeepsActionableBusinessContent()
    {
        const string rawResult = "Error: Available groups: Zürich, Bern, St. Gallen (3 matches).";

        var redacted = InternalIdentifierRedactor.Redact(rawResult);

        redacted.ShouldBe(rawResult);
    }

    [Test]
    public void Redact_ReplacesEveryOccurrence()
    {
        const string rawResult = "list_groups failed, then list_groups failed again.";

        var redacted = InternalIdentifierRedactor.Redact(rawResult);

        redacted.ShouldBe(
            $"{MutationGuardConstants.RedactedInternalIdentifier} failed, then " +
            $"{MutationGuardConstants.RedactedInternalIdentifier} failed again.");
    }

    [TestCase(null)]
    [TestCase("")]
    public void Redact_ReturnsEmpty_ForMissingText(string? rawResult)
    {
        InternalIdentifierRedactor.Redact(rawResult).ShouldBe(string.Empty);
    }

    [Test]
    public void RecipeStepFailedNotice_ComposedWithRedactor_ExposesNoInternalName()
    {
        const string rawResult = "Error: Skill 'group_ungrouped_by_city_name' not found";

        var notice = MutationGuardConstants.RecipeStepFailedNoticePrefix
            + InternalIdentifierRedactor.Redact(rawResult);

        notice.ShouldNotContain("group_ungrouped_by_city_name");
        notice.ShouldStartWith(MutationGuardConstants.RecipeStepFailedNoticePrefix);
    }
}
