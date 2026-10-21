// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Drives the real ScheduleTimelineBackgroundService through a real service container: an in-memory
/// DataBaseContext, the real TimelineCalculationService and one substitute per evaluator. Every evaluator
/// returns a single marker entry whose Comment names it, so a test can pin the exact entry order. Requests are
/// queued before StartAsync, which makes the drain/dedupe step deterministic; notifications are awaited through
/// a semaphore with a timeout instead of sleeps.
/// </summary>

using System.Collections.Concurrent;
using Klacks.Api.Application.DTOs.Notifications;
using Klacks.Api.Application.DTOs.Schedules;
using Klacks.Api.Application.Interfaces.Schedules;
using Klacks.Api.Application.Services.Schedules;
using Klacks.Api.Domain.Interfaces.RouteOptimization;
using Klacks.Api.Domain.Models.Scheduling;
using Klacks.Api.Domain.Services.Schedules;
using Microsoft.AspNetCore.Http;
using Klacks.Api.Infrastructure.Extensions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Shouldly;

namespace Klacks.UnitTest.Infrastructure.Services.ScheduleTimeline;

internal sealed class ScheduleTimelineTestHost : IAsyncDisposable
{
    public const string PeriodCapMarker = "periodCap";
    public const string RestDayRotationMarker = "restDayRotation";
    public const string CounterRuleMarker = "counterRule";
    public const string RestrictedWindowMarker = "restrictedWindow";
    public const string CompensatoryRestMarker = "compensatoryRest";
    public const string HolidayWorkMarker = "holidayWork";
    public const string PlanningDayMarker = "planningDay";
    public const string PlanningRangeMarker = "planningRange";

    private static readonly TimeSpan NotificationTimeout = TimeSpan.FromSeconds(20);

    private readonly ServiceProvider _provider;
    private readonly SemaphoreSlim _validationSignal = new(0);
    private bool _started;

    public ScheduleTimelineTestHost()
    {
        var databaseName = Guid.NewGuid().ToString();
        ContextOptions = new DbContextOptionsBuilder<DataBaseContext>()
            .UseInMemoryDatabase(databaseName, builder => builder.EnableNullChecks(false))
            .Options;

        Notifications = Substitute.For<IWorkNotificationService>();
        Notifications.NotifyCollisionsDetected(Arg.Any<CollisionListNotificationDto>())
            .Returns(call =>
            {
                Collisions.Enqueue(call.Arg<CollisionListNotificationDto>());
                return Task.CompletedTask;
            });
        Notifications.NotifyScheduleValidationsDetected(Arg.Any<ScheduleValidationListNotificationDto>())
            .Returns(call =>
            {
                Validations.Enqueue(call.Arg<ScheduleValidationListNotificationDto>());
                _validationSignal.Release();
                return Task.CompletedTask;
            });

        Travel = Substitute.For<ITravelTimeCalculationService>();
        Policies = Substitute.For<ISchedulingPolicyResolver>();
        Policies.GetForClientAsync(Arg.Any<Guid>(), Arg.Any<DateOnly>()).Returns(Policy());
        Policies.GetForClientsAsync(Arg.Any<IReadOnlyList<Guid>>(), Arg.Any<DateOnly>())
            .Returns(call => (IReadOnlyDictionary<Guid, SchedulingPolicy>)call.Arg<IReadOnlyList<Guid>>()
                .ToDictionary(id => id, _ => Policy()));
        Eligibility = Substitute.For<IEligibilityMatrixBuilder>();
        Eligibility.BuildAsync(
                Arg.Any<IReadOnlyCollection<Guid>>(),
                Arg.Any<IReadOnlyCollection<EligibilitySlot>>(),
                Arg.Any<IReadOnlySet<(string AgentId, Guid ShiftId, DateOnly Date)>?>(),
                Arg.Any<CancellationToken>())
            .Returns(EligibilityMatrix.Empty);

        PeriodCap = Substitute.For<IPeriodCapEvaluator>();
        PeriodCap.EvaluateAsync(Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<DateOnly>(), Arg.Any<Guid?>(), Arg.Any<CancellationToken>())
            .Returns(call => Marker(PeriodCapMarker, call.ArgAt<Guid>(0)));
        RestDayRotation = Substitute.For<IRestDayRotationEvaluator>();
        RestDayRotation.EvaluateAsync(Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<DateOnly>(), Arg.Any<Guid?>(), Arg.Any<CancellationToken>())
            .Returns(call => Marker(RestDayRotationMarker, call.ArgAt<Guid>(0)));
        CounterRule = Substitute.For<ICounterRuleEvaluator>();
        CounterRule.EvaluateAsync(Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<DateOnly>(), Arg.Any<Guid?>(), Arg.Any<CancellationToken>())
            .Returns(call => Marker(CounterRuleMarker, call.ArgAt<Guid>(0)));
        RestrictedWindow = Substitute.For<IRestrictedTimeWindowEvaluator>();
        RestrictedWindow.EvaluateAsync(Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<DateOnly>(), Arg.Any<Guid?>(), Arg.Any<CancellationToken>())
            .Returns(call => Marker(RestrictedWindowMarker, call.ArgAt<Guid>(0)));
        RestrictedWindow.EvaluateRangeAsync(Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<DateOnly>(), Arg.Any<DateOnly>(), Arg.Any<Guid?>(), Arg.Any<CancellationToken>())
            .Returns(call => Marker(RestrictedWindowMarker, call.ArgAt<Guid>(0)));
        CompensatoryReconciler = Substitute.For<ICompensatoryRestObligationReconciler>();
        CompensatoryEvaluator = Substitute.For<ICompensatoryRestEvaluator>();
        CompensatoryEvaluator.EvaluateAsync(Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<DateOnly>(), Arg.Any<Guid?>(), Arg.Any<CancellationToken>())
            .Returns(call => Marker(CompensatoryRestMarker, call.ArgAt<Guid>(0)));
        Escalation = Substitute.For<IComplianceEscalationService>();
        HolidayWork = Substitute.For<IHolidayWorkEvaluator>();
        HolidayWork.EvaluateAsync(Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<IReadOnlyCollection<DateOnly>>(), Arg.Any<CancellationToken>())
            .Returns(call => Marker(HolidayWorkMarker, call.ArgAt<Guid>(0)));
        PlanningRules = Substitute.For<IPlanningRuleEvaluatorService>();
        PlanningRules.EvaluateDayAsync(Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<DateOnly>(), Arg.Any<Guid?>(), Arg.Any<CancellationToken>())
            .Returns(call => Marker(PlanningDayMarker, call.ArgAt<Guid>(0)));
        PlanningRules.EvaluateRangeAsync(
                Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<DateOnly>(), Arg.Any<DateOnly>(), Arg.Any<Guid?>(),
                Arg.Any<IReadOnlyDictionary<Guid, string>>(), Arg.Any<CancellationToken>())
            .Returns(_ => Marker(PlanningRangeMarker, Guid.Empty));
        Store = Substitute.For<IScheduleTimelineStore>();

        _provider = BuildProvider().BuildServiceProvider();
        Service = new ScheduleTimelineBackgroundService(
            _provider, NullLogger<ScheduleTimelineBackgroundService>.Instance);
    }

    public DbContextOptions<DataBaseContext> ContextOptions { get; }

    public IWorkNotificationService Notifications { get; }

    public ITravelTimeCalculationService Travel { get; }

    public ISchedulingPolicyResolver Policies { get; }

    public IEligibilityMatrixBuilder Eligibility { get; }

    public IPeriodCapEvaluator PeriodCap { get; }

    public IRestDayRotationEvaluator RestDayRotation { get; }

    public ICounterRuleEvaluator CounterRule { get; }

    public IRestrictedTimeWindowEvaluator RestrictedWindow { get; }

    public ICompensatoryRestObligationReconciler CompensatoryReconciler { get; }

    public ICompensatoryRestEvaluator CompensatoryEvaluator { get; }

    public IComplianceEscalationService Escalation { get; }

    public IHolidayWorkEvaluator HolidayWork { get; }

    public IPlanningRuleEvaluatorService PlanningRules { get; }

    public IScheduleTimelineStore Store { get; }

    public ScheduleTimelineBackgroundService Service { get; }

    public ConcurrentQueue<CollisionListNotificationDto> Collisions { get; } = new();

    public ConcurrentQueue<ScheduleValidationListNotificationDto> Validations { get; } = new();

    public static SchedulingPolicy Policy()
        => new(
            MinRestHours: TimeSpan.FromHours(11),
            MaxDailyHours: TimeSpan.FromHours(10),
            MaxConsecutiveDays: 6,
            MaxWeeklyHours: TimeSpan.FromHours(50),
            MinRestDays: 1);

    public static List<ScheduleValidationNotificationDto> Marker(string comment, Guid clientId)
        => [new ScheduleValidationNotificationDto { ClientId = clientId, Comment = comment }];

    public DataBaseContext NewContext() => new(ContextOptions, Substitute.For<IHttpContextAccessor>());

    public async Task<Guid> AddEmployeeAsync(string name, string firstName)
    {
        await using var context = NewContext();
        var employee = new Client { Id = Guid.NewGuid(), Name = name, FirstName = firstName };
        context.Client.Add(employee);
        await context.SaveChangesAsync();
        return employee.Id;
    }

    public async Task<Guid> AddCustomerShiftAsync(string street, string zip, string city)
    {
        await using var context = NewContext();
        var customer = new Client { Id = Guid.NewGuid(), Name = city };
        context.Client.Add(customer);
        context.Address.Add(new Address
        {
            Id = Guid.NewGuid(),
            ClientId = customer.Id,
            ValidFrom = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            Street = street,
            Zip = zip,
            City = city,
        });
        var shift = new Shift { Id = Guid.NewGuid(), Name = city, ClientId = customer.Id };
        context.Shift.Add(shift);
        await context.SaveChangesAsync();
        return shift.Id;
    }

    public async Task<Work> AddWorkAsync(Guid clientId, Guid shiftId, DateOnly date, TimeOnly start, TimeOnly end)
    {
        await using var context = NewContext();
        var work = new Work
        {
            Id = Guid.NewGuid(),
            ClientId = clientId,
            ShiftId = shiftId,
            CurrentDate = date,
            StartTime = start,
            EndTime = end,
            WorkTime = (decimal)(end - start).TotalHours,
        };
        context.Work.Add(work);
        await context.SaveChangesAsync();
        return work;
    }

    public async Task StartAsync()
    {
        _started = true;
        await Service.StartAsync(CancellationToken.None);
    }

    public async Task<IReadOnlyList<ScheduleValidationListNotificationDto>> WaitForValidationsAsync(int count)
    {
        for (var i = 0; i < count; i++)
        {
            var signalled = await _validationSignal.WaitAsync(NotificationTimeout);
            signalled.ShouldBeTrue($"validation notification {i + 1} of {count} did not arrive in time");
        }

        return Validations.ToList();
    }

    public async ValueTask DisposeAsync()
    {
        if (_started)
        {
            await Service.StopAsync(CancellationToken.None);
        }

        Service.Dispose();
        await _provider.DisposeAsync();
        _validationSignal.Dispose();
    }

    private ServiceCollection BuildProvider()
    {
        var services = new ServiceCollection();
        services.AddScoped(_ => NewContext());
        services.AddSingleton<ITimelineCalculationService>(new TimelineCalculationService(
            Options.Create(new ScheduleTimeOptions()),
            NullLogger<TimelineCalculationService>.Instance));
        services.AddSingleton(Notifications);
        services.AddSingleton(Travel);
        services.AddSingleton(Policies);
        services.AddSingleton(Eligibility);
        services.AddSingleton(PeriodCap);
        services.AddSingleton(RestDayRotation);
        services.AddSingleton(CounterRule);
        services.AddSingleton(RestrictedWindow);
        services.AddSingleton(CompensatoryReconciler);
        services.AddSingleton(CompensatoryEvaluator);
        services.AddSingleton(Escalation);
        services.AddSingleton(HolidayWork);
        services.AddSingleton(PlanningRules);
        services.AddSingleton(Store);
        services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
        services.AddScheduleTimelineChecks();
        return services;
    }
}
