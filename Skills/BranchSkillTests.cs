// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Unit tests for create_branch and update_branch. Covers the verified success path, the
/// self-verification rollback path (a mismatch on re-read must surface as an error, never a false
/// success), name resolution for update_branch (exact, single partial, ambiguous, unknown) and the
/// validation guards (duplicate name, nothing to change).
/// </summary>

using Klacks.Api.Application.Skills;

namespace Klacks.UnitTest.Skills;

[TestFixture]
public class BranchSkillTests
{
    private IBranchRepository _branchRepository = null!;
    private IUnitOfWork _unitOfWork = null!;

    [SetUp]
    public void Setup()
    {
        _branchRepository = Substitute.For<IBranchRepository>();
        _unitOfWork = Substitute.For<IUnitOfWork>();

        _unitOfWork.ExecuteInTransactionAsync(Arg.Any<Func<Task<Guid>>>())
            .Returns(ci => ci.Arg<Func<Task<Guid>>>()());
    }

    private static SkillExecutionContext Ctx() => new()
    {
        UserId = Guid.NewGuid(),
        TenantId = Guid.NewGuid(),
        UserName = "tester",
        UserPermissions = new List<string> { "CanEditSettings" }
    };

    private static Branch Copy(Branch source) => new()
    {
        Id = source.Id,
        Name = source.Name,
        Address = source.Address,
        Phone = source.Phone,
        Email = source.Email
    };

    private static Branch NewBranch(string name) => new()
    {
        Id = Guid.NewGuid(),
        Name = name,
        Address = "Bahnhofstrasse 1, 8000 Zürich",
        Phone = "044 000 00 00",
        Email = "info@example.com"
    };

    [Test]
    public async Task Create_Succeeds_AndReportsVerified()
    {
        Branch? added = null;
        await _branchRepository.Add(Arg.Do<Branch>(b =>
        {
            b.Id = Guid.NewGuid();
            added = b;
        }));
        _branchRepository.GetNoTracking(Arg.Any<Guid>()).Returns(_ => Copy(added!));

        var result = await new CreateBranchSkill(_branchRepository, _unitOfWork).ExecuteAsync(Ctx(), new Dictionary<string, object>
        {
            ["name"] = " Filiale Winterthur ",
            ["address"] = "Bahnhofstrasse 12, 8400 Winterthur",
            ["email"] = "winterthur@example.com"
        });

        Assert.That(result.Success, Is.True, result.Message);
        Assert.That(result.Message, Does.Contain("verified"));
        Assert.That(added!.Name, Is.EqualTo("Filiale Winterthur"));
        Assert.That(added.Phone, Is.EqualTo(string.Empty));
        Assert.That(added.Email, Is.EqualTo("winterthur@example.com"));
        await _unitOfWork.Received(1).CompleteAsync();
    }

    [Test]
    public async Task Create_ReturnsError_WhenVerificationFails()
    {
        await _branchRepository.Add(Arg.Do<Branch>(b => b.Id = Guid.NewGuid()));
        _branchRepository.GetNoTracking(Arg.Any<Guid>()).Returns((Branch?)null);

        var result = await new CreateBranchSkill(_branchRepository, _unitOfWork).ExecuteAsync(Ctx(), new Dictionary<string, object>
        {
            ["name"] = "Filiale Winterthur",
            ["address"] = "Bahnhofstrasse 12, 8400 Winterthur"
        });

        Assert.That(result.Success, Is.False);
        Assert.That(result.Message, Does.Contain("rolled back"));
    }

    [Test]
    public async Task Create_ReturnsError_WhenNameExists()
    {
        _branchRepository.ExistsByNameAsync("Filiale Winterthur").Returns(true);

        var result = await new CreateBranchSkill(_branchRepository, _unitOfWork).ExecuteAsync(Ctx(), new Dictionary<string, object>
        {
            ["name"] = "Filiale Winterthur",
            ["address"] = "Bahnhofstrasse 12, 8400 Winterthur"
        });

        Assert.That(result.Success, Is.False);
        Assert.That(result.Message, Does.Contain("already exists"));
        await _branchRepository.DidNotReceive().Add(Arg.Any<Branch>());
    }

    [Test]
    public async Task Update_ResolvesByPartialName_AndChangesOnlyProvidedValues()
    {
        var basel = NewBranch("Geschäftsstelle Basel");
        var bern = NewBranch("Geschäftsstelle Bern");
        _branchRepository.List().Returns(new List<Branch> { basel, bern });
        _branchRepository.Get(basel.Id).Returns(basel);
        _branchRepository.GetNoTracking(basel.Id).Returns(_ => Copy(basel));

        var result = await new UpdateBranchSkill(_branchRepository, _unitOfWork).ExecuteAsync(Ctx(), new Dictionary<string, object>
        {
            ["branchName"] = "basel",
            ["phone"] = "061 111 11 11"
        });

        Assert.That(result.Success, Is.True, result.Message);
        Assert.That(result.Message, Does.Contain("verified"));
        Assert.That(basel.Phone, Is.EqualTo("061 111 11 11"));
        Assert.That(basel.Address, Is.EqualTo("Bahnhofstrasse 1, 8000 Zürich"));
        await _branchRepository.Received(1).Put(basel);
    }

    [Test]
    public async Task Update_ReturnsCandidates_WhenNameIsAmbiguous()
    {
        _branchRepository.List().Returns(new List<Branch> { NewBranch("Geschäftsstelle Basel"), NewBranch("Geschäftsstelle Bern") });

        var result = await new UpdateBranchSkill(_branchRepository, _unitOfWork).ExecuteAsync(Ctx(), new Dictionary<string, object>
        {
            ["branchName"] = "Geschäftsstelle",
            ["phone"] = "061 111 11 11"
        });

        Assert.That(result.Success, Is.False);
        Assert.That(result.Message, Does.Contain("Geschäftsstelle Basel").And.Contain("Geschäftsstelle Bern"));
        await _branchRepository.DidNotReceive().Put(Arg.Any<Branch>());
    }

    [Test]
    public async Task Update_ListsAvailableBranches_WhenNameUnknown()
    {
        _branchRepository.List().Returns(new List<Branch> { NewBranch("Geschäftsstelle Basel") });

        var result = await new UpdateBranchSkill(_branchRepository, _unitOfWork).ExecuteAsync(Ctx(), new Dictionary<string, object>
        {
            ["branchName"] = "Chur",
            ["phone"] = "081 000 00 00"
        });

        Assert.That(result.Success, Is.False);
        Assert.That(result.Message, Does.Contain("Available branches").And.Contain("Geschäftsstelle Basel"));
    }

    [Test]
    public async Task Update_ReturnsError_WhenNothingToChange()
    {
        var basel = NewBranch("Geschäftsstelle Basel");
        _branchRepository.List().Returns(new List<Branch> { basel });

        var result = await new UpdateBranchSkill(_branchRepository, _unitOfWork).ExecuteAsync(Ctx(), new Dictionary<string, object>
        {
            ["branchId"] = basel.Id.ToString()
        });

        Assert.That(result.Success, Is.False);
        Assert.That(result.Message, Does.Contain("Nothing to change"));
    }

    [Test]
    public async Task Update_ReturnsError_WhenNewNameTaken()
    {
        var basel = NewBranch("Geschäftsstelle Basel");
        _branchRepository.List().Returns(new List<Branch> { basel });
        _branchRepository.ExistsByNameAsync("Geschäftsstelle Bern", basel.Id).Returns(true);

        var result = await new UpdateBranchSkill(_branchRepository, _unitOfWork).ExecuteAsync(Ctx(), new Dictionary<string, object>
        {
            ["branchId"] = basel.Id.ToString(),
            ["name"] = "Geschäftsstelle Bern"
        });

        Assert.That(result.Success, Is.False);
        Assert.That(result.Message, Does.Contain("already exists"));
    }

    [Test]
    public async Task Update_ReturnsError_WhenVerificationFails()
    {
        var basel = NewBranch("Geschäftsstelle Basel");
        _branchRepository.List().Returns(new List<Branch> { basel });
        _branchRepository.Get(basel.Id).Returns(basel);
        _branchRepository.GetNoTracking(basel.Id).Returns(_ => new Branch { Id = basel.Id, Name = "Geschäftsstelle Basel", Address = basel.Address, Phone = "stale", Email = basel.Email });

        var result = await new UpdateBranchSkill(_branchRepository, _unitOfWork).ExecuteAsync(Ctx(), new Dictionary<string, object>
        {
            ["branchId"] = basel.Id.ToString(),
            ["phone"] = "061 111 11 11"
        });

        Assert.That(result.Success, Is.False);
        Assert.That(result.Message, Does.Contain("rolled back"));
    }
}
