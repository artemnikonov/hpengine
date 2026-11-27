#if IOS
// NOT ORIGINAL (iOS packaging, development builds only): downloads a missing game folder from the developer's Mac
// (hpengine/ios/content-server.sh) before the engine starts. Only present when the Xcode build phase put
// content-server.json into the app bundle; without it the app behaves as before (copy the game in through Files).
using System.Net.Security;
using System.Security.Cryptography;
using System.Text.Json;
using Foundation;
using SDL;
using static SDL.SDL3;

namespace HPEngine
{
    static class ContentDownload
    {
        sealed record Config(string[] urls, string token, string certSha256);

        sealed class Progress
        {
            public volatile string Status = "Looking for the content server";
            public long Done, Total;
            public volatile bool Finished;
            public Exception Error;
        }

        // games: (server folder name, destination install root). Downloads each one whose destination does not exist
        // yet; an existing folder (the player's own copy, even an incomplete one) is never touched.
        public static void EnsureGames(params (string Name, string Dest)[] games)
        {
            string configPath = Path.Combine(NSBundle.MainBundle.BundlePath, "content-server.json");
            var wanted = games.Where(g => !Directory.Exists(g.Dest)).ToArray();
            if (!File.Exists(configPath) || wanted.Length == 0) return;
            // JsonDocument, not JsonSerializer: no reflection-built types in the trimmed iOS app.
            using var json = JsonDocument.Parse(File.ReadAllText(configPath));
            var root = json.RootElement;
            var config = new Config(root.GetProperty("urls").EnumerateArray().Select(u => u.GetString()).ToArray(),
                root.GetProperty("token").GetString(), root.GetProperty("certSha256").GetString());

            var progress = new Progress();
            var work = Task.Run(() => Download(config, wanted, progress));
            ShowUntilDone(progress);
            work.Wait();
            if (progress.Error is not null) Console.Error.WriteLine("content download: " + progress.Error);
        }

        static async Task Download(Config config, (string Name, string Dest)[] games, Progress progress)
        {
            try
            {
                var handler = new SocketsHttpHandler
                {
                    ConnectTimeout = TimeSpan.FromSeconds(3),
                    SslOptions = new SslClientAuthenticationOptions
                    {
                        // Exactly the Mac's self-signed certificate (content-server.sh), no system trust.
                        RemoteCertificateValidationCallback = (_, cert, _, _) => cert is not null
                            && string.Equals(Convert.ToHexString(SHA256.HashData(cert.GetRawCertData())), config.certSha256,
                                StringComparison.OrdinalIgnoreCase),
                    },
                };
                using var http = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
                http.DefaultRequestHeaders.Authorization = new("Bearer", config.token);

                // The first connection on a device asks for Local Network access; keep trying while the player answers.
                string server = null;
                var manifests = new Dictionary<string, List<(long Size, string Path)>>();
                for (var deadline = DateTime.UtcNow.AddSeconds(90); server is null && DateTime.UtcNow < deadline;)
                {
                    foreach (string url in config.urls)
                    {
                        try
                        {
                            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                            foreach (var (name, _) in games)
                            {
                                using var response = await http.GetAsync($"{url}/{name}.manifest", cts.Token);
                                if (response.StatusCode == System.Net.HttpStatusCode.NotFound) continue;   // not served
                                response.EnsureSuccessStatusCode();
                                manifests[name] = ParseManifest(await response.Content.ReadAsStringAsync(cts.Token));
                            }
                            server = url;
                            break;
                        }
                        catch (Exception e) when (e is HttpRequestException or OperationCanceledException)
                        {
                            manifests.Clear();
                        }
                    }
                    if (server is null) await Task.Delay(2000);
                }
                if (server is null) throw new IOException("no content server answered: " + string.Join(", ", config.urls));

                progress.Total = manifests.Values.Sum(m => m.Sum(f => f.Size));
                foreach (var (name, dest) in games)
                {
                    if (!manifests.TryGetValue(name, out var files)) continue;
                    // Staged beside the destination and moved into place whole, so an interrupted download never looks
                    // like an install; files already complete in the staging folder are kept (resume).
                    string staging = dest + ".download";
                    foreach (var (size, path) in files)
                    {
                        progress.Status = $"Downloading {name.ToUpperInvariant()}: {path}";
                        string target = Path.Combine(staging, path);
                        if (File.Exists(target) && new FileInfo(target).Length == size)
                        {
                            Interlocked.Add(ref progress.Done, size);
                            continue;
                        }
                        Directory.CreateDirectory(Path.GetDirectoryName(target));
                        string url = $"{server}/{name}/" + string.Join('/', path.Split('/').Select(Uri.EscapeDataString));
                        using (var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead))
                        {
                            response.EnsureSuccessStatusCode();
                            await using var source = await response.Content.ReadAsStreamAsync();
                            await using var file = File.Create(target + ".part");
                            var buffer = new byte[1 << 16];
                            for (int n; (n = await source.ReadAsync(buffer)) > 0;)
                            {
                                await file.WriteAsync(buffer.AsMemory(0, n));
                                Interlocked.Add(ref progress.Done, n);
                            }
                        }
                        if (new FileInfo(target + ".part").Length != size) throw new IOException($"{name}/{path}: wrong size");
                        File.Move(target + ".part", target, overwrite: true);
                    }
                    Directory.CreateDirectory(Path.GetDirectoryName(dest));
                    Directory.Move(staging, dest);
                }
            }
            catch (Exception e) { progress.Error = e; }
            finally { progress.Finished = true; }
        }

        static List<(long, string)> ParseManifest(string text)
        {
            var files = new List<(long, string)>();
            foreach (string line in text.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                int tab = line.IndexOf('\t');
                if (tab <= 0 || !long.TryParse(line.AsSpan(0, tab), out long size)) throw new InvalidDataException("manifest line: " + line);
                string path = line[(tab + 1)..];
                if (path.StartsWith('/') || path.Split('/').Contains("..")) throw new InvalidDataException("manifest path: " + path);
                files.Add((size, path));
            }
            return files;
        }

        // A plain progress screen (SDL renderer + its debug font) on the main thread while the download runs.
        static unsafe void ShowUntilDone(Progress progress)
        {
            SDL_Window* window = null;
            SDL_Renderer* renderer = null;
            if (SDL_Init(SDL_InitFlags.SDL_INIT_VIDEO))
            {
                window = SDL_CreateWindow("HPEngine", 0, 0, SDL_WindowFlags.SDL_WINDOW_FULLSCREEN | SDL_WindowFlags.SDL_WINDOW_HIGH_PIXEL_DENSITY);
                if (window != null) renderer = SDL_CreateRenderer(window, (byte*)null);
            }
            while (!progress.Finished)
            {
                SDL_Event e;
                while (SDL_PollEvent(&e)) { }
                if (renderer != null)
                {
                    int w, h;
                    SDL_GetCurrentRenderOutputSize(renderer, &w, &h);
                    float scale = Math.Max(1, h / 240);
                    SDL_SetRenderScale(renderer, scale, scale);
                    float vw = w / scale, vh = h / scale;
                    SDL_SetRenderDrawColor(renderer, 0, 0, 0, 255);
                    SDL_RenderClear(renderer);
                    SDL_SetRenderDrawColor(renderer, 255, 255, 255, 255);
                    long total = progress.Total, done = Interlocked.Read(ref progress.Done);
                    string status = progress.Status;
                    int maxChars = (int)(vw - 32) / 8;
                    if (status.Length > maxChars) status = status[..maxChars];
                    SDL_RenderDebugText(renderer, 16, vh / 2 - 24, status);
                    if (total > 0)
                    {
                        SDL_RenderDebugText(renderer, 16, vh / 2 + 16, $"{done >> 20} / {total >> 20} MB");
                        var frame = new SDL_FRect { x = 16, y = vh / 2 - 8, w = vw - 32, h = 12 };
                        SDL_RenderRect(renderer, &frame);
                        var bar = new SDL_FRect { x = 18, y = vh / 2 - 6, w = (vw - 36) * done / total, h = 8 };
                        SDL_RenderFillRect(renderer, &bar);
                    }
                    SDL_RenderPresent(renderer);
                }
                SDL_Delay(50);
            }
            if (renderer != null) SDL_DestroyRenderer(renderer);
            if (window != null) SDL_DestroyWindow(window);
            SDL_PumpEvents();
        }
    }
}
#endif
