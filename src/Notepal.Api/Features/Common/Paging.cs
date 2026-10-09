using Notepal.Contracts;

namespace Notepal.Api.Features.Common;

internal static class Paging
{
    public static (int Page, int PageSize) Normalize(int page, int pageSize) =>
        (Math.Max(1, page), Math.Clamp(pageSize, 1, NoteLimits.MaxPageSize));
}
