using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace Modern;

public sealed class CourseService : ICourseService
{
    public Task<List<string>> GetPagedForInstructorAsync(int page, int pageSize)
        => Task.FromResult(new List<string>());

    public Task<string?> GetByIdForInstructorAsync(Guid id)
        => Task.FromResult<string?>(null);
}
