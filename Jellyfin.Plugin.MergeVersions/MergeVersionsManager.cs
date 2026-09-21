using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.IO;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.MergeVersions
{
    public class MergeVersionsManager : IDisposable
    {
        private readonly ILibraryManager _libraryManager;
        private readonly Timer _timer;
        private readonly ILogger<MergeVersionsManager> _logger; // TODO logging
        private readonly IFileSystem _fileSystem;
        private readonly IVideoVersions _videoVersions;

        public MergeVersionsManager(
            ILibraryManager libraryManager,
            ILogger<MergeVersionsManager> logger,
            IFileSystem fileSystem,
            IVideoVersions videoVersions
        )
        {
            _libraryManager = libraryManager;
            _logger = logger;
            _fileSystem = fileSystem;
            _videoVersions = videoVersions;
            _timer = new Timer(_ => OnTimerElapsed(), null, Timeout.Infinite, Timeout.Infinite);
        }

        private static string[] GetConfiguredMovieProviderIdKeys()
        {
            var keys = (Plugin.Instance?.Configuration?.MovieProviderIdKeys ?? Array.Empty<string>())
                .Where(k => !string.IsNullOrWhiteSpace(k))
                .Select(k => k.Trim())
                .ToArray();

            return keys.Length > 0 ? keys : new[] { "Tmdb" };
        }

        public async Task MergeMoviesAsync(IProgress<double> progress, ClaimsPrincipal user = null, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _logger.LogInformation("Scanning for repeated movies");

            var providerIdKeys = GetConfiguredMovieProviderIdKeys();
            _logger.LogInformation("Process Provider Ids: {Keys}", string.Join(", ", providerIdKeys));

            var ctx = BuildLibraryContext();

            var duplicateMovies = GetMoviesFromLibrary(providerIdKeys, ctx)
                .Where(x => !string.IsNullOrWhiteSpace(x.MergeKey))
                .GroupBy(x => x.MergeKey, StringComparer.OrdinalIgnoreCase)
                .Where(group => group.Count() > 1 &&
                                group.Any(x => IsNotYetMerged(x.Movie)))
                .Select(group => group.Select(x => x.Movie))
                .ToList();
            _logger.LogInformation("total duplicate Movies to process: {Count}", duplicateMovies.Count);

            var current = 0;
            foreach (var movies in duplicateMovies)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var first = movies.First();
                _logger.LogInformation("Merging {Name} ({Year})", first.Name, first.ProductionYear);
                await _videoVersions.MergeAsync(movies.Select(e => e.Id).ToArray(), user, cancellationToken).ConfigureAwait(false);
                progress?.Report(++current / (double)duplicateMovies.Count * 100);
            }

            cancellationToken.ThrowIfCancellationRequested();
            progress?.Report(100);
        }

        public async Task SplitMoviesAsync(IProgress<double> progress, ClaimsPrincipal user = null, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var ctx = BuildLibraryContext();
            var movies = GetMoviesFromLibrary(GetConfiguredMovieProviderIdKeys(), ctx)
                .Select(x => x.Movie)
                .ToList();
            var current = 0;
            foreach (var movie in movies)
            {
                cancellationToken.ThrowIfCancellationRequested();
                _logger.LogInformation("Splitting {Name} ({Year})", movie.Name, movie.ProductionYear);
                await _videoVersions.SplitAsync(movie.Id, user, cancellationToken).ConfigureAwait(false);
                progress?.Report(++current / (double)movies.Count * 100);
            }

            cancellationToken.ThrowIfCancellationRequested();
            progress?.Report(100);
        }

        private List<(Movie Movie, string MergeKey)> GetMoviesFromLibrary(IReadOnlyList<string> providerIdKeys, LibraryContext ctx)
        {
            return _libraryManager
                    .GetItemList(
                        new InternalItemsQuery
                        {
                            IncludeItemTypes = [BaseItemKind.Movie],
                            IsVirtualItem = false,
                            Recursive = true,
                        }
                )
                .OfType<Movie>()
                .Select(m => (Movie: m, MergeKey: GetMovieMergeKey(m, providerIdKeys)))
                .Where(x => IsEligible(x.Movie, ctx))
                .ToList();
        }
        private static string GetMovieMergeKey(Movie movie, IReadOnlyList<string> providerKeys)
        {
            var provider = GetFirstProviderId(movie, providerKeys);
            return provider.HasValue ? BuildProviderMergeKey(provider.Value) : null;
        }


        private static string[] GetConfiguredEpisodeProviderIdKeys()
        {
            var keys = (Plugin.Instance?.Configuration?.EpisodeProviderIdKeys ?? Array.Empty<string>())
                .Where(k => !string.IsNullOrWhiteSpace(k))
                .Select(k => k.Trim())
                .ToArray();

            return keys.Length > 0 ? keys : new[] { "Tvdb", "Tmdb", "Imdb" };
        }

        public async Task MergeEpisodesAsync(IProgress<double> progress, ClaimsPrincipal user = null, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _logger.LogInformation("Scanning for repeated episodes");

            var providerIdKeys = GetConfiguredEpisodeProviderIdKeys();
            _logger.LogInformation("Episode Provider Ids: {Keys}", string.Join(", ", providerIdKeys));

            var ctx = BuildLibraryContext();

            var enableEpisodeFallback = Plugin.Instance?.Configuration?.EnableEpisodeFallback ?? false;

            var episodes = GetEpisodesFromLibrary(providerIdKeys, ctx, enableEpisodeFallback);
            var duplicateEpisodes = episodes
                .Where(x => !string.IsNullOrWhiteSpace(x.MergeKey))
                .GroupBy(x => x.MergeKey, StringComparer.OrdinalIgnoreCase)
                .Where(x => x.Count() > 1 &&
                            x.Any(e => IsNotYetMerged(e.Episode)))
                .Select(g => g.Select(x => x.Episode))
                .ToList();

            _logger.LogInformation(
                "Found {EpisodeCount} episodes and {DuplicateGroupCount} duplicate episode groups",
                episodes.Count,
                duplicateEpisodes.Count);

            var current = 0;
            foreach (var episodeGroup in duplicateEpisodes)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var first = episodeGroup.First();
                _logger.LogInformation("Merging {Name} ({Year})", first.Name, first.ProductionYear);
                await _videoVersions.MergeAsync(episodeGroup.Select(e => e.Id).ToArray(), user, cancellationToken).ConfigureAwait(false);
                progress?.Report(++current / (double)duplicateEpisodes.Count * 100);
            }

            cancellationToken.ThrowIfCancellationRequested();
            progress?.Report(100);
        }

        public async Task SplitEpisodesAsync(IProgress<double> progress, ClaimsPrincipal user = null, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var ctx = BuildLibraryContext();
            var providerIdKeys = GetConfiguredEpisodeProviderIdKeys();
            var enableEpisodeFallback = Plugin.Instance?.Configuration?.EnableEpisodeFallback ?? false;
            var episodes = GetEpisodesFromLibrary(providerIdKeys, ctx, enableEpisodeFallback)
                .Select(x => x.Episode)
                .ToList();
            var current = 0;

            foreach (var episode in episodes)
            {
                cancellationToken.ThrowIfCancellationRequested();
                _logger.LogInformation("Splitting {EpisodeNumber} ({SeriesName})", episode.IndexNumber, episode.SeriesName);
                await _videoVersions.SplitAsync(episode.Id, user, cancellationToken).ConfigureAwait(false);
                progress?.Report(++current / (double)episodes.Count * 100);
            }

            cancellationToken.ThrowIfCancellationRequested();
            progress?.Report(100);
        }

        private List<(Episode Episode, string MergeKey)> GetEpisodesFromLibrary(
    IReadOnlyList<string> providerIdKeys, LibraryContext ctx, bool enableEpisodeFallback)
        {
            return _libraryManager
                .GetItemList(
                    new InternalItemsQuery
                    {
                        IncludeItemTypes = [BaseItemKind.Episode],
                        IsVirtualItem = false,
                        Recursive = true,
                    }
                )
                .OfType<Episode>()
                .Select(e => (Episode: e, MergeKey: GetEpisodeMergeKey(e, providerIdKeys, enableEpisodeFallback)))
                .Where(x => IsEligible(x.Episode, ctx))
                .ToList();
        }

        private static string GetEpisodeMergeKey(Episode episode, IReadOnlyList<string> providerKeys, bool enableEpisodeFallback)
        {
            var provider = GetFirstProviderId(episode, providerKeys);
            if (provider.HasValue)
            {
                return BuildProviderMergeKey(provider.Value);
            }

            if (enableEpisodeFallback)
            {

                if (episode.ParentIndexNumber.HasValue && episode.IndexNumber.HasValue)
                {
                    return $"number:{episode.SeriesName}:{episode.ParentIndexNumber}:{episode.IndexNumber}:{episode.IndexNumberEnd}";
                }

                return $"title:{episode.SeriesName}:{episode.SeasonName}:{episode.Name}:{episode.ProductionYear}";
            }

            return null;
        }


        private sealed class LibraryContext
        {
            public IReadOnlyList<string> VirtualFolderLocations { get; }
            public IReadOnlyList<string> ExcludedLocations { get; }

            public LibraryContext(
                IReadOnlyList<string> virtualFolderLocations,
                IReadOnlyList<string> excludedLocations)
            {
                VirtualFolderLocations = virtualFolderLocations;
                ExcludedLocations = excludedLocations;
            }
        }

        private LibraryContext BuildLibraryContext()
        {
            var excluded = Plugin.Instance?.PluginConfiguration?.LocationsExcluded
                           ?? Array.Empty<string>();

            var virtualFolderLocations = _libraryManager
                .GetVirtualFolders()
                .SelectMany(vf => vf.Locations ?? Array.Empty<string>())
                .ToArray();

            return new LibraryContext(virtualFolderLocations, excluded);
        }

        private bool IsEligible(BaseItem item, LibraryContext ctx)
        {
            if (IsInInactiveLibrary(item, ctx) || IsInExcludedLibrary(item, ctx))
            {
                return false;
            }
            return true;
        }

        private bool IsInExcludedLibrary(BaseItem item, LibraryContext ctx)
        {
            return ctx.ExcludedLocations
                     .Any(s => _fileSystem.ContainsSubPath(s, item.Path));
        }

        private bool IsInInactiveLibrary(BaseItem item, LibraryContext ctx)
        {
            if (item is not Movie)
            {
                return false;
            }

            var parentPath = item.DisplayParent?.Path;
            if (string.IsNullOrWhiteSpace(parentPath))
            {
                return false;
            }


            return ctx.VirtualFolderLocations
                .Any(libPath => string.Equals(libPath, parentPath, StringComparison.OrdinalIgnoreCase) ||
                                _fileSystem.ContainsSubPath(libPath, parentPath));
        }
        private void OnTimerElapsed() { }

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        protected virtual void Dispose(bool disposing)
        {
            if (disposing)
            {
                _timer?.Dispose();
            }
        }

        private static (string ProviderKey, string ProviderId)? GetFirstProviderId(BaseItem item, IReadOnlyList<string> keys)
        {
            if (item?.ProviderIds is null || keys is null || keys.Count == 0)
            {
                return null;
            }

            foreach (var key in keys)
            {
                if (string.IsNullOrWhiteSpace(key)) continue;

                foreach (var p in item.ProviderIds)
                {
                    if (string.Equals(p.Key, key, StringComparison.OrdinalIgnoreCase) &&
                        !string.IsNullOrWhiteSpace(p.Value))
                    {
                        return (p.Key, p.Value);
                    }
                }
            }

            return null;
        }
        private static string BuildProviderMergeKey((string ProviderKey, string ProviderId) provider)
            => $"provider:{provider.ProviderKey}:{provider.ProviderId}";


        private static bool IsNotYetMerged(Video item)
            => item.PrimaryVersionId == null && !item.LinkedAlternateVersions.Any();
    }
}
