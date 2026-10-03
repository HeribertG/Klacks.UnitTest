// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

using Klacks.Api.Application.Services.Setup;

namespace Klacks.UnitTest.Application.Services.Setup;

[TestFixture]
public class CounterRuleImportValuesTests
{
    [Test]
    public void NewImportedRule_IsMarkedAsImport_Approved_WithImportKeys()
    {
        var schedulingRuleId = Guid.NewGuid();
        var values = new CounterRuleImportValues(CounterEventType.NightShift, CounterPeriod.Year, 25, null);

        var rule = values.ToNewImportedRule(schedulingRuleId, "region-setup:compliance.counterRules:x", "hash");

        rule.Id.ShouldNotBe(Guid.Empty);
        rule.Origin.ShouldBe(RuleOrigin.Import);
        rule.ApprovalStatus.ShouldBe(RuleApprovalStatus.Approved);
        rule.EventType.ShouldBe(CounterEventType.NightShift);
        rule.Period.ShouldBe(CounterPeriod.Year);
        rule.Threshold.ShouldBe(25);
        rule.SchedulingRuleId.ShouldBe(schedulingRuleId);
        rule.ImportSourceKey.ShouldBe("region-setup:compliance.counterRules:x");
        rule.ImportContentHash.ShouldBe("hash");
    }
}
