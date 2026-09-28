using Hangfire.Dashboard;

namespace Burmalda.Lending.Api;

public sealed class HangfireAuthorization : IDashboardAuthorizationFilter
{
    public bool Authorize(DashboardContext context) => context.GetHttpContext().User.IsInRole("Admin");
}
