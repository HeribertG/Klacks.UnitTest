// Copyright (c) Heribert Gasparoli Private. All rights reserved.

using System.Reflection;
using Klacks.ScheduleRecovery.Engine;
using NUnit.Framework;
using Shouldly;

namespace Klacks.UnitTest.ScheduleRecovery;

/// <summary>
/// Guards the pivotal L7 decision: the recovery engine is a pure assembly that references neither
/// Klacks.Api nor Klacks.ScheduleOptimizer, so it carries none of their transitive GUI/CLI dependencies
/// (SkiaSharp / Spectre.Console). The test fails the moment any forbidden reference is added.
/// </summary>
[TestFixture]
public sealed class RecoveryEngineArchitectureTests
{
    private static readonly string[] ForbiddenReferences =
    [
        "Klacks.Api",
        "Klacks.ScheduleOptimizer",
        "SkiaSharp",
        "Spectre.Console",
        "CommandLine"
    ];

    private static readonly string[] AllowedNonFrameworkPrefixes =
    [
        "System.",
        "Microsoft.",
        "netstandard",
        "mscorlib"
    ];

    private static AssemblyName[] ReferencedAssemblies
        => typeof(LocalRepairEngine).Assembly.GetReferencedAssemblies();

    [Test]
    public void Engine_does_not_reference_Api_or_Optimizer_or_their_gui_dependencies()
    {
        var names = ReferencedAssemblies.Select(a => a.Name).ToList();

        foreach (var forbidden in ForbiddenReferences)
        {
            names.ShouldNotContain(forbidden);
        }
    }

    [Test]
    public void Engine_references_only_framework_assemblies()
    {
        var nonFramework = ReferencedAssemblies
            .Select(a => a.Name ?? string.Empty)
            .Where(name => !AllowedNonFrameworkPrefixes.Any(name.StartsWith))
            .ToList();

        nonFramework.ShouldBeEmpty(
            $"the pure engine must only depend on the framework, but also references: {string.Join(", ", nonFramework)}");
    }
}
