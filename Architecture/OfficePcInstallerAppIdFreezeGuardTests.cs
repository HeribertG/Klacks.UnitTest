// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Freeze guard for the Inno Setup AppId of the office PC installer (Klacks.Api/deploy/native/windows/installer/Klacks.iss).
/// The AppId names the Windows uninstall entry HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\{AppId}_is1, into
/// which Klacks.Updater writes DisplayVersion after an update (WindowsUninstallEntry.AppId, frozen by its own test in
/// Klacks.Updater). A changed AppId makes the updater write into a key that no longer exists, and existing installations
/// get a second entry under "Installed apps" with the next setup run.
/// The script stores the GUID with Inno's brace escaping ("{{" for a literal "{"), so the expected line keeps it.
/// </summary>

using System.Text.RegularExpressions;
using Klacks.UnitTest.TestHelpers;

namespace Klacks.UnitTest.Architecture;

[TestFixture]
public class OfficePcInstallerAppIdFreezeGuardTests
{
    private const string InstallerScriptPath = "deploy/native/windows/installer/Klacks.iss";
    private const string ExpectedAppIdLine = "AppId={{6F1B7D52-3C0E-4B8A-9E41-5A2D8C7F0B13}";
    private const int ExpectedAppIdLineCount = 1;

    private static readonly Regex AppIdDirective = new(@"^\s*AppId\s*=", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    [Test]
    public void InstallerScript_MustKeepTheFrozenAppId()
    {
        var scriptPath = Path.Combine(RepositoryRootLocator.ApiProject, InstallerScriptPath);
        File.Exists(scriptPath).ShouldBeTrue($"Installer script not found: {scriptPath}");

        var appIdLines = File.ReadAllLines(scriptPath)
            .Where(line => AppIdDirective.IsMatch(line))
            .Select(line => line.Trim())
            .ToList();

        appIdLines.Count.ShouldBe(ExpectedAppIdLineCount,
            $"Klacks.iss must contain exactly one AppId directive, found: {string.Join(", ", appIdLines)}");
        appIdLines[0].ShouldBe(ExpectedAppIdLine,
            "The Inno Setup AppId of the office PC installer is frozen. Klacks.Updater writes DisplayVersion into " +
            "HKLM\\SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\Uninstall\\{AppId}_is1 (WindowsUninstallEntry.AppId); " +
            "a changed AppId makes it write into an orphaned key, and existing installations get a second entry under " +
            "\"Installed apps\". If the change is intended, update WindowsUninstallEntry.AppId and its freeze test in " +
            "Klacks.Updater together with this constant.");
    }
}
