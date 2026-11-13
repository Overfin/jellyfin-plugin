using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Plugins;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Overfin.Jellyfin;

/// <summary>One change: an item's ID, numbered, and whether it's gone.</summary>
public readonly record struct Change(long Seq, string Id, bool Removed);

/// <summary>
/// The library's changes as Jellyfin raises them (films, series, seasons,
/// episodes added, changed or removed), numbered, kept in memory only: a
/// new generation at each start, and the oldest let go past Capacity. A
/// reader whose generation isn't this one, or whose place was let go, reads
/// the library again (Since says so).
/// </summary>
public sealed class ChangeFeed
{
    /// <summary>How many changes are kept.</summary>
    public const int Capacity = 200_000;

    private readonly object _lock = new();
    private readonly Queue<Change> _changes = new();
    private long _last;

    /// <summary>This start's generation: new each time Jellyfin starts.</summary>
    public string Generation { get; } = Guid.NewGuid().ToString("N");

    /// <summary>The last number given.</summary>
    public long Last
    {
        get
        {
            lock (_lock)
            {
                return _last;
            }
        }
    }

    /// <summary>Notes a change to an item (one of a kind the index keeps).</summary>
    public void Note(Guid id, bool removed)
    {
        lock (_lock)
        {
            _changes.Enqueue(new Change(++_last, id.ToString("N"), removed));
            while (_changes.Count > Capacity)
            {
                _changes.Dequeue();
            }
        }
    }

    /// <summary>
    /// The changes after `since` in this generation, at most `limit`, and
    /// the last number; null when the reader must read everything again (a
    /// generation of another start, or a place let go).
    /// </summary>
    public (List<Change> Changes, long Last)? Since(string generation, long since, int limit)
    {
        lock (_lock)
        {
            if (generation != Generation || since < 0 || since > _last)
            {
                return null;
            }

            var first = _changes.Count > 0 ? _changes.Peek().Seq : _last + 1;
            if (since < first - 1)
            {
                return null;
            }

            var list = new List<Change>();
            foreach (var c in _changes)
            {
                if (c.Seq > since)
                {
                    list.Add(c);
                    if (list.Count == limit)
                    {
                        break;
                    }
                }
            }

            return (list, _last);
        }
    }
}

/// <summary>Hooks the feed to Jellyfin's library events while Jellyfin runs.</summary>
public sealed class ChangeFeedHook(ILibraryManager library, ChangeFeed feed) : IHostedService
{
    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        library.ItemAdded += Added;
        library.ItemUpdated += Updated;
        library.ItemRemoved += Removed;
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken)
    {
        library.ItemAdded -= Added;
        library.ItemUpdated -= Updated;
        library.ItemRemoved -= Removed;
        return Task.CompletedTask;
    }

    private void Added(object? sender, ItemChangeEventArgs e) => Note(e.Item, false);

    private void Updated(object? sender, ItemChangeEventArgs e) => Note(e.Item, false);

    private void Removed(object? sender, ItemChangeEventArgs e) => Note(e.Item, true);

    // Films, series, seasons and episodes: the kinds the index keeps.
    private void Note(BaseItem item, bool removed)
    {
        if (item is Movie or Series or Season or Episode)
        {
            feed.Note(item.Id, removed);
        }
    }
}

/// <summary>Registers the plugin's services with Jellyfin.</summary>
public sealed class ServiceRegistrator : IPluginServiceRegistrator
{
    /// <inheritdoc />
    public void RegisterServices(IServiceCollection serviceCollection, IServerApplicationHost applicationHost)
    {
        serviceCollection.AddSingleton<ChangeFeed>();
        serviceCollection.AddHostedService<ChangeFeedHook>();
    }
}
