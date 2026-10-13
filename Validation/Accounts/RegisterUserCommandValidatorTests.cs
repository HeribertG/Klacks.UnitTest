// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// The validator must not judge the password or the e-mail format: Identity's published policy and the handler do,
/// with reason codes. A stricter copy here once rejected a lower-case password with '#' that the policy allows.
/// </summary>

using Klacks.Api.Application.Commands.Accounts;
using Klacks.Api.Application.DTOs.Registrations;
using Klacks.Api.Application.Validation.Accounts;

namespace Klacks.UnitTest.Validation.Accounts;

[TestFixture]
public class RegisterUserCommandValidatorTests
{
    private readonly RegisterUserCommandValidator _validator = new();

    private static RegisterUserCommand Cmd(string password = "lowercase1#", string email = "user@example.com", string firstName = "Anna") =>
        new(new RegistrationResource
        {
            Email = email,
            FirstName = firstName,
            LastName = "Muster",
            Password = password,
            UserName = "amuster",
        });

    [TestCase("lowercase1#")]
    [TestCase("x")]
    [TestCase("")]
    public async Task Validate_LeavesThePasswordToIdentity(string password)
    {
        var result = await _validator.ValidateAsync(Cmd(password: password));

        result.IsValid.ShouldBeTrue();
    }

    [Test]
    public async Task Validate_LeavesTheEmailFormatToTheHandler()
    {
        var result = await _validator.ValidateAsync(Cmd(email: "no-at-sign"));

        result.IsValid.ShouldBeTrue();
    }

    [Test]
    public async Task Validate_MissingFirstName_FailsWithAnEnglishMessage()
    {
        var result = await _validator.ValidateAsync(Cmd(firstName: string.Empty));

        result.IsValid.ShouldBeFalse();
        result.Errors.ShouldContain(failure => failure.ErrorMessage == "The first name is required.");
    }
}
