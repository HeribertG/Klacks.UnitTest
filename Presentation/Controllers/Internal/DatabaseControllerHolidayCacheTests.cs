// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Guards that the admin database endpoints drop all cached holiday calculators after every run, including a
/// failed one: migrations (e.g. statutory holiday promotions) and the seed write calendar rules directly in the
/// database and may have committed part of their work before the failure.
/// </summary>

using Klacks.Api.Presentation.Controllers.Internal;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Klacks.UnitTest.Presentation.Controllers.Internal;

[TestFixture]
public class DatabaseControllerHolidayCacheTests
{
    private const string FailureMessage = "Simulated failure after partial writes";

    private IDatabaseInitializer _initializer = null!;
    private IHolidayCalculatorCache _holidayCache = null!;
    private DatabaseController _controller = null!;

    [SetUp]
    public void SetUp()
    {
        _initializer = Substitute.For<IDatabaseInitializer>();
        _holidayCache = Substitute.For<IHolidayCalculatorCache>();
        _controller = new DatabaseController(_initializer, _holidayCache, Substitute.For<ILogger<DatabaseController>>());
    }

    [Test]
    public async Task InitializeDatabase_Success_InvalidatesAfterInitialization()
    {
        var result = await _controller.InitializeDatabase();

        result.ShouldBeOfType<OkObjectResult>();
        Received.InOrder(() =>
        {
            _initializer.InitializeAsync();
            _holidayCache.InvalidateAll();
        });
    }

    [Test]
    public async Task SeedDatabase_Success_InvalidatesAfterSeeding()
    {
        var result = await _controller.SeedDatabase();

        result.ShouldBeOfType<OkObjectResult>();
        Received.InOrder(() =>
        {
            _initializer.SeedDataAsync();
            _holidayCache.InvalidateAll();
        });
    }

    [Test]
    public async Task InitializeDatabase_FailureAfterPartialWrites_StillInvalidates()
    {
        _initializer.InitializeAsync().Returns(Task.FromException(new InvalidOperationException(FailureMessage)));

        var result = await _controller.InitializeDatabase();

        result.ShouldBeOfType<ObjectResult>().StatusCode.ShouldBe(StatusCodes.Status500InternalServerError);
        _holidayCache.Received(1).InvalidateAll();
    }

    [Test]
    public async Task SeedDatabase_FailureAfterPartialWrites_StillInvalidates()
    {
        _initializer.SeedDataAsync().Returns(Task.FromException(new InvalidOperationException(FailureMessage)));

        var result = await _controller.SeedDatabase();

        result.ShouldBeOfType<ObjectResult>().StatusCode.ShouldBe(StatusCodes.Status500InternalServerError);
        _holidayCache.Received(1).InvalidateAll();
    }
}
