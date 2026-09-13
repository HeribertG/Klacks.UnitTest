// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Pins the order the sharpener decides pending description proposals in. Both sources write into the
/// same table, but only one of them cost a person something: a correction is a user telling the assistant
/// it picked the wrong skill, a goldset-born proposal is the loop talking to itself. The goldset branch
/// produces its proposals in bulk and therefore with the newer timestamps, so a plain newest-first window
/// of three slots would push every correction out of reach run after run.
/// </summary>
namespace Klacks.UnitTest.Infrastructure.Repositories.Assistant;

using Klacks.Api.Domain.Constants;
using Klacks.Api.Domain.Models.Assistant;
using Klacks.Api.Infrastructure.Persistence;
using Klacks.Api.Infrastructure.Repositories.Assistant;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using NSubstitute;
using NUnit.Framework;
using Shouldly;

[TestFixture]
public class ProposedSkillChangePendingOrderTests
{
    private const int Limit = SkillLearningDefaults.MaxProposalsPerRun;

    private static readonly DateTime Older = new(2026, 9, 13, 3, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime Newer = new(2026, 9, 13, 4, 0, 0, DateTimeKind.Utc);

    private DbContextOptions<DataBaseContext> _options = null!;
    private IHttpContextAccessor _httpAccessor = null!;

    [SetUp]
    public void SetUp()
    {
        _options = new DbContextOptionsBuilder<DataBaseContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        _httpAccessor = Substitute.For<IHttpContextAccessor>();
    }

    private DataBaseContext CreateContext() => new(_options, _httpAccessor);

    private ProposedSkillChangeRepository NewRepository() => new(CreateContext());

    [Test]
    public async Task ACorrectionBornProposal_IsListedBeforeANewerGoldsetBornOne()
    {
        await InsertAsync(ProposedChangeOrigins.Correction, Older);
        await InsertAsync(ProposedChangeOrigins.GoldsetEval, Newer);

        var pending = await NewRepository().GetPendingAsync(ProposedChangeFields.Description, Limit);

        pending[0].Origin.ShouldBe(ProposedChangeOrigins.Correction);
        pending[1].Origin.ShouldBe(ProposedChangeOrigins.GoldsetEval);
    }

    [Test]
    public async Task AmongProposalsOfTheSameOrigin_TheNewestStillComesFirst()
    {
        await InsertAsync(ProposedChangeOrigins.Correction, Older);
        var newest = await InsertAsync(ProposedChangeOrigins.Correction, Newer);

        var pending = await NewRepository().GetPendingAsync(ProposedChangeFields.Description, Limit);

        pending[0].Id.ShouldBe(newest);
    }

    [Test]
    public async Task ADecidedProposal_IsNoLongerPending()
    {
        await InsertAsync(ProposedChangeOrigins.Correction, Older, ProposedChangeStatuses.AppliedAuto);

        (await NewRepository().GetPendingAsync(ProposedChangeFields.Description, Limit)).ShouldBeEmpty();
    }

    // OnBeforeSaving stamps CreateTime on every insert, so the timestamp is set in a second, tracked pass
    // where only UpdateTime is touched. Without that both rows would share one tick and the order would be
    // whatever the provider happened to return.
    private async Task<Guid> InsertAsync(
        string origin, DateTime createTime, string status = ProposedChangeStatuses.Pending)
    {
        var record = new ProposedSkillChange
        {
            Id = Guid.NewGuid(),
            AgentId = Guid.NewGuid(),
            SkillId = Guid.NewGuid(),
            SkillName = "list_clients",
            Field = ProposedChangeFields.Description,
            Origin = origin,
            ValueBefore = "Lists everything about clients.",
            ValueAfter = "Lists the contract data of one client.",
            Status = status
        };

        await NewRepository().AddAsync(record);

        await using var context = CreateContext();
        var stored = await context.ProposedSkillChanges.SingleAsync(p => p.Id == record.Id);
        stored.CreateTime = createTime;
        await context.SaveChangesAsync();

        return record.Id;
    }
}
