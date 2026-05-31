namespace adaptive_offline_gps.RoutingEngine.Bookmarks
{
    /// <summary>
    /// A user-saved location on the map. Stored as lat/lon so it survives
    /// graph rebuilds; the nearest junction is resolved at use time via the
    /// NodeGridIndex rather than baked in.
    /// </summary>
    public sealed class Bookmark
    {
        public string Name { get; set; } = "";
        public double Lat { get; set; }
        public double Lon { get; set; }
    }
}
