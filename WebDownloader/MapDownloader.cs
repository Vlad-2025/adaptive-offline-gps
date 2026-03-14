using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;

namespace adaptive_offline_gps.WebDownloader
{
    public class MapDownloader
    {
        private readonly HttpClient _http = new();

        public async Task DownloadAsync(
            string url,
            string destPath,
            IProgress<(double percent, string speed, long bytesTotal)>? progress = null,
            CancellationToken ct  = default)
        {
            using var response = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);

            response.EnsureSuccessStatusCode();

            var total = response.Content.Headers.ContentLength ?? -1;
            var sw = Stopwatch.StartNew();
            long received = 0;

            await using var src = await response.Content.ReadAsStreamAsync(ct);
            await using var dest = File.OpenWrite(destPath);

            var buffer = new byte[81920];   //  80kb chunks
            int read;

            while((read = await src.ReadAsync(buffer, ct)) > 0)
            {
                await dest.WriteAsync(buffer.AsMemory(0, read), ct);

                received += read;

                if(total > 0)
                {
                    double percent = (double)received / total;

                    double mbps = received / 1024.0 / 1024.0 / sw.Elapsed.TotalSeconds;

                    progress?.Report((percent, $"{mbps:F1} MB/s", total));
                }
            }
        }

        public async Task<long> GetFileSizeAsync(string url)
        {
            using var req = new HttpRequestMessage(HttpMethod.Head, url);
            using var res = await _http.SendAsync(req);

            return res.Content.Headers.ContentLength ?? -1;
        }
    }
}
