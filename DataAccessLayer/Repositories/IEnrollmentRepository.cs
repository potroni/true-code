using Common.Database.Repositories.Generic;
using DataAccessLayer.Entities;

namespace DataAccessLayer.Repositories;

public interface IEnrollmentRepository : IGenericRepository<Enrollment>
{
    Task<List<Enrollment>> GetByUserAsync(long userId, CancellationToken ct = default);
    Task<EnrollmentDetails?> GetDetailsAsync(long enrollmentId, long userId, CancellationToken ct = default);
    Task<long> SumActiveSeatsAsync(long classId, CancellationToken ct = default);
}
