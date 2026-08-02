// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Guards that a PeriodicTimer-driven background service ends cleanly when the host stops it.
/// WaitForNextTickAsync throws OperationCanceledException on the stopping token; unless the loop is
/// wrapped, that exception faults ExecuteTask and the host reports it as an unhandled background
/// service failure, which buries real errors in the log. StopAsync itself never rethrows, so the
/// assertion has to inspect ExecuteTask rather than expect a throw.
/// </summary>

using Klacks.Api.Infrastructure.Services.Schedules;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Klacks.UnitTest.Infrastructure.Services;

[TestFixture]
public class BackgroundServiceShutdownTests
{
    private static readonly TimeSpan StartupGrace = TimeSpan.FromMilliseconds(200);

    private ServiceProvider _serviceProvider = null!;

    [SetUp]
    public void SetUp()
    {
        _serviceProvider = new ServiceCollection().BuildServiceProvider();
    }

    [TearDown]
    public async Task TearDown()
    {
        await _serviceProvider.DisposeAsync();
    }

    [Test]
    public async Task WizardRunCaptureMeasurement_WhenStopped_DoesNotFaultTheExecuteTask()
    {
        using var sut = new WizardRunCaptureMeasurementBackgroundService(
            _serviceProvider.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<WizardRunCaptureMeasurementBackgroundService>.Instance);

        await sut.StartAsync(CancellationToken.None);
        await Task.Delay(StartupGrace);

        await sut.StopAsync(CancellationToken.None);

        sut.ExecuteTask.ShouldNotBeNull();
        sut.ExecuteTask!.Status.ShouldBe(
            TaskStatus.RanToCompletion,
            "the service must swallow the OperationCanceledException its own stopping token raises " +
            "inside WaitForNextTickAsync and return normally. Without that the task ends up Canceled, " +
            "which surfaces as a first-chance exception in the debugger on every shutdown.");
    }

}
