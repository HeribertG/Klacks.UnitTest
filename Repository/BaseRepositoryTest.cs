using System;
using Klacks.Api.Infrastructure.Persistence;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using NSubstitute;
using NUnit.Framework;

namespace UnitTest.Repository
{
    public abstract class BaseRepositoryTest
    {
        protected DataBaseContext TestDbContext;

        [SetUp]
        public virtual void BaseSetUp()
        {
            var options = new DbContextOptionsBuilder<DataBaseContext>()
                .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
                .Options;

            var mockHttpContextAccessor = Substitute.For<IHttpContextAccessor>();
            TestDbContext = new DataBaseContext(options, mockHttpContextAccessor);
            TestDbContext.Database.EnsureCreated();
        }

        [TearDown]
        public virtual void BaseTearDown()
        {
            TestDbContext?.Database.EnsureDeleted();
            TestDbContext?.Dispose();
        }
    }
}