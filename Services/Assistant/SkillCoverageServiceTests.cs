// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Unit tests for SkillCoverageService. Builds a tiny markdown file in a temp ContentRoot
/// and verifies covered / partial / missing counts + percentage round-trip.
/// </summary>

using Klacks.Api.Application.Services.Assistant.Coverage;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;

namespace Klacks.UnitTest.Services.Assistant;

[TestFixture]
public class SkillCoverageServiceTests
{
    private string _root = null!;
    private string _docsDir = null!;

    [SetUp]
    public void Setup()
    {
        _root = Path.Combine(Path.GetTempPath(), "klacksy-coverage-" + Guid.NewGuid());
        Directory.CreateDirectory(_root);
        var contentRoot = Path.Combine(_root, "Klacks.Api");
        Directory.CreateDirectory(contentRoot);
        _docsDir = Path.Combine(_root, "docs");
        Directory.CreateDirectory(_docsDir);
    }

    [TearDown]
    public void Cleanup()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, true);
        }
    }

    private SkillCoverageService BuildService()
    {
        var env = Substitute.For<IHostEnvironment>();
        env.ContentRootPath.Returns(Path.Combine(_root, "Klacks.Api"));
        return new SkillCoverageService(env, NullLogger<SkillCoverageService>.Instance);
    }

    private string CreateUseCaseFile(string body)
    {
        var path = Path.Combine(_docsDir, "klacksy-usecases.md");
        File.WriteAllText(path, body);
        return path;
    }

    [Test]
    public async Task ComputeAsync_FileMissing_ReturnsEmptyReport()
    {
        var sut = BuildService();

        var report = await sut.ComputeAsync();

        Assert.That(report.Total, Is.EqualTo(0));
        Assert.That(report.CoveragePercent, Is.EqualTo(0d));
    }

    [Test]
    public async Task ComputeAsync_CountsRowsByStatus()
    {
        CreateUseCaseFile("""
            # title

            | # | UseCase | Status | Skills | Notes |
            |---|---|---|---|---|
            | 1 | Foo | ✅ | foo | |
            | 2 | Bar | ✅ | bar | |
            | 3 | Baz | 🟡 | baz | partial |
            | 4 | Qux | ❌ | — | missing |
            """);
        var sut = BuildService();

        var report = await sut.ComputeAsync();

        Assert.That(report.Total, Is.EqualTo(4));
        Assert.That(report.Covered, Is.EqualTo(2));
        Assert.That(report.Partial, Is.EqualTo(1));
        Assert.That(report.Missing, Is.EqualTo(1));
        Assert.That(report.CoveragePercent, Is.EqualTo(50d));
    }

    [Test]
    public async Task ComputeAsync_IgnoresHeaderAndDividerRows()
    {
        CreateUseCaseFile("""
            # title

            | # | UseCase | Status |
            |---|---|---|
            | 1 | Only one | ✅ |
            """);
        var sut = BuildService();

        var report = await sut.ComputeAsync();

        Assert.That(report.Total, Is.EqualTo(1));
        Assert.That(report.Covered, Is.EqualTo(1));
        Assert.That(report.CoveragePercent, Is.EqualTo(100d));
    }
}
