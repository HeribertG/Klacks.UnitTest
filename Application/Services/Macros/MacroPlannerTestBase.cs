// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Shared arrangement of MacroAssignPlannerTests and MacroRevertPlannerTests: substituted holder and macro reads, a dry run
/// that returns no samples unless a test gives some, and helpers for holders, cut groups, macros and dry-run samples.
/// </summary>

using Klacks.Api.Domain.Models.Macros;

namespace Klacks.UnitTest.Application.Services.Macros;

public abstract class MacroPlannerTestBase
{
    protected const string PlainScript = "OUTPUT 1, 8";
    protected const decimal StoredValue = 5m;
    protected const decimal ComputedValue = 4m;

    protected IMacroReferenceRepository _references = null!;
    protected IMacroDryRunService _dryRun = null!;

    [SetUp]
    public void SetUpPlannerReads()
    {
        _references = Substitute.For<IMacroReferenceRepository>();
        _dryRun = Substitute.For<IMacroDryRunService>();
        _dryRun.RunAsync(
                Arg.Any<MacroAssignmentTarget>(), Arg.Any<IReadOnlyList<MacroDryRunHolder>>(), Arg.Any<CancellationToken>())
            .Returns(new MacroDryRunResult(0, 0, [], null, false));
    }

    protected MacroReferenceHolder GivenHolder(
        MacroAssignmentTarget target = MacroAssignmentTarget.Shift,
        Guid? macroId = null,
        ShiftStatus status = ShiftStatus.OriginalShift,
        Guid? cutGroupId = null,
        string name = "Night")
    {
        var holder = new MacroReferenceHolder(
            Guid.NewGuid(),
            target,
            name,
            macroId,
            target == MacroAssignmentTarget.Shift ? status : null,
            false,
            cutGroupId);
        _references.FindHolderAsync(target, holder.Id, Arg.Any<CancellationToken>()).Returns(holder);
        if (target == MacroAssignmentTarget.Shift)
        {
            GivenCutGroup(holder);
        }

        return holder;
    }

    protected void GivenCutGroup(MacroReferenceHolder holder, params MacroReferenceHolder[] siblings)
    {
        IReadOnlyList<MacroReferenceHolder> group = siblings.Prepend(holder).ToList();
        _references.FindCutGroupAsync(holder.CutGroupKey, Arg.Any<CancellationToken>()).Returns(group);
    }

    protected MacroSnapshot GivenMacro(string content = PlainScript)
    {
        var macro = new MacroSnapshot(
            Guid.NewGuid(), "Sunday plus", (int)MacroFunctionEnum.Custom, MacroCategoryEnum.Unspecified,
            MacroOrigin.AssistantExtension, content);
        _references.FindMacroAsync(macro.Id, Arg.Any<CancellationToken>()).Returns(macro);
        return macro;
    }

    protected void GivenDryRunSamples(params MacroDryRunSample[] samples)
    {
        _dryRun.RunAsync(
                Arg.Any<MacroAssignmentTarget>(), Arg.Any<IReadOnlyList<MacroDryRunHolder>>(), Arg.Any<CancellationToken>())
            .Returns(new MacroDryRunResult(samples.Length, 0, samples, null, false));
    }

    protected static MacroDryRunSample Sample(decimal? current, decimal? next) =>
        new(Guid.NewGuid(), new DateOnly(2026, 6, 10), StoredValue, current, next, false);
}
