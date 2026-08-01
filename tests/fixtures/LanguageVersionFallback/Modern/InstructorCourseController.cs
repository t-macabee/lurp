using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Modern;

namespace Modern.Api;

public sealed class InstructorCourseController
{
    private readonly ICourseService _service;

    public InstructorCourseController(ICourseService service)
    {
        _service = service;
    }

    public async Task<List<string>> GetMyCourses(int page, int pageSize)
        => await _service.GetPagedForInstructorAsync(page, pageSize);

    public async Task<string?> GetById(Guid id)
        => await _service.GetByIdForInstructorAsync(id);
}
