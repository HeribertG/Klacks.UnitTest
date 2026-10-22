// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Characterization of ErrorHandlingMiddleware: for every mapped exception type the exact status code, the final
/// content type, the raw response body (property and extension order included) and the single log entry (level,
/// rendered message, whether the exception object was attached). The expectations were taken from the code before
/// Invoke was split into per-exception handlers, so any refactoring that changes a byte of a response fails here.
/// </summary>

using System.Text.Encodings.Web;
using System.Text.Json;
using FluentValidation;
using FluentValidation.Results;
using Klacks.Api.Application.Constants;
using Klacks.Api.Application.DTOs.Exports;
using Klacks.Api.Application.Exceptions;
using Klacks.Api.Domain.Constants;
using Klacks.Api.Domain.Exceptions;
using Klacks.Api.Infrastructure.Exceptions;
using Klacks.UnitTest.TestHelpers;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace Klacks.UnitTest.Infrastructure.Exceptions;

[TestFixture]
public class ErrorHandlingMiddlewareCharacterizationTests
{
    private const string JsonContentType = "application/json; charset=utf-8";
    private const string RequestMethod = "GET";
    private const string RequestPath = "/api/backend/probe";
    private const string ProductionEnvironment = "Production";
    private const string DevelopmentEnvironment = "Development";

    private static readonly JsonSerializerOptions AspNetCoreDefaultJsonOptions = new(JsonSerializerDefaults.Web)
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private static readonly Guid FixedId = new("6f1c2a4e-9b3d-4c8e-a1f2-0d5e7b9c3a11");
    private static readonly Guid FixedClientId = new("11111111-2222-3333-4444-555555555555");

    public sealed record MappingCase(
        string Name,
        Func<Exception> Create,
        int Status,
        string ContentType,
        string Body,
        LogLevel LogLevel,
        string LogMessage,
        bool LogCarriesException)
    {
        public override string ToString() => Name;
    }

    private static IEnumerable<MappingCase> Cases()
    {
        yield return new MappingCase(
            "ValidationException",
            () => new ValidationException(
            [
                new ValidationFailure("Name", "Name is required."),
                new ValidationFailure("Email", "Email is invalid."),
                new ValidationFailure("Name", "Name is too short."),
            ]),
            400,
            JsonContentType,
            "{\"title\":\"One or more validation failures occurred.\",\"status\":400,\"detail\":\"Please refer to the errors property for additional details.\",\"errors\":{\"Name\":[\"Name is required.\",\"Name is too short.\"],\"Email\":[\"Email is invalid.\"]}}",
            LogLevel.Warning,
            "FluentValidation ValidationException caught by middleware",
            true);

        yield return new MappingCase(
            "AccountRegistrationRejectedException",
            () => new AccountRegistrationRejectedException(
                [AccountRegistrationErrorCodes.PasswordTooShort, AccountRegistrationErrorCodes.PasswordRequiresDigit],
                "Passwords must be at least 8 characters."),
            400,
            JsonContentType,
            $"{{\"title\":\"Bad Request\",\"status\":400,\"detail\":\"Passwords must be at least 8 characters.\",\"errorCode\":\"{AccountRegistrationErrorCodes.ErrorCode}\",\"reasons\":[\"{AccountRegistrationErrorCodes.PasswordTooShort}\",\"{AccountRegistrationErrorCodes.PasswordRequiresDigit}\"]}}",
            LogLevel.Warning,
            $"AccountRegistrationRejectedException caught by middleware: {AccountRegistrationErrorCodes.PasswordTooShort},{AccountRegistrationErrorCodes.PasswordRequiresDigit}",
            false);

        yield return new MappingCase(
            "ClientImportRejectedException",
            () => new ClientImportRejectedException("importProbeRejected", "The file is too large."),
            400,
            JsonContentType,
            "{\"title\":\"Bad Request\",\"status\":400,\"detail\":\"The file is too large.\",\"errorCode\":\"importProbeRejected\",\"code\":\"importProbeRejected\"}",
            LogLevel.Warning,
            "ClientImportRejectedException caught by middleware: importProbeRejected",
            true);

        yield return new MappingCase(
            "ClientImportConflictException",
            () => new ClientImportConflictException("The import was already committed."),
            409,
            JsonContentType,
            $"{{\"title\":\"Conflict\",\"status\":409,\"detail\":\"The import was already committed.\",\"errorCode\":\"{ClientImportErrorCodes.AlreadyCommitted}\",\"code\":\"{ClientImportErrorCodes.AlreadyCommitted}\"}}",
            LogLevel.Warning,
            $"ClientImportConflictException caught by middleware: {ClientImportErrorCodes.AlreadyCommitted}",
            true);

        yield return new MappingCase(
            "InvalidRequestException",
            () => new InvalidRequestException("The period is invalid."),
            400,
            JsonContentType,
            "{\"title\":\"Invalid request\",\"status\":400,\"detail\":\"The period is invalid.\"}",
            LogLevel.Warning,
            "InvalidRequestException caught by middleware: The period is invalid.",
            true);

        yield return new MappingCase(
            "CodedInvalidRequestException",
            () => new WorkBookingRefusedException(
                "The day is outside the membership period.",
                WorkBookingRefusalCodes.OutsideMembershipPeriod,
                new Dictionary<string, object?>
                {
                    [WorkBookingRefusalCodes.ClientIdField] = FixedClientId,
                    [WorkBookingRefusalCodes.DateField] = new DateOnly(2027, 3, 10),
                    [WorkBookingRefusalCodes.UntilField] = null,
                }),
            400,
            JsonContentType,
            $"{{\"title\":\"Invalid request\",\"status\":400,\"detail\":\"The day is outside the membership period.\",\"{WorkBookingRefusalCodes.ClientIdField}\":\"{FixedClientId}\",\"{WorkBookingRefusalCodes.DateField}\":\"2027-03-10\",\"{WorkBookingRefusalCodes.UntilField}\":null,\"errorCode\":\"{WorkBookingRefusalCodes.OutsideMembershipPeriod}\",\"code\":\"{WorkBookingRefusalCodes.OutsideMembershipPeriod}\"}}",
            LogLevel.Warning,
            "InvalidRequestException caught by middleware: The day is outside the membership period.",
            true);

        yield return new MappingCase(
            "KeyNotFoundException",
            () => new KeyNotFoundException("Shift 42 was not found."),
            404,
            JsonContentType,
            "{\"title\":\"Not Found\",\"status\":404,\"detail\":\"Shift 42 was not found.\"}",
            LogLevel.Warning,
            "KeyNotFoundException caught by middleware: Shift 42 was not found.",
            true);

        yield return new MappingCase(
            "DbUpdateException",
            () => new DbUpdateException("constraint violated"),
            400,
            JsonContentType,
            "{\"title\":\"Database Update Error\",\"status\":400,\"detail\":\"A database error occurred. Please check your input.\"}",
            LogLevel.Error,
            "DbUpdateException caught by middleware: constraint violated",
            true);

        yield return new MappingCase(
            "DbUpdateConcurrencyException",
            () => new DbUpdateConcurrencyException("row moved"),
            400,
            JsonContentType,
            "{\"title\":\"Database Update Error\",\"status\":400,\"detail\":\"A database error occurred. Please check your input.\"}",
            LogLevel.Error,
            "DbUpdateException caught by middleware: row moved",
            true);

        yield return new MappingCase(
            "DomainDatabaseUpdateExceptionDuplicate",
            () => new DatabaseUpdateException("duplicate key value", isDuplicate: true),
            400,
            JsonContentType,
            "{\"title\":\"Invalid request\",\"status\":400,\"detail\":\"A duplicate entry already exists.\"}",
            LogLevel.Error,
            "DatabaseUpdateException caught by middleware: duplicate key value",
            true);

        yield return new MappingCase(
            "DomainDatabaseUpdateExceptionForeignKey",
            () => new DatabaseUpdateException("violates foreign key constraint", isForeignKeyViolation: true),
            400,
            JsonContentType,
            "{\"title\":\"Invalid request\",\"status\":400,\"detail\":\"Referenced data not found or constraint violation.\"}",
            LogLevel.Error,
            "DatabaseUpdateException caught by middleware: violates foreign key constraint",
            true);

        yield return new MappingCase(
            "DomainDatabaseUpdateExceptionOther",
            () => new DatabaseUpdateException("value too long"),
            400,
            JsonContentType,
            "{\"title\":\"Invalid request\",\"status\":400,\"detail\":\"Database constraint violation.\"}",
            LogLevel.Error,
            "DatabaseUpdateException caught by middleware: value too long",
            true);

        yield return new MappingCase(
            "DomainConcurrencyException",
            () => new ConcurrencyException("The record was modified by another user. Please refresh and try again."),
            400,
            JsonContentType,
            "{\"title\":\"Invalid request\",\"status\":400,\"detail\":\"The record was modified by another user. Please refresh and try again.\"}",
            LogLevel.Error,
            "ConcurrencyException caught by middleware: The record was modified by another user. Please refresh and try again.",
            true);

        yield return new MappingCase(
            "ContainerLockedException",
            () => new ContainerLockedException("The container is locked."),
            423,
            JsonContentType,
            "{\"title\":\"Container Locked\",\"status\":423,\"detail\":\"The container is locked.\"}",
            LogLevel.Warning,
            "ContainerLockedException caught by middleware: The container is locked.",
            true);

        yield return new MappingCase(
            "AutofillRunConflictException",
            () => new AutofillRunConflictException(FixedId, AutofillFamily.HolisticHarmonizer),
            409,
            JsonContentType,
            $"{{\"title\":\"Conflict\",\"status\":409,\"detail\":\"A HolisticHarmonizer run for the same selection is already in progress.\",\"errorCode\":\"{AutofillLimits.RunConflictErrorCode}\",\"runningJobId\":\"{FixedId}\"}}",
            LogLevel.Information,
            $"Autofill run conflict: HolisticHarmonizer for the same selection is already running as {FixedId}",
            false);

        yield return new MappingCase(
            "PlanningRuleConfigurationException",
            () => new PlanningRuleConfigurationException(FixedId, "bad json"),
            422,
            JsonContentType,
            $"{{\"title\":\"Unprocessable Entity\",\"status\":422,\"detail\":\"Approved hard planning constraint {FixedId} is invalid and blocks planning until it is fixed or revoked: bad json\",\"errorCode\":\"{PlanningRuleConfigurationException.ErrorCode}\",\"code\":\"{PlanningRuleConfigurationException.ErrorCode}\"}}",
            LogLevel.Error,
            $"PlanningRuleConfigurationException caught by middleware: {FixedId}",
            true);

        yield return new MappingCase(
            "PeriodValidationConflictException",
            () => new PeriodValidationConflictException("The error count changed.", 4),
            409,
            JsonContentType,
            $"{{\"title\":\"Conflict\",\"status\":409,\"detail\":\"The error count changed.\",\"errorCode\":\"{PeriodValidationConflictException.ErrorCode}\",\"currentErrorCount\":4}}",
            LogLevel.Warning,
            "PeriodValidationConflictException caught by middleware: The error count changed.",
            true);

        yield return new MappingCase(
            "PayrollExportBlockedException",
            () => new PayrollExportBlockedException(new PayrollCompletenessResult
            {
                BlockerTotal = 7,
                Blockers =
                [
                    new PayrollExportBlockerDto
                    {
                        ClientId = FixedClientId,
                        ClientName = "Muster, Max",
                        IdNumber = 42,
                        Date = new DateOnly(2027, 3, 10),
                        Reason = PayrollExportBlockReason.DayNotLocked,
                        RequiresGlobalClose = true,
                        EntryCount = 2,
                    },
                ],
            }),
            409,
            JsonContentType,
            $"{{\"title\":\"Conflict\",\"status\":409,\"detail\":\"The payroll export is blocked by 7 open item(s). Close the listed entries and days first.\",\"errorCode\":\"{PayrollExportErrorCodes.Blocked}\",\"blockers\":[{{\"clientId\":\"{FixedClientId}\",\"clientName\":\"Muster, Max\",\"idNumber\":42,\"date\":\"2027-03-10\",\"groupId\":null,\"groupName\":null,\"reason\":\"DayNotLocked\",\"requiresGlobalClose\":true,\"entryCount\":2}}],\"blockerTotal\":7}}",
            LogLevel.Warning,
            "PayrollExportBlockedException caught by middleware: 7 blockers",
            true);

        yield return new MappingCase(
            "ConflictException",
            () => new ConflictException("The slot is taken."),
            409,
            JsonContentType,
            "{\"title\":\"Conflict\",\"status\":409,\"detail\":\"The slot is taken.\"}",
            LogLevel.Warning,
            "ConflictException caught by middleware: The slot is taken.",
            true);

        yield return new MappingCase(
            "CodedConflictException",
            () => new WorkWriteConflictException(
                "The sporadic shift is fully booked.",
                WorkWriteConflictCodes.SporadicShiftDayFull,
                new Dictionary<string, object?>
                {
                    [WorkWriteConflictCodes.ShiftNameField] = "Night watch",
                    [WorkWriteConflictCodes.EngagedField] = 2,
                    ["errorCode"] = "mustNotWin",
                }),
            409,
            JsonContentType,
            $"{{\"title\":\"Conflict\",\"status\":409,\"detail\":\"The sporadic shift is fully booked.\",\"{WorkWriteConflictCodes.ShiftNameField}\":\"Night watch\",\"{WorkWriteConflictCodes.EngagedField}\":2,\"errorCode\":\"{WorkWriteConflictCodes.SporadicShiftDayFull}\"}}",
            LogLevel.Warning,
            "ConflictException caught by middleware: The sporadic shift is fully booked.",
            true);

        yield return new MappingCase(
            "StaleWizardResultException",
            () => new StaleWizardResultException("The plan changed.", 3, 4, 1, 2, true),
            409,
            JsonContentType,
            "{\"title\":\"Conflict\",\"status\":409,\"detail\":\"The plan changed.\",\"errorCode\":\"staleWizardResult\",\"expectedWorkCount\":3,\"actualWorkCount\":4,\"expectedBreakCount\":1,\"actualBreakCount\":2,\"placementChanged\":true}",
            LogLevel.Warning,
            "StaleWizardResultException caught by middleware: The plan changed.",
            true);

        yield return new MappingCase(
            "ForbiddenException",
            () => new ForbiddenException("A higher role is needed."),
            403,
            JsonContentType,
            "{\"title\":\"Forbidden\",\"status\":403,\"detail\":\"A higher role is needed.\"}",
            LogLevel.Warning,
            "ForbiddenException caught by middleware: A higher role is needed.",
            true);

        yield return new MappingCase(
            "UnauthorizedException",
            () => new UnauthorizedException("no session"),
            401,
            JsonContentType,
            "{\"title\":\"Unauthorized\",\"status\":401,\"detail\":\"no session\"}",
            LogLevel.Warning,
            "UnauthorizedException caught by middleware: no session",
            true);

        yield return new MappingCase(
            "NotFoundException",
            () => new NotFoundException("The group was not found."),
            404,
            JsonContentType,
            "{\"title\":\"Not Found\",\"status\":404,\"detail\":\"The group was not found.\"}",
            LogLevel.Warning,
            "NotFoundException caught by middleware: The group was not found.",
            true);

        yield return new MappingCase(
            "AutofillLimitExceededException",
            () => new AutofillLimitExceededException("autofillProbeTooLarge", AutofillFamily.Wizard1, 120, 30, 62, 100, 50, 60, 223200, 200000),
            400,
            JsonContentType,
            "{\"title\":\"Bad Request\",\"status\":400,\"detail\":\"The requested Wizard1 run is too large: 120 agents, 30 shifts, 62 days.\",\"errorCode\":\"autofillProbeTooLarge\",\"code\":\"autofillProbeTooLarge\",\"agents\":120,\"shifts\":30,\"periodDays\":62,\"maxAgents\":100,\"maxShifts\":50,\"maxPeriodDays\":60,\"slotProduct\":223200,\"maxSlotProduct\":200000}",
            LogLevel.Information,
            "Autofill run refused: Wizard1 with 120 agents, 30 shifts, 62 days",
            false);

        yield return new MappingCase(
            "BadRequestException",
            () => new BadRequestException("The input is malformed."),
            400,
            JsonContentType,
            "{\"title\":\"Bad Request\",\"status\":400,\"detail\":\"The input is malformed.\"}",
            LogLevel.Warning,
            "BadRequestException caught by middleware: The input is malformed.",
            true);

        yield return new MappingCase(
            "OperationCanceledException",
            () => new OperationCanceledException(),
            499,
            string.Empty,
            string.Empty,
            LogLevel.Debug,
            $"Request cancelled: {RequestMethod} {RequestPath}",
            false);

        yield return new MappingCase(
            "TaskCanceledException",
            () => new TaskCanceledException(),
            499,
            string.Empty,
            string.Empty,
            LogLevel.Debug,
            $"Request cancelled: {RequestMethod} {RequestPath}",
            false);

        yield return new MappingCase(
            "PostgresQueryCancelled",
            () => new PostgresException("canceling statement due to user request", "ERROR", "ERROR", "57014"),
            499,
            string.Empty,
            string.Empty,
            LogLevel.Debug,
            $"PostgreSQL query cancelled: {RequestMethod} {RequestPath}",
            false);

        yield return new MappingCase(
            "PostgresOtherSqlState",
            () => new PostgresException("duplicate key value", "ERROR", "ERROR", "23505"),
            500,
            JsonContentType,
            "{\"title\":\"An unexpected error occurred\",\"status\":500,\"detail\":\"An internal server error has occurred.\"}",
            LogLevel.Error,
            "An unhandled exception occurred: 23505: duplicate key value",
            true);

        yield return new MappingCase(
            "UnhandledExceptionInProduction",
            () => new InvalidOperationException("boom"),
            500,
            JsonContentType,
            "{\"title\":\"An unexpected error occurred\",\"status\":500,\"detail\":\"An internal server error has occurred.\"}",
            LogLevel.Error,
            "An unhandled exception occurred: boom",
            true);
    }

    [TestCaseSource(nameof(Cases))]
    public async Task EveryMappedException_KeepsItsStatusContentTypeBodyAndLogEntry(MappingCase mapping)
    {
        var thrown = mapping.Create();

        var (status, contentType, body, entries) = await Invoke(thrown, ProductionEnvironment);

        status.ShouldBe(mapping.Status);
        contentType.ShouldBe(mapping.ContentType);
        body.ShouldBe(mapping.Body);
        entries.Count.ShouldBe(1);
        entries[0].Level.ShouldBe(mapping.LogLevel);
        entries[0].Message.ShouldBe(mapping.LogMessage);
        if (mapping.LogCarriesException)
        {
            entries[0].Exception.ShouldBeSameAs(thrown);
        }
        else
        {
            entries[0].Exception.ShouldBeNull();
        }
    }

    [Test]
    public async Task UnhandledExceptionInDevelopment_ExposesTheMessageAndTheStackTrace()
    {
        var thrown = new InvalidOperationException("boom");

        var (status, contentType, body, entries) = await Invoke(thrown, DevelopmentEnvironment);

        status.ShouldBe(StatusCodes.Status500InternalServerError);
        contentType.ShouldBe(JsonContentType);
        thrown.StackTrace.ShouldNotBeNull();
        body.ShouldBe(
            "{\"title\":\"An unexpected error occurred\",\"status\":500,\"detail\":\"boom\",\"stackTrace\":" +
            JsonSerializer.Serialize(thrown.StackTrace, AspNetCoreDefaultJsonOptions) + "}");
        entries.Count.ShouldBe(1);
        entries[0].Level.ShouldBe(LogLevel.Error);
        entries[0].Exception.ShouldBeSameAs(thrown);
    }

    [Test]
    public async Task UnhandledExceptionWithoutHostEnvironment_SetsStatusAndProblemContentTypeBeforeTheLookupFails()
    {
        var logger = new RecordingLogger<ErrorHandlingMiddleware>();
        var thrown = new ApplicationException("boom");
        var middleware = new ErrorHandlingMiddleware(_ => throw thrown, logger);
        var context = new DefaultHttpContext { RequestServices = new ServiceCollection().BuildServiceProvider() };
        context.Response.Body = new MemoryStream();

        await Should.ThrowAsync<InvalidOperationException>(() => middleware.Invoke(context));

        context.Response.StatusCode.ShouldBe(StatusCodes.Status500InternalServerError);
        context.Response.ContentType.ShouldBe("application/problem+json");
        context.Response.Body.Length.ShouldBe(0);
        logger.Entries.Count.ShouldBe(1);
        logger.Entries[0].Level.ShouldBe(LogLevel.Error);
        logger.Entries[0].Exception.ShouldBeSameAs(thrown);
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task CancelledRequest_StripsLineBreaksFromTheLoggedMethod(bool postgresCancellation)
    {
        var logger = new RecordingLogger<ErrorHandlingMiddleware>();
        Exception thrown = postgresCancellation
            ? new PostgresException("canceling statement due to user request", "ERROR", "ERROR", "57014")
            : new OperationCanceledException();
        var middleware = new ErrorHandlingMiddleware(_ => throw thrown, logger);
        var context = CreateContext(ProductionEnvironment);
        context.Request.Method = "GE\r\nT";

        await middleware.Invoke(context);

        context.Response.StatusCode.ShouldBe(499);
        logger.Entries.Count.ShouldBe(1);
        logger.Entries[0].Message.ShouldBe(postgresCancellation
            ? $"PostgreSQL query cancelled: GET {RequestPath}"
            : $"Request cancelled: GET {RequestPath}");
    }

    [Test]
    public async Task NoException_LeavesTheResponseAndTheLogUntouched()
    {
        var logger = new RecordingLogger<ErrorHandlingMiddleware>();
        var middleware = new ErrorHandlingMiddleware(_ => Task.CompletedTask, logger);
        var context = CreateContext(ProductionEnvironment);

        await middleware.Invoke(context);

        context.Response.StatusCode.ShouldBe(StatusCodes.Status200OK);
        context.Response.Body.Length.ShouldBe(0);
        logger.Entries.ShouldBeEmpty();
    }

    private static async Task<(int Status, string ContentType, string Body, List<RecordedLogEntry> Entries)> Invoke(
        Exception thrown, string environmentName)
    {
        var logger = new RecordingLogger<ErrorHandlingMiddleware>();
        var middleware = new ErrorHandlingMiddleware(_ => throw thrown, logger);
        var context = CreateContext(environmentName);

        await middleware.Invoke(context);

        context.Response.Body.Seek(0, SeekOrigin.Begin);
        using var reader = new StreamReader(context.Response.Body);
        var body = await reader.ReadToEndAsync();

        return (context.Response.StatusCode, context.Response.ContentType ?? string.Empty, body, logger.Entries);
    }

    private static DefaultHttpContext CreateContext(string environmentName)
    {
        var environment = Substitute.For<IWebHostEnvironment>();
        environment.EnvironmentName.Returns(environmentName);
        var services = new ServiceCollection().AddSingleton(environment).BuildServiceProvider();

        var context = new DefaultHttpContext { RequestServices = services };
        context.Request.Method = RequestMethod;
        context.Request.Path = RequestPath;
        context.Response.Body = new MemoryStream();
        return context;
    }
}
