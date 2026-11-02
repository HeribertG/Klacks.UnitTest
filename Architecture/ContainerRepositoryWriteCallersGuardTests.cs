// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// The container template and override repositories are written only by the six command handlers, because those apply
/// IContainerVisibilityGuard and refuse the container of a key. Every other writer, skills included, goes through these
/// commands. A write here is a write method or a tracked read, whose changes the next save stores. The scan finds receivers
/// by their declared repository type (field, parameter, or a local resolved from a service provider) and looks for calls
/// on those names; a repository passed on as an argument is followed only through the entry in ImplementationDetails. The
/// repository classes themselves are not scanned. An unclassified repository member fails, so a new write method cannot
/// slip past the list.
/// </summary>

using System.Reflection;
using System.Text.RegularExpressions;
using Klacks.UnitTest.TestHelpers;

namespace Klacks.UnitTest.Architecture;

[TestFixture]
public class ContainerRepositoryWriteCallersGuardTests
{
    private const string SourceFilePattern = "*.cs";
    private const string LineCommentPrefix = "//";
    private const int MinimumScannedFiles = 1000;
    private const string ReceiverGroup = "receiver";
    private const string RepositoryTypes =
        "(?:IContainerTemplateRepository|ContainerTemplateRepository|IContainerShiftOverrideRepository|ContainerShiftOverrideRepository)";

    private static readonly string[] SkippedDirectories = ["bin", "obj", "Migrations"];

    private static readonly string[] RepositoryImplementations =
    [
        "Infrastructure/Repositories/Schedules/ContainerTemplateRepository.cs",
        "Infrastructure/Repositories/Schedules/ContainerShiftOverrideRepository.cs"
    ];

    private static readonly SortedSet<string> AllowedWriters = new(StringComparer.Ordinal)
    {
        "Application/Handlers/ContainerShiftOverrides/DeleteContainerShiftOverrideCommandHandler.cs",
        "Application/Handlers/ContainerShiftOverrides/PostContainerShiftOverrideCommandHandler.cs",
        "Application/Handlers/ContainerShiftOverrides/PutContainerShiftOverrideCommandHandler.cs",
        "Application/Handlers/ContainerTemplates/DeleteContainerTemplatesCommandHandler.cs",
        "Application/Handlers/ContainerTemplates/PostContainerTemplatesCommandHandler.cs",
        "Application/Handlers/ContainerTemplates/PutContainerTemplatesCommandHandler.cs"
    };

    private static readonly IReadOnlyDictionary<string, string> ImplementationDetails = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["Domain/Services/ContainerTemplates/ContainerTemplateService.cs"] =
            "the item diff behind ContainerTemplateRepository.PutWithItems; only that repository holds this service"
    };

    private static readonly string[] WriteMembers =
    [
        "Add", "Put", "PutWithItems", "Remove", "Delete", "UpdateItem", "CreateItem", "DeleteItem",
        "GetWithTracking", "GetTemplatesForContainerWithTracking", "GetItemsForTemplate"
    ];

    private static readonly string[] ReadMembers =
    [
        "Get", "GetNoTracking", "Exists", "List", "Detach", "GetTemplatesForContainer",
        "GetContainerIdsWithTemplateAsync", "GetUsedShiftIds", "GetByContainerAndDate", "GetByContainerAndDateWithItems",
        "GetByContainerAndDateRange", "HasWorkForOverride"
    ];

    private static readonly Regex DeclaredReceiver = new(
        $@"\b{RepositoryTypes}\s+(?<{ReceiverGroup}>[A-Za-z_][A-Za-z0-9_]*)\s*[,;)=]",
        RegexOptions.Compiled);

    private static readonly Regex ResolvedReceiver = new(
        $@"\bvar\s+(?<{ReceiverGroup}>[A-Za-z_][A-Za-z0-9_]*)\s*=[^;]*GetRequiredService<{RepositoryTypes}>",
        RegexOptions.Compiled);

    [Test]
    public void TheRepositoryWrites_AreCalledByExactlyTheSixHandlers()
    {
        var (writers, scanned) = ScanApi();

        scanned.ShouldBeGreaterThan(
            MinimumScannedFiles,
            $"Only {scanned} source files were scanned. The guard cannot have inspected the real source tree.");

        writers.Except(ImplementationDetails.Keys).OrderBy(path => path, StringComparer.Ordinal).ToList().ShouldBe(
            AllowedWriters.ToList(),
            "Only the six command handlers may write templates and overrides: they apply IContainerVisibilityGuard and refuse " +
            "the container of a key. Send the command instead of calling the repository. A handler that left this list must " +
            "leave AllowedWriters too.");
    }

    [Test]
    public void ImplementationDetails_AreStillWriters()
    {
        var (writers, _) = ScanApi();

        ImplementationDetails.Keys.Where(path => !writers.Contains(path)).ShouldBeEmpty(
            "This entry excuses a file that no longer writes through the repository. Remove it.");
    }

    [Test]
    public void EveryRepositoryMember_IsClassifiedAsWriteOrRead()
    {
        var members = new[] { typeof(IContainerTemplateRepository), typeof(IContainerShiftOverrideRepository) }
            .SelectMany(type => type.GetInterfaces().Append(type))
            .SelectMany(type => type.GetMethods(BindingFlags.Public | BindingFlags.Instance))
            .Select(method => method.Name)
            .Distinct(StringComparer.Ordinal);

        members.Except(WriteMembers).Except(ReadMembers).ShouldBeEmpty(
            "A repository member is unclassified. Add it to WriteMembers when it changes data or hands out tracked entities, " +
            "otherwise to ReadMembers.");
    }

    [Test]
    public void Scanner_FindsWritesOnEveryDeclaredReceiver()
    {
        const string source = """
            public class Sample(IContainerTemplateRepository primary)
            {
                private readonly IContainerShiftOverrideRepository _overrides;

                public async Task Run(IServiceProvider provider)
                {
                    var resolved = provider.GetRequiredService<IContainerTemplateRepository>();
                    await primary.Add(template);
                    await _overrides
                        .Delete(entity);
                    resolved.Remove(template);
                }
            }
            """;

        FindWriteCalls(source).ShouldBe(["primary.Add", "_overrides.Delete", "resolved.Remove"], ignoreOrder: true);
    }

    [Test]
    public void Scanner_IgnoresReads_OtherReceivers_AndComments()
    {
        const string source = """
            public class Sample
            {
                private readonly IContainerTemplateRepository _templates;
                private readonly List<Item> _items;

                public async Task Run()
                {
                    // _templates.Add(template);
                    await _templates.GetTemplatesForContainer(id);
                    await _templates.GetNoTracking(id);
                    _items.Add(item);
                }
            }
            """;

        FindWriteCalls(source).ShouldBeEmpty();
    }

    private static (SortedSet<string> Writers, int Scanned) ScanApi()
    {
        var apiRoot = RepositoryRootLocator.ApiProject;
        var writers = new SortedSet<string>(StringComparer.Ordinal);
        var scanned = 0;

        foreach (var file in Directory.EnumerateFiles(apiRoot, SourceFilePattern, SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(apiRoot, file).Replace(Path.DirectorySeparatorChar, '/');
            if (relative.Split('/').SkipLast(1).Any(segment => SkippedDirectories.Contains(segment, StringComparer.Ordinal))
                || RepositoryImplementations.Contains(relative, StringComparer.Ordinal))
            {
                continue;
            }

            scanned++;
            if (FindWriteCalls(File.ReadAllText(file)).Count > 0)
            {
                writers.Add(relative);
            }
        }

        return (writers, scanned);
    }

    private static List<string> FindWriteCalls(string source)
    {
        var code = string.Join('\n', source.Split('\n').Select(StripLineComment));
        var receivers = DeclaredReceiver.Matches(code)
            .Concat(ResolvedReceiver.Matches(code))
            .Select(match => match.Groups[ReceiverGroup].Value)
            .Distinct(StringComparer.Ordinal)
            .ToList();
        if (receivers.Count == 0)
        {
            return [];
        }

        var call = new Regex(
            $@"\b(?<{ReceiverGroup}>{string.Join('|', receivers.Select(Regex.Escape))})\s*\.\s*(?<member>{string.Join('|', WriteMembers)})\s*\(");

        return call.Matches(code)
            .Select(match => $"{match.Groups[ReceiverGroup].Value}.{match.Groups["member"].Value}")
            .ToList();
    }

    private static string StripLineComment(string line)
    {
        var comment = line.IndexOf(LineCommentPrefix, StringComparison.Ordinal);
        return comment < 0 ? line : line[..comment];
    }
}
