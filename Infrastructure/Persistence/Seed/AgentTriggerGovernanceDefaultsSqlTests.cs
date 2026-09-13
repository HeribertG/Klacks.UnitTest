// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Guards the per-kind max_action value the governance seed installs: next_period_scheduling_due must
/// seed at the Execute ceiling (ProactiveGovernanceDefaults.SeededMaxActionOverrides), while every
/// other governed kind keeps the fail-safe Hint default.
/// </summary>

using Klacks.Api.Data.Seed;
using Klacks.Api.Domain.Constants;
using Klacks.Api.Domain.Enums;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using NUnit.Framework;
using Shouldly;

namespace Klacks.UnitTest.Infrastructure.Persistence.Seed;

[TestFixture]
public class AgentTriggerGovernanceDefaultsSqlTests
{
    private static List<string> ApplyStatements()
    {
        var builder = new MigrationBuilder(activeProvider: null);
        AgentTriggerGovernanceDefaultsSql.Apply(builder);
        return builder.Operations.OfType<SqlOperation>().Select(o => o.Sql).ToList();
    }

    [Test]
    public void Apply_SeedsNextPeriodSchedulingDueAtExecute()
    {
        var statements = ApplyStatements();

        var statement = statements.Single(s => s.Contains($"'{AgentTriggerKinds.NextPeriodSchedulingDue}'"));

        statement.ShouldContain($"NULL, {(int)ProactiveMaxAction.Execute},");
    }

    [Test]
    public void Apply_SeedsEmptyContainerAtHint()
    {
        var statements = ApplyStatements();

        var statement = statements.Single(s => s.Contains($"'{AgentTriggerKinds.EmptyContainer}'"));

        statement.ShouldContain($"NULL, {(int)ProactiveMaxAction.Hint},");
    }

    [Test]
    public void SeededMaxActionFor_ReturnsExecuteOnlyForNextPeriodSchedulingDue()
    {
        foreach (var triggerKind in ProactiveGovernanceDefaults.GovernedKinds)
        {
            var expected = triggerKind == AgentTriggerKinds.NextPeriodSchedulingDue
                ? ProactiveMaxAction.Execute
                : ProactiveMaxAction.Hint;

            ProactiveGovernanceDefaults.SeededMaxActionFor(triggerKind).ShouldBe(expected);
        }
    }
}
