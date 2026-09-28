namespace Burmalda.Web.Models;

public static class BookSearchExtensions
{
    public static string PublicationLabel(this BookSearchItem book) => book.Isbn.Length > 8 ? book.Isbn[^8..] : book.Isbn;
}
