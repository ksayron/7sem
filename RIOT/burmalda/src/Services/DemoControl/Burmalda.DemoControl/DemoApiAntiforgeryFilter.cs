using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;

namespace Burmalda.DemoControl;

public sealed class DemoApiAntiforgeryFilter(IAntiforgery antiforgery) : IEndpointFilter
{
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        if (HttpMethods.IsPost(context.HttpContext.Request.Method))
        {
            // A header alone cannot exempt an ambient admin cookie from CSRF protection.
            var bearer = await context.HttpContext.AuthenticateAsync("Bearer");
            if (!bearer.Succeeded || bearer.Principal?.IsInRole("Admin") != true)
                await antiforgery.ValidateRequestAsync(context.HttpContext);
        }
        return await next(context);
    }
}
