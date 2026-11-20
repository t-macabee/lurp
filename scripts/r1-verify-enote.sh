#!/usr/bin/env bash
# R1: Eleven-cycle incremental convergence on eNoteV2 (7 projects).
# Usage: ./scripts/r1-verify-enote.sh <solution-dir> <solution-file>
# Example (Windows): ./scripts/r1-verify-enote.sh /c/Users/Tarik/Desktop/eNoteV2/eNote eNote.sln
# Example (Linux):   ./scripts/r1-verify-enote.sh /home/user/eNoteV2/eNote eNote.sln
set -euo pipefail

SOLUTION_DIR="${1:?Usage: $0 <solution-dir> <solution-file>}"
SOLUTION_FILE="${2:?}"

native_path() {
    if command -v cygpath >/dev/null 2>&1; then
        cygpath -w "$1"
    else
        printf '%s\n' "$1"
    fi
}

REPO_ROOT="$(cd "$(dirname "$0")/.." && pwd)"

TMPROOT="$(mktemp -d)"
trap 'rm -rf "$TMPROOT"' EXIT
SCRATCH="$TMPROOT/eNote"
echo "=== R1-eNote: copying solution to scratchpad: $SCRATCH ==="
cp -r "$SOLUTION_DIR" "$SCRATCH"
SOLUTION_PATH="$SCRATCH/$SOLUTION_FILE"
INCR_DIR="$TMPROOT/incr-db"
mkdir -p "$INCR_DIR"

NATIVE_INCR_DIR="$(native_path "$INCR_DIR")"
NATIVE_SOLUTION_PATH="$(native_path "$SOLUTION_PATH")"

echo "=== R1-eNote: restoring scratchpad solution ==="
dotnet restore "$NATIVE_SOLUTION_PATH"

LURP_PROJ="$REPO_ROOT/src/Lurp.csproj"
# Set LURP_CMD to run the packed tool instead of the local build, for example:
#   LURP_CMD='/c/tools/lurp/lurp.exe' ./scripts/r1-verify-enote.sh <dir> <sln>   # Windows
#   LURP_CMD='/usr/local/bin/lurp' ./scripts/r1-verify-enote.sh <dir> <sln>       # Linux
LURP_CMD="${LURP_CMD:-dotnet run --no-build --project $LURP_PROJ --}"
# Set R1_COMPARE_CMD to run a pre-built r1-compare instead of `dotnet run`, for example:
#   R1_COMPARE_CMD='C:/tools/r1-compare.exe' ./scripts/r1-verify-enote.sh <dir> <sln>   # Windows
#   R1_COMPARE_CMD='/usr/local/bin/r1-compare' ./scripts/r1-verify-enote.sh <dir> <sln> # Linux
R1_COMPARE_CMD="${R1_COMPARE_CMD:-dotnet run --no-build --project $REPO_ROOT/scripts/r1-compare/r1-compare.csproj --}"

check_restored_log() {
    local log="$1"
    # Both warning sites emit "have not been restored":
    # src/Workspace/IndexRunner.cs:74 and src/Workspace/IncrementalIndexer.cs:181.
    if grep -q "have not been restored" "$log"; then
        echo "R1 FAIL: $log reports unrestored projects" >&2
        exit 1
    fi
}

run_full() {
    local outdir_native="$1"
    local log
    log="$(mktemp "$TMPROOT/full-XXXXXX.log")"
    echo "  [full] -> $outdir_native"
    # shellcheck disable=SC2086 # LURP_CMD is a command plus arguments.
    $LURP_CMD \
        --mode=index --solution="$NATIVE_SOLUTION_PATH" --output-dir="$outdir_native" \
        --strategy=full 2>&1 | tee "$log" | tail -5
    check_restored_log "$log"
}

run_incr() {
    local log
    log="$(mktemp "$TMPROOT/incr-XXXXXX.log")"
    echo "  [incr] -> $NATIVE_INCR_DIR"
    # shellcheck disable=SC2086 # LURP_CMD is a command plus arguments.
    $LURP_CMD \
        --mode=index --solution="$NATIVE_SOLUTION_PATH" --output-dir="$NATIVE_INCR_DIR" \
        2>&1 | tee "$log" | tail -5
    check_restored_log "$log"
}

compare_cycle() {
    local cycle="$1"
    local title="$2"
    local full_dir="$TMPROOT/full-$cycle"
    local full_native
    mkdir -p "$full_dir"
    full_native="$(native_path "$full_dir")"
    echo "=== CYCLE $cycle compare ($title) ==="
    run_full "$full_native"
    local rc=0
    # shellcheck disable=SC2086 # R1_COMPARE_CMD is a command plus arguments.
    $R1_COMPARE_CMD "$NATIVE_INCR_DIR/index.db" "$full_native/index.db" || rc=$?
    rm -rf "$full_dir"
    if [[ "$rc" -ne 0 ]]; then
        echo "R1 FAIL after cycle $cycle ($title)" >&2
        exit 1
    fi
}

edit_file() { cat > "$SCRATCH/$1"; }
rename_file() { mv "$SCRATCH/$1" "$SCRATCH/$2"; }

IFACE="eNote.Application/Features/Rentals/ReferenceData/IReferenceCrudService.cs"
BASE="eNote.Application/Features/Rentals/ReferenceData/ReferenceCrudService.cs"
SVC="eNote.Application/Features/Rentals/ReferenceData/InstrumentTypes/InstrumentTypeService.cs"
REQ="eNote.Application/Features/Rentals/ReferenceData/InstrumentTypes/InstrumentTypeRequest.cs"

# ==================== STEP 0 ====================
echo ""
echo "=== STEP 0: Full index -> snapshot A ==="
run_full "$NATIVE_INCR_DIR"

# ==================== EDIT 1: comment ====================
echo ""
echo "=== EDIT 1: Add doc comment ==="
edit_file "$IFACE" <<'CSHARP'
using eNote.Application.Common.Search;

namespace eNote.Application.Features.Rentals.ReferenceData;

public interface IReferenceCrudService<TDto, TRequest, TSearch> where TSearch : BaseSearchObject
{
    Task<PagedResult<TDto>> GetPagedAsync(TSearch search, CancellationToken cancellationToken = default);
    /// <summary>Retrieves a single entity by its unique identifier.</summary>
    Task<TDto> GetByIdAsync(int id, CancellationToken cancellationToken = default);
    Task<TDto> CreateAsync(TRequest request, CancellationToken cancellationToken = default);
    Task<TDto> UpdateAsync(int id, TRequest request, CancellationToken cancellationToken = default);
    Task DeleteAsync(int id, CancellationToken cancellationToken = default);
}
CSHARP
run_incr
compare_cycle 1 "Add doc comment"

# ==================== EDIT 2: signature change ====================
echo ""
echo "=== EDIT 2: Add includeDeleted param to GetPagedAsync ==="
edit_file "$IFACE" <<'CSHARP'
using eNote.Application.Common.Search;

namespace eNote.Application.Features.Rentals.ReferenceData;

public interface IReferenceCrudService<TDto, TRequest, TSearch> where TSearch : BaseSearchObject
{
    Task<PagedResult<TDto>> GetPagedAsync(TSearch search, CancellationToken cancellationToken = default, bool includeDeleted = false);
    /// <summary>Retrieves a single entity by its unique identifier.</summary>
    Task<TDto> GetByIdAsync(int id, CancellationToken cancellationToken = default);
    Task<TDto> CreateAsync(TRequest request, CancellationToken cancellationToken = default);
    Task<TDto> UpdateAsync(int id, TRequest request, CancellationToken cancellationToken = default);
    Task DeleteAsync(int id, CancellationToken cancellationToken = default);
}
CSHARP

edit_file "$BASE" <<'CSHARP'
using eNote.Application.Common.Search;

namespace eNote.Application.Features.Rentals.ReferenceData;

public abstract class ReferenceCrudService<TEntity, TDto, TRequest, TSearch>(IAppDbContext context)
    : IReferenceCrudService<TDto, TRequest, TSearch>
    where TEntity : BaseEntity
    where TSearch : BaseSearchObject
{
    protected IAppDbContext Db => context;

    protected abstract string NotFoundMessage { get; }
    protected abstract TDto Map(TEntity entity);
    protected abstract TEntity CreateEntity(TRequest request);
    protected abstract void ApplyUpdate(TEntity entity, TRequest request);
    protected abstract IOrderedQueryable<TEntity> Order(IQueryable<TEntity> query);
    protected abstract IQueryable<TEntity> ApplySearch(IQueryable<TEntity> query, TSearch search);
    protected virtual Task EnsureDeletableAsync(TEntity entity, CancellationToken ct = default) =>
        Task.CompletedTask;

    public Task<PagedResult<TDto>> GetPagedAsync(TSearch search, CancellationToken cancellationToken = default, bool includeDeleted = false) =>
        Order(ApplySearch(Db.Set<TEntity>().AsNoTracking(), search)).ToPagedResultAsync(search, Map, ct: cancellationToken);

    public async Task<TDto> GetByIdAsync(int id, CancellationToken cancellationToken = default)
    {
        var entity = await Db.Set<TEntity>()
            .AsNoTracking()
            .FirstOrDefaultAsync(x => x.Id == id, cancellationToken)
            ?? throw new NotFoundException(NotFoundMessage);

        return Map(entity);
    }

    public async Task<TDto> CreateAsync(TRequest request, CancellationToken cancellationToken = default)
    {
        var entity = CreateEntity(request);
        Db.Set<TEntity>().Add(entity);
        await Db.SaveChangesAsync(cancellationToken);
        return Map(entity);
    }

    public async Task<TDto> UpdateAsync(int id, TRequest request, CancellationToken cancellationToken = default)
    {
        var entity = await Db.Set<TEntity>()
            .FirstOrDefaultAsync(x => x.Id == id, cancellationToken)
            ?? throw new NotFoundException(NotFoundMessage);

        ApplyUpdate(entity, request);
        await Db.SaveChangesAsync(cancellationToken);
        return Map(entity);
    }

    public async Task DeleteAsync(int id, CancellationToken cancellationToken = default)
    {
        var entity = await Db.Set<TEntity>()
            .FirstOrDefaultAsync(x => x.Id == id, cancellationToken)
            ?? throw new NotFoundException(NotFoundMessage);

        await EnsureDeletableAsync(entity, cancellationToken);
        Db.Set<TEntity>().Remove(entity);
        await Db.SaveChangesAsync(cancellationToken);
    }
}
CSHARP
run_incr
compare_cycle 2 "Add includeDeleted param to GetPagedAsync"

# ==================== EDIT 3: add ExistsAsync method ====================
echo ""
echo "=== EDIT 3: Add ExistsAsync to interface + base ==="
edit_file "$IFACE" <<'CSHARP'
using eNote.Application.Common.Search;

namespace eNote.Application.Features.Rentals.ReferenceData;

public interface IReferenceCrudService<TDto, TRequest, TSearch> where TSearch : BaseSearchObject
{
    Task<PagedResult<TDto>> GetPagedAsync(TSearch search, CancellationToken cancellationToken = default, bool includeDeleted = false);
    /// <summary>Retrieves a single entity by its unique identifier.</summary>
    Task<TDto> GetByIdAsync(int id, CancellationToken cancellationToken = default);
    Task<bool> ExistsAsync(int id, CancellationToken cancellationToken = default);
    Task<TDto> CreateAsync(TRequest request, CancellationToken cancellationToken = default);
    Task<TDto> UpdateAsync(int id, TRequest request, CancellationToken cancellationToken = default);
    Task DeleteAsync(int id, CancellationToken cancellationToken = default);
}
CSHARP

edit_file "$BASE" <<'CSHARP'
using eNote.Application.Common.Search;

namespace eNote.Application.Features.Rentals.ReferenceData;

public abstract class ReferenceCrudService<TEntity, TDto, TRequest, TSearch>(IAppDbContext context)
    : IReferenceCrudService<TDto, TRequest, TSearch>
    where TEntity : BaseEntity
    where TSearch : BaseSearchObject
{
    protected IAppDbContext Db => context;

    protected abstract string NotFoundMessage { get; }
    protected abstract TDto Map(TEntity entity);
    protected abstract TEntity CreateEntity(TRequest request);
    protected abstract void ApplyUpdate(TEntity entity, TRequest request);
    protected abstract IOrderedQueryable<TEntity> Order(IQueryable<TEntity> query);
    protected abstract IQueryable<TEntity> ApplySearch(IQueryable<TEntity> query, TSearch search);
    protected virtual Task EnsureDeletableAsync(TEntity entity, CancellationToken ct = default) =>
        Task.CompletedTask;

    public Task<PagedResult<TDto>> GetPagedAsync(TSearch search, CancellationToken cancellationToken = default, bool includeDeleted = false) =>
        Order(ApplySearch(Db.Set<TEntity>().AsNoTracking(), search)).ToPagedResultAsync(search, Map, ct: cancellationToken);

    public async Task<TDto> GetByIdAsync(int id, CancellationToken cancellationToken = default)
    {
        var entity = await Db.Set<TEntity>()
            .AsNoTracking()
            .FirstOrDefaultAsync(x => x.Id == id, cancellationToken)
            ?? throw new NotFoundException(NotFoundMessage);
        return Map(entity);
    }

    public async Task<bool> ExistsAsync(int id, CancellationToken cancellationToken = default) =>
        await Db.Set<TEntity>().AnyAsync(x => x.Id == id, cancellationToken);

    public async Task<TDto> CreateAsync(TRequest request, CancellationToken cancellationToken = default)
    {
        var entity = CreateEntity(request);
        Db.Set<TEntity>().Add(entity);
        await Db.SaveChangesAsync(cancellationToken);
        return Map(entity);
    }

    public async Task<TDto> UpdateAsync(int id, TRequest request, CancellationToken cancellationToken = default)
    {
        var entity = await Db.Set<TEntity>()
            .FirstOrDefaultAsync(x => x.Id == id, cancellationToken)
            ?? throw new NotFoundException(NotFoundMessage);
        ApplyUpdate(entity, request);
        await Db.SaveChangesAsync(cancellationToken);
        return Map(entity);
    }

    public async Task DeleteAsync(int id, CancellationToken cancellationToken = default)
    {
        var entity = await Db.Set<TEntity>()
            .FirstOrDefaultAsync(x => x.Id == id, cancellationToken)
            ?? throw new NotFoundException(NotFoundMessage);
        await EnsureDeletableAsync(entity, cancellationToken);
        Db.Set<TEntity>().Remove(entity);
        await Db.SaveChangesAsync(cancellationToken);
    }
}
CSHARP
run_incr
compare_cycle 3 "Add ExistsAsync to interface + base"

# ==================== EDIT 4: add ArchiveAsync method ====================
echo ""
echo "=== EDIT 4: Add ArchiveAsync to interface + base ==="
edit_file "$IFACE" <<'CSHARP'
using eNote.Application.Common.Search;

namespace eNote.Application.Features.Rentals.ReferenceData;

public interface IReferenceCrudService<TDto, TRequest, TSearch> where TSearch : BaseSearchObject
{
    Task<PagedResult<TDto>> GetPagedAsync(TSearch search, CancellationToken cancellationToken = default, bool includeDeleted = false);
    /// <summary>Retrieves a single entity by its unique identifier.</summary>
    Task<TDto> GetByIdAsync(int id, CancellationToken cancellationToken = default);
    Task<bool> ExistsAsync(int id, CancellationToken cancellationToken = default);
    Task<TDto> CreateAsync(TRequest request, CancellationToken cancellationToken = default);
    Task<TDto> UpdateAsync(int id, TRequest request, CancellationToken cancellationToken = default);
    Task DeleteAsync(int id, CancellationToken cancellationToken = default);
    Task ArchiveAsync(int id, CancellationToken cancellationToken = default);
}
CSHARP

edit_file "$BASE" <<'CSHARP'
using eNote.Application.Common.Search;

namespace eNote.Application.Features.Rentals.ReferenceData;

public abstract class ReferenceCrudService<TEntity, TDto, TRequest, TSearch>(IAppDbContext context)
    : IReferenceCrudService<TDto, TRequest, TSearch>
    where TEntity : BaseEntity
    where TSearch : BaseSearchObject
{
    protected IAppDbContext Db => context;

    protected abstract string NotFoundMessage { get; }
    protected abstract TDto Map(TEntity entity);
    protected abstract TEntity CreateEntity(TRequest request);
    protected abstract void ApplyUpdate(TEntity entity, TRequest request);
    protected abstract IOrderedQueryable<TEntity> Order(IQueryable<TEntity> query);
    protected abstract IQueryable<TEntity> ApplySearch(IQueryable<TEntity> query, TSearch search);
    protected virtual Task EnsureDeletableAsync(TEntity entity, CancellationToken ct = default) =>
        Task.CompletedTask;

    public Task<PagedResult<TDto>> GetPagedAsync(TSearch search, CancellationToken cancellationToken = default, bool includeDeleted = false) =>
        Order(ApplySearch(Db.Set<TEntity>().AsNoTracking(), search)).ToPagedResultAsync(search, Map, ct: cancellationToken);

    public async Task<TDto> GetByIdAsync(int id, CancellationToken cancellationToken = default)
    {
        var entity = await Db.Set<TEntity>()
            .AsNoTracking()
            .FirstOrDefaultAsync(x => x.Id == id, cancellationToken)
            ?? throw new NotFoundException(NotFoundMessage);
        return Map(entity);
    }

    public async Task<bool> ExistsAsync(int id, CancellationToken cancellationToken = default) =>
        await Db.Set<TEntity>().AnyAsync(x => x.Id == id, cancellationToken);

    public async Task<TDto> CreateAsync(TRequest request, CancellationToken cancellationToken = default)
    {
        var entity = CreateEntity(request);
        Db.Set<TEntity>().Add(entity);
        await Db.SaveChangesAsync(cancellationToken);
        return Map(entity);
    }

    public async Task<TDto> UpdateAsync(int id, TRequest request, CancellationToken cancellationToken = default)
    {
        var entity = await Db.Set<TEntity>()
            .FirstOrDefaultAsync(x => x.Id == id, cancellationToken)
            ?? throw new NotFoundException(NotFoundMessage);
        ApplyUpdate(entity, request);
        await Db.SaveChangesAsync(cancellationToken);
        return Map(entity);
    }

    public async Task DeleteAsync(int id, CancellationToken cancellationToken = default)
    {
        var entity = await Db.Set<TEntity>()
            .FirstOrDefaultAsync(x => x.Id == id, cancellationToken)
            ?? throw new NotFoundException(NotFoundMessage);
        await EnsureDeletableAsync(entity, cancellationToken);
        Db.Set<TEntity>().Remove(entity);
        await Db.SaveChangesAsync(cancellationToken);
    }

    public async Task ArchiveAsync(int id, CancellationToken cancellationToken = default)
    {
        var entity = await Db.Set<TEntity>()
            .FirstOrDefaultAsync(x => x.Id == id, cancellationToken)
            ?? throw new NotFoundException(NotFoundMessage);
        await Db.SaveChangesAsync(cancellationToken);
    }
}
CSHARP
run_incr
compare_cycle 4 "Add ArchiveAsync to interface + base"

# ==================== EDIT 5: file rename ====================
echo ""
echo "=== EDIT 5: Rename InstrumentTypeRequest.cs -> InstrumentTypeUpsertRequest.cs ==="
rename_file "$REQ" "eNote.Application/Features/Rentals/ReferenceData/InstrumentTypes/InstrumentTypeUpsertRequest.cs"
edit_file "eNote.Application/Features/Rentals/ReferenceData/InstrumentTypes/InstrumentTypeUpsertRequest.cs" <<'CSHARP'
namespace eNote.Application.Features.Rentals.ReferenceData.InstrumentTypes;

/// <summary>Request model for creating or updating an instrument type (renamed file).</summary>
public sealed class InstrumentTypeRequest
{
    public string Type { get; set; } = null!;
    public decimal MonthlyFee { get; set; }
}
CSHARP
run_incr
compare_cycle 5 "Rename InstrumentTypeRequest.cs"

# ==================== EDIT 6: add new file implementing existing interface ====================
echo ""
echo "=== EDIT 6: Add VirtualInstrumentTypeService.cs (new file implementing IReferenceCrudService) ==="
NEW_SVC="eNote.Application/Features/Rentals/ReferenceData/InstrumentTypes/VirtualInstrumentTypeService.cs"
edit_file "$NEW_SVC" <<'CSHARP'
using eNote.Application.Common.Search;

namespace eNote.Application.Features.Rentals.ReferenceData.InstrumentTypes;

/// <summary>Stub implementation added by R1 edit-shape test (add-file).</summary>
public sealed class VirtualInstrumentTypeService
    : IReferenceCrudService<object, object, BaseSearchObject>
{
    public Task<PagedResult<object>> GetPagedAsync(BaseSearchObject search, CancellationToken cancellationToken = default, bool includeDeleted = false)
        => throw new NotImplementedException();
    public Task<object> GetByIdAsync(int id, CancellationToken cancellationToken = default)
        => throw new NotImplementedException();
    public Task<bool> ExistsAsync(int id, CancellationToken cancellationToken = default)
        => throw new NotImplementedException();
    public Task<object> CreateAsync(object request, CancellationToken cancellationToken = default)
        => throw new NotImplementedException();
    public Task<object> UpdateAsync(int id, object request, CancellationToken cancellationToken = default)
        => throw new NotImplementedException();
    public Task DeleteAsync(int id, CancellationToken cancellationToken = default)
        => throw new NotImplementedException();
    public Task ArchiveAsync(int id, CancellationToken cancellationToken = default)
        => throw new NotImplementedException();
}
CSHARP
run_incr
compare_cycle 6 "Add VirtualInstrumentTypeService.cs"

# ==================== EDIT 7: attribute change in InstrumentTypeService ====================
# Must alter compiled output so SnapshotIdentity.Create produces a NEW snapshot id
# (an unused using is elided by Roslyn and leaves the compilation checksum unchanged,
# causing the incremental indexer to reuse the prior snapshot without re-extraction).
# A fully-qualified [Description] attribute lands in assembly metadata, forcing a
# distinct hash, without touching the interface-implementation edge structure.
echo ""
echo "=== EDIT 7: Add [Description] attribute to InstrumentTypeService.cs ==="
edit_file "$SVC" <<'CSHARP'
namespace eNote.Application.Features.Rentals.ReferenceData.InstrumentTypes;

[System.ComponentModel.Description("R1 convergence probe")]
public sealed class InstrumentTypeService(IAppDbContext context)
    : ReferenceCrudService<InstrumentType, InstrumentTypeDto, InstrumentTypeRequest, InstrumentTypeSearchObject>(context), IInstrumentTypeService
{
    protected override string NotFoundMessage => Messages.InstrumentTypeNotFound;

    protected override InstrumentTypeDto Map(InstrumentType entity) => new()
    {
        Id = entity.Id,
        Type = entity.Type,
        MonthlyFee = entity.MonthlyFee
    };

    protected override InstrumentType CreateEntity(InstrumentTypeRequest request) => new()
    {
        Type = request.Type.Trim(),
        MonthlyFee = request.MonthlyFee
    };

    protected override void ApplyUpdate(InstrumentType entity, InstrumentTypeRequest request)
    {
        entity.Type = request.Type.Trim();
        entity.MonthlyFee = request.MonthlyFee;
    }

    protected override IQueryable<InstrumentType> ApplySearch(IQueryable<InstrumentType> query, InstrumentTypeSearchObject search) => query.ApplySearch(search);
    protected override IOrderedQueryable<InstrumentType> Order(IQueryable<InstrumentType> query) => query.OrderBy(x => x.Type);

    protected override async Task EnsureDeletableAsync(InstrumentType entity, CancellationToken ct = default)
    {
        if (await Db.Set<Instrument>().AnyAsync(x => x.InstrumentTypeId == entity.Id, ct))
        {
            throw new BusinessException(Messages.InstrumentTypeDeleteBlocked);
        }
    }
}
CSHARP
run_incr
compare_cycle 7 "Add [Description] attribute"

# ==================== EDIT 8: member in existing file steals binding (no arc) ====================
echo ""
echo "=== EDIT 8: Add CoursePagingExtensions in CourseRankingEntryDto.cs ==="
edit_file "eNote.Application/Features/Academic/Courses/CourseRankingEntryDto.cs" <<'CSHARP'
using eNote.Application.Common.Search;

namespace eNote.Application.Features.Academic.Courses;

public class CourseRankingEntryDto
{
    public int Rank { get; set; }
    public int StudentId { get; set; }

    public string StudentName { get; set; } = null!;

    public double? AverageGrade { get; set; }
    public int GradedSubmissions { get; set; }
}

public static class CoursePagingExtensions
{
    public static Task<PagedResult<TModel>> ToPagedResultAsync<TEntity, TSearch, TModel>(
        this IQueryable<TEntity> query,
        TSearch search,
        Func<TEntity, TModel> map,
        Func<IQueryable<TEntity>, IOrderedQueryable<TEntity>>? orderBy = null,
        CancellationToken ct = default)
        where TSearch : BaseSearchObject
        => PagingExtensions.ToPagedResultAsync(query, search, map, orderBy, ct);
}
CSHARP
run_incr
compare_cycle 8 "Add CoursePagingExtensions in CourseRankingEntryDto.cs"

# ==================== EDIT 9: global using alias steals binding ====================
echo ""
echo "=== EDIT 9: Alias NotFoundException in GlobalUsings.cs ==="
edit_file "eNote.Application/GlobalUsings.cs" <<'CSHARP'
global using System.Linq;
global using System.Threading;
global using System.Threading.Tasks;
global using Microsoft.EntityFrameworkCore;
global using eNote.Application.Common.Exceptions;
global using eNote.Application.Common.Interfaces;
global using eNote.Application.Common.Localization;
global using eNote.Application.Common.Paging;
global using eNote.Application.Common.Persistence;
global using eNote.Application.Common.Time;
global using eNote.Application.Features.Academic;
global using eNote.Domain.Entities.Academic;
global using eNote.Domain.Entities.Assignments;
global using eNote.Domain.Entities.Communication;
global using eNote.Domain.Entities.Identity;
global using eNote.Domain.Entities.Rentals;
global using eNote.Domain.Entities.Shared;
global using eNote.Domain.Entities.Shared.Base;
global using eNote.Domain.Enums;
global using NotFoundException = eNote.Application.Common.Exceptions.ConflictException;
CSHARP
run_incr
compare_cycle 9 "Alias NotFoundException in GlobalUsings.cs"

# ==================== EDIT 10: new file in referenced project steals binding ====================
echo ""
echo "=== EDIT 10: Add eNote.Domain/QueryTrackingExtensions.cs ==="
edit_file "eNote.Domain/QueryTrackingExtensions.cs" <<'CSHARP'
namespace eNote;

public static class QueryTrackingExtensions
{
    public static System.Linq.IQueryable<T> AsNoTracking<T>(this System.Linq.IQueryable<T> source) where T : class => source;
}
CSHARP
run_incr
compare_cycle 10 "Add eNote.Domain/QueryTrackingExtensions.cs"

# ==================== EDIT 11: rename type used in unchanged documents ====================
echo ""
echo "=== EDIT 11: Rename MusicStoreSearchObject -> MusicStoreFilterObject ==="
edit_file "eNote.Application/Features/Rentals/ReferenceData/MusicStores/MusicStoreSearchObject.cs" <<'CSHARP'
using eNote.Application.Common.Search;

namespace eNote.Application.Features.Rentals.ReferenceData.MusicStores;

public sealed class MusicStoreFilterObject : BaseSearchObject
{
    public string? StoreName { get; set; }
}
CSHARP
run_incr
compare_cycle 11 "Rename MusicStoreSearchObject to MusicStoreFilterObject"

echo ""
echo "=== R1-eNote COMPLETE (11/11 cycles matched) ==="
echo "Scratchpad: $SCRATCH"
exit 0
