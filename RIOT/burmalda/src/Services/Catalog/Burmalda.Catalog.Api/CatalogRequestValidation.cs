namespace Burmalda.Catalog.Api;

public static class CatalogRequestValidation
{
    public static bool IsValid(CreateBookRequest request) =>
        !string.IsNullOrWhiteSpace(request.Isbn) && !string.IsNullOrWhiteSpace(request.Title)
        && request.Authors is { Length: > 0 } && request.Authors.All(x => !string.IsNullOrWhiteSpace(x))
        && request.Genres is not null;

    public static bool IsValid(ListCopyRequest request) => request.BookId != Guid.Empty
        && Enum.IsDefined(request.Condition) && request.LendingPrice >= 0
        && request.MaximumLoanDays is >= 1 and <= 90 && !string.IsNullOrWhiteSpace(request.Location);
}
