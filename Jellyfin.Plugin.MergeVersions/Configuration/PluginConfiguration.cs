using System;
using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.MergeVersions.Configuration
{
    public class PluginConfiguration : BasePluginConfiguration
    {

        public string[] LocationsExcluded { get; set; }

        /// <summary>
        /// Tmdb, Imdb
        /// </summary>
        public string[] MovieProviderIdKeys { get; set; }
        /// <summary>
        /// "Tvdb", "Tmdb", "Imdb"
        /// </summary>
        public string[] EpisodeProviderIdKeys { get; set; }

        /// <summary>
        /// use season number title
        /// </summary>
        public bool EnableEpisodeFallback { get; set; }

        public PluginConfiguration()
        {
            LocationsExcluded = Array.Empty<String>();
            MovieProviderIdKeys = new[] { "Tmdb" };
            EpisodeProviderIdKeys = new[] { "Tvdb", "Tmdb", "Imdb" };
            EnableEpisodeFallback = false;
        }
    }
}
