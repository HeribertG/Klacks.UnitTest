// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// The request of a bulk creation of stand-ins: count from 1 to StandInConstants.MaxBulkCount, a name pattern with the
/// index placeholder exactly once and some text, at most StandInConstants.MaxNamePatternLength characters, a group, a
/// period whose end is not before its start and no qualification twice.
/// </summary>

using Klacks.Api.Application.Commands.Clients;
using Klacks.Api.Application.DTOs.Staffs;
using Klacks.Api.Application.Validation.Clients;
using Klacks.Api.Domain.Constants;
using Shouldly;

namespace Klacks.UnitTest.Validation.Clients;

[TestFixture]
public class CreateStandInsCommandValidatorTests
{
    private readonly CreateStandInsCommandValidator _validator = new();

    private static CreateStandInsResource Valid() => new()
    {
        Count = 8,
        NamePattern = "Fair {n}",
        GroupId = Guid.NewGuid(),
        ValidFrom = new DateOnly(2026, 12, 1),
        ValidUntil = new DateOnly(2026, 12, 31),
        QualificationIds = [Guid.NewGuid()],
    };

    [Test]
    public async Task Valid_Request_HasNoError()
    {
        (await _validator.ValidateAsync(new CreateStandInsCommand(Valid()))).Errors.ShouldBeEmpty();
    }

    [Test]
    public async Task OpenEndedPeriod_WithoutQualifications_HasNoError()
    {
        var resource = Valid();
        resource.ValidUntil = null;
        resource.QualificationIds = [];

        (await _validator.ValidateAsync(new CreateStandInsCommand(resource))).Errors.ShouldBeEmpty();
    }

    [TestCase(0)]
    [TestCase(-1)]
    [TestCase(StandInConstants.MaxBulkCount + 1)]
    public async Task Count_OutsideTheRange_IsInvalid(int count)
    {
        var resource = Valid();
        resource.Count = count;

        await ShouldFailAsync(resource, nameof(CreateStandInsResource.Count));
    }

    [TestCase("Fair")]
    [TestCase("Fair {n} {n}")]
    [TestCase("{n}")]
    [TestCase("  {n} ")]
    [TestCase("")]
    public async Task NamePattern_WithoutExactlyOnePlaceholderOrWithoutText_IsInvalid(string pattern)
    {
        var resource = Valid();
        resource.NamePattern = pattern;

        await ShouldFailAsync(resource, nameof(CreateStandInsResource.NamePattern));
    }

    [Test]
    public async Task NamePattern_LongerThanTheLimit_IsInvalid()
    {
        var resource = Valid();
        resource.NamePattern = new string('a', StandInConstants.MaxNamePatternLength - 2) + "{n}";

        await ShouldFailAsync(resource, nameof(CreateStandInsResource.NamePattern));
    }

    [Test]
    public async Task NamePattern_AtTheLimit_IsValid()
    {
        var resource = Valid();
        resource.NamePattern = new string('a', StandInConstants.MaxNamePatternLength - 3) + "{n}";

        (await _validator.ValidateAsync(new CreateStandInsCommand(resource))).Errors.ShouldBeEmpty();
    }

    [Test]
    public async Task MissingGroup_IsInvalid()
    {
        var resource = Valid();
        resource.GroupId = Guid.Empty;

        await ShouldFailAsync(resource, nameof(CreateStandInsResource.GroupId));
    }

    [Test]
    public async Task EndBeforeStart_IsInvalid()
    {
        var resource = Valid();
        resource.ValidUntil = resource.ValidFrom.AddDays(-1);

        await ShouldFailAsync(resource, nameof(CreateStandInsResource.ValidUntil));
    }

    [Test]
    public async Task QualificationTwice_IsInvalid()
    {
        var resource = Valid();
        var qualificationId = Guid.NewGuid();
        resource.QualificationIds = [qualificationId, qualificationId];

        await ShouldFailAsync(resource, nameof(CreateStandInsResource.QualificationIds));
    }

    [Test]
    public async Task MissingStart_IsInvalid()
    {
        var resource = Valid();
        resource.ValidFrom = default;
        resource.ValidUntil = null;

        await ShouldFailAsync(resource, nameof(CreateStandInsResource.ValidFrom));
    }

    private async Task ShouldFailAsync(CreateStandInsResource resource, string property)
    {
        var result = await _validator.ValidateAsync(new CreateStandInsCommand(resource));

        result.Errors.ShouldContain(error => error.PropertyName == $"Resource.{property}");
    }
}
