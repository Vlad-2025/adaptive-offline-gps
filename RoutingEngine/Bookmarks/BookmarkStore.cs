using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace adaptive_offline_gps.RoutingEngine.Bookmarks
{
    /// <summary>
    /// Loads / saves the bookmark list for one graph. One store per loaded map.
    /// Persists to &lt;basename&gt;.bookmarks.json next to the .rgph file.
    ///
    /// Names are kept unique per store. On conflict during Add or Rename,
    /// an " (N)" suffix is appended until the name is free. The caller is told
    /// the actual stored name via the returned Bookmark instance.
    /// </summary>
    public sealed class BookmarkStore
    {
        private readonly string _path;
        private readonly List<Bookmark> _items = new();

        /// <summary>Read-only snapshot of current bookmarks (in insertion order).</summary>
        public IReadOnlyList<Bookmark> All => _items;

        private BookmarkStore(string path) { _path = path; }

        //  Path helpers --------------------------------------------------------

        /// <summary>
        /// Path to the bookmark file for a given base graph path.
        /// e.g. ".../malta-roads.osm.rgph" -> ".../malta-roads.osm.bookmarks.json"
        /// </summary>
        public static string PathFor(string baseRgphPath)
        {
            string dir = Path.GetDirectoryName(baseRgphPath) ?? ".";
            string name = Path.GetFileNameWithoutExtension(baseRgphPath);
            return Path.Combine(dir, $"{name}.bookmarks.json");
        }

        //  Lifecycle -----------------------------------------------------------

        /// <summary>
        /// Load (or create empty) the store for a graph.
        /// </summary>
        public static BookmarkStore Load(string baseRgphPath)
        {
            string path = PathFor(baseRgphPath);
            var store = new BookmarkStore(path);

            if (File.Exists(path))
            {
                try
                {
                    var json = File.ReadAllText(path);
                    var items = JsonSerializer.Deserialize<List<Bookmark>>(json);
                    if (items != null)
                        store._items.AddRange(items.Where(b => b != null && !string.IsNullOrWhiteSpace(b.Name)));
                }
                catch (Exception ex)
                {
                    //  Corrupt file -> log and start fresh. Don't lose the user's
                    //  next save by failing here.
                    Debug.WriteLine($"BookmarkStore: failed to load {path}: {ex.Message}");
                }
            }

            return store;
        }

        public void Save()
        {
            try
            {
                var json = JsonSerializer.Serialize(_items, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(_path, json);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"BookmarkStore: failed to save {_path}: {ex.Message}");
            }
        }

        //  Operations ----------------------------------------------------------

        /// <summary>
        /// Add a new bookmark. The returned instance carries the actual stored
        /// name, which may differ from the requested name (auto-numbered on conflict).
        /// </summary>
        public Bookmark Add(string requestedName, double lat, double lon)
        {
            string name = ResolveUniqueName(requestedName, excluding: null);
            var b = new Bookmark
            {
                Name = name,
                Lat = lat,
                Lon = lon
            };
            _items.Add(b);
            Save();

            return b;
        }

        /// <summary>
        /// Rename an existing bookmark. Returns the actual stored name (auto-numbered if conflict).
        /// Returns null if the bookmark instance isn't part of this store.
        /// </summary>
        public string Rename(Bookmark bookmark, string requestedName)
        {
            if (!_items.Contains(bookmark))
                return null;

            bookmark.Name = ResolveUniqueName(requestedName, excluding: bookmark);

            Save();

            return bookmark.Name;
        }

        public bool Remove(Bookmark bookmark)
        {
            bool removed = _items.Remove(bookmark);
            if (removed) Save();
            return removed;
        }

        public void Clear()
        {
            _items.Clear();
            Save();
        }

        //  Name conflict resolution -------------------------------------------

        private static readonly Regex SuffixRegex = new(@"^(?<base>.*?)\s*\((?<n>\d+)\)\s*$", RegexOptions.Compiled);

        /// <summary>
        /// Returns a name guaranteed unique among the store's items (excluding
        /// the optional <paramref name="excluding"/> instance, to allow rename-to-same).
        /// Strategy: if name is free, use it. Otherwise append " (2)", " (3)", ...
        /// If the requested name already ends in "(N)", continue from N+1.
        /// </summary>
        private string ResolveUniqueName(string requestedName, Bookmark excluding)
        {
            string name = (requestedName ?? "").Trim();

            if (string.IsNullOrEmpty(name))
                name = "Unnamed";

            if (!IsTaken(name, excluding))
                return name;

            //  If the input is already "Foo (3)", increment from 4. Otherwise
            //  start at " (2)" because the unsuffixed form is the "(1)".
            string baseName = name;
            int next = 2;
            var m = SuffixRegex.Match(name);

            if (m.Success && int.TryParse(m.Groups["n"].Value, out int n))
            {
                baseName = m.Groups["base"].Value;
                next = n + 1;
            }

            while (true)
            {
                string candidate = $"{baseName} ({next})";

                if (!IsTaken(candidate, excluding))
                    return candidate;

                next++;
            }
        }

        private bool IsTaken(string name, Bookmark excluding)
        {
            foreach (var b in _items)
            {
                if (b == excluding)
                    continue;

                if (string.Equals(b.Name, name, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            return false;
        }
    }
}
