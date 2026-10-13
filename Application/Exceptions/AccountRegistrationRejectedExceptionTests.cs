// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

using Klacks.Api.Application.Exceptions;
using Klacks.Api.Domain.Constants;
using Microsoft.AspNetCore.Identity;

namespace Klacks.UnitTest.Application.Exceptions;

[TestFixture]
public class AccountRegistrationRejectedExceptionTests
{
    [Test]
    public void FromIdentityErrors_KeepsEachCodeOnceInOrderAndJoinsTheDescriptions()
    {
        var describer = new IdentityErrorDescriber();
        var tooShort = describer.PasswordTooShort(PasswordPolicyDefaults.RequiredLength);
        var digit = describer.PasswordRequiresDigit();

        var ex = AccountRegistrationRejectedException.FromIdentityErrors([tooShort, digit, tooShort]);

        ex.Reasons.ShouldBe([AccountRegistrationErrorCodes.PasswordTooShort, AccountRegistrationErrorCodes.PasswordRequiresDigit]);
        ex.Message.ShouldContain(tooShort.Description);
        ex.Message.ShouldContain(digit.Description);
    }

    [Test]
    public void FromIdentityErrors_NoErrors_GivesNoReasonsAndTheGenericMessage()
    {
        var ex = AccountRegistrationRejectedException.FromIdentityErrors(null);

        ex.Reasons.ShouldBeEmpty();
        ex.Message.ShouldBe(AccountRegistrationMessages.Rejected);
    }

    [Test]
    public void FromReasons_BlankCodesAndDescriptions_AreDropped()
    {
        var ex = AccountRegistrationRejectedException.FromReasons(
            ["", " ", AccountRegistrationErrorCodes.DuplicateEmail],
            ["", "  "]);

        ex.Reasons.ShouldBe([AccountRegistrationErrorCodes.DuplicateEmail]);
        ex.Message.ShouldBe(AccountRegistrationMessages.Rejected);
    }

    [Test]
    public void InvalidEmail_CarriesTheInvalidEmailReason()
    {
        var ex = AccountRegistrationRejectedException.InvalidEmail();

        ex.Reasons.ShouldBe([AccountRegistrationErrorCodes.InvalidEmail]);
        ex.Message.ShouldBe(AccountRegistrationMessages.InvalidEmail);
    }
}
