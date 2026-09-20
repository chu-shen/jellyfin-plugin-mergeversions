using MediaBrowser.Model.Plugins;
using System;

namespace Jellyfin.Plugin.MergeVersions.Configuration
{
    public class PluginConfiguration : BasePluginConfiguration
    {

        public string[] LocationsExcluded { get; set; }

        
        /// <summary>
        /// Tmdb, Imdb
        /// </summary>
        public string[] ProviderIdKeys { get; set; }

        public PluginConfiguration()
        {
            LocationsExcluded = Array.Empty<String>();
            ProviderIdKeys = new[] { "Tmdb" };
        }
    }
}
