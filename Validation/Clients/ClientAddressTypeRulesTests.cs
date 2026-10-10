using Klacks.Api.Application.Validation.Clients;
using Klacks.Api.Domain.Enums;
using NUnit.Framework;

namespace Klacks.UnitTest.Validation.Clients;

[TestFixture]
public class ClientAddressTypeRulesTests
{
    [TestCase(EntityTypeEnum.Employee, AddressTypeEnum.Employee, true)]
    [TestCase(EntityTypeEnum.Employee, AddressTypeEnum.Workplace, false)]
    [TestCase(EntityTypeEnum.Employee, AddressTypeEnum.InvoicingAddress, false)]
    [TestCase(EntityTypeEnum.ExternEmp, AddressTypeEnum.Employee, true)]
    [TestCase(EntityTypeEnum.ExternEmp, AddressTypeEnum.Workplace, true)]
    [TestCase(EntityTypeEnum.ExternEmp, AddressTypeEnum.InvoicingAddress, false)]
    [TestCase(EntityTypeEnum.Customer, AddressTypeEnum.Employee, true)]
    [TestCase(EntityTypeEnum.Customer, AddressTypeEnum.Workplace, true)]
    [TestCase(EntityTypeEnum.Customer, AddressTypeEnum.InvoicingAddress, true)]
    public void IsAllowed_FollowsTheEntityTypeTable(EntityTypeEnum clientType, AddressTypeEnum addressType, bool expected)
    {
        Assert.That(ClientAddressTypeRules.IsAllowed(clientType, addressType), Is.EqualTo(expected));
    }

    [Test]
    public void AllowedTypes_ListsExactlyTheTypesPerEntityType()
    {
        Assert.That(ClientAddressTypeRules.AllowedTypes(EntityTypeEnum.Employee), Is.EqualTo(new[] { AddressTypeEnum.Employee }));
        Assert.That(ClientAddressTypeRules.AllowedTypes(EntityTypeEnum.ExternEmp),
            Is.EqualTo(new[] { AddressTypeEnum.Employee, AddressTypeEnum.Workplace }));
        Assert.That(ClientAddressTypeRules.AllowedTypes(EntityTypeEnum.Customer), Has.Count.EqualTo(3));
    }
}
