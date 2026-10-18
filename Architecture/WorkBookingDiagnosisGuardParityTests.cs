// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

using System.Reflection;
using Klacks.Api.Application.Interfaces;
using Klacks.UnitTest.TestHelpers;
using Klacks.Api.Application.Services.Schedules;
using WorkPostHandler = Klacks.Api.Application.Handlers.Works.PostCommandHandler;

namespace Klacks.UnitTest.Architecture;

/// <summary>
/// Keeps the booking diagnosis from falling behind the real booking: every guard, lock service or checker the Works
/// POST handler injects must also be injected by WorkBookingDiagnosisService, either as the same type or as the
/// declared non-throwing equivalent (the day lock service throws from the sealed-day repository rule the diagnosis
/// reads directly; the client visibility guard is part of the row access guard, which adds the scenario check). A new
/// guard in the handler turns this test red until the diagnosis knows it. Also pins that the diagnosis injects the
/// one shared write guard, so sporadic and hard-blocking checks are the very methods the write paths throw from, and
/// (source scan) that the diagnosis hands the guard the Work itself: it never builds a PlannedWorkRow by hand nor picks
/// the row-list overload with its own operation, so the row and the scenario skip are decided in the guard only, for
/// the diagnosis exactly as for the write.
/// </summary>
[TestFixture]
public class WorkBookingDiagnosisGuardParityTests
{
    private static readonly string[] CheckTypeMarkers = ["Guard", "Checker", "LockService"];

    private const string DiagnosisSourcePath = "Klacks.Api/Application/Services/Schedules/WorkBookingDiagnosisService.cs";

    private static readonly string[] ForbiddenInDiagnosisSource = ["new PlannedWorkRow(", "WorkWriteOperation."];

    private static readonly IReadOnlyDictionary<Type, Type> DeclaredEquivalents = new Dictionary<Type, Type>
    {
        [typeof(IDayLockService)] = typeof(ISealedDayRepository),
        [typeof(IClientVisibilityGuard)] = typeof(IScheduleRowAccessGuard),
    };

    [Test]
    public void EveryCheckOfThePostHandler_IsKnownToTheDiagnosis()
    {
        var diagnosisDependencies = ConstructorParameterTypes(typeof(WorkBookingDiagnosisService));
        var missing = ConstructorParameterTypes(typeof(WorkPostHandler))
            .Where(IsCheck)
            .Where(type => !diagnosisDependencies.Contains(type)
                           && !(DeclaredEquivalents.TryGetValue(type, out var equivalent) && diagnosisDependencies.Contains(equivalent)))
            .Select(type => type.Name)
            .ToList();

        missing.ShouldBeEmpty(
            "PostCommandHandler (Works) checks something WorkBookingDiagnosisService does not: " +
            string.Join(", ", missing) + ". Teach the diagnosis the same check (or declare a non-throwing equivalent here).");
    }

    [Test]
    public void TheDiagnosis_InjectsTheSharedWriteGuardAndTheShiftVisibilityGuard()
    {
        var diagnosisDependencies = ConstructorParameterTypes(typeof(WorkBookingDiagnosisService));

        diagnosisDependencies.ShouldContain(typeof(Klacks.Api.Application.Interfaces.Schedules.IWorkWriteGuard));
        diagnosisDependencies.ShouldContain(typeof(IShiftVisibilityGuard));
        diagnosisDependencies.ShouldContain(typeof(Klacks.Api.Application.Interfaces.Schedules.IClientPlanningPeriodGuard));
    }

    [Test]
    public void TheDiagnosis_HandsTheGuardTheWork_AndNeverBuildsTheGuardRowItself()
    {
        var source = File.ReadAllText(RepositoryRootLocator.RequireFile(DiagnosisSourcePath));

        ForbiddenInDiagnosisSource
            .Where(fragment => source.Contains(fragment, StringComparison.Ordinal))
            .ShouldBeEmpty(
                "WorkBookingDiagnosisService must call IWorkWriteGuard.FindHardBlockingRefusalAsync(Work, ...) and build " +
                "rows only through PlannedWorkRow.From, so it cannot drift from the write path.");
        source.ShouldContain("FindHardBlockingRefusalAsync(work,");
    }

    private static bool IsCheck(Type type) =>
        CheckTypeMarkers.Any(marker => type.Name.Contains(marker, StringComparison.Ordinal));

    private static HashSet<Type> ConstructorParameterTypes(Type type) =>
        type.GetConstructors(BindingFlags.Public | BindingFlags.Instance)
            .Single()
            .GetParameters()
            .Select(parameter => parameter.ParameterType)
            .ToHashSet();
}
