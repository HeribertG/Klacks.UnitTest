// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

using Klacks.ScheduleOptimizer.TokenEvolution.Initialization;

namespace Klacks.UnitTest.Autofill.Support;

/// <summary>
/// One rule setting the swap-chain measurement is run with.
/// </summary>
/// <param name="Label">Name of the setting in the report</param>
/// <param name="OpenSlot">Rung the employee taking the open slot is filtered with</param>
/// <param name="Receiver">Rung every employee taking over a handed-on shift is filtered with</param>
/// <param name="PackageRestMayShorten">
/// True to let the package rest (48 h between two packages) step aside on every move; the minimum pause of 11 h,
/// the weekly rest days and every other hard rule still hold
/// </param>
public sealed record SwapChainProbeVariant(
    string Label, SlotRelaxation OpenSlot, SlotRelaxation Receiver, bool PackageRestMayShorten = false)
{
    /// <summary>
    /// Every move keeps the 5/2 block ideal and the package rest.
    /// A soft deviation (package rest, block ideal) the engine's own plan already held is tolerated when a move recreates it.
    /// </summary>
    public static SwapChainProbeVariant Strict { get; } =
        new("strict (block ideal kept on every move)", SlotRelaxation.None, SlotRelaxation.None);

    /// <summary>
    /// Only the open slot may exceed the block ideal, the rule of today's one-move relocation.
    /// A soft deviation (package rest, block ideal) the engine's own plan already held is tolerated when a move recreates it.
    /// </summary>
    public static SwapChainProbeVariant IdealRelaxedOnTheSlot { get; } =
        new("block ideal relaxed on the open slot only", SlotRelaxation.All, SlotRelaxation.None);

    /// <summary>
    /// Every move may exceed the block ideal; the package rest and the hard rules hold.
    /// A soft deviation (package rest, block ideal) the engine's own plan already held is tolerated when a move recreates it.
    /// </summary>
    public static SwapChainProbeVariant IdealRelaxedOnEveryMove { get; } =
        new("block ideal relaxed on every move", SlotRelaxation.All, SlotRelaxation.All);

    /// <summary>
    /// Owner ruling 2026-11-11 ("Variante eins"): like <see cref="IdealRelaxedOnEveryMove"/>, and the package rest may be
    /// shortened to cover a slot that would stay open otherwise, never below the minimum pause. Every shortening a
    /// chain causes is counted as an exception.
    /// </summary>
    public static SwapChainProbeVariant PackageRestShortenedOnEveryMove { get; } =
        new("block ideal relaxed and package rest shortenable (never below 11 h) on every move",
            SlotRelaxation.All, SlotRelaxation.All, PackageRestMayShorten: true);

    /// <summary>The settings in the order they are reported.</summary>
    public static IReadOnlyList<SwapChainProbeVariant> All { get; } =
        [Strict, IdealRelaxedOnTheSlot, IdealRelaxedOnEveryMove, PackageRestShortenedOnEveryMove];

    /// <summary>The settings whose refusals are broken down by rule for every slot that stays open.</summary>
    public static IReadOnlyList<SwapChainProbeVariant> Diagnosed { get; } =
        [IdealRelaxedOnEveryMove, PackageRestShortenedOnEveryMove];
}
