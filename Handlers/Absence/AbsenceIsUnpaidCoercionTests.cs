// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Tests that IsUnpaid is coerced to false unless HideInGantt and AppliesToContainer are both true.
/// Covers both the rule logic directly and the POST/PUT handler invocation paths.
/// </summary>

using Shouldly;
using Klacks.Api.Application.Commands;
using Klacks.Api.Application.DTOs.Schedules;
using Klacks.Api.Application.Handlers.Absences;
using Klacks.Api.Application.Interfaces;
using Klacks.Api.Application.Mappers;
using Klacks.Api.Domain.Common;
using Klacks.Api.Domain.Interfaces;
using Klacks.Api.Domain.Interfaces.Translation;
using Klacks.Api.Domain.Models.Schedules;
using Microsoft.Extensions.Logging;
using NSubstitute;
using NUnit.Framework;

namespace Klacks.UnitTest.Handlers.Absence;

[TestFixture]
public class AbsenceIsUnpaidCoercionTests
{
    [TestCase(false, false, false)]
    [TestCase(false, true, false)]
    [TestCase(true, false, false)]
    [TestCase(true, true, true)]
    public void IsUnpaid_IsOnlyRetainedWhenBothGatingFlagsTrue(
        bool hideInGantt, bool appliesToContainer, bool expected)
    {
        var entity = new Klacks.Api.Domain.Models.Schedules.Absence
        {
            HideInGantt = hideInGantt,
            AppliesToContainer = appliesToContainer,
            IsUnpaid = true
        };

        if (!(entity.HideInGantt && entity.AppliesToContainer))
        {
            entity.IsUnpaid = false;
        }

        entity.IsUnpaid.ShouldBe(expected);
    }
}

[TestFixture]
public class AbsencePostHandlerCoercionTests
{
    private IAbsenceRepository _absenceRepository = null!;
    private IMultiLanguageTranslationService _translationService = null!;
    private IUnitOfWork _unitOfWork = null!;
    private SettingsMapper _settingsMapper = null!;
    private PostCommandHandler _handler = null!;

    [SetUp]
    public void Setup()
    {
        _absenceRepository = Substitute.For<IAbsenceRepository>();
        _translationService = Substitute.For<IMultiLanguageTranslationService>();
        _unitOfWork = Substitute.For<IUnitOfWork>();
        _settingsMapper = new SettingsMapper();
        var logger = Substitute.For<ILogger<PostCommandHandler>>();

        _translationService.IsConfiguredAsync().Returns(false);
        _unitOfWork.ExecuteInTransactionAsync(Arg.Any<Func<Task<AbsenceResource?>>>())
            .Returns(ci => ci.ArgAt<Func<Task<AbsenceResource?>>>(0)());
        _unitOfWork.CompleteAsync().Returns(Task.CompletedTask);

        _handler = new PostCommandHandler(
            _absenceRepository,
            _settingsMapper,
            _translationService,
            _unitOfWork,
            logger);
    }

    [Test]
    public async Task Handle_Post_WhenGatingFlagsNotBothTrue_CoercesIsUnpaidToFalse()
    {
        var resource = new AbsenceResource
        {
            HideInGantt = true,
            AppliesToContainer = false,
            IsUnpaid = true,
            Name = new MultiLanguage { De = "Test" }
        };
        var command = new PostCommand<AbsenceResource>(resource);

        Api.Domain.Models.Schedules.Absence? captured = null;
        await _absenceRepository.Add(Arg.Do<Api.Domain.Models.Schedules.Absence>(a => captured = a));

        await _handler.Handle(command, CancellationToken.None);

        captured.ShouldNotBeNull();
        captured!.IsUnpaid.ShouldBeFalse();
    }

    [Test]
    public async Task Handle_Post_WhenBothGatingFlagsTrue_RetainsIsUnpaid()
    {
        var resource = new AbsenceResource
        {
            HideInGantt = true,
            AppliesToContainer = true,
            IsUnpaid = true,
            Name = new MultiLanguage { De = "Test" }
        };
        var command = new PostCommand<AbsenceResource>(resource);

        Api.Domain.Models.Schedules.Absence? captured = null;
        await _absenceRepository.Add(Arg.Do<Api.Domain.Models.Schedules.Absence>(a => captured = a));

        await _handler.Handle(command, CancellationToken.None);

        captured.ShouldNotBeNull();
        captured!.IsUnpaid.ShouldBeTrue();
    }
}

[TestFixture]
public class AbsencePutHandlerCoercionTests
{
    private IAbsenceRepository _repository = null!;
    private IMultiLanguageTranslationService _translationService = null!;
    private IUnitOfWork _unitOfWork = null!;
    private SettingsMapper _settingsMapper = null!;
    private PutCommandHandler _handler = null!;

    [SetUp]
    public void Setup()
    {
        _repository = Substitute.For<IAbsenceRepository>();
        _translationService = Substitute.For<IMultiLanguageTranslationService>();
        _unitOfWork = Substitute.For<IUnitOfWork>();
        _settingsMapper = new SettingsMapper();
        var logger = Substitute.For<ILogger<PutCommandHandler>>();

        _translationService.IsConfiguredAsync().Returns(false);
        _unitOfWork.CompleteAsync().Returns(Task.CompletedTask);

        _handler = new PutCommandHandler(
            _settingsMapper,
            _repository,
            _translationService,
            _unitOfWork,
            logger);
    }

    [Test]
    public async Task Handle_Put_WhenGatingFlagsNotBothTrue_CoercesIsUnpaidToFalse()
    {
        var id = Guid.NewGuid();
        var dbAbsence = new Api.Domain.Models.Schedules.Absence
        {
            Id = id,
            HideInGantt = false,
            AppliesToContainer = true,
            IsUnpaid = false,
            Name = new MultiLanguage { De = "Existing" }
        };
        _repository.Get(id).Returns(dbAbsence);

        var resource = new AbsenceResource
        {
            Id = id,
            HideInGantt = false,
            AppliesToContainer = true,
            IsUnpaid = true,
            Name = new MultiLanguage { De = "Updated" }
        };
        var command = new PutCommand<AbsenceResource>(resource);

        await _handler.Handle(command, CancellationToken.None);

        dbAbsence.IsUnpaid.ShouldBeFalse();
    }

    [Test]
    public async Task Handle_Put_WhenBothGatingFlagsTrue_RetainsIsUnpaid()
    {
        var id = Guid.NewGuid();
        var dbAbsence = new Api.Domain.Models.Schedules.Absence
        {
            Id = id,
            HideInGantt = true,
            AppliesToContainer = true,
            IsUnpaid = false,
            Name = new MultiLanguage { De = "Existing" }
        };
        _repository.Get(id).Returns(dbAbsence);

        var resource = new AbsenceResource
        {
            Id = id,
            HideInGantt = true,
            AppliesToContainer = true,
            IsUnpaid = true,
            Name = new MultiLanguage { De = "Updated" }
        };
        var command = new PutCommand<AbsenceResource>(resource);

        await _handler.Handle(command, CancellationToken.None);

        dbAbsence.IsUnpaid.ShouldBeTrue();
    }
}
