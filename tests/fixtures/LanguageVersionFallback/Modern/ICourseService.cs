using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace Modern;

public interface ICourseService
{
    Task<List<string>> GetPagedForInstructorAsync(int page, int pageSize);

    Task<string?> GetByIdForInstructorAsync(Guid id);
}
