// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Unit tests for create_calendar_selection, update_calendar_selection and
/// delete_calendar_selection. Covers token parsing (national vs. subdivision notation,
/// reminderOnly to OfficialOverride mapping), validation errors, the no-op update path, the
/// self-verification rollback path (a mismatch on re-read must surface as an error, never a
/// false success) and the delete guards (seeded system data / still referenced).
/// </summary>

using Klacks.Api.Application.Commands;
using Klacks.Api.Application.DTOs.Schedules;
using Klacks.Api.Application.Skills;
using Klacks.Api.Domain.Exceptions;
using Klacks.Api.Infrastructure.Mediator;

namespace Klacks.UnitTest.Skills;

[TestFixture]
public class CalendarSelectionCrudSkillTests
{
    private IMediator _mediator = null!;
    private ICalendarSelectionRepository _calendarSelectionRepository = null!;
    private ISettingsTokenService _settingsTokenService = null!;
    private IUnitOfWork _unitOfWork = null!;

    [SetUp]
    public void Setup()
    {
        _mediator = Substitute.For<IMediator>();
        _calendarSelectionRepository = Substitute.For<ICalendarSelectionRepository>();
        _settingsTokenService = Substitute.For<ISettingsTokenService>();
        _unitOfWork = Substitute.For<IUnitOfWork>();

        _settingsTokenService.GetRuleTokenListAsync(Arg.Any<bool>()).Returns(AvailableTokens());

        _unitOfWork.ExecuteInTransactionAsync(Arg.Any<Func<Task<Guid>>>())
            .Returns(ci => ci.Arg<Func<Task<Guid>>>()());
        _unitOfWork.ExecuteInTransactionAsync(Arg.Any<Func<Task<bool>>>())
            .Returns(ci => ci.Arg<Func<Task<bool>>>()());
    }

    private static List<StateCountryToken> AvailableTokens() => new()
    {
        new StateCountryToken { Id = Guid.NewGuid(), Country = "US", State = "US", CountryName = MultiLanguage.Empty(), StateName = MultiLanguage.Empty() },
        new StateCountryToken { Id = Guid.NewGuid(), Country = "CH", State = "CH", CountryName = MultiLanguage.Empty(), StateName = MultiLanguage.Empty() },
        new StateCountryToken { Id = Guid.NewGuid(), Country = "CH", State = "BE", CountryName = MultiLanguage.Empty(), StateName = MultiLanguage.Empty() }
    };

    private static SkillExecutionContext Ctx() => new()
    {
        UserId = Guid.NewGuid(),
        TenantId = Guid.NewGuid(),
        UserName = "tester",
        UserPermissions = new List<string> { "CanEditSettings" }
    };

    private static CalendarSelection ExistingSelection(Guid id) => new()
    {
        Id = id,
        Name = "Bern",
        SelectedCalendars = new List<SelectedCalendar>
        {
            new() { Id = Guid.NewGuid(), CalendarSelectionId = id, Country = "CH", State = "BE", OfficialOverride = null }
        }
    };

    private CreateCalendarSelectionSkill CreateSkill() =>
        new(_mediator, _calendarSelectionRepository, _settingsTokenService, _unitOfWork);

    private UpdateCalendarSelectionSkill UpdateSkill() =>
        new(_mediator, _calendarSelectionRepository, _settingsTokenService, _unitOfWork);

    private DeleteCalendarSelectionSkill DeleteSkill() =>
        new(_mediator, _calendarSelectionRepository, _unitOfWork);

    [Test]
    public async Task Create_Succeeds_WithNationalAndCantonTokens_AndReminderOnly()
    {
        var skill = CreateSkill();
        var newId = Guid.NewGuid();
        CalendarSelectionResource? sentResource = null;
        _mediator.Send(Arg.Do<PostCommand<CalendarSelectionResource>>(c => sentResource = c.Resource), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                sentResource!.Id = newId;
                return Task.FromResult<CalendarSelectionResource?>(sentResource);
            });
        _calendarSelectionRepository.GetNoTrackingWithSelectedCalendars(newId).Returns(_ => new CalendarSelection
        {
            Id = newId,
            Name = sentResource!.Name,
            SelectedCalendars = sentResource.SelectedCalendars.Select(sc => new SelectedCalendar
            {
                Id = Guid.NewGuid(),
                CalendarSelectionId = newId,
                Country = sc.Country,
                State = sc.State,
                OfficialOverride = sc.OfficialOverride
            }).ToList()
        });

        var parameters = new Dictionary<string, object>
        {
            ["name"] = "Bern+USA",
            ["calendars"] = new List<string> { "US", "CH-BE" },
            ["reminderOnlyCalendars"] = new List<string> { "US" }
        };

        var result = await skill.ExecuteAsync(Ctx(), parameters);

        Assert.That(result.Success, Is.True, result.Message);
        Assert.That(result.Message, Does.Contain("verified"));
        Assert.That(sentResource!.SelectedCalendars.Count, Is.EqualTo(2));
        Assert.That(sentResource.SelectedCalendars.Single(sc => sc.Country == "US" && sc.State == "US").OfficialOverride, Is.False);
        Assert.That(sentResource.SelectedCalendars.Single(sc => sc.Country == "CH" && sc.State == "BE").OfficialOverride, Is.Null);
    }

    [Test]
    public async Task Create_ReturnsError_WhenCalendarsMissing()
    {
        var skill = CreateSkill();
        var parameters = new Dictionary<string, object> { ["name"] = "Bern" };

        var result = await skill.ExecuteAsync(Ctx(), parameters);

        Assert.That(result.Success, Is.False);
        Assert.That(result.Message, Does.Contain("calendars"));
        await _mediator.DidNotReceive().Send(Arg.Any<PostCommand<CalendarSelectionResource>>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task Create_ReturnsError_WhenTokenUnknown()
    {
        var skill = CreateSkill();
        var parameters = new Dictionary<string, object>
        {
            ["name"] = "Bern",
            ["calendars"] = new List<string> { "ZZ-ZZ" }
        };

        var result = await skill.ExecuteAsync(Ctx(), parameters);

        Assert.That(result.Success, Is.False);
        Assert.That(result.Message, Does.Contain("does not match a known"));
        await _mediator.DidNotReceive().Send(Arg.Any<PostCommand<CalendarSelectionResource>>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task Create_ReturnsError_WhenReminderOnlyTokenNotInCalendars()
    {
        var skill = CreateSkill();
        var parameters = new Dictionary<string, object>
        {
            ["name"] = "Bern",
            ["calendars"] = new List<string> { "US" },
            ["reminderOnlyCalendars"] = new List<string> { "CH-BE" }
        };

        var result = await skill.ExecuteAsync(Ctx(), parameters);

        Assert.That(result.Success, Is.False);
        Assert.That(result.Message, Does.Contain("not part of 'calendars'"));
    }

    [Test]
    public async Task Create_ReturnsError_WhenVerificationFails()
    {
        var skill = CreateSkill();
        var newId = Guid.NewGuid();
        _mediator.Send(Arg.Any<PostCommand<CalendarSelectionResource>>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<CalendarSelectionResource?>(new CalendarSelectionResource { Id = newId, Name = "Bern" }));
        _calendarSelectionRepository.GetNoTrackingWithSelectedCalendars(newId).Returns((CalendarSelection?)null);

        var parameters = new Dictionary<string, object>
        {
            ["name"] = "Bern",
            ["calendars"] = new List<string> { "US" }
        };

        var result = await skill.ExecuteAsync(Ctx(), parameters);

        Assert.That(result.Success, Is.False);
        Assert.That(result.Message, Does.Contain("verification"));
    }

    [Test]
    public async Task Update_NoOp_WhenNoFieldsSupplied()
    {
        var id = Guid.NewGuid();
        _calendarSelectionRepository.List().Returns(new List<CalendarSelection> { ExistingSelection(id) });
        var skill = UpdateSkill();

        var result = await skill.ExecuteAsync(Ctx(), new Dictionary<string, object> { ["calendarSelectionId"] = id.ToString() });

        Assert.That(result.Success, Is.True);
        Assert.That(result.Message, Does.Contain("No fields"));
        await _mediator.DidNotReceive().Send(Arg.Any<PutCommand<CalendarSelectionResource>>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task Update_RenamesMergesToken_AndSetsReminderOnly_AndPersists()
    {
        var id = Guid.NewGuid();
        _calendarSelectionRepository.List().Returns(new List<CalendarSelection> { ExistingSelection(id) });
        CalendarSelectionResource? sentResource = null;
        _mediator.Send(Arg.Do<PutCommand<CalendarSelectionResource>>(c => sentResource = c.Resource), Arg.Any<CancellationToken>())
            .Returns(ci => Task.FromResult<CalendarSelectionResource?>(sentResource));
        _calendarSelectionRepository.GetNoTrackingWithSelectedCalendars(id).Returns(_ => new CalendarSelection
        {
            Id = id,
            Name = sentResource!.Name,
            SelectedCalendars = sentResource.SelectedCalendars.Select(sc => new SelectedCalendar
            {
                Id = Guid.NewGuid(),
                CalendarSelectionId = id,
                Country = sc.Country,
                State = sc.State,
                OfficialOverride = sc.OfficialOverride
            }).ToList()
        });

        var skill = UpdateSkill();
        var parameters = new Dictionary<string, object>
        {
            ["calendarSelectionId"] = id.ToString(),
            ["newName"] = "Bern City",
            ["addCalendars"] = new List<string> { "US" },
            ["reminderOnlyCalendars"] = new List<string> { "US" }
        };

        var result = await skill.ExecuteAsync(Ctx(), parameters);

        Assert.That(result.Success, Is.True, result.Message);
        Assert.That(result.Message, Does.Contain("verified"));
        Assert.That(sentResource!.Name, Is.EqualTo("Bern City"));
        Assert.That(sentResource.SelectedCalendars.Count, Is.EqualTo(2));
        Assert.That(sentResource.SelectedCalendars.Single(sc => sc.Country == "US").OfficialOverride, Is.False);
        Assert.That(sentResource.SelectedCalendars.Single(sc => sc.Country == "CH" && sc.State == "BE").OfficialOverride, Is.Null);
    }

    [Test]
    public async Task Update_RemovesToken_WhenOthersRemain()
    {
        var id = Guid.NewGuid();
        var existing = ExistingSelection(id);
        existing.SelectedCalendars.Add(new SelectedCalendar { Id = Guid.NewGuid(), CalendarSelectionId = id, Country = "US", State = "US", OfficialOverride = null });
        _calendarSelectionRepository.List().Returns(new List<CalendarSelection> { existing });
        CalendarSelectionResource? sentResource = null;
        _mediator.Send(Arg.Do<PutCommand<CalendarSelectionResource>>(c => sentResource = c.Resource), Arg.Any<CancellationToken>())
            .Returns(ci => Task.FromResult<CalendarSelectionResource?>(sentResource));
        _calendarSelectionRepository.GetNoTrackingWithSelectedCalendars(id).Returns(_ => new CalendarSelection
        {
            Id = id,
            Name = sentResource!.Name,
            SelectedCalendars = sentResource.SelectedCalendars.Select(sc => new SelectedCalendar
            {
                Id = Guid.NewGuid(),
                CalendarSelectionId = id,
                Country = sc.Country,
                State = sc.State,
                OfficialOverride = sc.OfficialOverride
            }).ToList()
        });

        var skill = UpdateSkill();
        var result = await skill.ExecuteAsync(Ctx(), new Dictionary<string, object>
        {
            ["calendarSelectionId"] = id.ToString(),
            ["removeCalendars"] = new List<string> { "CH-BE" }
        });

        Assert.That(result.Success, Is.True, result.Message);
        Assert.That(sentResource!.SelectedCalendars.Count, Is.EqualTo(1));
        Assert.That(sentResource.SelectedCalendars.Single().Country, Is.EqualTo("US"));
    }

    [Test]
    public async Task Update_ReturnsError_WhenRemovingLastCalendar()
    {
        var id = Guid.NewGuid();
        _calendarSelectionRepository.List().Returns(new List<CalendarSelection> { ExistingSelection(id) });
        var skill = UpdateSkill();

        var result = await skill.ExecuteAsync(Ctx(), new Dictionary<string, object>
        {
            ["calendarSelectionId"] = id.ToString(),
            ["removeCalendars"] = new List<string> { "CH-BE" }
        });

        Assert.That(result.Success, Is.False);
        Assert.That(result.Message, Does.Contain("at least one"));
        await _mediator.DidNotReceive().Send(Arg.Any<PutCommand<CalendarSelectionResource>>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task Update_ReturnsError_WhenAddTokenUnknown()
    {
        var id = Guid.NewGuid();
        _calendarSelectionRepository.List().Returns(new List<CalendarSelection> { ExistingSelection(id) });
        var skill = UpdateSkill();

        var result = await skill.ExecuteAsync(Ctx(), new Dictionary<string, object>
        {
            ["calendarSelectionId"] = id.ToString(),
            ["addCalendars"] = new List<string> { "ZZ-ZZ" }
        });

        Assert.That(result.Success, Is.False);
        Assert.That(result.Message, Does.Contain("does not match a known"));
    }

    [Test]
    public async Task Update_ReturnsError_WhenReminderAndInheritConflict()
    {
        var id = Guid.NewGuid();
        _calendarSelectionRepository.List().Returns(new List<CalendarSelection> { ExistingSelection(id) });
        var skill = UpdateSkill();

        var result = await skill.ExecuteAsync(Ctx(), new Dictionary<string, object>
        {
            ["calendarSelectionId"] = id.ToString(),
            ["reminderOnlyCalendars"] = new List<string> { "CH-BE" },
            ["inheritCalendars"] = new List<string> { "CH-BE" }
        });

        Assert.That(result.Success, Is.False);
        Assert.That(result.Message, Does.Contain("both"));
    }

    [Test]
    public async Task Update_ReturnsError_WhenSelectionNotFound()
    {
        _calendarSelectionRepository.List().Returns(new List<CalendarSelection>());
        var skill = UpdateSkill();

        var result = await skill.ExecuteAsync(Ctx(), new Dictionary<string, object>
        {
            ["calendarSelectionId"] = Guid.NewGuid().ToString(),
            ["newName"] = "X"
        });

        Assert.That(result.Success, Is.False);
        Assert.That(result.Message, Does.Contain("not found"));
    }

    [Test]
    public async Task Update_ReturnsError_WhenVerificationFails()
    {
        var id = Guid.NewGuid();
        var existing = ExistingSelection(id);
        _calendarSelectionRepository.List().Returns(new List<CalendarSelection> { existing });
        _mediator.Send(Arg.Any<PutCommand<CalendarSelectionResource>>(), Arg.Any<CancellationToken>())
            .Returns(ci => Task.FromResult<CalendarSelectionResource?>(ci.Arg<PutCommand<CalendarSelectionResource>>().Resource));
        _calendarSelectionRepository.GetNoTrackingWithSelectedCalendars(id).Returns(existing);

        var skill = UpdateSkill();
        var result = await skill.ExecuteAsync(Ctx(), new Dictionary<string, object>
        {
            ["calendarSelectionId"] = id.ToString(),
            ["newName"] = "Bern City"
        });

        Assert.That(result.Success, Is.False);
        Assert.That(result.Message, Does.Contain("verification"));
    }

    [Test]
    public async Task Delete_Succeeds_AndConfirmsRemoval()
    {
        var id = Guid.NewGuid();
        var existing = ExistingSelection(id);
        _calendarSelectionRepository.List().Returns(new List<CalendarSelection> { existing });
        _mediator.Send(Arg.Any<DeleteCommand<CalendarSelectionResource>>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<CalendarSelectionResource?>(new CalendarSelectionResource { Id = id, Name = existing.Name }));
        _calendarSelectionRepository.GetNoTracking(id).Returns((CalendarSelection?)null);

        var skill = DeleteSkill();
        var result = await skill.ExecuteAsync(Ctx(), new Dictionary<string, object> { ["calendarSelectionId"] = id.ToString() });

        Assert.That(result.Success, Is.True, result.Message);
        Assert.That(result.Message, Does.Contain("verified"));
        await _mediator.Received(1).Send(
            Arg.Is<DeleteCommand<CalendarSelectionResource>>(c => c.Id == id), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task Delete_ResolvesByName_WhenIdOmitted()
    {
        var id = Guid.NewGuid();
        var existing = ExistingSelection(id);
        _calendarSelectionRepository.List().Returns(new List<CalendarSelection> { existing });
        _mediator.Send(Arg.Any<DeleteCommand<CalendarSelectionResource>>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<CalendarSelectionResource?>(new CalendarSelectionResource { Id = id, Name = existing.Name }));
        _calendarSelectionRepository.GetNoTracking(id).Returns((CalendarSelection?)null);

        var skill = DeleteSkill();
        var result = await skill.ExecuteAsync(Ctx(), new Dictionary<string, object> { ["calendarSelectionName"] = "Bern" });

        Assert.That(result.Success, Is.True, result.Message);
        await _mediator.Received(1).Send(
            Arg.Is<DeleteCommand<CalendarSelectionResource>>(c => c.Id == id), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task Delete_ReturnsError_WhenSeededGuardBlocks()
    {
        var id = Guid.NewGuid();
        var existing = ExistingSelection(id);
        _calendarSelectionRepository.List().Returns(new List<CalendarSelection> { existing });
        _mediator.Send(Arg.Any<DeleteCommand<CalendarSelectionResource>>(), Arg.Any<CancellationToken>())
            .Returns<CalendarSelectionResource>(_ => throw new InvalidRequestException(
                $"Calendar Selection '{existing.Name}' is seeded system data and cannot be deleted."));

        var skill = DeleteSkill();
        var result = await skill.ExecuteAsync(Ctx(), new Dictionary<string, object> { ["calendarSelectionId"] = id.ToString() });

        Assert.That(result.Success, Is.False);
        Assert.That(result.Message, Does.Contain("seeded"));
    }

    [Test]
    public async Task Delete_ReturnsError_WhenInUseGuardBlocks()
    {
        var id = Guid.NewGuid();
        var existing = ExistingSelection(id);
        _calendarSelectionRepository.List().Returns(new List<CalendarSelection> { existing });
        _mediator.Send(Arg.Any<DeleteCommand<CalendarSelectionResource>>(), Arg.Any<CancellationToken>())
            .Returns<CalendarSelectionResource>(_ => throw new InvalidRequestException(
                $"Calendar Selection '{existing.Name}' is in use by: 2 Group(s). Remove these references first."));

        var skill = DeleteSkill();
        var result = await skill.ExecuteAsync(Ctx(), new Dictionary<string, object> { ["calendarSelectionId"] = id.ToString() });

        Assert.That(result.Success, Is.False);
        Assert.That(result.Message, Does.Contain("in use by"));
    }

    [Test]
    public async Task Delete_ReturnsError_WhenVerificationFails()
    {
        var id = Guid.NewGuid();
        var existing = ExistingSelection(id);
        _calendarSelectionRepository.List().Returns(new List<CalendarSelection> { existing });
        _mediator.Send(Arg.Any<DeleteCommand<CalendarSelectionResource>>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<CalendarSelectionResource?>(new CalendarSelectionResource { Id = id, Name = existing.Name }));
        _calendarSelectionRepository.GetNoTracking(id).Returns(existing);

        var skill = DeleteSkill();
        var result = await skill.ExecuteAsync(Ctx(), new Dictionary<string, object> { ["calendarSelectionId"] = id.ToString() });

        Assert.That(result.Success, Is.False);
        Assert.That(result.Message, Does.Contain("still appears"));
    }

    [Test]
    public async Task Delete_ReturnsError_WhenNotFound()
    {
        _calendarSelectionRepository.List().Returns(new List<CalendarSelection>());
        var skill = DeleteSkill();

        var result = await skill.ExecuteAsync(Ctx(), new Dictionary<string, object> { ["calendarSelectionId"] = Guid.NewGuid().ToString() });

        Assert.That(result.Success, Is.False);
        Assert.That(result.Message, Does.Contain("not found"));
    }
}
