// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Tests for PlaygroundPageContextNormalizer: text fields are cut at PlaygroundMaxContextFieldChars, the client
/// multi-selection at PlaygroundMaxSelectedClientIds entries, and regular values and null contexts stay untouched.
/// </summary>

using Klacks.Api.Domain.Constants;
using Klacks.Api.Domain.Models.Assistant;
using Klacks.Api.Domain.Services.Playground;

namespace Klacks.UnitTest.Services.Playground;

[TestFixture]
public class PlaygroundPageContextNormalizerTests
{
    private const int FieldLimit = PlaygroundHardeningConstants.PlaygroundMaxContextFieldChars;

    [Test]
    public void Normalize_NullContext_DoesNothing()
    {
        Should.NotThrow(() => PlaygroundPageContextNormalizer.Normalize(null));
    }

    [Test]
    public void Normalize_CutsEveryTextField()
    {
        var context = new AssistantPageContext
        {
            CurrentRoute = new string('a', FieldLimit + 1),
            SelectedGroupId = new string('b', FieldLimit + 1),
            SelectedPeriodFrom = new string('c', FieldLimit + 1),
            SelectedPeriodUntil = new string('d', FieldLimit + 1),
            SelectedClientId = new string('e', FieldLimit + 1),
            SelectedEntityType = new string('f', FieldLimit + 1),
        };

        PlaygroundPageContextNormalizer.Normalize(context);

        context.CurrentRoute.ShouldBe(new string('a', FieldLimit));
        context.SelectedGroupId!.Length.ShouldBe(FieldLimit);
        context.SelectedPeriodFrom!.Length.ShouldBe(FieldLimit);
        context.SelectedPeriodUntil!.Length.ShouldBe(FieldLimit);
        context.SelectedClientId!.Length.ShouldBe(FieldLimit);
        context.SelectedEntityType!.Length.ShouldBe(FieldLimit);
    }

    [Test]
    public void Normalize_ExactlyTheLimit_IsKept()
    {
        var route = new string('r', FieldLimit);
        var context = new AssistantPageContext { CurrentRoute = route };

        PlaygroundPageContextNormalizer.Normalize(context);

        context.CurrentRoute.ShouldBe(route);
    }

    [Test]
    public void Normalize_CapsCountAndLengthOfTheClientSelection()
    {
        var context = new AssistantPageContext
        {
            SelectedClientIds = Enumerable.Range(0, PlaygroundHardeningConstants.PlaygroundMaxSelectedClientIds + 10)
                .Select(i => i + new string('x', FieldLimit * 2))
                .ToList(),
        };

        PlaygroundPageContextNormalizer.Normalize(context);

        context.SelectedClientIds!.Count.ShouldBe(PlaygroundHardeningConstants.PlaygroundMaxSelectedClientIds);
        context.SelectedClientIds.ShouldAllBe(id => id.Length == FieldLimit);
    }

    [Test]
    public void Normalize_UnsetFields_StayNull()
    {
        var context = new AssistantPageContext();

        PlaygroundPageContextNormalizer.Normalize(context);

        context.CurrentRoute.ShouldBeNull();
        context.SelectedClientIds.ShouldBeNull();
    }
}
