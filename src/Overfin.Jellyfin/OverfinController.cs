using MediaBrowser.Controller;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Overfin.Jellyfin;

/// <summary>The plugin's version and Jellyfin's, for Overfin to check at start.</summary>
public record InfoAnswer(string Plugin, string Jellyfin);

/// <summary>The plugin's routes, for Overfin alone.</summary>
[ApiController]
[Route("Overfin")]
[Authorize(Policy = "RequiresElevation")]
[OverfinOnly]
public class OverfinController : ControllerBase
{
    private readonly IServerApplicationHost _host;

    public OverfinController(IServerApplicationHost host)
    {
        _host = host;
    }

    /// <summary>GET /Overfin/Info: which plugin and which Jellyfin answer.</summary>
    [HttpGet("Info")]
    public ActionResult<InfoAnswer> Info() =>
        new InfoAnswer(typeof(Plugin).Assembly.GetName().Version?.ToString(3) ?? "", _host.ApplicationVersionString);
}
