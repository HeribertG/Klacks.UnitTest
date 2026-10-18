// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

using Klacks.Api.Application.Skills;
using Klacks.Api.Domain.Services.ShiftSchedule;

namespace Klacks.UnitTest.Skills;

/// <summary>
/// ShiftResolver: a unique visible name resolves; a hidden shift is left out before anything is counted, so a hidden
/// name reads exactly like an unknown one and never appears in an ambiguity list; same-named cuts are narrowed to the
/// piece valid on the date; genuine duplicates are listed, never picked; a hidden id reads like a missing id; the name
/// search receives the caller's group restriction, so the repository can filter before its limit.
/// </summary>
[TestFixture]
public class ShiftResolverTests
{
    private const string Name = "Frühdienst";
    private const string ShiftIdParameter = "shiftId";
    private static readonly DateOnly Day = new(2026, 6, 15);

    private IShiftRepository _shifts = null!;
    private IShiftVisibilityGuard _guard = null!;
    private IShiftGroupFilterService _groupFilter = null!;
    private HashSet<Guid> _hidden = null!;
    private readonly List<Guid> _restriction = [Guid.NewGuid()];

    [SetUp]
    public void SetUp()
    {
        _shifts = Substitute.For<IShiftRepository>();
        _guard = Substitute.For<IShiftVisibilityGuard>();
        _hidden = [];
        _groupFilter = Substitute.For<IShiftGroupFilterService>();
        _groupFilter.ResolveShiftListGroupIdsAsync(null).Returns(_restriction);
        _guard.FilterVisibleAsync(Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(call => (IReadOnlySet<Guid>)call.Arg<IReadOnlyCollection<Guid>>().Where(id => !_hidden.Contains(id)).ToHashSet());
    }

    [Test]
    public async Task UniqueVisibleName_Resolves()
    {
        var shift = Piece(new DateOnly(2026, 1, 1), null);
        GivenMatches(shift);

        var (resolved, error) = await ResolveByNameAsync();

        resolved.ShouldBeSameAs(shift);
        error.ShouldBeNull();
    }

    [Test]
    public async Task HiddenName_ReadsExactlyLikeAnUnknownName()
    {
        var hiddenShift = Piece(new DateOnly(2026, 1, 1), null);
        _hidden.Add(hiddenShift.Id);
        GivenMatches(hiddenShift);
        var (_, hiddenError) = await ResolveByNameAsync();

        GivenMatches();
        var (_, unknownError) = await ResolveByNameAsync();

        hiddenError.ShouldBe(ShiftResolver.NameNotFoundMessage(Name));
        hiddenError.ShouldBe(unknownError);
    }

    [Test]
    public async Task SameNamedCuts_AreNarrowedToThePieceValidOnTheDate()
    {
        var before = Piece(new DateOnly(2026, 1, 1), new DateOnly(2026, 5, 31));
        var current = Piece(new DateOnly(2026, 6, 1), null);
        GivenMatches(before, current);

        var (resolved, _) = await ResolveByNameAsync();

        resolved.ShouldBeSameAs(current);
    }

    [Test]
    public async Task GenuineDuplicates_AreListed_WithoutTheHiddenOne()
    {
        var first = Piece(new DateOnly(2026, 1, 1), null);
        var second = Piece(new DateOnly(2026, 2, 1), null);
        var hiddenShift = Piece(new DateOnly(2026, 3, 1), null);
        _hidden.Add(hiddenShift.Id);
        GivenMatches(first, second, hiddenShift);

        var (resolved, error) = await ResolveByNameAsync();

        resolved.ShouldBeNull();
        error.ShouldContain(first.Id.ToString());
        error.ShouldContain(second.Id.ToString());
        error.ShouldNotContain(hiddenShift.Id.ToString());
        error.ShouldContain($"'{ShiftIdParameter}'");
    }

    [Test]
    public async Task NameSearch_PassesTheCallersGroupRestrictionToTheRepository()
    {
        GivenMatches(Piece(new DateOnly(2026, 1, 1), null));

        await ResolveByNameAsync();

        await _shifts.Received(1).FindPlannableByNameAsync(
            Name, Arg.Is<IReadOnlyCollection<Guid>?>(ids => ids != null && ids.SequenceEqual(_restriction)),
            Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task HiddenId_ReadsLikeAMissingId()
    {
        var hiddenId = Guid.NewGuid();
        _guard.ExistsAndVisibleAsync(hiddenId, Arg.Any<CancellationToken>()).Returns(false);

        var (resolved, error) = await ShiftResolver.ResolveByIdOrNameAsync(
            _shifts, _guard, _groupFilter, hiddenId.ToString(), null, Day, ShiftIdParameter, CancellationToken.None);

        resolved.ShouldBeNull();
        error.ShouldBe(ShiftResolver.IdNotFoundMessage(hiddenId));
        await _shifts.DidNotReceiveWithAnyArgs().Get(default);
    }

    private Task<(Shift? Shift, string? Error)> ResolveByNameAsync() =>
        ShiftResolver.ResolveByIdOrNameAsync(_shifts, _guard, _groupFilter, null, Name, Day, ShiftIdParameter, CancellationToken.None);

    private void GivenMatches(params Shift[] shifts) =>
        _shifts.FindPlannableByNameAsync(Name, Arg.Any<IReadOnlyCollection<Guid>?>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(shifts.ToList());

    private static Shift Piece(DateOnly from, DateOnly? until) => new()
    {
        Id = Guid.NewGuid(),
        Name = Name,
        Abbreviation = "F",
        FromDate = from,
        UntilDate = until,
    };
}
