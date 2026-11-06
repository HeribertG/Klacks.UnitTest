// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// The ERP drop folder exists from startup on: ErpDropFolderInitializer creates the storage root and the inbox folder
/// of every drop point on a real file-system storage, and the default inbox when no drop point exists yet or the drop
/// points cannot be read.
/// </summary>

using Klacks.Api.Application.Interfaces;
using Klacks.Api.Application.Services.Imports;
using Klacks.Api.Domain.Models.Imports;
using Klacks.Api.Domain.Services.Imports;
using Klacks.Api.Infrastructure.Services.Imports;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Klacks.UnitTest.Application.Services.Imports;

[TestFixture]
public class ErpDropFolderInitializerTests
{
    private string _rootPath = null!;
    private IErpDropPointRepository _repository = null!;
    private ErpDropFolderInitializer _initializer = null!;

    [SetUp]
    public void SetUp()
    {
        _rootPath = Path.Combine(Path.GetTempPath(), "klacks-erp-drop-folder-tests", Guid.NewGuid().ToString("N"));
        _repository = Substitute.For<IErpDropPointRepository>();
        var storage = new FileSystemObjectStorageService(Options.Create(new ErpObjectStorageOptions { RootPath = _rootPath }));
        _initializer = new ErpDropFolderInitializer(_repository, storage, NullLogger<ErpDropFolderInitializer>.Instance);
    }

    [TearDown]
    public void TearDown()
    {
        if (Directory.Exists(_rootPath))
        {
            Directory.Delete(_rootPath, recursive: true);
        }
    }

    [Test]
    public async Task EnsureFoldersAsync_FreshInstallationWithoutDropPoint_CreatesTheDefaultInbox()
    {
        _repository.List().Returns(new List<ErpDropPoint>());

        var health = await _initializer.EnsureFoldersAsync();

        Directory.Exists(Path.Combine(_rootPath, "erp", "orders")).ShouldBeTrue();
        health.Prefixes.Select(prefix => prefix.Prefix).ShouldBe(["erp/orders/"]);
        health.Prefixes.ShouldAllBe(prefix => prefix.Ready);
    }

    [Test]
    public async Task EnsureFoldersAsync_WithDropPoints_CreatesEachInboxOnce()
    {
        _repository.List().Returns(new List<ErpDropPoint>
        {
            new() { BucketPrefix = "erp/orders" },
            new() { BucketPrefix = "erp/orders/" },
            new() { BucketPrefix = "erp/second" },
            new() { BucketPrefix = "" },
        });

        var health = await _initializer.EnsureFoldersAsync();

        Directory.Exists(Path.Combine(_rootPath, "erp", "orders")).ShouldBeTrue();
        Directory.Exists(Path.Combine(_rootPath, "erp", "second")).ShouldBeTrue();
        health.Prefixes.Select(prefix => prefix.Prefix).ShouldBe(["erp/orders/", "erp/second/"]);
    }

    [Test]
    public async Task EnsureFoldersAsync_DropPointsUnreadable_StillCreatesTheDefaultInbox()
    {
        _repository.List().Returns<List<ErpDropPoint>>(_ => throw new InvalidOperationException("database not ready"));

        await _initializer.EnsureFoldersAsync();

        Directory.Exists(Path.Combine(_rootPath, "erp", "orders")).ShouldBeTrue();
    }
}
