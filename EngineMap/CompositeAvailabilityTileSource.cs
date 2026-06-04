using BruTile;
using BruTile.Web;
using System;
using System.Diagnostics;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace adaptive_offline_gps.EngineMap
{
    public class CompositeAvailabilityTileSource : IHttpTileSource
    {
        public enum SourceState { Primary, Secondary }

        private readonly HttpTileSource _primary;
        private readonly HttpTileSource _secondary;
        private volatile SourceState _state = SourceState.Primary;

        public event Action<SourceState>? StateChanged;
        public SourceState CurrentState => _state;

        public ITileSchema Schema => _primary.Schema;
        public string Name => "CompositeAvailability";
        public Attribution Attribution => _primary.Attribution;

        public CompositeAvailabilityTileSource(HttpTileSource primary, HttpTileSource secondary)
        {
            _primary = primary;
            _secondary = secondary;
        }

        public async Task<byte[]?> GetTileAsync(HttpClient httpClient, TileInfo tileInfo, CancellationToken? cancellation = null)
        {
            // Try primary. Treat both exceptions AND null/empty returns as failure.
            try
            {
                var bytes = await _primary.GetTileAsync(httpClient, tileInfo, cancellation);
                if (bytes != null && bytes.Length > 0)
                {
                    SetState(SourceState.Primary);
                    return bytes;
                }
                Debug.WriteLine($"TileSource: primary returned null/empty for {tileInfo.Index.Level}/{tileInfo.Index.Col}/{tileInfo.Index.Row}, trying secondary");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"TileSource: primary threw ({ex.GetType().Name}: {ex.Message}), trying secondary");
            }

            // Fall through to secondary
            SetState(SourceState.Secondary);
            return await _secondary.GetTileAsync(httpClient, tileInfo, cancellation);
        }

        private void SetState(SourceState s)
        {
            if (_state == s) return;
            _state = s;
            StateChanged?.Invoke(s);
        }
    }
}