namespace adaptive_offline_gps.RoutingEngine.RoadGraphBuilding
{
    public struct CHEdge
    {
        public int Target;
        public float Weight;

        /// <summary>
        /// -1 = original edge (no shortcut).
        /// Otherwise, the node that was contracted to create this shortcut.
        /// Used to recursively unpack the path back to real road geometry.
        /// </summary>
        public int ShortcutMid;

        public CHEdge(int target, float weight, int shortcutMid)
        {
            Target = target;
            Weight = weight;
            ShortcutMid = shortcutMid;
        }
    }
}