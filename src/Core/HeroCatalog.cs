using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json;
using RampageTracker.Models;

namespace RampageTracker.Core
{
    public static class HeroCatalog
    {
        private static readonly object _lock = new();
        private static bool _loaded = false;
        private static Dictionary<int, HeroRef> _byId = new();

        // Try to load heroes.json from repoRoot/data or repoRoot/src/data
        private static void EnsureLoaded()
        {
            if (_loaded) return;
            lock (_lock)
            {
                if (_loaded) return;
                try
                {
                    var root = Directory.GetCurrentDirectory();
                    if (new DirectoryInfo(root).Name.Equals("src", StringComparison.OrdinalIgnoreCase))
                    {
                        var parent = Directory.GetParent(root);
                        if (parent != null) root = parent.FullName;
                    }
                    var candidates = new[]
                    {
                        Path.Combine(root, "data", "heroes.json"),
                        Path.Combine(root, "src", "data", "heroes.json")
                    };
                    var path = candidates.FirstOrDefault(File.Exists);
                    if (path != null)
                    {
                        var json = File.ReadAllText(path);
                        var list = JsonConvert.DeserializeObject<List<HeroRef>>(json) ?? new List<HeroRef>();
                        _byId = list.Where(h => h != null).GroupBy(h => h.Id).ToDictionary(g => g.Key, g => g.First());
                    }
                }
                catch { /* ignore, fallbacks below */ }
                finally { _loaded = true; }
            }
        }

        public static string GetLocalizedName(int? heroId)
        {
            EnsureLoaded();
            if (heroId.HasValue && _byId.TryGetValue(heroId.Value, out var h) && !string.IsNullOrWhiteSpace(h.LocalizedName))
                return h.LocalizedName!;
            return heroId.HasValue ? $"Hero {heroId.Value}" : "Unknown";
        }

        public static string GetReactSlug(int? heroId)
        {
            EnsureLoaded();
            if (heroId.HasValue && _byId.TryGetValue(heroId.Value, out var h) && !string.IsNullOrWhiteSpace(h.InternalName))
            {
                // InternalName is like "npc_dota_hero_anti_mage" → slug "anti_mage"
                var name = h.InternalName!;
                const string prefix = "npc_dota_hero_";
                if (name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    return name.Substring(prefix.Length);
                return name.Replace("npc_dota_hero_", "");
            }
            // Fallback: unknown
            return "unknown";
        }
    }
}
