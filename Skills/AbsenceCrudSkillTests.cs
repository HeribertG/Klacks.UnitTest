// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Unit tests for create_absence, update_absence and delete_absence skills.
/// Covers MultiLanguage merge, no-op update, delete-refusal when in use.
/// </summary>

using Klacks.Api.Application.Interfaces;
using Klacks.Api.Application.Skills;
using Klacks.Api.Domain.Common;
using Klacks.Api.Domain.Interfaces;
using Klacks.Api.Domain.Models.Assistant;
using Klacks.Api.Domain.Models.Schedules;

namespace Klacks.UnitTest.Skills;

[TestFixture]
public class AbsenceCrudSkillTests
{
    private IAbsenceRepository _absenceRepository = null!;
    private IUnitOfWork _unitOfWork = null!;

    [SetUp]
    public void Setup()
    {
        _absenceRepository = Substitute.For<IAbsenceRepository>();
        _unitOfWork = Substitute.For<IUnitOfWork>();
    }

    private static SkillExecutionContext Ctx() => new()
    {
        UserId = Guid.NewGuid(),
        TenantId = Guid.NewGuid(),
        UserName = "tester",
        UserPermissions = new List<string> { "CanEditSettings" }
    };

    private static Absence MakeAbsence(string nameDe, string abbrev, bool undeletable = false)
    {
        var name = new MultiLanguage();
        name.SetValue("de", nameDe);
        var abbreviation = new MultiLanguage();
        abbreviation.SetValue("de", abbrev);
        return new Absence
        {
            Id = Guid.NewGuid(),
            Name = name,
            Abbreviation = abbreviation,
            Description = MultiLanguage.Empty(),
            Color = "#CCCCCC",
            DefaultLength = 1,
            Undeletable = undeletable
        };
    }

    [Test]
    public async Task CreateAbsence_AddsAndCompletes()
    {
        var skill = new CreateAbsenceSkill(_absenceRepository, _unitOfWork);
        var parameters = new Dictionary<string, object>
        {
            ["nameDe"] = "Ferien",
            ["abbreviationDe"] = "F",
            ["isUnpaid"] = false
        };

        var result = await skill.ExecuteAsync(Ctx(), parameters);

        Assert.That(result.Success, Is.True);
        await _absenceRepository.Received(1).Add(Arg.Is<Absence>(a =>
            a.Name.De == "Ferien" && a.Abbreviation.De == "F"));
        await _unitOfWork.Received(1).CompleteAsync();
    }

    [Test]
    public async Task UpdateAbsence_ReturnsNoOp_WhenNoFieldsSupplied()
    {
        var skill = new UpdateAbsenceSkill(_absenceRepository, _unitOfWork);
        var existing = MakeAbsence("Krank", "K");
        _absenceRepository.Get(existing.Id).Returns(existing);
        var parameters = new Dictionary<string, object> { ["absenceId"] = existing.Id.ToString() };

        var result = await skill.ExecuteAsync(Ctx(), parameters);

        Assert.That(result.Success, Is.True);
        Assert.That(result.Message, Does.Contain("No fields"));
        await _absenceRepository.DidNotReceive().Put(Arg.Any<Absence>());
    }

    [Test]
    public async Task UpdateAbsence_RenamesGermanLabelAndPersists()
    {
        var skill = new UpdateAbsenceSkill(_absenceRepository, _unitOfWork);
        var existing = MakeAbsence("Krank", "K");
        _absenceRepository.Get(existing.Id).Returns(existing);
        var parameters = new Dictionary<string, object>
        {
            ["absenceId"] = existing.Id.ToString(),
            ["nameDe"] = "Krankheit"
        };

        var result = await skill.ExecuteAsync(Ctx(), parameters);

        Assert.That(result.Success, Is.True);
        await _absenceRepository.Received(1).Put(Arg.Is<Absence>(a => a.Name.De == "Krankheit"));
    }

    [Test]
    public async Task DeleteAbsence_RefusesWhenUndeletable()
    {
        var skill = new DeleteAbsenceSkill(_absenceRepository, _unitOfWork);
        var existing = MakeAbsence("System", "SY", undeletable: true);
        _absenceRepository.Get(existing.Id).Returns(existing);
        var parameters = new Dictionary<string, object> { ["absenceId"] = existing.Id.ToString() };

        var result = await skill.ExecuteAsync(Ctx(), parameters);

        Assert.That(result.Success, Is.False);
        Assert.That(result.Message, Does.Contain("Undeletable"));
        await _absenceRepository.DidNotReceive().Delete(Arg.Any<Guid>());
    }

    [Test]
    public async Task DeleteAbsence_RefusesWhenStillInUse()
    {
        var skill = new DeleteAbsenceSkill(_absenceRepository, _unitOfWork);
        var existing = MakeAbsence("Ferien", "F");
        _absenceRepository.Get(existing.Id).Returns(existing);
        _absenceRepository.CountActiveBreaksByAbsenceAsync(existing.Id, Arg.Any<CancellationToken>()).Returns(3);
        _absenceRepository.CountActiveBreakPlaceholdersByAbsenceAsync(existing.Id, Arg.Any<CancellationToken>()).Returns(0);
        var parameters = new Dictionary<string, object> { ["absenceId"] = existing.Id.ToString() };

        var result = await skill.ExecuteAsync(Ctx(), parameters);

        Assert.That(result.Success, Is.False);
        Assert.That(result.Message, Does.Contain("in use"));
        await _absenceRepository.DidNotReceive().Delete(Arg.Any<Guid>());
    }

    [Test]
    public async Task DeleteAbsence_SoftDeletesWhenSafe()
    {
        var skill = new DeleteAbsenceSkill(_absenceRepository, _unitOfWork);
        var existing = MakeAbsence("Ferien", "F");
        _absenceRepository.Get(existing.Id).Returns(existing);
        _absenceRepository.CountActiveBreaksByAbsenceAsync(existing.Id, Arg.Any<CancellationToken>()).Returns(0);
        _absenceRepository.CountActiveBreakPlaceholdersByAbsenceAsync(existing.Id, Arg.Any<CancellationToken>()).Returns(0);
        var parameters = new Dictionary<string, object> { ["absenceId"] = existing.Id.ToString() };

        var result = await skill.ExecuteAsync(Ctx(), parameters);

        Assert.That(result.Success, Is.True);
        await _absenceRepository.Received(1).Delete(existing.Id);
        await _unitOfWork.Received(1).CompleteAsync();
    }
}
