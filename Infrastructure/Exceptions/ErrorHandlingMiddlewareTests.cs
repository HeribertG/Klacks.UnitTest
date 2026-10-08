// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// The status codes ErrorHandlingMiddleware maps the authorisation exceptions to. The distinction is
/// load-bearing: 401 makes the SPA log the user out, so a missing RIGHT must not come out as one, and
/// 400 describes a malformed request, which a rights problem is not. ForbiddenException is the only
/// path to 403 from inside a handler.
/// </summary>

using System.Text.Json;
using Klacks.Api.Application.Constants;
using Klacks.Api.Application.DTOs.Exports;
using Klacks.Api.Application.DTOs.Schedules;
using Klacks.Api.Application.Exceptions;
using Klacks.Api.Domain.Exceptions;
using Klacks.Api.Infrastructure.Exceptions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace Klacks.UnitTest.Infrastructure.Exceptions;

[TestFixture]
public class ErrorHandlingMiddlewareTests
{
    private static async Task<(int StatusCode, string ContentType, string Body)> Invoke(Exception thrown)
    {
        var middleware = new ErrorHandlingMiddleware(
            _ => throw thrown,
            Substitute.For<ILogger<ErrorHandlingMiddleware>>());

        var context = new DefaultHttpContext();
        context.Response.Body = new MemoryStream();

        await middleware.Invoke(context);

        context.Response.Body.Seek(0, SeekOrigin.Begin);
        using var reader = new StreamReader(context.Response.Body);

        return (context.Response.StatusCode, context.Response.ContentType ?? string.Empty, await reader.ReadToEndAsync());
    }

    [Test]
    public async Task ForbiddenException_Becomes403WithTheMessageAsDetail()
    {
        var response = await Invoke(new ForbiddenException("Unsealing this entry needs a higher role than yours."));

        response.StatusCode.ShouldBe(StatusCodes.Status403Forbidden);

        // The middleware sets "application/problem+json" but WriteAsJsonAsync overwrites it with
        // "application/json; charset=utf-8" — pre-existing behaviour shared by every branch, so the
        // header is deliberately not asserted here.
        using var problem = JsonDocument.Parse(response.Body);
        problem.RootElement.GetProperty("status").GetInt32().ShouldBe(StatusCodes.Status403Forbidden);
        problem.RootElement.GetProperty("detail").GetString()
            .ShouldBe("Unsealing this entry needs a higher role than yours.");
    }

    [Test]
    public async Task PlanningRuleConfigurationException_Becomes422WithErrorCode_NeverA500()
    {
        var response = await Invoke(new PlanningRuleConfigurationException(Guid.NewGuid(), "bad json"));

        response.StatusCode.ShouldBe(StatusCodes.Status422UnprocessableEntity);
        using var problem = JsonDocument.Parse(response.Body);
        problem.RootElement.GetProperty("errorCode").GetString().ShouldBe(PlanningRuleConfigurationException.ErrorCode);
    }

    [Test]
    public async Task UnauthorizedException_StaysA401()
    {
        var response = await Invoke(new UnauthorizedException("no session"));

        response.StatusCode.ShouldBe(StatusCodes.Status401Unauthorized);
    }

    [Test]
    public async Task ConflictException_Becomes409WithTheMessageAndNoErrorCode()
    {
        var response = await Invoke(new ConflictException("slot taken"));

        response.StatusCode.ShouldBe(StatusCodes.Status409Conflict);
        using var problem = JsonDocument.Parse(response.Body);
        problem.RootElement.GetProperty("detail").GetString().ShouldBe("slot taken");
        problem.RootElement.TryGetProperty("errorCode", out _).ShouldBeFalse();
    }

    [Test]
    public async Task WorkWriteConflictException_Becomes409WithErrorCodeAndDetailsNextToTheMessage()
    {
        var details = new Dictionary<string, object?>
        {
            [WorkWriteConflictCodes.ShiftNameField] = "Night watch",
            [WorkWriteConflictCodes.DateField] = new DateOnly(2027, 3, 10),
            [WorkWriteConflictCodes.EngagedField] = 2,
            [WorkWriteConflictCodes.CapacityField] = 2,
        };

        var response = await Invoke(new WorkWriteConflictException(
            "Sporadic shift is fully booked", WorkWriteConflictCodes.SporadicShiftDayFull, details));

        response.StatusCode.ShouldBe(StatusCodes.Status409Conflict);
        using var problem = JsonDocument.Parse(response.Body);
        var root = problem.RootElement;
        root.GetProperty("detail").GetString().ShouldBe("Sporadic shift is fully booked");
        root.GetProperty("errorCode").GetString().ShouldBe(WorkWriteConflictCodes.SporadicShiftDayFull);
        root.GetProperty("shiftName").GetString().ShouldBe("Night watch");
        root.GetProperty("date").GetString().ShouldBe("2027-03-10");
        root.GetProperty("engaged").GetInt32().ShouldBe(2);
        root.GetProperty("capacity").GetInt32().ShouldBe(2);
    }

    [Test]
    public async Task WorkWriteConflictException_WithConflictItems_WritesThemAsCamelCaseObjects()
    {
        var clientId = Guid.NewGuid();
        var item = new WorkConflictItem(
            "schedule.error-list.qualification-missing",
            clientId,
            new DateOnly(2027, 3, 10),
            new Dictionary<string, string> { ["qualificationId"] = "q-1", ["minLevel"] = "2" });
        var details = new Dictionary<string, object?> { [WorkWriteConflictCodes.ConflictsField] = new List<WorkConflictItem> { item } };

        var response = await Invoke(new WorkWriteConflictException(
            "Work blocked", WorkWriteConflictCodes.BlockedByConflicts, details));

        using var problem = JsonDocument.Parse(response.Body);
        var conflict = problem.RootElement.GetProperty("conflicts")[0];
        conflict.GetProperty("code").GetString().ShouldBe("schedule.error-list.qualification-missing");
        conflict.GetProperty("clientId").GetGuid().ShouldBe(clientId);
        conflict.GetProperty("date").GetString().ShouldBe("2027-03-10");
        conflict.GetProperty("params").GetProperty("qualificationId").GetString().ShouldBe("q-1");
        conflict.GetProperty("params").GetProperty("minLevel").GetString().ShouldBe("2");
    }

    [Test]
    public async Task InvalidRequestException_StaysA400()
    {
        var response = await Invoke(new InvalidRequestException("this entry is not sealed"));

        response.StatusCode.ShouldBe(StatusCodes.Status400BadRequest);
    }

    [Test]
    public async Task ScenarioNotActiveException_Becomes409CarryingItsErrorCode()
    {
        var response = await Invoke(new ScenarioNotActiveException("Scenario is no longer active."));

        response.StatusCode.ShouldBe(StatusCodes.Status409Conflict);
        using var problem = JsonDocument.Parse(response.Body);
        problem.RootElement.GetProperty("detail").GetString().ShouldBe("Scenario is no longer active.");
        problem.RootElement.GetProperty("errorCode").GetString().ShouldBe(ScenarioNotActiveException.ErrorCode);
    }

    [Test]
    public async Task OtherDbUpdateException_StaysA400()
    {
        var response = await Invoke(new Microsoft.EntityFrameworkCore.DbUpdateException("constraint violated"));

        response.StatusCode.ShouldBe(StatusCodes.Status400BadRequest);
    }

    [Test]
    public async Task PlanningConstraintConcurrencyException_Becomes409CarryingItsErrorCode()
    {
        var response = await Invoke(new PlanningConstraintConcurrencyException(new ConcurrencyException("row changed")));

        response.StatusCode.ShouldBe(StatusCodes.Status409Conflict);
        using var problem = JsonDocument.Parse(response.Body);
        problem.RootElement.GetProperty("errorCode").GetString().ShouldBe(PlanningConstraintConcurrencyException.ErrorCode);
    }

    [Test]
    public async Task PlainConflictException_StaysA409WithoutErrorCode()
    {
        var response = await Invoke(new ConflictException("blocked by compliance"));

        response.StatusCode.ShouldBe(StatusCodes.Status409Conflict);
        using var problem = JsonDocument.Parse(response.Body);
        problem.RootElement.GetProperty("detail").GetString().ShouldBe("blocked by compliance");
        problem.RootElement.TryGetProperty("errorCode", out _).ShouldBeFalse();
    }

    [Test]
    public async Task PayrollExportBlockedException_Becomes409WithErrorCodeBlockersAndTotal()
    {
        var clientId = Guid.NewGuid();
        var completeness = new PayrollCompletenessResult
        {
            BlockerTotal = 7,
            Blockers =
            [
                new PayrollExportBlockerDto
                {
                    ClientId = clientId,
                    ClientName = "Muster, Max",
                    IdNumber = 42,
                    Date = new DateOnly(2027, 3, 10),
                    Reason = PayrollExportBlockReason.DayNotLocked,
                    RequiresGlobalClose = true,
                    EntryCount = 2
                }
            ]
        };

        var response = await Invoke(new PayrollExportBlockedException(completeness));

        response.StatusCode.ShouldBe(StatusCodes.Status409Conflict);
        using var problem = JsonDocument.Parse(response.Body);
        var root = problem.RootElement;
        root.GetProperty("errorCode").GetString().ShouldBe("payrollExportBlocked");
        root.GetProperty("blockerTotal").GetInt32().ShouldBe(7);
        var blocker = root.GetProperty("blockers")[0];
        blocker.GetProperty("clientId").GetGuid().ShouldBe(clientId);
        blocker.GetProperty("clientName").GetString().ShouldBe("Muster, Max");
        blocker.GetProperty("date").GetString().ShouldBe("2027-03-10");
        blocker.GetProperty("reason").GetString().ShouldBe("DayNotLocked");
        blocker.GetProperty("requiresGlobalClose").GetBoolean().ShouldBeTrue();
        blocker.GetProperty("entryCount").GetInt32().ShouldBe(2);
    }

    [Test]
    public async Task PayrollExportNothingNewException_Becomes409WithCodeAndNoBlockers()
    {
        var response = await Invoke(new PayrollExportNothingNewException());

        response.StatusCode.ShouldBe(StatusCodes.Status409Conflict);
        using var problem = JsonDocument.Parse(response.Body);
        problem.RootElement.GetProperty("errorCode").GetString().ShouldBe("payrollExportNothingNew");
        problem.RootElement.TryGetProperty("blockers", out _).ShouldBeFalse();
        problem.RootElement.TryGetProperty("blockerTotal", out _).ShouldBeFalse();
    }

    [Test]
    public async Task PayrollExportConcurrentException_Becomes409WithCodeAndNoBlockers()
    {
        var response = await Invoke(new PayrollExportConcurrentException());

        response.StatusCode.ShouldBe(StatusCodes.Status409Conflict);
        using var problem = JsonDocument.Parse(response.Body);
        problem.RootElement.GetProperty("errorCode").GetString().ShouldBe("payrollExportConcurrent");
        problem.RootElement.TryGetProperty("blockers", out _).ShouldBeFalse();
    }
}
