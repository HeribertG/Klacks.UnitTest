// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Structural guard for GoalCandidateDto: OwnerPermissionsCsv is an internal authorization snapshot
/// (frozen permissions for unattended plan execution) and must never be exposed to the client. This
/// test fails loudly if a future edit re-adds the property to the DTO, instead of relying on review
/// diligence alone.
/// </summary>

using System.Linq;
using System.Reflection;
using Klacks.Api.Application.DTOs.Assistant;
using NUnit.Framework;
using Shouldly;

namespace Klacks.UnitTest.Application.DTOs.Assistant;

[TestFixture]
public class GoalCandidateDtoTests
{
    [Test]
    public void GoalCandidateDto_NeverExposesOwnerPermissionsCsv()
    {
        var propertyNames = typeof(GoalCandidateDto).GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Select(p => p.Name)
            .ToArray();

        propertyNames.ShouldNotContain("OwnerPermissionsCsv");
    }
}
