// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Tests for SkillSelectionTrajectoryRepository.GetUncorrectedWrongSkillAsync. Pins that the
/// CorrectionTypes.WrongSkill filter runs inside the database query, not afterwards in memory: an
/// implicit correction (WasCorrected true, CorrectionType not WrongSkill) must never occupy a slot of
/// the fixed-size window, or it starves the wrong-skill corrections the sharpening actually needs to see.
/// </summary>

using Klacks.Api.Domain.Constants;
using Klacks.Api.Domain.Models.Assistant;
using Klacks.Api.Infrastructure.Persistence;
using Klacks.Api.Infrastructure.Repositories.Assistant;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using NSubstitute;
using NUnit.Framework;
using Shouldly;

namespace Klacks.UnitTest.Infrastructure.Repositories.Assistant;

[TestFixture]
public class SkillSelectionTrajectoryRepositoryGetUncorrectedWrongSkillTests
{
    private static readonly Guid AgentId = Guid.NewGuid();
    private static readonly DateTime StartUtc = new(2026, 8, 27, 6, 0, 0, DateTimeKind.Utc);

    private DbContextOptions<DataBaseContext> _options = null!;
    private IHttpContextAccessor _httpAccessor = null!;

    [SetUp]
    public void SetUp()
    {
        _options = new DbContextOptionsBuilder<DataBaseContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        _httpAccessor = Substitute.For<IHttpContextAccessor>();
    }

    private DataBaseContext CreateContext() => new(_options, _httpAccessor);

    private SkillSelectionTrajectoryRepository CreateRepository() => new(CreateContext());

    private SkillSelectionTrajectory MakeTrajectory(string correctionType, DateTime createTime) => new()
    {
        Id = Guid.NewGuid(),
        AgentId = AgentId,
        Locale = "de",
        IntentExcerpt = "Zeige mir die Umsatzstatistik pro Kunde",
        LlmChosenSkill = "list_clients",
        WasCorrected = true,
        CorrectionType = correctionType,
        CreateTime = createTime
    };

    // The window is 30 rows in production; an implicit correction that occupies a slot without ever
    // being stamped keeps blocking that slot forever, so the wrong-skill filter must run in the query.
    [Test]
    public async Task AnImplicitCorrection_NeverOccupiesASlotOfTheWindow()
    {
        var wrongSkill = MakeTrajectory(CorrectionTypes.WrongSkill, StartUtc);
        var implicitCorrection = MakeTrajectory(CorrectionTypes.Implicit, StartUtc.AddMinutes(1));

        await using (var seed = CreateContext())
        {
            seed.SkillSelectionTrajectories.Add(wrongSkill);
            seed.SkillSelectionTrajectories.Add(implicitCorrection);
            await seed.SaveChangesAsync();
        }

        var result = await CreateRepository().GetUncorrectedWrongSkillAsync(AgentId, 30);

        result.Count.ShouldBe(1);
        result[0].Id.ShouldBe(wrongSkill.Id);
    }

    [Test]
    public async Task AWrongSkillCorrectionAlreadySharpened_IsExcluded()
    {
        var trajectory = MakeTrajectory(CorrectionTypes.WrongSkill, StartUtc);
        trajectory.SharpenedAtUtc = StartUtc.AddMinutes(5);

        await using (var seed = CreateContext())
        {
            seed.SkillSelectionTrajectories.Add(trajectory);
            await seed.SaveChangesAsync();
        }

        var result = await CreateRepository().GetUncorrectedWrongSkillAsync(AgentId, 30);

        result.ShouldBeEmpty();
    }
}
