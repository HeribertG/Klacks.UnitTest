// Copyright (c) Heribert Gasparoli Private. All rights reserved.

using Klacks.Api.Application.Skills.Generic;
using NUnit.Framework;
using Shouldly;

namespace Klacks.UnitTest.Skills;

[TestFixture]
public class PageExplainSkillRoutesTests
{
    [TestCase("/workplace/dashboard", "explain_page_dashboard")]
    [TestCase("/workplace/schedule", "explain_page_schedule")]
    [TestCase("/workplace/absence", "explain_page_absence")]
    [TestCase("/workplace/client-availability", "explain_page_availability")]
    [TestCase("/workplace/shift", "explain_page_shifts")]
    [TestCase("/workplace/client", "explain_page_employees")]
    [TestCase("/workplace/group", "explain_page_groups")]
    [TestCase("/workplace/period-closing", "explain_page_period_closing")]
    [TestCase("/workplace/inbox", "explain_page_inbox")]
    [TestCase("/workplace/settings", "explain_page_settings_overview")]
    [TestCase("/workplace/profile", "explain_page_profile")]
    public void ResolveSkillName_ExactRoute_ReturnsSkill(string route, string expected)
    {
        PageExplainSkillRoutes.ResolveSkillName(route).ShouldBe(expected);
    }

    [TestCase("/workplace/edit-address/0c4f2e1a-aaaa-bbbb-cccc-000000000001", "explain_page_employees")]
    [TestCase("/workplace/edit-shift/123", "explain_page_shifts")]
    [TestCase("/workplace/cut-shift/123", "explain_page_shifts")]
    [TestCase("/workplace/edit-group/abc", "explain_page_groups")]
    public void ResolveSkillName_RouteWithEntityId_ReturnsSkill(string route, string expected)
    {
        PageExplainSkillRoutes.ResolveSkillName(route).ShouldBe(expected);
    }

    [TestCase("/workplace/dashboard?tab=resources", "explain_page_dashboard")]
    [TestCase("/workplace/dashboard#locations", "explain_page_dashboard")]
    [TestCase("/workplace/settings/", "explain_page_settings_overview")]
    [TestCase("/WORKPLACE/Dashboard", "explain_page_dashboard")]
    public void ResolveSkillName_NormalizesQueryFragmentSlashAndCase(string route, string expected)
    {
        PageExplainSkillRoutes.ResolveSkillName(route).ShouldBe(expected);
    }

    [Test]
    public void ResolveSkillName_ClientAvailability_DoesNotFallBackToClient()
    {
        PageExplainSkillRoutes.ResolveSkillName("/workplace/client-availability/2026")
            .ShouldBe("explain_page_availability");
    }

    [TestCase("/workplace/unknown-page")]
    [TestCase("/login")]
    [TestCase("")]
    [TestCase(null)]
    [TestCase("/")]
    public void ResolveSkillName_UnknownOrEmptyRoute_ReturnsNull(string? route)
    {
        PageExplainSkillRoutes.ResolveSkillName(route).ShouldBeNull();
    }
}
