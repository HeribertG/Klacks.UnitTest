// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Tests for AnalyseScenarioService, focused on the DateTime.Kind produced for
/// GroupItem.ValidFrom/ValidUntil when a scenario membership is created from a DateOnly.
/// The GroupItem.ValidFrom/ValidUntil columns are mapped as "timestamp with time zone" in
/// PostgreSQL, which requires DateTimeKind.Utc; DateOnly.ToDateTime(TimeOnly) alone produces
/// DateTimeKind.Unspecified and fails at SaveChanges with an Npgsql ArgumentException.
/// </summary>
using Klacks.Api.Domain.Models.Associations;
using Klacks.Api.Infrastructure.Persistence;
using Klacks.Api.Infrastructure.Services.AnalyseScenarios;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using NSubstitute;
using Shouldly;

namespace Klacks.UnitTest.Infrastructure.Services.AnalyseScenarios;

[TestFixture]
public class AnalyseScenarioServiceTests
{
    private DataBaseContext _context = null!;
    private AnalyseScenarioService _service = null!;

    [SetUp]
    public void Setup()
    {
        var options = new DbContextOptionsBuilder<DataBaseContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;
        var httpContextAccessor = Substitute.For<IHttpContextAccessor>();
        _context = new DataBaseContext(options, httpContextAccessor);
        _service = new AnalyseScenarioService(_context);
    }

    [TearDown]
    public void TearDown()
    {
        _context.Dispose();
    }

    [Test]
    public async Task AddScenarioMembershipAsync_StampsValidFromAndValidUntil_WithUtcKind()
    {
        var token = Guid.NewGuid();
        var clientId = Guid.NewGuid();
        var groupId = Guid.NewGuid();
        var validFrom = new DateOnly(2026, 7, 1);
        var validUntil = new DateOnly(2026, 7, 31);

        await _service.AddScenarioMembershipAsync(token, clientId, groupId, validFrom, validUntil, CancellationToken.None);

        var membership = _context.ChangeTracker.Entries<GroupItem>().Single().Entity;
        membership.ValidFrom.ShouldNotBeNull();
        membership.ValidUntil.ShouldNotBeNull();
        membership.ValidFrom!.Value.Kind.ShouldBe(DateTimeKind.Utc);
        membership.ValidUntil!.Value.Kind.ShouldBe(DateTimeKind.Utc);
    }
}
