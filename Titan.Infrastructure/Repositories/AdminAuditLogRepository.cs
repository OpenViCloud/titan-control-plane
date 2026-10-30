using Microsoft.EntityFrameworkCore;
using Titan.Domain.Aggregates.AdminAuditLog;
using Titan.Domain.Repositories;
using Titan.Infrastructure.Persistence;

namespace Titan.Infrastructure.Repositories;

public class AdminAuditLogRepository(TitanDbContext db) : IAdminAuditLogRepository
{
    public void Add(AdminAuditLog entry) => db.AdminAuditLogs.Add(entry);

    public Task SaveAsync(CancellationToken ct = default) => db.SaveChangesAsync(ct);

    public Task<List<AdminAuditLog>> ListAsync(int limit, CancellationToken ct = default) =>
        db.AdminAuditLogs
            .OrderByDescending(a => a.CreatedAt)
            .Take(limit)
            .AsNoTracking()
            .ToListAsync(ct);
}
