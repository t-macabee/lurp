#!/usr/bin/env bash
# R1: Five-cycle incremental convergence on eNoteV2 (7 projects).
# Usage: ./scripts/r1-verify-enote.sh <solution-dir> <solution-file>
# Example: ./scripts/r1-verify-enote.sh /c/Users/Tarik/Desktop/eNoteV2/eNote eNote.sln
set -euo pipefail

SOLUTION_DIR="${1:?Usage: $0 <solution-dir> <solution-file>}"
SOLUTION_FILE="${2:?}"
REPO_ROOT="$(cd "$(dirname "$0")/.." && pwd)"

TMPROOT="$(mktemp -d)"
SCRATCH="$TMPROOT/eNote"
echo "=== R1-eNote: copying solution to scratchpad: $SCRATCH ==="
cp -r "$SOLUTION_DIR" "$SCRATCH"
SOLUTION_PATH="$SCRATCH/$SOLUTION_FILE"
INCR_DIR="$SCRATCH/incr-db"
FULL_DIR="$SCRATCH/full-db"
mkdir -p "$INCR_DIR" "$FULL_DIR"

WIN_INCR_DIR="$(cygpath -w "$INCR_DIR")"
WIN_FULL_DIR="$(cygpath -w "$FULL_DIR")"
WIN_SOLUTION_PATH="$(cygpath -w "$SOLUTION_PATH")"
LURP_PROJ="$REPO_ROOT/src/Lurp.csproj"

run_full() {
    local outdir_win="$1"
    echo "  [full] -> $outdir_win"
    dotnet run --no-build --project "$LURP_PROJ" -- \
        --mode=index --solution="$WIN_SOLUTION_PATH" --output-dir="$outdir_win" \
        --strategy=full 2>&1 | tail -5
}

run_incr() {
    echo "  [incr] -> $WIN_INCR_DIR"
    dotnet run --no-build --project "$LURP_PROJ" -- \
        --mode=index --solution="$WIN_SOLUTION_PATH" --output-dir="$WIN_INCR_DIR" \
        2>&1 | tail -5
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
run_full "$WIN_INCR_DIR"

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
        if (entity is ISoftDeletable sd) { sd.IsDeleted = true; await Db.SaveChangesAsync(cancellationToken); }
    }
}
CSHARP
run_incr

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

# ==================== STEP 6: fresh full rebuild ====================
echo ""
echo "=== STEP 6: Fresh full rebuild -> snapshot C ==="
run_full "$WIN_FULL_DIR"

# ==================== COMPARE ====================
echo ""
echo "=== COMPARISON: B5 vs C ==="
dotnet run --no-build --project "$REPO_ROOT/scripts/r1-compare/r1-compare.csproj" -- "$WIN_INCR_DIR/index.db" "$WIN_FULL_DIR/index.db"
EXIT_CODE=$?

echo ""
echo "=== R1-eNote COMPLETE ==="
echo "Scratchpad: $SCRATCH"
exit $EXIT_CODE
