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

// A second source-level implementation keeps the fixture's interface-dispatch
// candidates genuinely plural. It must not make a call through ICourseService
// look like a direct call to either implementation.
public sealed class AlternateCourseService : ICourseService
{
    public Task<List<string>> GetPagedForInstructorAsync(int page, int pageSize)
        => Task.FromResult(new List<string>());

    public Task<string?> GetByIdForInstructorAsync(Guid id)
        => Task.FromResult<string?>(null);
}

// Unlike InstructorCourseController, this caller deliberately receives the
// concrete type. Its call is the control case for a direct compiler-proved
// caller/callee relationship in the same indexed source fixture.
public sealed class DirectCourseServiceCaller
{
    private readonly CourseService _service;

    public DirectCourseServiceCaller(CourseService service)
    {
        _service = service;
    }

    public Task<string?> GetByIdDirectly(Guid id)
        => _service.GetByIdForInstructorAsync(id);
}
