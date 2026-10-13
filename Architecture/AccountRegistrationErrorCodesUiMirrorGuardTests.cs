// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Drift guard for the rejected-registration contract: the Angular constants in
/// Klacks.Ui/src/app/domain/constants/account-registration.constants.ts carry the SAME error code and the SAME
/// reason codes as AccountRegistrationErrorCodes. A reason only the backend sends falls back to the generic text in
/// the UI; a reason only the UI knows is a translation nobody ever shows.
///
/// Same structure as PermissionConstantsUiMirrorGuardTests: a source scan of the TypeScript file, inconclusive when
/// Klacks.Ui is not checked out next to Klacks.Api, a hard failure when it is checked out but lacks the file.
/// Only plain string literals of ACCOUNT_REGISTRATION_REJECTED_ERROR_CODE and the ACCOUNT_REGISTRATION_REASONS
/// object are read.
/// </summary>

using System.Reflection;
using System.Text.RegularExpressions;
using Klacks.Api.Domain.Constants;
using Klacks.UnitTest.TestHelpers;

namespace Klacks.UnitTest.Architecture;

[TestFixture]
public class AccountRegistrationErrorCodesUiMirrorGuardTests
{
    private const string UiProjectDirectory = "Klacks.Ui";
    private const string ReasonsObjectName = "ACCOUNT_REGISTRATION_REASONS";

    private static readonly string[] ConstantsFileSegments =
    [
        "src", "app", "domain", "constants", "account-registration.constants.ts"
    ];

    private static readonly Regex ErrorCodeDeclaration = new(
        @"(?:export\s+)?const\s+ACCOUNT_REGISTRATION_REJECTED_ERROR_CODE\s*(?::[^=]+)?=\s*['""](?<value>[^'""]+)['""]",
        RegexOptions.Compiled);

    private static readonly Regex ReasonsObjectDeclaration = new(
        @"(?:export\s+)?const\s+ACCOUNT_REGISTRATION_REASONS\s*(?::[^=]+)?=\s*\{",
        RegexOptions.Compiled);

    private static readonly Regex StringLiteral = new(
        @"['""](?<value>[^'""]+)['""]",
        RegexOptions.Compiled);

    [Test]
    public void UiConstants_MustMirrorTheBackendErrorCodeAndReasons()
    {
        var uiProject = RepositoryRootLocator.FindDirectory(UiProjectDirectory);
        if (uiProject == null)
        {
            Assert.Inconclusive(RepositoryRootLocator.NotFoundMessage(UiProjectDirectory));
            return;
        }

        var path = Path.Combine(uiProject, Path.Combine(ConstantsFileSegments));
        File.Exists(path).ShouldBeTrue(
            $"The Angular account-registration constants file is missing: {path}. {UiProjectDirectory} is checked " +
            "out, so the UI cannot recognise a rejected registration — that is drift, not a missing checkout.");

        var source = File.ReadAllText(path);

        var errorCode = ErrorCodeDeclaration.Match(source);
        errorCode.Success.ShouldBeTrue($"No ACCOUNT_REGISTRATION_REJECTED_ERROR_CODE literal was found in {path}.");
        errorCode.Groups["value"].Value.ShouldBe(AccountRegistrationErrorCodes.ErrorCode);

        var uiReasons = ReadReasons(source);
        uiReasons.Count.ShouldBeGreaterThan(
            0,
            $"No string literal was read from {ReasonsObjectName} in {path}; a green result would compare nothing.");

        uiReasons.OrderBy(reason => reason, StringComparer.Ordinal).ShouldBe(
            BackendReasons().OrderBy(reason => reason, StringComparer.Ordinal),
            $"{ReasonsObjectName} must hold exactly the reason codes of AccountRegistrationErrorCodes.");
    }

    private static IReadOnlySet<string> ReadReasons(string source)
    {
        var values = new HashSet<string>(StringComparer.Ordinal);
        var match = ReasonsObjectDeclaration.Match(source);
        if (!match.Success)
        {
            return values;
        }

        foreach (Match literal in StringLiteral.Matches(ExtractBracedBody(source, match.Index + match.Length - 1)))
        {
            values.Add(literal.Groups["value"].Value);
        }

        return values;
    }

    private static string ExtractBracedBody(string source, int openingBraceIndex)
    {
        var depth = 0;
        for (var i = openingBraceIndex; i < source.Length; i++)
        {
            if (source[i] == '{')
            {
                depth++;
            }
            else if (source[i] == '}')
            {
                depth--;
                if (depth == 0)
                {
                    return source.Substring(openingBraceIndex + 1, i - openingBraceIndex - 1);
                }
            }
        }

        return string.Empty;
    }

    private static IReadOnlySet<string> BackendReasons()
    {
        var values = new HashSet<string>(StringComparer.Ordinal);
        foreach (var field in typeof(AccountRegistrationErrorCodes).GetFields(BindingFlags.Public | BindingFlags.Static))
        {
            if (field.IsLiteral && field.FieldType == typeof(string)
                && field.Name != nameof(AccountRegistrationErrorCodes.ErrorCode)
                && field.GetRawConstantValue() is string value)
            {
                values.Add(value);
            }
        }

        return values;
    }
}
