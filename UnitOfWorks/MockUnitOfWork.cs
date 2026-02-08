using Klacks.Api.Infrastructure.Persistence;
using Klacks.Api.Application.Interfaces;
using Microsoft.Extensions.Logging;

namespace Klacks.UnitTest.Mocks
{
    public class MockUnitOfWork : IUnitOfWork
    {
        private readonly DataBaseContext _context;
        private readonly ILogger<MockUnitOfWork> _logger;

        public MockUnitOfWork(DataBaseContext context, ILogger<MockUnitOfWork> logger)
        {
            _context = context;
            _logger = logger;
        }

        public async Task CompleteAsync()
        {
            await _context.SaveChangesAsync();
        }

        public int Complete()
        {
            return _context.SaveChanges();
        }

        public Task<ITransaction> BeginTransactionAsync()
        {
            _logger.LogInformation("Mock-Transaktion gestartet (keine echte Transaktion)");
            return Task.FromResult<ITransaction>(new MockTransaction());
        }

        public Task CommitTransactionAsync(ITransaction transaction)
        {
            _logger.LogInformation("Mock-Transaktion committed (keine echte Transaktion)");
            return Task.CompletedTask;
        }

        public Task RollbackTransactionAsync(ITransaction transaction)
        {
            _logger.LogInformation("Mock-Transaktion zurückgerollt (keine echte Transaktion)");
            return Task.CompletedTask;
        }

        private class MockTransaction : ITransaction
        {
            public void Dispose()
            {
            }

            public ValueTask DisposeAsync()
            {
                return ValueTask.CompletedTask;
            }
        }
    }
}
