// Copyright (c) Heribert Gasparoli Private. All rights reserved.

using Klacks.Api.Domain.Services.Accounts;
using NUnit.Framework;
using Shouldly;

namespace Klacks.UnitTest.Domain.Services.Accounts;

[TestFixture]
public class RequireOwnAdminGateDecisionTests
{
    [Test]
    public void Decide_NotExemptAndSeedAdminActive_GateIsActive()
    {
        RequireOwnAdminGateDecision.Decide(isExempt: false, seedAdminStillActive: true).ShouldBeTrue();
    }

    [Test]
    public void Decide_NotExemptAndSeedAdminDeactivated_GateIsInactive()
    {
        RequireOwnAdminGateDecision.Decide(isExempt: false, seedAdminStillActive: false).ShouldBeFalse();
    }

    [Test]
    public void Decide_ExemptAndSeedAdminActive_GateIsInactive()
    {
        RequireOwnAdminGateDecision.Decide(isExempt: true, seedAdminStillActive: true).ShouldBeFalse();
    }

    [Test]
    public void Decide_ExemptAndSeedAdminDeactivated_GateIsInactive()
    {
        RequireOwnAdminGateDecision.Decide(isExempt: true, seedAdminStillActive: false).ShouldBeFalse();
    }
}
