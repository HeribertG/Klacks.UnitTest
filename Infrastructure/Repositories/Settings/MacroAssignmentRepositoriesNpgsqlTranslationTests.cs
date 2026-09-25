// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Proves that every query of MacroReferenceRepository and MacroAssignmentHistoryRepository translates to SQL on the real
/// Npgsql provider: each method runs against an unreachable server, so an untranslatable expression would fail with
/// "could not be translated" before any connection attempt, while a translatable one fails only on the connection.
/// Pattern: EmailClientAssignmentServiceNpgsqlTranslationTests.
/// </summary>

using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Klacks.UnitTest.Infrastructure.Repositories.Settings;

[TestFixture]
public class MacroAssignmentRepositoriesNpgsqlTranslationTests
{
    private const string UnreachableConnectionString =
        "Host=127.0.0.1;Port=1;Database=klacks_model_only;Username=postgres;Password=admin;Timeout=2;Command Timeout=2";
    private const string TranslationFailureMarker = "could not be translated";

    private DataBaseContext _context = null!;
    private MacroReferenceRepository _references = null!;
    private MacroAssignmentHistoryRepository _history = null!;

    [SetUp]
    public void SetUp()
    {
        var options = new DbContextOptionsBuilder<DataBaseContext>()
            .UseNpgsql(UnreachableConnectionString)
            .UseSnakeCaseNamingConvention()
            .Options;
        _context = new DataBaseContext(options, Substitute.For<IHttpContextAccessor>());
        _references = new MacroReferenceRepository(_context);
        _history = new MacroAssignmentHistoryRepository(_context);
    }

    [TearDown]
    public void TearDown() => _context.Dispose();

    [Test]
    public async Task FindHolder_Shift_TranslatesToSql() =>
        AssertConnectionFailureOnly(await Should.ThrowAsync<Exception>(
            () => _references.FindHolderAsync(MacroAssignmentTarget.Shift, Guid.NewGuid())));

    [Test]
    public async Task FindHolder_AbsenceType_TranslatesToSql() =>
        AssertConnectionFailureOnly(await Should.ThrowAsync<Exception>(
            () => _references.FindHolderAsync(MacroAssignmentTarget.AbsenceType, Guid.NewGuid())));

    [Test]
    public async Task FindCutGroup_TranslatesToSql() =>
        AssertConnectionFailureOnly(await Should.ThrowAsync<Exception>(
            () => _references.FindCutGroupAsync(Guid.NewGuid())));

    [Test]
    public async Task FindMacro_TranslatesToSql() =>
        AssertConnectionFailureOnly(await Should.ThrowAsync<Exception>(
            () => _references.FindMacroAsync(Guid.NewGuid())));

    [Test]
    public async Task SetMacroId_Shift_TranslatesToSql() =>
        AssertConnectionFailureOnly(await Should.ThrowAsync<Exception>(
            () => _references.SetMacroIdAsync(MacroAssignmentTarget.Shift, Guid.NewGuid(), Guid.NewGuid())));

    [Test]
    public async Task SetMacroId_AbsenceType_TranslatesToSql() =>
        AssertConnectionFailureOnly(await Should.ThrowAsync<Exception>(
            () => _references.SetMacroIdAsync(MacroAssignmentTarget.AbsenceType, Guid.NewGuid(), null)));

    [Test]
    public async Task HistoryGetSwitch_TranslatesToSql() =>
        AssertConnectionFailureOnly(await Should.ThrowAsync<Exception>(() => _history.GetSwitchAsync(Guid.NewGuid())));

    [Test]
    public async Task HistoryGetLatest_TranslatesToSql() =>
        AssertConnectionFailureOnly(await Should.ThrowAsync<Exception>(
            () => _history.GetLatestAsync(MacroAssignmentTarget.Shift, Guid.NewGuid())));

    private static void AssertConnectionFailureOnly(Exception exception)
    {
        var message = exception.ToString();

        message.ShouldNotContain(TranslationFailureMarker);
        message.ShouldContain(nameof(NpgsqlException));
    }
}
