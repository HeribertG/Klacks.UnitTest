// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Drift guard between the employee import contract constants of Klacks.Api (issue codes, error codes,
/// issue fields) and their mirror in Klacks.Ui/src/app/domain/constants/client-import.constants.ts. The
/// UI builds translation keys and cell highlighting from these strings, so a code the backend emits but
/// the UI does not list renders as a generic text, and a field the UI does not know highlights no cell.
/// The check is a source scan of the TypeScript file (plain string literals only). Where Klacks.Ui is not
/// checked out next to Klacks.Api the guard reports itself inconclusive; a Klacks.Ui without the constants
/// file is a hard failure.
/// </summary>

using System.Reflection;
using System.Text.RegularExpressions;
using Klacks.Api.Domain.Constants;

namespace Klacks.UnitTest.ClientImport;

[TestFixture]
public class ClientImportUiContractGuardTests
{
    private const string UiConstantsRelativePath = "Klacks.Ui/src/app/domain/constants/client-import.constants.ts";
    private const string StringLiteralPattern = @"'(?<value>[^']+)'";
    private const string ErrorCodeAlreadyCommittedConstant = "CLIENT_IMPORT_ERROR_CODE_ALREADY_COMMITTED";

    [Test]
    public void IssueCodes_MatchTheUiList()
    {
        var source = ReadUiConstants();
        if (source == null)
        {
            return;
        }

        var ui = LiteralsOfArray(source, "CLIENT_IMPORT_ISSUE_CODES");

        ui.ShouldBe(ConstantsOf(typeof(ClientImportIssueCodes)), ignoreOrder: true);
    }

    [Test]
    public void ErrorCodes_MatchTheUiFileAndRequestLists()
    {
        var source = ReadUiConstants();
        if (source == null)
        {
            return;
        }

        var ui = LiteralsOfArray(source, "CLIENT_IMPORT_FILE_ERROR_CODES")
            .Concat(LiteralsOfArray(source, "CLIENT_IMPORT_REQUEST_ERROR_CODES"))
            .Append(LiteralOfConstant(source, ErrorCodeAlreadyCommittedConstant))
            .ToList();

        ui.ShouldBe(ConstantsOf(typeof(ClientImportErrorCodes)), ignoreOrder: true);
    }

    [Test]
    public void IssueFields_MatchTheUiFieldMap()
    {
        var source = ReadUiConstants();
        if (source == null)
        {
            return;
        }

        var declaration = Regex.Match(source, @"CLIENT_IMPORT_FIELDS\s*=\s*\{(?<body>.*?)\}\s*as const", RegexOptions.Singleline);
        declaration.Success.ShouldBeTrue("CLIENT_IMPORT_FIELDS is missing in the UI constants file.");

        var ui = Regex.Matches(declaration.Groups["body"].Value, @":\s*" + StringLiteralPattern)
            .Select(match => match.Groups["value"].Value)
            .ToList();

        ui.ShouldBe(ConstantsOf(typeof(ClientImportFields)), ignoreOrder: true);
    }

    private static List<string> ConstantsOf(Type type) =>
        type.GetFields(BindingFlags.Public | BindingFlags.Static | BindingFlags.FlattenHierarchy)
            .Where(field => field.IsLiteral && field.FieldType == typeof(string))
            .Select(field => (string)field.GetRawConstantValue()!)
            .ToList();

    private static List<string> LiteralsOfArray(string source, string name)
    {
        var declaration = Regex.Match(source, name + @"\s*(?::[^=]+)?=\s*\[(?<body>.*?)\]", RegexOptions.Singleline);
        declaration.Success.ShouldBeTrue($"{name} is missing in the UI constants file.");

        return Regex.Matches(declaration.Groups["body"].Value, StringLiteralPattern)
            .Select(match => match.Groups["value"].Value)
            .ToList();
    }

    private static string LiteralOfConstant(string source, string name)
    {
        var declaration = Regex.Match(source, name + @"\s*(?::[^=]+)?=\s*" + StringLiteralPattern);
        declaration.Success.ShouldBeTrue($"{name} is missing in the UI constants file.");
        return declaration.Groups["value"].Value;
    }

    private static string? ReadUiConstants()
    {
        var directory = new DirectoryInfo(TestContext.CurrentContext.TestDirectory);
        var segments = UiConstantsRelativePath.Split('/');
        var uiRoot = string.Empty;

        while (directory != null)
        {
            var candidateRoot = Path.Combine(directory.FullName, segments[0]);
            if (Directory.Exists(candidateRoot))
            {
                uiRoot = candidateRoot;
                break;
            }

            directory = directory.Parent;
        }

        if (uiRoot.Length == 0)
        {
            Assert.Inconclusive($"'{segments[0]}' is not checked out next to Klacks.Api.");
            return null;
        }

        var path = Path.Combine([directory!.FullName, .. segments]);
        File.Exists(path).ShouldBeTrue($"'{UiConstantsRelativePath}' is missing in the checked-out Klacks.Ui.");
        return File.ReadAllText(path);
    }
}
