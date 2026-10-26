// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Tests the background worker AddressGeocodingBackfillService: every pass resolves the runner from a fresh DI
/// scope (the worker is a singleton), a failing pass is logged and the loop goes on, and stopping the host ends it.
/// </summary>

using Klacks.Api.Application.Interfaces.Geocoding;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Klacks.UnitTest.Services.Geocoding;

[TestFixture]
public class AddressGeocodingBackfillServiceTests
{
    private static readonly TimeSpan WaitLimit = TimeSpan.FromSeconds(10);

    [Test]
    public async Task ExecuteAsync_KeepsRunningPassesAfterAFailure_EachInItsOwnScope()
    {
        // Arrange
        var passes = 0;
        var thirdPass = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var scopes = new HashSet<IAddressGeocodingBackfillRunner>();
        var services = new ServiceCollection();
        services.AddScoped(_ =>
        {
            var runner = Substitute.For<IAddressGeocodingBackfillRunner>();
            runner.RunBatchAsync(Arg.Any<CancellationToken>()).Returns(_ =>
            {
                passes++;
                if (passes == 1)
                {
                    throw new InvalidOperationException("database down");
                }

                if (passes == 3)
                {
                    thirdPass.TrySetResult();
                }

                return Task.FromResult(false);
            });
            scopes.Add(runner);
            return runner;
        });
        using var provider = services.BuildServiceProvider();
        using var worker = new AddressGeocodingBackfillService(
            provider.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<AddressGeocodingBackfillService>.Instance,
            TimeSpan.Zero,
            TimeSpan.Zero);

        // Act
        await worker.StartAsync(CancellationToken.None);
        await thirdPass.Task.WaitAsync(WaitLimit);
        await worker.StopAsync(CancellationToken.None);

        // Assert
        passes.ShouldBeGreaterThanOrEqualTo(3);
        scopes.Count.ShouldBe(passes);
    }
}
