using System.ComponentModel.DataAnnotations;
using Microsoft.EntityFrameworkCore;
using OSI.Signal9.Contracts;

namespace OSI.Signal9.API.Infrastructure;

/// <summary>
/// Standard ?page=&amp;pageSize= query parameters for collection resources.
/// </summary>
public record PageQuery
{
    [Range(1, int.MaxValue)]
    public int Page { get; init; } = 1;

    [Range(1, 100)]
    public int PageSize { get; init; } = 25;
}

internal static class PagingExtensions
{
    public static async Task<PagedResult<TDto>> ToPagedResultAsync<TEntity, TDto>(
        this IQueryable<TEntity> ordered,
        PageQuery page,
        Func<TEntity, TDto> map,
        CancellationToken cancellationToken)
    {
        var total = await ordered.CountAsync(cancellationToken);
        var items = await ordered
            .Skip((page.Page - 1) * page.PageSize)
            .Take(page.PageSize)
            .ToListAsync(cancellationToken);

        return new PagedResult<TDto>(items.Select(map).ToList(), page.Page, page.PageSize, total);
    }
}
