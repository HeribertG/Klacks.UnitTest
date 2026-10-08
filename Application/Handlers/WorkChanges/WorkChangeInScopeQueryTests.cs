// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// update_workchange / delete_workchange used to read through GetQuery&lt;WorkChangeResource&gt;, which only sees the
/// main plan, so scenario changes (e.g. cover_absence replacements) were unreachable for Klacksy.
/// GetWorkChangeInScopeQuery reads in exactly one scope; another scope, a hidden owner or replacement client and a
/// missing change are answered alike.
/// </summary>

using Klacks.Api.Application.Handlers.WorkChanges;
using Klacks.Api.Application.Mappers;
using Klacks.Api.Application.Queries.Schedules;
using Microsoft.Extensions.Logging;

namespace Klacks.UnitTest.Application.Handlers.WorkChanges;

[TestFixture]
public class WorkChangeInScopeQueryTests
{
    private IWorkChangeRepository _repository = null!;
    private IClientVisibilityGuard _visibilityGuard = null!;
    private Guid _hiddenClientId;
    private GetInScopeQueryHandler _handler = null!;

    [SetUp]
    public void SetUp()
    {
        _repository = Substitute.For<IWorkChangeRepository>();
        _visibilityGuard = Substitute.For<IClientVisibilityGuard>();
        _hiddenClientId = Guid.NewGuid();
        _visibilityGuard.AreAllVisibleAsync(Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(ci => !ci.ArgAt<IReadOnlyCollection<Guid>>(0).Contains(_hiddenClientId));
        _handler = new GetInScopeQueryHandler(
            _repository, _visibilityGuard, new ScheduleMapper(), Substitute.For<ILogger<GetInScopeQueryHandler>>());
    }

    [Test]
    public async Task ScenarioChange_InItsScenario_IsReturned()
    {
        var token = Guid.NewGuid();
        var change = Given(Guid.NewGuid(), null, token);

        var result = await _handler.Handle(new GetWorkChangeInScopeQuery(change.Id, token), CancellationToken.None);

        result.Id.ShouldBe(change.Id);
    }

    [Test]
    public async Task LegacyChangeWithoutOwnToken_OnAScenarioWork_BelongsToThatScenario()
    {
        var token = Guid.NewGuid();
        var change = Given(Guid.NewGuid(), null, token);
        change.AnalyseToken = null;

        var result = await _handler.Handle(new GetWorkChangeInScopeQuery(change.Id, token), CancellationToken.None);
        await Should.ThrowAsync<KeyNotFoundException>(
            () => _handler.Handle(new GetWorkChangeInScopeQuery(change.Id, null), CancellationToken.None));

        result.Id.ShouldBe(change.Id);
    }

    [Test]
    public async Task OtherScope_HiddenOwner_HiddenReplacement_AndMissing_AreAnsweredAlike()
    {
        var scenarioChange = Given(Guid.NewGuid(), null, Guid.NewGuid());
        var hiddenOwner = Given(_hiddenClientId, null, null);
        var hiddenReplacement = Given(Guid.NewGuid(), _hiddenClientId, null);
        var missingId = Guid.NewGuid();

        var messages = new List<string>();
        foreach (var id in new[] { scenarioChange.Id, hiddenOwner.Id, hiddenReplacement.Id, missingId })
        {
            var ex = await Should.ThrowAsync<KeyNotFoundException>(
                () => _handler.Handle(new GetWorkChangeInScopeQuery(id, null), CancellationToken.None));
            messages.Add(ex.Message.Replace(id.ToString(), "<id>"));
        }

        messages.Distinct().ShouldHaveSingleItem().ShouldBe("WorkChange with ID <id> not found");
    }

    private WorkChange Given(Guid ownerId, Guid? replaceClientId, Guid? analyseToken)
    {
        var work = new Work { Id = Guid.NewGuid(), ClientId = ownerId, AnalyseToken = analyseToken };
        var change = new WorkChange
        {
            Id = Guid.NewGuid(),
            WorkId = work.Id,
            Work = work,
            ReplaceClientId = replaceClientId,
            AnalyseToken = analyseToken
        };
        _repository.GetWithWorkInAnyScope(change.Id).Returns(change);
        return change;
    }
}
