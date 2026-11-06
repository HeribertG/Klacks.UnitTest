// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Drift guard for the chat error codes: the Angular constants in
/// Klacks.Ui/src/app/domain/constants/assistant-chat-error.constants.ts declare exactly the codes of
/// AssistantChatErrorCodes. A code only the backend sends falls back to its English message in the chat; a code only
/// the UI knows is a translation nobody ever shows.
///
/// Same structure as AccountRegistrationErrorCodesUiMirrorGuardTests: a source scan of the TypeScript file,
/// inconclusive when Klacks.Ui is not checked out next to Klacks.Api, a hard failure when it is checked out but lacks
/// the file. Only plain string literals of constants named *_ERROR_CODE are read.
/// </summary>

using System.Reflection;
using System.Text.RegularExpressions;
using Klacks.Api.Domain.Constants;
using Klacks.UnitTest.TestHelpers;

namespace Klacks.UnitTest.Architecture;

[TestFixture]
public class AssistantChatErrorCodesUiMirrorGuardTests
{
    private const string UiProjectDirectory = "Klacks.Ui";

    private static readonly string[] ConstantsFileSegments =
    [
        "src", "app", "domain", "constants", "assistant-chat-error.constants.ts"
    ];

    private static readonly Regex ErrorCodeDeclaration = new(
        @"(?:export\s+)?const\s+[A-Z0-9_]+_ERROR_CODE\s*(?::[^=]+)?=\s*['""](?<value>[^'""]+)['""]",
        RegexOptions.Compiled);

    [Test]
    public void UiConstants_MustMirrorTheBackendChatErrorCodes()
    {
        var uiProject = RepositoryRootLocator.FindDirectory(UiProjectDirectory);
        if (uiProject == null)
        {
            Assert.Inconclusive(RepositoryRootLocator.NotFoundMessage(UiProjectDirectory));
            return;
        }

        var path = Path.Combine(uiProject, Path.Combine(ConstantsFileSegments));
        File.Exists(path).ShouldBeTrue(
            $"The Angular chat error constants file is missing: {path}. {UiProjectDirectory} is checked out, so the " +
            "chat cannot translate a coded error event — that is drift, not a missing checkout.");

        var uiCodes = ErrorCodeDeclaration.Matches(File.ReadAllText(path))
            .Select(match => match.Groups["value"].Value)
            .ToHashSet(StringComparer.Ordinal);

        uiCodes.Count.ShouldBeGreaterThan(0, $"No *_ERROR_CODE literal was read from {path}; a green result would compare nothing.");

        uiCodes.OrderBy(code => code, StringComparer.Ordinal).ShouldBe(
            BackendCodes().OrderBy(code => code, StringComparer.Ordinal),
            $"{path} must declare exactly the codes of {nameof(AssistantChatErrorCodes)}.");
    }

    private static IReadOnlySet<string> BackendCodes() =>
        typeof(AssistantChatErrorCodes)
            .GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(field => field.IsLiteral && field.FieldType == typeof(string))
            .Select(field => (string)field.GetRawConstantValue()!)
            .ToHashSet(StringComparer.Ordinal);
}
