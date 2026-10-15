// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

using FluentValidation.Results;
using Klacks.Api.Application.Commands.Assistant;
using Klacks.Api.Application.Validation.Assistant;

namespace Klacks.UnitTest.Validation.Assistant;

[TestFixture]
public class CancelEscalationChainCommandValidatorTests
{
    private const string ReasonRequiredMessage = "A reason is required to cancel an escalation chain.";

    private CancelEscalationChainCommandValidator _validator = null!;

    [SetUp]
    public void Setup()
    {
        _validator = new CancelEscalationChainCommandValidator();
    }

    [TestCase("")]
    [TestCase(" ")]
    [TestCase("\t\n ")]
    public async Task Validate_ShouldBeInvalid_WhenReasonIsBlank(string reason)
    {
        var result = await _validator.ValidateAsync(Command(reason));

        Assert.That(result.IsValid, Is.False);
        Assert.That(result.Errors, Has.Some.Matches<ValidationFailure>(f => f.ErrorMessage == ReasonRequiredMessage));
    }

    [Test]
    public async Task Validate_ShouldBeInvalid_WhenReasonIsNull()
    {
        var result = await _validator.ValidateAsync(Command(null!));

        Assert.That(result.IsValid, Is.False);
    }

    [Test]
    public async Task Validate_ShouldBeValid_WhenReasonHasText()
    {
        var result = await _validator.ValidateAsync(Command("Shift was covered by phone"));

        Assert.That(result.IsValid, Is.True);
    }

    private static CancelEscalationChainCommand Command(string reason) =>
        new(Guid.NewGuid(), "planner-a", "Planner A", reason);
}
