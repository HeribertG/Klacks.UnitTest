// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Unit tests for the macro DSL reference shown by start_company_rule: the OUTPUT channels it lists are exactly
/// the channels the backend processes (a listed channel that is silently discarded would lead the model into a
/// macro that the apply step refuses), and its example script passes the OUTPUT channel policy.
/// </summary>

using System.Globalization;
using System.Text.RegularExpressions;
using Klacks.Api.Application.Skills;
using Klacks.Api.Application.Skills.CompanyRules;
using Klacks.Api.Domain.Constants;
using Klacks.Api.Infrastructure.Services.Macros;

namespace Klacks.UnitTest.Skills;

[TestFixture]
public class CompanyRuleMacroDslReferenceTests
{
    private const string ChannelLinePrefix = "OUTPUT channels";
    private const string ExamplePrefix = "Example: ";

    private static readonly Regex ListedChannel = new(@"(?<channel>\d+) = ", RegexOptions.Compiled);

    private static string Line(string prefix) =>
        CompanyRuleMacroDslReference.Reference.Split('\n').Single(line => line.StartsWith(prefix, StringComparison.Ordinal));

    [Test]
    public void ListedOutputChannels_AreExactlyTheChannelsTheBackendProcesses()
    {
        var listed = ListedChannel.Matches(Line(ChannelLinePrefix))
            .Select(match => int.Parse(match.Groups["channel"].Value, CultureInfo.InvariantCulture))
            .ToList();

        listed.ShouldBe(MacroOutputChannels.Supported.OrderBy(channel => channel), ignoreOrder: true);
    }

    [Test]
    public void Example_PassesTheOutputChannelPolicy()
    {
        var example = Line(ExamplePrefix)[ExamplePrefix.Length..];

        var violation = MacroOutputChannelPolicy.FindViolation(new MacroOutputChannelInspector().Inspect(example));

        violation.ShouldBeNull();
    }
}
