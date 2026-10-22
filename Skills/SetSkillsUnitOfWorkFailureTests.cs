// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Pins what set_client_qualification, set_shift_required_qualification and set_shift_preferences do when their command
/// fails. The commands persist through IUnitOfWork.CompleteAsync, which turns every EF Core write failure into the Domain
/// DatabaseUpdateException (unknown employee/shift/qualification = foreign-key violation) or ConcurrencyException
/// (Klacks.IntegrationTest UnitOfWorkFailureTranslationTests proves it on PostgreSQL); neither is an EF Core
/// DbUpdateException, so both escape the skills unchanged. A KeyNotFoundException (caller may not see the employee/shift)
/// becomes the skill's error message in the two qualification skills and escapes set_shift_preferences.
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

    private static readonly Guid ClientId = Guid.NewGuid();
    private static readonly Guid ShiftId = Guid.NewGuid();
    private static readonly Guid QualificationId = Guid.NewGuid();

    private static IEnumerable<TestCaseData> UnitOfWorkFailures()
    {
        yield return new TestCaseData((Func<Exception>)(() =>
            new DatabaseUpdateException("insert or update violates foreign key constraint", isForeignKeyViolation: true)))
            .SetName("{m}_ForeignKeyViolation");
        yield return new TestCaseData((Func<Exception>)(() => new ConcurrencyException("modified by another user")))
            .SetName("{m}_ConcurrencyFailure");
    }

    [TestCaseSource(nameof(UnitOfWorkFailures))]
    public async Task SetClientQualification_UnitOfWorkFailure_Escapes(Func<Exception> failure)
    {
        var mediator = Substitute.For<IMediator>();
        mediator.Send(Arg.Any<SetClientQualificationCommand>(), Arg.Any<CancellationToken>()).ThrowsAsync(failure());

        var thrown = await Should.ThrowAsync<Exception>(() => new SetClientQualificationSkill(mediator).ExecuteAsync(Ctx(), ClientQualificationParameters()));

        thrown.GetType().ShouldBe(failure().GetType());
    }

    [TestCaseSource(nameof(UnitOfWorkFailures))]
    public async Task SetShiftRequiredQualification_UnitOfWorkFailure_Escapes(Func<Exception> failure)
    {
        var mediator = Substitute.For<IMediator>();
        mediator.Send(Arg.Any<SetShiftRequiredQualificationCommand>(), Arg.Any<CancellationToken>()).ThrowsAsync(failure());

        var thrown = await Should.ThrowAsync<Exception>(() => new SetShiftRequiredQualificationSkill(mediator).ExecuteAsync(Ctx(), RequiredQualificationParameters()));

        thrown.GetType().ShouldBe(failure().GetType());
    }

    [TestCaseSource(nameof(UnitOfWorkFailures))]
    public async Task SetShiftPreferences_UnitOfWorkFailure_Escapes(Func<Exception> failure)
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
