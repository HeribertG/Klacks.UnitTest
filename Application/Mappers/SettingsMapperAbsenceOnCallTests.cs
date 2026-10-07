// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

using Klacks.Api.Application.DTOs.Schedules;
using Klacks.Api.Application.Mappers;
using Klacks.Api.Domain.Common;
using Klacks.Api.Domain.Models.Schedules;

namespace Klacks.UnitTest.Application.Mappers;

/// <summary>
/// The absence Put handler copies the resource onto the tracked entity through the generated mapper, so the
/// on-call flag must survive both directions, otherwise every save from the settings page would drop it.
/// </summary>
[TestFixture]
public class SettingsMapperAbsenceOnCallTests
{
    private readonly SettingsMapper _mapper = new();

    [Test]
    public void UpdateAbsenceEntity_CopiesTheOnCallFlag()
    {
        var target = NewAbsence(isOnCall: false);

        _mapper.UpdateAbsenceEntity(new AbsenceResource
        {
            Id = target.Id,
            Name = new MultiLanguage(),
            Abbreviation = new MultiLanguage(),
            Description = new MultiLanguage(),
            IsOnCall = true
        }, target);

        target.IsOnCall.ShouldBeTrue();
    }

    [Test]
    public void ToAbsenceResource_ExposesTheOnCallFlag()
    {
        _mapper.ToAbsenceResource(NewAbsence(isOnCall: true)).IsOnCall.ShouldBeTrue();
    }

    private static Absence NewAbsence(bool isOnCall) => new()
    {
        Id = Guid.NewGuid(),
        Name = new MultiLanguage(),
        Abbreviation = new MultiLanguage(),
        Description = new MultiLanguage(),
        IsOnCall = isOnCall
    };
}
