// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Unit tests for PayrollArtifactStorage on a temporary directory: an uploaded artifact is read back byte for byte,
/// a missing artifact (also in a missing directory) is reported as null, a delete removes it, a key that leaves the
/// storage root is rejected and an empty root path falls back to the payroll default, never to the ERP drop zone.
/// </summary>
using Klacks.Api.Domain.Services.Exports;
using Klacks.Api.Infrastructure.Services.Exports;
using Microsoft.Extensions.Options;
using Shouldly;

namespace Klacks.UnitTest.Infrastructure.Services.Exports;

[TestFixture]
public class PayrollArtifactStorageTests
{
    private const string Key = "payroll-export/20260101-20260131/artifact.csv";

    private string _root = null!;
    private PayrollArtifactStorage _storage = null!;

    [SetUp]
    public void Setup()
    {
        _root = Path.Combine(Path.GetTempPath(), "klacks-payroll-storage-" + Guid.NewGuid().ToString("N"));
        _storage = new PayrollArtifactStorage(Options.Create(new PayrollObjectStorageOptions { RootPath = _root }));
    }

    [TearDown]
    public void TearDown()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Test]
    public async Task UploadedArtifact_IsReadBackByteForByte()
    {
        byte[] content = [1, 2, 3, 250];

        await _storage.UploadAsync(Key, content);

        (await _storage.ReadAsync(Key)).ShouldBe(content);
        File.Exists(Path.Combine(_root, "payroll-export", "20260101-20260131", "artifact.csv")).ShouldBeTrue();
    }

    [Test]
    public async Task MissingArtifact_IsNull_AlsoWhenTheDirectoryDoesNotExist()
    {
        (await _storage.ReadAsync(Key)).ShouldBeNull();

        await _storage.UploadAsync(Key, [1]);

        (await _storage.ReadAsync("payroll-export/20260101-20260131/other.csv")).ShouldBeNull();
    }

    [Test]
    public async Task DeletedArtifact_IsNoLongerRead()
    {
        await _storage.UploadAsync(Key, [1]);

        await _storage.DeleteAsync(Key);

        (await _storage.ReadAsync(Key)).ShouldBeNull();
    }

    [Test]
    public async Task KeyOutsideTheRoot_IsRejected()
    {
        await Should.ThrowAsync<ArgumentException>(() => _storage.UploadAsync("../escape.csv", [1]));
        await Should.ThrowAsync<ArgumentException>(() => _storage.ReadAsync("../escape.csv"));
    }

    [Test]
    public void EmptyRootPath_FallsBackToThePayrollDefault()
    {
        var options = Options.Create(new PayrollObjectStorageOptions { RootPath = " " });

        Should.NotThrow(() => new PayrollArtifactStorage(options));
        PayrollObjectStorageOptions.DefaultRootPath.ShouldBe("PayrollExports");
    }
}
