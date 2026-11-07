// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

using Klacks.Api.Domain.Enums;
using Klacks.Api.Domain.Interfaces.Associations;
using Klacks.Api.Domain.Models.Staffs;
using Klacks.Api.Infrastructure.Plugins;
using NSubstitute;
using NUnit.Framework;
using Shouldly;

namespace Klacks.UnitTest.Infrastructure.Plugins;

[TestFixture]
public class ClientGroupReaderBridgeTests
{
    [Test]
    public async Task GroupAudience_LeavesStandInsAndDeletedClientsOut()
    {
        var groupId = Guid.NewGuid();
        var employee = new Client { Id = Guid.NewGuid(), Type = EntityTypeEnum.Employee };
        var externEmployee = new Client { Id = Guid.NewGuid(), Type = EntityTypeEnum.ExternEmp };
        var customer = new Client { Id = Guid.NewGuid(), Type = EntityTypeEnum.Customer };
        var standIn = new Client { Id = Guid.NewGuid(), Type = EntityTypeEnum.StandIn };
        var deleted = new Client { Id = Guid.NewGuid(), Type = EntityTypeEnum.Employee, IsDeleted = true };
        var membership = Substitute.For<IGroupMembershipService>();
        membership.GetGroupHierarchyMembersAsync(groupId).Returns([employee, externEmployee, customer, standIn, deleted, employee]);

        var ids = await new ClientGroupReaderBridge(membership).GetClientIdsInGroupAsync(groupId);

        ids.ShouldBe([employee.Id, externEmployee.Id, customer.Id], ignoreOrder: true);
    }
}
