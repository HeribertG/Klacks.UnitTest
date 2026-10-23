// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Tests ClientRepository.GetDisplayNamesAsync: one batched read that labels the requested clients as
/// "FirstName Name" (falling back to Company), omits unknown ids and never returns clients that were not asked for.
/// </summary>

using Klacks.Api.Domain.Enums;
using Klacks.Api.Domain.Models.Staffs;
using Klacks.Api.Domain.Services.Common;
using Klacks.Api.Infrastructure.Interfaces;
using Klacks.Api.Infrastructure.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Klacks.Api.Infrastructure.Services.Clients;

namespace Klacks.UnitTest.Repository;

[TestFixture]
public class ClientRepositoryDisplayNamesTests
{
    private DataBaseContext _context = null!;
    private ClientRepository _repository = null!;

    [SetUp]
    public void SetUp()
    {
        var options = new DbContextOptionsBuilder<DataBaseContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;

        _context = new DataBaseContext(options, Substitute.For<IHttpContextAccessor>());
        _repository = new ClientRepository(
            _context,
            Substitute.For<IMacroEngine>(),
            Substitute.For<IClientChangeTrackingService>(),
            Substitute.For<IClientEntityManagementService>(),
            new EntityCollectionUpdateService(_context),
            Substitute.For<IClientValidator>(),
            Substitute.For<ILogger<ClientRepository>>());
    }

    [TearDown]
    public void TearDown() => _context.Dispose();

    [Test]
    public async Task GetDisplayNamesAsync_PersonsAreLabelledFirstNameThenName()
    {
        var anna = await SeedAsync("Anna", "Meier", null);

        var names = await _repository.GetDisplayNamesAsync(new[] { anna });

        names[anna].ShouldBe("Anna Meier");
    }

    [Test]
    public async Task GetDisplayNamesAsync_ClientWithoutPersonName_FallsBackToCompany()
    {
        var company = await SeedAsync(string.Empty, string.Empty, "  Muster AG ");

        var names = await _repository.GetDisplayNamesAsync(new[] { company });

        names[company].ShouldBe("Muster AG");
    }

    [Test]
    public async Task GetDisplayNamesAsync_ReturnsOnlyTheRequestedKnownClients()
    {
        var anna = await SeedAsync("Anna", "Meier", null);
        var ben = await SeedAsync("Ben", "Keller", null);
        var unknown = Guid.NewGuid();

        var names = await _repository.GetDisplayNamesAsync(new[] { anna, unknown });

        names.Keys.ShouldBe(new[] { anna });
        names.ContainsKey(ben).ShouldBeFalse();
    }

    [Test]
    public async Task GetDisplayNamesAsync_SoftDeletedClient_IsNotReturned()
    {
        var anna = await SeedAsync("Anna", "Meier", null);
        var deleted = await SeedAsync("Dora", "Gelöscht", null, isDeleted: true);

        var names = await _repository.GetDisplayNamesAsync(new[] { anna, deleted });

        names.Keys.ShouldBe(new[] { anna });
    }

    [Test]
    public async Task GetDisplayNamesAsync_NoIds_ReturnsEmpty()
    {
        await SeedAsync("Anna", "Meier", null);

        var names = await _repository.GetDisplayNamesAsync(Array.Empty<Guid>());

        names.ShouldBeEmpty();
    }

    private async Task<Guid> SeedAsync(string firstName, string name, string? company, bool isDeleted = false)
    {
        var client = new Client
        {
            Id = Guid.NewGuid(),
            Type = EntityTypeEnum.Employee,
            FirstName = firstName,
            Name = name,
            Company = company ?? string.Empty,
            IsDeleted = isDeleted
        };
        _context.Client.Add(client);
        await _context.SaveChangesAsync();
        return client.Id;
    }
}
