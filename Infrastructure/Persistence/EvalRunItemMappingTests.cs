// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Pins the relational shape of the Package B schema: the eval_run_items table, the partition columns
/// on skill_learning_golden_cases, the origin column on proposed_skill_changes, the widened status
/// column and the composite lookup index the repeated-message read of skill_selection_trajectories
/// needs. The model is built against the Npgsql provider without opening a connection, because the
/// in-memory provider has no relational metadata and would answer every question here with an exception.
/// </summary>
namespace Klacks.UnitTest.Infrastructure.Persistence;

using Klacks.Api.Domain.Constants;
using Klacks.Api.Domain.Models.Assistant;
using Klacks.Api.Infrastructure.Persistence;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using NSubstitute;
using NUnit.Framework;
using Shouldly;

[TestFixture]
public class EvalRunItemMappingTests
{
    private const string UnusedConnectionString =
        "Host=localhost;Port=5434;Database=klacks_model_only;Username=postgres;Password=admin";

    private DataBaseContext _context = null!;

    [SetUp]
    public void SetUp()
    {
        var options = new DbContextOptionsBuilder<DataBaseContext>()
            .UseNpgsql(UnusedConnectionString)
            .UseSnakeCaseNamingConvention()
            .Options;

        _context = new DataBaseContext(options, Substitute.For<IHttpContextAccessor>());
    }

    [TearDown]
    public void TearDown() => _context.Dispose();

    [Test]
    public void EvalRunItem_IsMappedToItsOwnTable()
    {
        var entityType = _context.Model.FindEntityType(typeof(EvalRunItem));

        entityType.ShouldNotBeNull();
        entityType!.GetTableName().ShouldBe("eval_run_items");
    }

    [Test]
    public void EvalRunItem_CarriesTheColumnsTheGateAndTheLearnerRead()
    {
        var entityType = _context.Model.FindEntityType(typeof(EvalRunItem))!;
        var columns = entityType.GetProperties().Select(p => p.GetColumnName()).ToList();

        columns.ShouldContain("eval_run_id");
        columns.ShouldContain("item_id");
        columns.ShouldContain("locale");
        columns.ShouldContain("expected_tool");
        columns.ShouldContain("chosen_tool");
        columns.ShouldContain("toolset_names_json");
        columns.ShouldContain("retrieval_hit");
        columns.ShouldContain("selection_hit");
        columns.ShouldContain("passed");
        columns.ShouldContain("latency_ms");
        columns.ShouldContain("learning_consumed_at_utc");
    }

    [Test]
    public void GoldenCase_CarriesOriginAndPartition()
    {
        var entityType = _context.Model.FindEntityType(typeof(SkillLearningGoldenCase))!;
        var columns = entityType.GetProperties().Select(p => p.GetColumnName()).ToList();

        columns.ShouldContain("origin");
        columns.ShouldContain("partition");
    }

    [Test]
    public void SkillSelectionTrajectory_CarriesTheCompositeUserHashLookupIndex()
    {
        var entityType = DesignTimeEntityType(typeof(SkillSelectionTrajectory));

        var index = entityType.GetIndexes().SingleOrDefault(candidate =>
            candidate.Properties.Select(p => p.GetColumnName())
                .SequenceEqual(new[] { "user_id", "user_message_hash", "create_time" }));

        index.ShouldNotBeNull();
        index!.IsDescending.ShouldBe(new[] { false, false, true });
    }

    [Test]
    public void SkillSelectionTrajectory_NoLongerCarriesTheSingleColumnHashIndex()
    {
        var entityType = DesignTimeEntityType(typeof(SkillSelectionTrajectory));

        entityType.GetIndexes()
            .Any(candidate => candidate.Properties.Count == 1
                && candidate.Properties[0].GetColumnName() == "user_message_hash")
            .ShouldBeFalse();
    }

    [Test]
    public void ProposedSkillChange_CarriesOriginAndAStatusColumnLongEnoughForBlockedRegression()
    {
        var entityType = _context.Model.FindEntityType(typeof(ProposedSkillChange))!;

        entityType.GetProperties().Select(p => p.GetColumnName()).ShouldContain("origin");

        var status = entityType.GetProperty(nameof(ProposedSkillChange.Status));
        status.GetMaxLength().ShouldNotBeNull();
        status.GetMaxLength()!.Value.ShouldBeGreaterThanOrEqualTo(
            ProposedChangeStatuses.BlockedRegression.Length);
    }

    /// <summary>
    /// The design-time model rather than <see cref="DbContext.Model"/>, because the optimised runtime
    /// model drops the per-column sort order of an index and throws when it is asked for it.
    /// </summary>
    private IEntityType DesignTimeEntityType(Type clrType) =>
        _context.GetService<IDesignTimeModel>().Model.FindEntityType(clrType)!;
}
