// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Guards who gets automatic updates by default. The initial policy (Update:InitialAutoEnabled/InitialNotifyOnly)
/// must be set by the customer deployments only - the on-prem compose stack (which the marketplace compose bundle
/// ships verbatim) and the native office-PC installer - and must never appear in the vendor's own production compose
/// files or in any appsettings file baked into the shared image, because the vendor production is deployed by CI and
/// must not start replacing itself.
/// The super-repository copy of the server compose file only exists in the full checkout; where it is absent it is
/// skipped.
/// </summary>

using Klacks.UnitTest.TestHelpers;

namespace Klacks.UnitTest.Architecture;

[TestFixture]
public class UpdateInitialPolicyDeploymentGuardTests
{
    private const string InitialAutoEnabledKey = "InitialAutoEnabled";
    private const string InitialNotifyOnlyKey = "InitialNotifyOnly";
    private const string OnPremAutoEnabledLine = "- Update__InitialAutoEnabled=${UPDATE_AUTO_ENABLED:-true}";
    private const string OnPremNotifyOnlyLine = "- Update__InitialNotifyOnly=${UPDATE_NOTIFY_ONLY:-false}";
    private const string NativeAutoEnabledEntry = "InitialAutoEnabled = 'true'";
    private const string NativeNotifyOnlyEntry = "InitialNotifyOnly  = 'false'";
    private const string AppSettingsPattern = "appsettings*.json";

    private static readonly string[] ApiServerComposeSegments = ["Klacks.Api", "deploy", "docker-compose-server.yml"];
    private static readonly string[] SuperRepoServerComposeSegments = ["docker-compose-server.yml"];
    private static readonly string[] OnPremComposeSegments = ["Klacks.Api", "deploy", "onprem", "docker-compose.yml"];
    private static readonly string[] NativeInstallerSegments = ["Klacks.Api", "deploy", "native", "windows", "Install-Klacks.ps1"];

    [Test]
    public void OnPremCompose_SeedsAutomaticUpdatesOnByDefault()
    {
        var content = File.ReadAllText(RepositoryRootLocator.RequireFile(OnPremComposeSegments));

        content.ShouldContain(OnPremAutoEnabledLine);
        content.ShouldContain(OnPremNotifyOnlyLine);
    }

    [Test]
    public void NativeInstaller_SeedsAutomaticUpdatesOnByDefault()
    {
        var content = File.ReadAllText(RepositoryRootLocator.RequireFile(NativeInstallerSegments));

        content.ShouldContain(NativeAutoEnabledEntry);
        content.ShouldContain(NativeNotifyOnlyEntry);
    }

    [Test]
    public void VendorServerCompose_NeverSetsTheInitialPolicy()
    {
        var files = new List<string> { RepositoryRootLocator.RequireFile(ApiServerComposeSegments) };
        var superRepoCompose = RepositoryRootLocator.FindFile(SuperRepoServerComposeSegments);
        if (superRepoCompose != null)
        {
            files.Add(superRepoCompose);
        }

        foreach (var file in files)
        {
            AssertNoInitialPolicy(file);
        }
    }

    [Test]
    public void AppSettings_NeverSetTheInitialPolicy()
    {
        var files = Directory.GetFiles(RepositoryRootLocator.ApiProject, AppSettingsPattern, SearchOption.TopDirectoryOnly);

        files.ShouldNotBeEmpty();
        foreach (var file in files)
        {
            AssertNoInitialPolicy(file);
        }
    }

    private static void AssertNoInitialPolicy(string file)
    {
        var content = File.ReadAllText(file);
        content.ShouldNotContain(InitialAutoEnabledKey, Case.Sensitive,
            $"{file} must not set the initial auto-update policy - it is reserved for customer installations.");
        content.ShouldNotContain(InitialNotifyOnlyKey, Case.Sensitive,
            $"{file} must not set the initial auto-update policy - it is reserved for customer installations.");
    }
}
