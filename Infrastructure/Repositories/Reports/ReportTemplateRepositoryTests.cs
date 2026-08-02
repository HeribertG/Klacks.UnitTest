// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Tests for ReportTemplateRepository: verifies that updating a template snapshots the previous
/// state, that unchanged saves do not fill the ring buffer and that the list endpoint omits history.
/// </summary>
namespace Klacks.UnitTest.Infrastructure.Repositories.Reports;

using Klacks.Api.Domain.Enums;
using Klacks.Api.Domain.Models.Reports;
using Klacks.Api.Infrastructure.Persistence;
using Klacks.Api.Infrastructure.Repositories.Reports;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using NSubstitute;
using NUnit.Framework;
using Shouldly;

[TestFixture]
public class ReportTemplateRepositoryTests
{
    private DataBaseContext _context = null!;
    private ReportTemplateRepository _repository = null!;

    [SetUp]
    public void SetUp()
    {
        var options = new DbContextOptionsBuilder<DataBaseContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        var httpAccessor = Substitute.For<IHttpContextAccessor>();
        _context = new DataBaseContext(options, httpAccessor);
        _context.Database.EnsureCreated();
        _repository = new ReportTemplateRepository(_context);
    }

    [TearDown]
    public void TearDown()
    {
        _context.Database.EnsureDeleted();
        _context.Dispose();
    }

    private static ReportTemplate BuildTemplate(string name = "Stundenrapport")
    {
        return new ReportTemplate
        {
            Id = Guid.NewGuid(),
            Name = name,
            Description = string.Empty,
            Type = ReportType.Schedule,
            SourceId = "schedule",
            DataSetIds = ["work"],
            PageSetup = new ReportPageSetup(),
            Sections =
            [
                new ReportSection
                {
                    Type = ReportSectionType.WorkTable,
                    Visible = true,
                    SortOrder = 1,
                    Fields =
                    [
                        new ReportField { Name = "Stunden", DataBinding = "entry.hours", Width = 20 },
                    ],
                },
            ],
        };
    }

    private static ReportTemplate CloneForUpdate(ReportTemplate source)
    {
        return new ReportTemplate
        {
            Id = source.Id,
            Name = source.Name,
            Description = source.Description,
            Type = source.Type,
            SourceId = source.SourceId,
            DataSetIds = [.. source.DataSetIds],
            PageSetup = new ReportPageSetup
            {
                Orientation = source.PageSetup.Orientation,
                Size = source.PageSetup.Size,
                Margins = source.PageSetup.Margins,
            },
            Sections = source.Sections
                .Select(s => new ReportSection
                {
                    Type = s.Type,
                    Visible = s.Visible,
                    SortOrder = s.SortOrder,
                    Fields = s.Fields.Select(f => new ReportField
                    {
                        Name = f.Name,
                        DataBinding = f.DataBinding,
                        Width = f.Width,
                    }).ToList(),
                })
                .ToList(),
        };
    }

    [Test]
    public async Task UpdateAsync_stores_the_previous_state_as_a_version()
    {
        var created = await _repository.CreateAsync(BuildTemplate());

        var update = CloneForUpdate(created);
        update.Name = "Stundenrapport neu";
        await _repository.UpdateAsync(update);

        var reloaded = await _repository.GetByIdAsync(created.Id);
        reloaded!.Name.ShouldBe("Stundenrapport neu");
        reloaded.Versions.Count.ShouldBe(1);
        reloaded.Versions[0].Name.ShouldBe("Stundenrapport");
    }

    [Test]
    public async Task UpdateAsync_does_not_snapshot_when_nothing_changed()
    {
        var created = await _repository.CreateAsync(BuildTemplate());

        await _repository.UpdateAsync(CloneForUpdate(created));
        await _repository.UpdateAsync(CloneForUpdate(created));
        await _repository.UpdateAsync(CloneForUpdate(created));

        var reloaded = await _repository.GetByIdAsync(created.Id);
        reloaded!.Versions.ShouldBeEmpty();
    }

    [Test]
    public async Task UpdateAsync_snapshots_a_changed_layout()
    {
        var created = await _repository.CreateAsync(BuildTemplate());

        var update = CloneForUpdate(created);
        update.Sections[0].Fields.Add(new ReportField { Name = "Datum", DataBinding = "entry.date", Width = 15 });
        await _repository.UpdateAsync(update);

        var reloaded = await _repository.GetByIdAsync(created.Id);
        reloaded!.Versions.Count.ShouldBe(1);
        reloaded.Versions[0].Sections[0].Fields.Count.ShouldBe(1);
    }

    [Test]
    public async Task UpdateAsync_keeps_only_the_newest_versions()
    {
        var created = await _repository.CreateAsync(BuildTemplate());

        for (var i = 1; i <= 13; i++)
        {
            var update = CloneForUpdate(created);
            update.Name = $"Fassung {i}";
            await _repository.UpdateAsync(update);
            created = (await _repository.GetByIdAsync(created.Id))!;
        }

        var reloaded = await _repository.GetByIdAsync(created.Id);
        reloaded!.Versions.Count.ShouldBe(10);
        reloaded.Versions.First().Name.ShouldBe("Fassung 3");
        reloaded.Versions.Last().Name.ShouldBe("Fassung 12");
    }

    [Test]
    public async Task GetAllAsync_does_not_carry_the_version_history()
    {
        var created = await _repository.CreateAsync(BuildTemplate());
        var update = CloneForUpdate(created);
        update.Name = "Zweite Fassung";
        await _repository.UpdateAsync(update);

        var all = await _repository.GetAllAsync();

        all.Single().Versions.ShouldBeEmpty();
    }
}
