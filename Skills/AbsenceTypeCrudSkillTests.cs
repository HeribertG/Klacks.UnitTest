// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Unit tests for the absence-type master-data skills — create (duplicate guard, verified),
/// update (name resolution, partial update, verify) and delete (undeletable guard,
/// in-use guard with counts, verified soft delete).
/// </summary>

using Klacks.Api.Application.Interfaces;
using Klacks.Api.Application.Skills;
using Klacks.Api.Domain.Common;
using Klacks.Api.Domain.Interfaces;
using Klacks.Api.Domain.Models.Assistant;
using Klacks.Api.Domain.Models.Schedules;

namespace Klacks.UnitTest.Skills;

[TestFixture]
public class AbsenceTypeCrudSkillTests
{
    private IAbsenceRepository _absenceRepository = null!;
    private IUnitOfWork _unitOfWork = null!;
    private Absence? _persisted;

    [SetUp]
    public void Setup()
    {
        _absenceRepository = Substitute.For<IAbsenceRepository>();
        _unitOfWork = Substitute.For<IUnitOfWork>();
        _unitOfWork.ExecuteInTransactionAsync(Arg.Any<Func<Task<bool>>>())
            .Returns(ci => ci.Arg<Func<Task<bool>>>()());
        _unitOfWork.ExecuteInTransactionAsync(Arg.Any<Func<Task<Guid>>>())
            .Returns(ci => ci.Arg<Func<Task<Guid>>>()());
        _persisted = null;
        _absenceRepository.Add(Arg.Do<Absence>(a => _persisted = a)).Returns(Task.CompletedTask);
    }

    private static SkillExecutionContext Ctx() => new()
    {
        UserId = Guid.NewGuid(),
        TenantId = Guid.NewGuid(),
        UserName = "tester",
        UserPermissions = new List<string> { "CanEditSettings" }
    };

    private static Absence Type(string name, string abbreviation = "XX", bool undeletable = false)
    {
        var ml = new MultiLanguage();
        var ab = new MultiLanguage();
        foreach (var l in MultiLanguage.CoreLanguages)
        {
            ml.SetValue(l, name);
            ab.SetValue(l, abbreviation);
        }

        return new Absence
        {
            Id = Guid.NewGuid(),
            Name = ml,
            Abbreviation = ab,
            Description = new MultiLanguage(),
            Undeletable = undeletable
        };
    }

    [Test]
    public async Task Create_AddsType_AndReportsVerified()
    {
        _absenceRepository.List().Returns(new List<Absence>());
        _absenceRepository.GetNoTracking(Arg.Any<Guid>()).Returns(_ => _persisted);
        var skill = new CreateAbsenceTypeSkill(_absenceRepository, _unitOfWork);

        var result = await skill.ExecuteAsync(Ctx(), new Dictionary<string, object>
        {
            ["name"] = "Weiterbildung",
            ["abbreviation"] = "WB",
            ["withSaturday"] = true
        });

        result.Success.ShouldBeTrue(result.Message);
        result.Message.ShouldContain("verified");
        await _absenceRepository.Received(1).Add(Arg.Is<Absence>(a =>
            a.Name.De == "Weiterbildung" && a.WithSaturday));
    }

    [Test]
    public async Task Create_RefusesDuplicateName()
    {
        _absenceRepository.List().Returns(new List<Absence> { Type("Ferien") });
        var skill = new CreateAbsenceTypeSkill(_absenceRepository, _unitOfWork);

        var result = await skill.ExecuteAsync(Ctx(), new Dictionary<string, object>
        {
            ["name"] = "ferien",
            ["abbreviation"] = "FE"
        });

        result.Success.ShouldBeFalse();
        result.Message.ShouldContain("already exists");
        await _absenceRepository.DidNotReceive().Add(Arg.Any<Absence>());
    }

    [Test]
    public async Task Update_ResolvesByName_ChangesColor_AndVerifies()
    {
        var type = Type("Ferien", "FE");
        _absenceRepository.List().Returns(new List<Absence> { type });
        _absenceRepository.GetNoTracking(type.Id).Returns(type);
        var skill = new UpdateAbsenceTypeSkill(_absenceRepository, _unitOfWork);

        var result = await skill.ExecuteAsync(Ctx(), new Dictionary<string, object>
        {
            ["typeName"] = "Ferien",
            ["color"] = "#00AA00"
        });

        result.Success.ShouldBeTrue(result.Message);
        result.Message.ShouldContain("verified");
        type.Color.ShouldBe("#00AA00");
        await _absenceRepository.Received(1).Put(type);
    }

    [Test]
    public async Task Update_ReturnsError_WhenNameUnknown()
    {
        _absenceRepository.List().Returns(new List<Absence> { Type("Ferien") });
        var skill = new UpdateAbsenceTypeSkill(_absenceRepository, _unitOfWork);

        var result = await skill.ExecuteAsync(Ctx(), new Dictionary<string, object>
        {
            ["typeName"] = "Sabbatical",
            ["color"] = "#00AA00"
        });

        result.Success.ShouldBeFalse();
        result.Message.ShouldContain("not found");
        result.Message.ShouldContain("Ferien");
    }

    [Test]
    public async Task Delete_RefusesUndeletableSeededType()
    {
        var type = Type("Ferien", undeletable: true);
        _absenceRepository.List().Returns(new List<Absence> { type });
        var skill = new DeleteAbsenceTypeSkill(_absenceRepository, _unitOfWork);

        var result = await skill.ExecuteAsync(Ctx(), new Dictionary<string, object>
        {
            ["typeName"] = "Ferien"
        });

        result.Success.ShouldBeFalse();
        result.Message.ShouldContain("protected");
        await _absenceRepository.DidNotReceive().Delete(Arg.Any<Guid>());
    }

    [Test]
    public async Task Delete_RefusesTypeInUse_WithCounts()
    {
        var type = Type("Weiterbildung");
        _absenceRepository.List().Returns(new List<Absence> { type });
        _absenceRepository.CountActiveBreaksByAbsenceAsync(type.Id, Arg.Any<CancellationToken>()).Returns(3);
        _absenceRepository.CountActiveBreakPlaceholdersByAbsenceAsync(type.Id, Arg.Any<CancellationToken>()).Returns(1);
        var skill = new DeleteAbsenceTypeSkill(_absenceRepository, _unitOfWork);

        var result = await skill.ExecuteAsync(Ctx(), new Dictionary<string, object>
        {
            ["typeName"] = "Weiterbildung"
        });

        result.Success.ShouldBeFalse();
        result.Message.ShouldContain("3 active booking(s)");
        result.Message.ShouldContain("1 placeholder(s)");
        await _absenceRepository.DidNotReceive().Delete(Arg.Any<Guid>());
    }

    [Test]
    public async Task Delete_SoftDeletes_AndVerifies()
    {
        var type = Type("Weiterbildung");
        _absenceRepository.List().Returns(new List<Absence> { type });
        _absenceRepository.GetNoTracking(type.Id).Returns((Absence?)null);
        var skill = new DeleteAbsenceTypeSkill(_absenceRepository, _unitOfWork);

        var result = await skill.ExecuteAsync(Ctx(), new Dictionary<string, object>
        {
            ["typeName"] = "Weiterbildung"
        });

        result.Success.ShouldBeTrue(result.Message);
        result.Message.ShouldContain("verified");
        await _absenceRepository.Received(1).Delete(type.Id);
    }

    [Test]
    public async Task Delete_ReturnsError_WhenTypeStillVisibleAfterDelete()
    {
        var type = Type("Weiterbildung");
        _absenceRepository.List().Returns(new List<Absence> { type });
        _absenceRepository.GetNoTracking(type.Id).Returns(type);
        var skill = new DeleteAbsenceTypeSkill(_absenceRepository, _unitOfWork);

        var result = await skill.ExecuteAsync(Ctx(), new Dictionary<string, object>
        {
            ["typeName"] = "Weiterbildung"
        });

        result.Success.ShouldBeFalse();
        result.Message.ShouldContain("verification failed");
    }
}
