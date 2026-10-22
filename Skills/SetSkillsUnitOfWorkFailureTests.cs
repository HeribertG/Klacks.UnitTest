// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Pins what set_client_qualification, set_shift_required_qualification and set_shift_preferences do when their command
/// fails. The commands persist through IUnitOfWork.CompleteAsync, which turns every EF Core write failure into the Domain
/// DatabaseUpdateException (unknown employee/shift/qualification = foreign-key violation) or ConcurrencyException
/// (Klacks.IntegrationTest UnitOfWorkFailureTranslationTests proves it on PostgreSQL). A foreign-key violation becomes the
/// skill's friendly error message; every other database failure and a concurrency failure escape unchanged. A
/// KeyNotFoundException (caller may not see the employee/shift) becomes the skill's error message in the two qualification
/// skills and escapes set_shift_preferences.
/// </summary>

using Klacks.Api.Application.Commands.ClientShiftPreferences;
using Klacks.Api.Application.Commands.Qualifications;
using Klacks.Api.Application.Skills;
using Klacks.Api.Domain.Exceptions;
using Klacks.Api.Infrastructure.Mediator;
using NSubstitute.ExceptionExtensions;

namespace Klacks.UnitTest.Skills;

[TestFixture]
public class SetSkillsUnitOfWorkFailureTests
{
    private const string ClientQualificationError = "Could not set the qualification — the employee or qualification id may not exist.";
    private const string RequiredQualificationError = "Could not set the required qualification — the shift or qualification id may not exist.";
    private const string PreferenceError = "Could not set the preference — the employee or shift id may not exist.";

    private static readonly Guid ClientId = Guid.NewGuid();
    private static readonly Guid ShiftId = Guid.NewGuid();
    private static readonly Guid QualificationId = Guid.NewGuid();

    private static IEnumerable<TestCaseData> EscapingUnitOfWorkFailures()
    {
        yield return new TestCaseData((Func<Exception>)(() =>
            new DatabaseUpdateException("duplicate key value", isDuplicate: true)))
            .SetName("{m}_DuplicateKey");
        yield return new TestCaseData((Func<Exception>)(() => new DatabaseUpdateException("database error")))
            .SetName("{m}_OtherDatabaseFailure");
        yield return new TestCaseData((Func<Exception>)(() => new ConcurrencyException("modified by another user")))
            .SetName("{m}_ConcurrencyFailure");
    }

    private static DatabaseUpdateException ForeignKeyViolation() =>
        new("insert or update violates foreign key constraint", isForeignKeyViolation: true);

    [Test]
    public async Task SetClientQualification_ForeignKeyViolation_ReturnsTheErrorMessage()
    {
        var mediator = Substitute.For<IMediator>();
        mediator.Send(Arg.Any<SetClientQualificationCommand>(), Arg.Any<CancellationToken>()).ThrowsAsync(ForeignKeyViolation());

        var result = await new SetClientQualificationSkill(mediator).ExecuteAsync(Ctx(), ClientQualificationParameters());

        result.Success.ShouldBeFalse();
        result.Message.ShouldBe(ClientQualificationError);
    }

    [Test]
    public async Task SetShiftRequiredQualification_ForeignKeyViolation_ReturnsTheErrorMessage()
    {
        var mediator = Substitute.For<IMediator>();
        mediator.Send(Arg.Any<SetShiftRequiredQualificationCommand>(), Arg.Any<CancellationToken>()).ThrowsAsync(ForeignKeyViolation());

        var result = await new SetShiftRequiredQualificationSkill(mediator).ExecuteAsync(Ctx(), RequiredQualificationParameters());

        result.Success.ShouldBeFalse();
        result.Message.ShouldBe(RequiredQualificationError);
    }

    [Test]
    public async Task SetShiftPreferences_ForeignKeyViolation_ReturnsTheErrorMessage()
    {
        var mediator = Substitute.For<IMediator>();
        mediator.Send(Arg.Any<SetShiftPreferenceCommand>(), Arg.Any<CancellationToken>()).ThrowsAsync(ForeignKeyViolation());

        var result = await new SetShiftPreferencesSkill(mediator).ExecuteAsync(Ctx(), PreferenceParameters());

        result.Success.ShouldBeFalse();
        result.Message.ShouldBe(PreferenceError);
    }

    [TestCaseSource(nameof(EscapingUnitOfWorkFailures))]
    public async Task SetClientQualification_OtherUnitOfWorkFailure_Escapes(Func<Exception> failure)
    {
        var mediator = Substitute.For<IMediator>();
        mediator.Send(Arg.Any<SetClientQualificationCommand>(), Arg.Any<CancellationToken>()).ThrowsAsync(failure());

        var thrown = await Should.ThrowAsync<Exception>(() => new SetClientQualificationSkill(mediator).ExecuteAsync(Ctx(), ClientQualificationParameters()));

        thrown.GetType().ShouldBe(failure().GetType());
    }

    [TestCaseSource(nameof(EscapingUnitOfWorkFailures))]
    public async Task SetShiftRequiredQualification_OtherUnitOfWorkFailure_Escapes(Func<Exception> failure)
    {
        var mediator = Substitute.For<IMediator>();
        mediator.Send(Arg.Any<SetShiftRequiredQualificationCommand>(), Arg.Any<CancellationToken>()).ThrowsAsync(failure());

        var thrown = await Should.ThrowAsync<Exception>(() => new SetShiftRequiredQualificationSkill(mediator).ExecuteAsync(Ctx(), RequiredQualificationParameters()));

        thrown.GetType().ShouldBe(failure().GetType());
    }

    [TestCaseSource(nameof(EscapingUnitOfWorkFailures))]
    public async Task SetShiftPreferences_OtherUnitOfWorkFailure_Escapes(Func<Exception> failure)
    {
        var mediator = Substitute.For<IMediator>();
        mediator.Send(Arg.Any<SetShiftPreferenceCommand>(), Arg.Any<CancellationToken>()).ThrowsAsync(failure());

        var thrown = await Should.ThrowAsync<Exception>(() => new SetShiftPreferencesSkill(mediator).ExecuteAsync(Ctx(), PreferenceParameters()));

        thrown.GetType().ShouldBe(failure().GetType());
    }

    [Test]
    public async Task SetClientQualification_HiddenOrUnknownEmployee_ReturnsTheErrorMessage()
    {
        var mediator = Substitute.For<IMediator>();
        mediator.Send(Arg.Any<SetClientQualificationCommand>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new KeyNotFoundException($"Client with ID {ClientId} not found"));

        var result = await new SetClientQualificationSkill(mediator).ExecuteAsync(Ctx(), ClientQualificationParameters());

        result.Success.ShouldBeFalse();
        result.Message.ShouldBe(ClientQualificationError);
    }

    [Test]
    public async Task SetShiftRequiredQualification_HiddenOrUnknownShift_ReturnsTheErrorMessage()
    {
        var mediator = Substitute.For<IMediator>();
        mediator.Send(Arg.Any<SetShiftRequiredQualificationCommand>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new KeyNotFoundException($"Shift with ID {ShiftId} not found"));

        var result = await new SetShiftRequiredQualificationSkill(mediator).ExecuteAsync(Ctx(), RequiredQualificationParameters());

        result.Success.ShouldBeFalse();
        result.Message.ShouldBe(RequiredQualificationError);
    }

    [Test]
    public async Task SetShiftPreferences_HiddenOrUnknownEmployee_Escapes()
    {
        var mediator = Substitute.For<IMediator>();
        mediator.Send(Arg.Any<SetShiftPreferenceCommand>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new KeyNotFoundException($"Client with ID {ClientId} not found"));

        await Should.ThrowAsync<KeyNotFoundException>(() => new SetShiftPreferencesSkill(mediator).ExecuteAsync(Ctx(), PreferenceParameters()));
    }

    private static SkillExecutionContext Ctx() => new()
    {
        UserId = Guid.NewGuid(),
        TenantId = Guid.NewGuid(),
        UserName = "tester",
        UserPermissions = new List<string> { "CanEditClients", "CanEditShifts" }
    };

    private static Dictionary<string, object> ClientQualificationParameters() => new()
    {
        ["clientId"] = ClientId.ToString(),
        ["qualificationId"] = QualificationId.ToString(),
        ["level"] = 3
    };

    private static Dictionary<string, object> RequiredQualificationParameters() => new()
    {
        ["shiftId"] = ShiftId.ToString(),
        ["qualificationId"] = QualificationId.ToString(),
        ["minLevel"] = 2
    };

    private static Dictionary<string, object> PreferenceParameters() => new()
    {
        ["clientId"] = ClientId.ToString(),
        ["shiftId"] = ShiftId.ToString(),
        ["preference"] = "preferred"
    };
}
