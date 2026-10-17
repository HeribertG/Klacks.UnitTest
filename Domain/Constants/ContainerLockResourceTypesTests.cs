// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// The container lock resource types fail closed: every resource type constant must be listed in the one table that
/// declares its key (container or work), otherwise the lock endpoint would refuse it, and anything not in the table is
/// unknown. Found by reflection, so a new constant without a table entry fails here instead of slipping through.
/// </summary>

using System.Reflection;
using Klacks.Api.Domain.Constants;
using Klacks.Api.Domain.Enums;

namespace Klacks.UnitTest.Domain.Constants;

[TestFixture]
public class ContainerLockResourceTypesTests
{
    private static IEnumerable<string> ResourceTypeConstants() =>
        typeof(ContainerLockResourceTypes)
            .GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(field => field.IsLiteral && field.FieldType == typeof(string))
            .Select(field => (string)field.GetRawConstantValue()!);

    [Test]
    public void EveryResourceTypeConstant_IsDeclaredInTheKeyTable()
    {
        var constants = ResourceTypeConstants().ToList();

        constants.ShouldNotBeEmpty();
        constants.Where(type => ContainerLockResourceTypes.FindKey(type) == null)
            .ShouldBeEmpty("every resource type constant needs a key in ContainerLockResourceTypes");
    }

    [TestCase(ContainerLockResourceTypes.ContainerTemplate, ContainerLockKey.Container)]
    [TestCase(ContainerLockResourceTypes.ContainerShiftOverride, ContainerLockKey.Container)]
    [TestCase(ContainerLockResourceTypes.ContainerWork, ContainerLockKey.Work)]
    public void ResourceType_DeclaresItsKey(string resourceType, ContainerLockKey expected)
    {
        ContainerLockResourceTypes.FindKey(resourceType).ShouldBe(expected);
    }

    [TestCase(null)]
    [TestCase("")]
    [TestCase("Anything")]
    [TestCase("containertemplate")]
    public void UnknownResourceType_HasNoKey(string? resourceType)
    {
        ContainerLockResourceTypes.FindKey(resourceType).ShouldBeNull();
    }
}
