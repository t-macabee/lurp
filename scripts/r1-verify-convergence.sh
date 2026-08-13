#!/usr/bin/env bash
# R1: Five-cycle incremental convergence on a real solution.
# Usage: ./scripts/r1-verify-convergence.sh <solution-dir> <solution-file>
# Example: ./scripts/r1-verify-convergence.sh /c/Users/Tarik/Desktop/FIT-RS2-2026/eCommerce eCommerce.sln
set -euo pipefail

SOLUTION_DIR="${1:?Usage: $0 <solution-dir> <solution-file>}"
SOLUTION_FILE="${2:?}"
REPO_ROOT="$(cd "$(dirname "$0")/.." && pwd)"
LURP_PROJ="$REPO_ROOT/src/Lurp.csproj"
COMPARE_EXE="$REPO_ROOT/scripts/r1-compare/bin/Debug/net10.0/r1-compare"

# --- scratchpad setup ---
TMPROOT="$(mktemp -d)"
SCRATCH="$TMPROOT/$(basename "$SOLUTION_DIR")"
echo "=== R1: copying solution to scratchpad: $SCRATCH ==="
cp -r "$SOLUTION_DIR" "$SCRATCH"
SOLUTION_PATH="$SCRATCH/$SOLUTION_FILE"
INCR_DIR="$SCRATCH/incr-db"
FULL_DIR="$SCRATCH/full-db"
mkdir -p "$INCR_DIR" "$FULL_DIR"

# Git Bash /tmp != dotnet /tmp — convert to Windows paths for dotnet commands
WIN_INCR_DIR="$(cygpath -w "$INCR_DIR" 2>/dev/null || echo "$INCR_DIR")"
WIN_FULL_DIR="$(cygpath -w "$FULL_DIR" 2>/dev/null || echo "$FULL_DIR")"
WIN_SOLUTION_PATH="$(cygpath -w "$SOLUTION_PATH" 2>/dev/null || echo "$SOLUTION_PATH")"
DB_PATH="$INCR_DIR/index.db"
DB_FULL_PATH="$FULL_DIR/index.db"

# --- helpers ---
run_full() {
    local outdir_win="$1"
    echo "  [full] → $outdir_win"
    dotnet run --no-build --project "$LURP_PROJ" -- \
        --mode=index --solution="$WIN_SOLUTION_PATH" --output-dir="$outdir_win" \
        --strategy=full 2>&1 | tail -5
}

run_incr() {
    echo "  [incr] → $WIN_INCR_DIR"
    dotnet run --no-build --project "$LURP_PROJ" -- \
        --mode=index --solution="$WIN_SOLUTION_PATH" --output-dir="$WIN_INCR_DIR" \
        2>&1 | tail -5
}

edit_file() {
    local rel="$1"
    local target="$SCRATCH/$rel"
    echo "  EDIT: $rel"
    cat > "$target"
}

delete_file() {
    local rel="$1"
    echo "  DELETE: $rel"
    rm -f "$SCRATCH/$rel"
}

rename_file() {
    local old="$1" new="$2"
    echo "  RENAME: $old -> $new"
    mv "$SCRATCH/$old" "$SCRATCH/$new"
}

# ==================== STEP 0: full index → snapshot A ====================
echo ""
echo "=== STEP 0: Full index → snapshot A ==="
run_full "$WIN_INCR_DIR"

# ==================== EDIT 1: semantics-preserving comment ====================
echo ""
echo "=== EDIT 1: Add doc comment to IBaseReadService.GetByIdAsync ==="
edit_file "eCommerce.Services/IBaseReadService.cs" <<'CSHARP'
using eCommerce.Model.Responses;
using eCommerce.Model.SearchObjects;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace eCommerce.Services
{
    public interface IBaseReadService<TResponse, TSearch>
        where TSearch : BaseSearchObject
    {
        /// <summary>
        /// Retrieves an entity by its unique identifier.
        /// </summary>
        Task<TResponse> GetByIdAsync(int id);
        Task<PageResult<TResponse>> GetAllAsync(TSearch? search = null);
    }
}
CSHARP

run_incr

# ==================== EDIT 2: signature change ====================
echo ""
echo "=== EDIT 2: Add optional CancellationToken to GetAllAsync ==="
edit_file "eCommerce.Services/IBaseReadService.cs" <<'CSHARP'
using eCommerce.Model.Responses;
using eCommerce.Model.SearchObjects;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace eCommerce.Services
{
    public interface IBaseReadService<TResponse, TSearch>
        where TSearch : BaseSearchObject
    {
        /// <summary>
        /// Retrieves an entity by its unique identifier.
        /// </summary>
        Task<TResponse> GetByIdAsync(int id);
        Task<PageResult<TResponse>> GetAllAsync(TSearch? search = null, CancellationToken cancellationToken = default);
    }
}
CSHARP

edit_file "eCommerce.Services/BaseReadService.cs" <<'CSHARP'
using eCommerce.Model.Responses;
using eCommerce.Model.SearchObjects;
using eCommerce.Services.Database;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Linq.Dynamic.Core;

namespace eCommerce.Services
{
    public abstract class BaseReadService<TEntity, TResponse, TSearch> : IBaseReadService<TResponse, TSearch>
        where TEntity : class
        where TSearch : BaseSearchObject
    {
        protected readonly MapsterMapper.IMapper _mapper;
        protected readonly ECommerceDbContext _dbContext;

        protected BaseReadService(MapsterMapper.IMapper mapper, ECommerceDbContext dbContext)
        {
            _mapper = mapper;
            _dbContext = dbContext;
        }

        /// <summary>
        /// Applies search filters to the query. Override in derived classes to implement specific filtering logic.
        /// </summary>
        protected abstract IEnumerable<TEntity> ApplyFilters(IEnumerable<TEntity> query, TSearch? search);

        public virtual async Task<PageResult<TResponse>> GetAllAsync(TSearch? search = null, CancellationToken cancellationToken = default)
        {
            IEnumerable<TEntity> query = this._dbContext.Set<TEntity>();

            query = await IncludeRelatedEntitiesAsync(search, query.AsQueryable());
            query = ApplyFilters(query, search);

            int? totalCount = null;

            if (search.IncludeTotalCount ?? false)
            {
                totalCount = query.Count();
            }

            if (!string.IsNullOrWhiteSpace(search.SortBy))
            {
                query = query.AsQueryable().OrderBy(search.SortBy);
            }

            if (search.Page.HasValue)
            {
                query = query.Skip((search.Page.Value - 1) * search.PageSize.Value);
            }

            if (search.PageSize.HasValue)
            {
                query = query.Take(search.PageSize.Value);
            }

            var list = query.Select(item => _mapper.Map<TResponse>(item)).ToList();

            var pageResult = new PageResult<TResponse>
            {
                Items = list,
                TotalCount = totalCount
            };

            return await Task.FromResult(pageResult);
        }

        protected virtual async Task<IQueryable<TEntity>> IncludeRelatedEntitiesAsync(TSearch? search, IQueryable<TEntity> query = null)
        {
            return query;
        }

        public virtual async Task<TResponse> GetByIdAsync(int id)
        {
            var entity = this._dbContext.Set<TEntity>().Find(id);
            if (entity == null)
            {
                throw new KeyNotFoundException($"{typeof(TEntity).Name} with id {id} not found.");
            }

            return await Task.FromResult(_mapper.Map<TResponse>(entity));
        }
    }
}
CSHARP

# Also update OrderService.GetAllAsync override to match new signature
edit_file "eCommerce.Services/OrderService.cs" <<'CSHARP'
using eCommerce.Model.Exceptions;
using eCommerce.Model.Requests;
using eCommerce.Model.Responses;
using eCommerce.Model.SearchObjects;
using eCommerce.Services.Database;
using MapsterMapper;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Stripe;
using System.Threading;

namespace eCommerce.Services;

public class OrderService : BaseReadService<Order, OrderResponse, OrderSearchObject>, IOrderService
{
    private readonly IAuthenticatedUserAccessor _userAccessor;
    private readonly IConfiguration _configuration;

    public OrderService(ECommerceDbContext dbContext, IMapper mapper, IAuthenticatedUserAccessor userAccessor, IConfiguration configuration)
        : base(mapper, dbContext)
    {
        _userAccessor = userAccessor;
        _configuration = configuration;
    }

    public override async Task<PageResult<OrderResponse>> GetAllAsync(OrderSearchObject? search = null, CancellationToken cancellationToken = default)
    {
        search ??= new OrderSearchObject();
        if (string.IsNullOrWhiteSpace(search.SortBy))
        {
            search.SortBy = "OrderDate desc";
        }

        return await base.GetAllAsync(search, cancellationToken);
    }

    protected override async Task<IQueryable<Order>> IncludeRelatedEntitiesAsync(
        OrderSearchObject? search,
        IQueryable<Order> query = null!)
    {
        return await Task.FromResult(query.Include(o => o.OrderItems).ThenInclude(oi => oi.Product));
    }

    protected override IEnumerable<Order> ApplyFilters(IEnumerable<Order> query, OrderSearchObject? search)
    {
        var userId = _userAccessor.GetUserId();
        if (!userId.HasValue)
        {
            return Enumerable.Empty<Order>();
        }

        query = query.Where(o => o.UserId == userId.Value);

        if (search?.Status.HasValue == true)
        {
            query = query.Where(o => (int)o.Status == search.Status.Value);
        }

        return query;
    }

    public override async Task<OrderResponse> GetByIdAsync(int id)
    {
        var userId = _userAccessor.GetUserId();
        if (!userId.HasValue)
        {
            throw new KeyNotFoundException($"{typeof(Order).Name} with id {id} not found.");
        }

        var order = await _dbContext.Orders
            .AsNoTracking()
            .Include(o => o.OrderItems)
            .ThenInclude(oi => oi.Product)
            .FirstOrDefaultAsync(o => o.Id == id && o.UserId == userId.Value);

        if (order == null)
        {
            throw new KeyNotFoundException($"{typeof(Order).Name} with id {id} not found.");
        }

        return _mapper.Map<OrderResponse>(order);
    }

    public async Task<OrderResponse> CheckoutAsync(CheckoutRequest request)
    {
        var userId = _userAccessor.GetUserId()
                     ?? throw new InvalidOperationException("User id claim is missing.");

        if (request.Items == null || request.Items.Count == 0)
        {
            throw new ClinetException("Cart is empty.");
        }

        await using var tx = await _dbContext.Database.BeginTransactionAsync();

        try
        {
            var merged = request.Items
                .Where(i => i.Quantity > 0)
                .GroupBy(i => i.ProductId)
                .Select(g => new { ProductId = g.Key, Quantity = g.Sum(x => x.Quantity) })
                .ToList();

            decimal total = 0;
            var order = new Order
            {
                UserId = userId,
                OrderDate = DateTime.UtcNow,
                OrderNumber = $"O-{DateTime.UtcNow:yyyyMMddHHmmss}-{userId}",
                Status = OrderStatus.Processing,
                ShippingAddress = OrDash(request.ShippingAddress),
                ShippingCity = OrDash(request.ShippingCity),
                ShippingState = OrDash(request.ShippingState),
                ShippingZipCode = OrDash(request.ShippingZipCode),
                ShippingCountry = OrDash(request.ShippingCountry),
                PaymentTransactionId = request.PaymentIntentId,
                PaymentDate = request.PaymentIntentId != null ? DateTime.UtcNow : (DateTime?)null
            };

            foreach (var line in merged)
            {
                var product = await _dbContext.Products.FindAsync(line.ProductId);
                if (product == null)
                {
                    throw new ClinetException($"Product {line.ProductId} was not found.");
                }

                if (!product.IsActive)
                {
                    throw new ClinetException($"Product '{product.Name}' is not available.");
                }

                if (product.StockQuantity < line.Quantity)
                {
                    throw new ClinetException($"Insufficient stock for '{product.Name}'.");
                }

                var unitPrice = product.Price;
                total += unitPrice * line.Quantity;
                product.StockQuantity -= line.Quantity;

                order.OrderItems.Add(new OrderItem
                {
                    ProductId = product.Id,
                    Quantity = line.Quantity,
                    UnitPrice = unitPrice,
                });
            }

            order.TotalAmount = total;
            _dbContext.Orders.Add(order);
            await _dbContext.SaveChangesAsync();
            await tx.CommitAsync();

            return await GetByIdAsync(order.Id);
        }
        catch
        {
            await tx.RollbackAsync();
            throw;
        }
    }

    private static string OrDash(string? value) =>
        string.IsNullOrWhiteSpace(value) ? "—" : value.Trim();

    public async Task<PaymentIntentResponse> CreatePaymentIntentAsync(CreatePaymentIntentRequest request)
    {
        if(request.Items == null || request.Items.Count == 0)
        {
            throw new ClinetException("Cart is empty.");
        }

        var merged = request.Items
            .Where(i => i.Quantity > 0)
            .GroupBy(i => i.ProductId)
            .Select(g => new { ProductId = g.Key, Quantity = g.Sum(x => x.Quantity) })
            .ToList();

        decimal total = 0;
        foreach (var line in merged)
        {
            var product = _dbContext.Products.Find(line.ProductId);
            if (product == null) {
                throw new ClinetException($"Product {line.ProductId} was not found."); 
            }

            total += product.Price * line.Quantity;
        }

        var secretKey = _configuration["Stripe:SecretKey"] 
                        ?? throw new InvalidOperationException("Stripe secret key is not configured.");

        StripeConfiguration.ApiKey = secretKey;
        var options = new PaymentIntentCreateOptions
        {
            Amount = (long)(total * 100),
            Currency = "usd",
            AutomaticPaymentMethods = new PaymentIntentAutomaticPaymentMethodsOptions
            {
                Enabled = true,
            },
        };

        var service = new PaymentIntentService();
        var intent = await service.CreateAsync(options);

        return new PaymentIntentResponse
        {
            ClientSecret = intent.ClientSecret,
            PublishableKey = _configuration["Stripe:PublishableKey"] 
                             ?? throw new InvalidOperationException("Stripe publishable key is not configured.")
        };
    }
}
CSHARP

run_incr

# ==================== EDIT 3: add new method to IOrderService ====================
echo ""
echo "=== EDIT 3: Add CancelAsync to IOrderService + OrderService ==="
edit_file "eCommerce.Services/IOrderService.cs" <<'CSHARP'
using eCommerce.Model.Requests;
using eCommerce.Model.Responses;
using eCommerce.Model.SearchObjects;

namespace eCommerce.Services;

public interface IOrderService : IBaseReadService<OrderResponse, OrderSearchObject>
{
    Task<OrderResponse> CheckoutAsync(CheckoutRequest request);

    Task<PaymentIntentResponse> CreatePaymentIntentAsync(CreatePaymentIntentRequest request);

    Task<bool> CancelAsync(int id);
}
CSHARP

# Append CancelAsync to OrderService (edit whole file to keep it simple)
edit_file "eCommerce.Services/OrderService.cs" <<'CSHARP'
using eCommerce.Model.Exceptions;
using eCommerce.Model.Requests;
using eCommerce.Model.Responses;
using eCommerce.Model.SearchObjects;
using eCommerce.Services.Database;
using MapsterMapper;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Stripe;
using System.Threading;

namespace eCommerce.Services;

public class OrderService : BaseReadService<Order, OrderResponse, OrderSearchObject>, IOrderService
{
    private readonly IAuthenticatedUserAccessor _userAccessor;
    private readonly IConfiguration _configuration;

    public OrderService(ECommerceDbContext dbContext, IMapper mapper, IAuthenticatedUserAccessor userAccessor, IConfiguration configuration)
        : base(mapper, dbContext)
    {
        _userAccessor = userAccessor;
        _configuration = configuration;
    }

    public override async Task<PageResult<OrderResponse>> GetAllAsync(OrderSearchObject? search = null, CancellationToken cancellationToken = default)
    {
        search ??= new OrderSearchObject();
        if (string.IsNullOrWhiteSpace(search.SortBy))
        {
            search.SortBy = "OrderDate desc";
        }

        return await base.GetAllAsync(search, cancellationToken);
    }

    protected override async Task<IQueryable<Order>> IncludeRelatedEntitiesAsync(
        OrderSearchObject? search,
        IQueryable<Order> query = null!)
    {
        return await Task.FromResult(query.Include(o => o.OrderItems).ThenInclude(oi => oi.Product));
    }

    protected override IEnumerable<Order> ApplyFilters(IEnumerable<Order> query, OrderSearchObject? search)
    {
        var userId = _userAccessor.GetUserId();
        if (!userId.HasValue)
        {
            return Enumerable.Empty<Order>();
        }

        query = query.Where(o => o.UserId == userId.Value);

        if (search?.Status.HasValue == true)
        {
            query = query.Where(o => (int)o.Status == search.Status.Value);
        }

        return query;
    }

    public override async Task<OrderResponse> GetByIdAsync(int id)
    {
        var userId = _userAccessor.GetUserId();
        if (!userId.HasValue)
        {
            throw new KeyNotFoundException($"{typeof(Order).Name} with id {id} not found.");
        }

        var order = await _dbContext.Orders
            .AsNoTracking()
            .Include(o => o.OrderItems)
            .ThenInclude(oi => oi.Product)
            .FirstOrDefaultAsync(o => o.Id == id && o.UserId == userId.Value);

        if (order == null)
        {
            throw new KeyNotFoundException($"{typeof(Order).Name} with id {id} not found.");
        }

        return _mapper.Map<OrderResponse>(order);
    }

    public async Task<bool> CancelAsync(int id)
    {
        var order = await _dbContext.Orders.FindAsync(id);
        if (order == null) return false;
        order.Status = OrderStatus.Cancelled;
        await _dbContext.SaveChangesAsync();
        return true;
    }

    public async Task<OrderResponse> CheckoutAsync(CheckoutRequest request)
    {
        var userId = _userAccessor.GetUserId()
                     ?? throw new InvalidOperationException("User id claim is missing.");

        if (request.Items == null || request.Items.Count == 0)
        {
            throw new ClinetException("Cart is empty.");
        }

        await using var tx = await _dbContext.Database.BeginTransactionAsync();

        try
        {
            var merged = request.Items
                .Where(i => i.Quantity > 0)
                .GroupBy(i => i.ProductId)
                .Select(g => new { ProductId = g.Key, Quantity = g.Sum(x => x.Quantity) })
                .ToList();

            decimal total = 0;
            var order = new Order
            {
                UserId = userId,
                OrderDate = DateTime.UtcNow,
                OrderNumber = $"O-{DateTime.UtcNow:yyyyMMddHHmmss}-{userId}",
                Status = OrderStatus.Processing,
                ShippingAddress = OrDash(request.ShippingAddress),
                ShippingCity = OrDash(request.ShippingCity),
                ShippingState = OrDash(request.ShippingState),
                ShippingZipCode = OrDash(request.ShippingZipCode),
                ShippingCountry = OrDash(request.ShippingCountry),
                PaymentTransactionId = request.PaymentIntentId,
                PaymentDate = request.PaymentIntentId != null ? DateTime.UtcNow : (DateTime?)null
            };

            foreach (var line in merged)
            {
                var product = await _dbContext.Products.FindAsync(line.ProductId);
                if (product == null)
                {
                    throw new ClinetException($"Product {line.ProductId} was not found.");
                }

                if (!product.IsActive)
                {
                    throw new ClinetException($"Product '{product.Name}' is not available.");
                }

                if (product.StockQuantity < line.Quantity)
                {
                    throw new ClinetException($"Insufficient stock for '{product.Name}'.");
                }

                var unitPrice = product.Price;
                total += unitPrice * line.Quantity;
                product.StockQuantity -= line.Quantity;

                order.OrderItems.Add(new OrderItem
                {
                    ProductId = product.Id,
                    Quantity = line.Quantity,
                    UnitPrice = unitPrice,
                });
            }

            order.TotalAmount = total;
            _dbContext.Orders.Add(order);
            await _dbContext.SaveChangesAsync();
            await tx.CommitAsync();

            return await GetByIdAsync(order.Id);
        }
        catch
        {
            await tx.RollbackAsync();
            throw;
        }
    }

    private static string OrDash(string? value) =>
        string.IsNullOrWhiteSpace(value) ? "—" : value.Trim();

    public async Task<PaymentIntentResponse> CreatePaymentIntentAsync(CreatePaymentIntentRequest request)
    {
        if(request.Items == null || request.Items.Count == 0)
        {
            throw new ClinetException("Cart is empty.");
        }

        var merged = request.Items
            .Where(i => i.Quantity > 0)
            .GroupBy(i => i.ProductId)
            .Select(g => new { ProductId = g.Key, Quantity = g.Sum(x => x.Quantity) })
            .ToList();

        decimal total = 0;
        foreach (var line in merged)
        {
            var product = _dbContext.Products.Find(line.ProductId);
            if (product == null) {
                throw new ClinetException($"Product {line.ProductId} was not found."); 
            }

            total += product.Price * line.Quantity;
        }

        var secretKey = _configuration["Stripe:SecretKey"] 
                        ?? throw new InvalidOperationException("Stripe secret key is not configured.");

        StripeConfiguration.ApiKey = secretKey;
        var options = new PaymentIntentCreateOptions
        {
            Amount = (long)(total * 100),
            Currency = "usd",
            AutomaticPaymentMethods = new PaymentIntentAutomaticPaymentMethodsOptions
            {
                Enabled = true,
            },
        };

        var service = new PaymentIntentService();
        var intent = await service.CreateAsync(options);

        return new PaymentIntentResponse
        {
            ClientSecret = intent.ClientSecret,
            PublishableKey = _configuration["Stripe:PublishableKey"] 
                             ?? throw new InvalidOperationException("Stripe publishable key is not configured.")
        };
    }
}
CSHARP

run_incr

# ==================== EDIT 4: base/interface change ====================
echo ""
echo "=== EDIT 4: Add DeleteAsync to IBaseReadService + BaseReadService ==="
edit_file "eCommerce.Services/IBaseReadService.cs" <<'CSHARP'
using eCommerce.Model.Responses;
using eCommerce.Model.SearchObjects;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace eCommerce.Services
{
    public interface IBaseReadService<TResponse, TSearch>
        where TSearch : BaseSearchObject
    {
        /// <summary>
        /// Retrieves an entity by its unique identifier.
        /// </summary>
        Task<TResponse> GetByIdAsync(int id);
        Task<PageResult<TResponse>> GetAllAsync(TSearch? search = null, CancellationToken cancellationToken = default);
        Task<bool> DeleteAsync(int id);
    }
}
CSHARP

edit_file "eCommerce.Services/BaseReadService.cs" <<'CSHARP'
using eCommerce.Model.Responses;
using eCommerce.Model.SearchObjects;
using eCommerce.Services.Database;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Linq.Dynamic.Core;

namespace eCommerce.Services
{
    public abstract class BaseReadService<TEntity, TResponse, TSearch> : IBaseReadService<TResponse, TSearch>
        where TEntity : class
        where TSearch : BaseSearchObject
    {
        protected readonly MapsterMapper.IMapper _mapper;
        protected readonly ECommerceDbContext _dbContext;

        protected BaseReadService(MapsterMapper.IMapper mapper, ECommerceDbContext dbContext)
        {
            _mapper = mapper;
            _dbContext = dbContext;
        }

        /// <summary>
        /// Applies search filters to the query. Override in derived classes to implement specific filtering logic.
        /// </summary>
        protected abstract IEnumerable<TEntity> ApplyFilters(IEnumerable<TEntity> query, TSearch? search);

        public virtual async Task<PageResult<TResponse>> GetAllAsync(TSearch? search = null, CancellationToken cancellationToken = default)
        {
            IEnumerable<TEntity> query = this._dbContext.Set<TEntity>();

            query = await IncludeRelatedEntitiesAsync(search, query.AsQueryable());
            query = ApplyFilters(query, search);

            int? totalCount = null;

            if (search.IncludeTotalCount ?? false)
            {
                totalCount = query.Count();
            }

            if (!string.IsNullOrWhiteSpace(search.SortBy))
            {
                query = query.AsQueryable().OrderBy(search.SortBy);
            }

            if (search.Page.HasValue)
            {
                query = query.Skip((search.Page.Value - 1) * search.PageSize.Value);
            }

            if (search.PageSize.HasValue)
            {
                query = query.Take(search.PageSize.Value);
            }

            var list = query.Select(item => _mapper.Map<TResponse>(item)).ToList();

            var pageResult = new PageResult<TResponse>
            {
                Items = list,
                TotalCount = totalCount
            };

            return await Task.FromResult(pageResult);
        }

        protected virtual async Task<IQueryable<TEntity>> IncludeRelatedEntitiesAsync(TSearch? search, IQueryable<TEntity> query = null)
        {
            return query;
        }

        public virtual async Task<TResponse> GetByIdAsync(int id)
        {
            var entity = this._dbContext.Set<TEntity>().Find(id);
            if (entity == null)
            {
                throw new KeyNotFoundException($"{typeof(TEntity).Name} with id {id} not found.");
            }

            return await Task.FromResult(_mapper.Map<TResponse>(entity));
        }

        public virtual async Task<bool> DeleteAsync(int id)
        {
            var entity = await _dbContext.Set<TEntity>().FindAsync(id);
            if (entity == null) return false;
            _dbContext.Set<TEntity>().Remove(entity);
            await _dbContext.SaveChangesAsync();
            return true;
        }
    }
}
CSHARP

run_incr

# ==================== EDIT 5: file rename ====================
echo ""
echo "=== EDIT 5: Rename Class1.cs → ServiceExtensions.cs ==="
rename_file "eCommerce.Services/Class1.cs" "eCommerce.Services/ServiceExtensions.cs"
edit_file "eCommerce.Services/ServiceExtensions.cs" <<'CSHARP'
namespace eCommerce.Services;

/// <summary>
/// Extension methods for service layer configuration.
/// </summary>
public static class ServiceExtensions
{
    public static string ToSlug(this string value)
    {
        return value.Trim().ToLowerInvariant().Replace(" ", "-");
    }
}
CSHARP

run_incr

# ==================== EDIT 6: attribute change on existing controller ====================
echo ""
echo "=== EDIT 6: Change [HttpGet(\"MaxName\")] → [HttpPost(\"MaxName\")] in ProductsController.cs ==="
CTRL="eCommerce.WebAPI/Controllers/ProductsController.cs"
edit_file "$CTRL" <<'CSHARP'
using eCommerce.Services;
using eCommerce.Model.Responses;
using eCommerce.Model.SearchObjects;
using Microsoft.AspNetCore.Mvc;
using eCommerce.Model.Requests;

namespace eCommerce.WebAPI.Controllers;

public class ProductsController : BaseCRUDController<ProductResponse, ProductSearchObject, ProductInsertRequest, ProductUpdateRequest, IProductService>
{
    public ProductsController(IProductService productService) : base(productService)
    {
    }

    /// <summary>
    /// Retrieves the product that has the longest name matching the given search criteria.
    /// </summary>
    /// <param name="search">Optional search criteria to filter products.</param>
    /// <returns>The longest description product</returns>
    /// <remarks>
    /// Sample response:
    ///
    ///     POST /Todo
    ///     {
    ///         "id": 2,
    ///         "name": "Mechanical Keyboard",
    ///         "description": "RGB backlit mechanical keyboard with blue switches.",
    ///         "price": 79.99,
    ///         "stockQuantity": 75,
    ///         "isActive": true,
    ///         "createdAt": "2026-02-27T12:15:43.4170502Z",
    ///         "updatedAt": null,
    ///         "sku": "MK-2002",
    ///         "weight": 1200,
    ///         "productTypeId": 2,
    ///         "unitOfMeasureId": 1
    ///     }
    ///
    /// </remarks>
    /// <response code="200">Product found - returns the product with the longest name.</response>
    /// <response code="404">No product matches the provided search criteria.</response>

    [HttpPost("MaxName")]
    [Produces("application/json")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<ProductResponse>> GetWithMaxName([FromQuery] ProductSearchObject? search)
    {
        var result = await _service.GetWithMaxNameAsync(search);
        return Ok(result);

    }


    [HttpPost("{id}/Activate")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<ProductResponse>> Activate(int id)
    {
        var result = await _service.ActivateAsync(id);
        return Ok(result);
    }

    [HttpPost("{id}/Deactivate")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<ProductResponse>> Deactivate(int id)
    {
        var result = await _service.DeactivateAsync(id);
        return Ok(result);
    }

    [HttpGet("{id}/AllowedActions")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<List<string>>> GetAllowedActions(int id)
    {
        var result = await _service.GetAllowedActionsAsync(id);
        return Ok(result);
    }

}
CSHARP
run_incr

# ==================== STEP 6: fresh full rebuild → snapshot C ====================
echo ""
echo "=== STEP 6: Fresh full rebuild → snapshot C ==="
run_full "$WIN_FULL_DIR"

# ==================== COMPARE ====================
echo ""
echo "=== COMPARISON: B5 (incremental) vs C (full rebuild) ==="
dotnet run --no-build --project "$REPO_ROOT/scripts/r1-compare/r1-compare.csproj" -- "$WIN_INCR_DIR/index.db" "$WIN_FULL_DIR/index.db"
EXIT_CODE=$?

echo ""
echo "=== R1 COMPLETE ==="
echo "Scratchpad: $SCRATCH"
exit $EXIT_CODE
