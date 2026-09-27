// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Proves the column names, types and default of the two learning columns against the real Npgsql model:
/// seed_version must default to 0 so every existing row counts as never seeded under the new rule, and the
/// gate metrics must be jsonb and nullable.
/// </summary>
namespace Klacks.UnitTest.Infrastructure.Persistence;

using Klacks.Api.Domain.Constants;
using Klacks.Api.Domain.Models.Assistant;
using Klacks.Api.Infrastructure.Persistence;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using NSubstitute;
using NUnit.Framework;
using Shouldly;

[TestFixture]
public class LearningColumnsMappingTests
{
    private const string UnusedConnectionString =
        "Host=127.0.0.1;Port=1;Database=klacks_model_only;Username=postgres;Password=admin";

    private static DataBaseContext NewContext() => new(
        new DbContextOptionsBuilder<DataBaseContext>()
            .UseNpgsql(UnusedConnectionString)
            .UseSnakeCaseNamingConvention()
            .Options,
        Substitute.For<IHttpContextAccessor>());

    [Test]
    public void SeedVersion_IsANonNullIntegerWithDefaultZero()
    {
        using var context = NewContext();
        var property = context.Model.FindEntityType(typeof(AgentSkill))!.FindProperty(nameof(AgentSkill.SeedVersion))!;

        property.GetColumnName().ShouldBe("seed_version");
        property.IsNullable.ShouldBeFalse();
        property.GetDefaultValue().ShouldBe(AgentSkillDefaults.UnseededVersion);
    }

    [Test]
    public void GateMetricsJson_IsANullableJsonbColumn()
    {
        using var context = NewContext();
        var property = context.Model.FindEntityType(typeof(ProposedSkillChange))!
            .FindProperty(nameof(ProposedSkillChange.GateMetricsJson))!;

        property.GetColumnName().ShouldBe("gate_metrics_json");
        property.GetColumnType().ShouldBe("jsonb");
        property.IsNullable.ShouldBeTrue();
    }
}
