using Klacks.Api.Datas;
using Klacks.Api.Interfaces;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Logging;

namespace UnitTest.Mocks
{
    /// <summary>
    /// Mock-Implementierung von IUnitOfWork für Tests, die keine echten Transaktionen benötigt.
    /// </summary>
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

        // Mock-Implementierung ohne echte Transaktionen
        public Task<IDbContextTransaction> BeginTransactionAsync()
        {
            _logger.LogInformation("Mock-Transaktion gestartet (keine echte Transaktion)");
            return Task.FromResult<IDbContextTransaction>(new MockTransaction());
        }

        public Task CommitTransactionAsync(IDbContextTransaction transaction)
        {
            _logger.LogInformation("Mock-Transaktion committed (keine echte Transaktion)");
            return Task.CompletedTask;
        }

        public Task RollbackTransactionAsync(IDbContextTransaction transaction)
        {
            _logger.LogInformation("Mock-Transaktion zurückgerollt (keine echte Transaktion)");
            return Task.CompletedTask;
        }

        /// <summary>
        /// Eine einfache Mock-Implementierung von IDbContextTransaction für Tests.
        /// </summary>
        private class MockTransaction : IDbContextTransaction
        {
            public Guid TransactionId => Guid.NewGuid();

            public void Commit()
            {
                // Nichts zu tun
            }

            public Task CommitAsync(CancellationToken cancellationToken = default)
            {
                return Task.CompletedTask;
            }

            public void Dispose()
            {
                // Nichts zu tun
            }

            public ValueTask DisposeAsync()
            {
                return ValueTask.CompletedTask;
            }

            public void Rollback()
            {
                // Nichts zu tun
            }

            public Task RollbackAsync(CancellationToken cancellationToken = default)
            {
                return Task.CompletedTask;
            }
        }
    }
}