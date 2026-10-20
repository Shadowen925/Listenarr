/*
 * Listenarr - Audiobook Management System
 * Copyright (C) 2024-2026 Listenarr Contributors
 *
 * This program is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Affero General Public License as published
 * by the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 *
 * This program is distributed in the hope that it will be useful,
 * but WITHOUT ANY WARRANTY; without even the implied warranty of
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
 * GNU Affero General Public License for more details.
 *
 * You should have received a copy of the GNU Affero General Public License
 * along with this program. If not, see <https://www.gnu.org/licenses/>.
 */
using Microsoft.EntityFrameworkCore;

namespace Listenarr.Infrastructure.Persistence.Repositories
{
    public class EfRemotePathMappingRepository : IRemotePathMappingRepository
    {
        private readonly IDbContextFactory<ListenArrDbContext> _dbFactory;

        public EfRemotePathMappingRepository(
            IDbContextFactory<ListenArrDbContext> dbFactory)
        {
            _dbFactory = dbFactory ?? throw new ArgumentNullException(nameof(dbFactory));
        }

        public async Task<List<RemotePathMapping>> GetAllAsync(CancellationToken ct = default)
        {
            await using var db = await _dbFactory.CreateDbContextAsync(ct);
            return await db.RemotePathMappings.AsNoTracking().ToListAsync(ct);
        }

        public async Task<RemotePathMapping?> GetByIdAsync(int id, CancellationToken ct = default)
        {
            await using var db = await _dbFactory.CreateDbContextAsync(ct);
            return await db.RemotePathMappings
                .AsNoTracking()
                .SingleOrDefaultAsync(mapping => mapping.Id == id, ct);
        }

        public async Task<List<RemotePathMapping>> GetByClientIdAsync(string downloadClientId, CancellationToken ct = default)
        {
            await using var db = await _dbFactory.CreateDbContextAsync(ct);
            return await db.RemotePathMappings
                .AsNoTracking()
                .Where(m => m.DownloadClientId == downloadClientId)
                .OrderByDescending(m => m.RemotePath.Length)
                .ToListAsync(ct);
        }

        public async Task<RemotePathMapping> SaveAsync(RemotePathMapping mapping, CancellationToken ct = default)
        {
            await using var db = await _dbFactory.CreateDbContextAsync(ct);
            var existing = mapping.Id > 0
                ? await db.RemotePathMappings.SingleOrDefaultAsync(
                    candidate => candidate.Id == mapping.Id,
                    ct)
                : null;
            if (existing == null)
            {
                db.RemotePathMappings.Add(mapping);
            }
            else
            {
                db.Entry(existing).CurrentValues.SetValues(mapping);
            }
            await db.SaveChangesAsync(ct);
            return existing ?? mapping;
        }

        public async Task<bool> DeleteAsync(int id, CancellationToken ct = default)
        {
            await using var db = await _dbFactory.CreateDbContextAsync(ct);
            var mapping = await db.RemotePathMappings
                .SingleOrDefaultAsync(candidate => candidate.Id == id, ct);
            if (mapping == null) return false;
            db.RemotePathMappings.Remove(mapping);
            await db.SaveChangesAsync(ct);
            return true;
        }
    }
}
