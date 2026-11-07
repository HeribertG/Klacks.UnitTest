// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Switching an absence type to or from on-call (Absence.IsOnCall) changes what its absences count (an on-call absence
/// counts no hours), so the update recomputes the cached period hours after the commit, through the absence-type PUT and
/// through the skill update_absence_type. Any other change leaves them alone. Before 2026-11-07 neither path did.
/// </summary>

using Klacks.Api.Application.Commands;
using Klacks.Api.Application.DTOs.Schedules;
using Klacks.Api.Application.Handlers.Absences;
using Klacks.Api.Application.Interfaces.Schedules;
using Klacks.Api.Application.Mappers;
using Klacks.Api.Application.Skills;
using Klacks.Api.Domain.Interfaces.Translation;
using Microsoft.Extensions.Logging;
using AbsenceType = Klacks.Api.Domain.Models.Schedules.Absence;

namespace Klacks.UnitTest.Handlers.Absence;

[TestFixture]
public class AbsenceOnCallSwitchRefreshTests
{
    private IAbsenceRepository _repository = null!;
    private IUnitOfWork _unitOfWork = null!;
    private IOnCallSwitchPeriodHoursRefresher _refresher = null!;
    private AbsenceType _type = null!;

    [SetUp]
    public void SetUp()
    {
        _type = new AbsenceType
        {
            Id = Guid.NewGuid(),
            IsOnCall = false,
            Name = new MultiLanguage { De = "Pikett" },
            Abbreviation = new MultiLanguage { De = "PI" },
            Description = new MultiLanguage()
        };
        _repository = Substitute.For<IAbsenceRepository>();
        _repository.Get(_type.Id).Returns(_ => _type);
        _repository.GetNoTracking(_type.Id).Returns(_ => _type);
        _unitOfWork = Substitute.For<IUnitOfWork>();
        _unitOfWork.ExecuteInTransactionAsync(Arg.Any<Func<Task<bool>>>())
            .Returns(ci => ci.Arg<Func<Task<bool>>>()());
        _refresher = Substitute.For<IOnCallSwitchPeriodHoursRefresher>();
    }

    [TestCase(false, true)]
    [TestCase(true, false)]
    public async Task Put_SwitchingOnCall_RefreshesTheCachedHours_AfterTheCommit(bool before, bool after)
    {
        _type.IsOnCall = before;

        await NewPutHandler().Handle(new PutCommand<AbsenceResource>(Resource(isOnCall: after)), CancellationToken.None);

        Received.InOrder(() =>
        {
            _unitOfWork.CompleteAsync();
            _refresher.RefreshAsync(_type.Id, CancellationToken.None);
        });
    }

    [Test]
    public async Task Put_WithoutOnCallChange_DoesNotRefresh()
    {
        await NewPutHandler().Handle(new PutCommand<AbsenceResource>(Resource(isOnCall: false)), CancellationToken.None);

        await _unitOfWork.Received(1).CompleteAsync();
        await _refresher.DidNotReceiveWithAnyArgs().RefreshAsync(default, default);
    }

    [Test]
    public async Task Skill_SwitchingOnCall_RefreshesTheCachedHours_AfterTheTransaction()
    {
        var result = await NewSkill().ExecuteAsync(
            Context(), Parameters(("isOnCall", true)), CancellationToken.None);

        result.Success.ShouldBeTrue();
        Received.InOrder(() =>
        {
            _unitOfWork.ExecuteInTransactionAsync(Arg.Any<Func<Task<bool>>>());
            _refresher.RefreshAsync(_type.Id, CancellationToken.None);
        });
    }

    [Test]
    public async Task Skill_WithoutOnCallChange_DoesNotRefresh()
    {
        var result = await NewSkill().ExecuteAsync(
            Context(), Parameters(("color", "#00AA00")), CancellationToken.None);

        result.Success.ShouldBeTrue();
        await _refresher.DidNotReceiveWithAnyArgs().RefreshAsync(default, default);
    }

    private AbsenceResource Resource(bool isOnCall) => new()
    {
        Id = _type.Id,
        IsOnCall = isOnCall,
        Name = new MultiLanguage { De = "Pikett" },
        Abbreviation = new MultiLanguage { De = "PI" },
        Description = new MultiLanguage()
    };

    private PutCommandHandler NewPutHandler()
    {
        var translation = Substitute.For<IMultiLanguageTranslationService>();
        translation.IsConfiguredAsync().Returns(false);
        return new PutCommandHandler(
            new SettingsMapper(), _repository, translation, _unitOfWork, _refresher,
            Substitute.For<ILogger<PutCommandHandler>>());
    }

    private UpdateAbsenceTypeSkill NewSkill() => new(_repository, _unitOfWork, _refresher);

    private static SkillExecutionContext Context() => new()
    {
        UserId = Guid.NewGuid(),
        TenantId = Guid.NewGuid(),
        UserName = "tester",
        UserPermissions = new List<string> { "CanEditSettings" }
    };

    private Dictionary<string, object> Parameters(params (string Key, object Value)[] values)
    {
        var parameters = new Dictionary<string, object> { ["absenceTypeId"] = _type.Id.ToString() };
        foreach (var (key, value) in values)
        {
            parameters[key] = value;
        }

        return parameters;
    }
}
