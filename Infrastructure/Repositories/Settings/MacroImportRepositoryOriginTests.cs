// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Unit tests pinning that every macro inserted through the region-setup import repository is recorded
/// with the Import origin, whatever origin the caller left on the entity.
/// </summary>

using Klacks.Api.Infrastructure.Repositories.Settings;
using Klacks.Api.Infrastructure.Scripting;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;

namespace Klacks.UnitTest.Infrastructure.Repositories.Settings;

[TestFixture]
public class MacroImportRepositoryOriginTests
{
    private DataBaseContext _context = null!;

    [SetUp]
    public void SetUp()
    {
        var options = new DbContextOptionsBuilder<DataBaseContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        _context = new DataBaseContext(options, Substitute.For<IHttpContextAccessor>());
    }

    [TearDown]
    public void TearDown()
    {
        _context.Dispose();
    }

    [Test]
    public async Task Add_RecordsImportOrigin()
    {
        var repository = new MacroImportRepository(_context, new MacroCache());
        var macro = new Macro
        {
            Id = Guid.NewGuid(),
            Name = "Imported",
            Content = "OUTPUT 1, 0",
            Description = new MultiLanguage(),
            ImportSourceKey = "region-setup:macros.imported",
            Origin = MacroOrigin.User
        };

        repository.Add(macro);
        await _context.SaveChangesAsync();

        (await _context.Macro.AsNoTracking().SingleAsync(m => m.Id == macro.Id)).Origin.ShouldBe(MacroOrigin.Import);
    }
}
