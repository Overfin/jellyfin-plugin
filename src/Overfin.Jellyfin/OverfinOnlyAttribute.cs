using System;
using System.Linq;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace Overfin.Jellyfin;

/// <summary>
/// Lets through only Overfin's key (KeyCheck); any other administrator's
/// request gets a 404, as if the plugin weren't there. Jellyfin's own
/// policy, before it, refuses everyone who isn't an administrator.
/// </summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method)]
internal sealed class OverfinOnlyAttribute : ActionFilterAttribute
{
    public override void OnActionExecuting(ActionExecutingContext context)
    {
        var user = context.HttpContext.User;
        string? Claim(string type) => user.Claims.FirstOrDefault(c => c.Type == type)?.Value;
        var isApiKey = string.Equals(Claim("Jellyfin-IsApiKey"), "True", StringComparison.OrdinalIgnoreCase);
        if (!KeyCheck.IsOverfins(Plugin.Instance?.Configuration.KeyHash, isApiKey, Claim("Jellyfin-Token")))
        {
            context.Result = new NotFoundResult();
        }
    }
}
