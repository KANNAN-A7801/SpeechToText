using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using SpeechToText.Data;
using SpeechToText.Models;

namespace SpeechToText.Services;

public class GeminiLiveBridge(IConfiguration cfg, IServiceScopeFactory scopes, ILogger<GeminiLiveBridge> log)
{
    public async Task RunAsync(WebSocket browser, CancellationToken ct)
    {
        var transcript = new StringBuilder();
        var lastTextAt = DateTime.UtcNow;
        using var gemini = new ClientWebSocket();
        try
        {
            var apiKey = cfg["Gemini:ApiKey"];
            if (string.IsNullOrWhiteSpace(apiKey))
                apiKey = cfg["GEMINI_API_KEY"];
            var model = cfg["Gemini:Model"];
            if (string.IsNullOrWhiteSpace(apiKey) || apiKey.StartsWith("PUT_"))
                throw new Exception("Gemini API key is not set. Set Gemini__ApiKey in .env or environment variables.");

            var url = "wss://generativelanguage.googleapis.com/ws/google.ai.generativelanguage.v1beta.GenerativeService.BidiGenerateContent?key=" + apiKey;
            await gemini.ConnectAsync(new Uri(url), ct);
            log.LogInformation("Connected to Gemini, model {Model}", model);

            await SendJson(gemini, new
            {
                setup = new
                {
                    model = "models/" + model,
                    generationConfig = new { responseModalities = new[] { "AUDIO" } },
                    systemInstruction = new { parts = new[] { new { text =
                        "You are a silent transcriber for mixed Hindi and English speech. Never reply." } } },
                    inputAudioTranscription = new { }
                }
            }, ct);

            var setupDone = new TaskCompletionSource();

            var downstream = Task.Run(async () =>
            {
                var buf = new byte[64 * 1024];
                while (gemini.State == WebSocketState.Open)
                {
                    var msg = await ReceiveText(gemini, buf, ct);
                    if (msg is null)
                    {
                        var why = $"Gemini closed: {gemini.CloseStatus} {gemini.CloseStatusDescription}";
                        log.LogError(why);
                        setupDone.TrySetException(new Exception(why));
                        throw new Exception(why);
                    }
                    log.LogDebug("Gemini: {Msg}", msg.Length > 300 ? msg[..300] : msg);
                    using var doc = JsonDocument.Parse(msg);
                    var root = doc.RootElement;
                    if (root.TryGetProperty("setupComplete", out _)) { setupDone.TrySetResult(); await SendJson(browser, new { type = "ready" }, ct); }
                    if (root.TryGetProperty("serverContent", out var sc) &&
                        sc.TryGetProperty("inputTranscription", out var it) &&
                        it.TryGetProperty("text", out var t))
                    {
                        var text = t.GetString() ?? "";
                        transcript.Append(text);
                        lastTextAt = DateTime.UtcNow;
                        await SendJson(browser, new { type = "partial", text }, ct);
                    }
                }
            }, ct);

            var upstream = Task.Run(async () =>
            {
                await setupDone.Task; // do not send audio until Gemini accepted the setup
                var buf = new byte[16 * 1024];
                while (browser.State == WebSocketState.Open)
                {
                    var r = await browser.ReceiveAsync(buf, ct);
                    if (r.MessageType == WebSocketMessageType.Close) break;
                    if (r.MessageType == WebSocketMessageType.Text &&
                        Encoding.UTF8.GetString(buf, 0, r.Count) == "stop") break;
                    if (r.MessageType == WebSocketMessageType.Binary)
                        await SendJson(gemini, new
                        {
                            realtimeInput = new
                            {
                                audio = new { data = Convert.ToBase64String(buf, 0, r.Count), mimeType = "audio/pcm;rate=16000" }
                            }
                        }, ct);
                }
            }, ct);

            await Task.WhenAny(upstream, downstream);
            log.LogInformation("Stop received / session ended, waiting briefly for last transcription");
            if (downstream.IsFaulted) await downstream; // rethrow Gemini error
            // Tell Gemini the audio ended so it flushes the remaining transcription,
            // then wait until no new text has arrived for 2 s (max 6 s).
            try { await SendJson(gemini, new { realtimeInput = new { audioStreamEnd = true } }, CancellationToken.None); } catch { }
            var waitUntil = DateTime.UtcNow.AddSeconds(6);
            lastTextAt = DateTime.UtcNow;
            while (DateTime.UtcNow < waitUntil && (DateTime.UtcNow - lastTextAt).TotalSeconds < 2)
                await Task.Delay(200, CancellationToken.None);
            log.LogInformation("Transcript so far: {Len} characters", transcript.Length);
        }
        catch (Exception ex)
        {
            log.LogError(ex, "Transcription session failed");
            await TrySend(browser, new { type = "error", message = ex.Message });
        }

        // Save to MySQL. Any failure is reported to the page instead of hanging.
        var final = transcript.ToString().Trim();
        log.LogInformation("Saving transcript, {Len} characters", final.Length);
        try
        {
            if (final.Length > 0)
            {
                using var scope = scopes.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                var row = new Transcription { Text = final };
                db.Transcriptions.Add(row);
                await db.SaveChangesAsync();
                log.LogInformation("Saved transcription id {Id}", row.Id);
                await TrySend(browser, new { type = "saved", id = row.Id });
            }
            else
            {
                await TrySend(browser, new { type = "error", message = "No speech was transcribed, nothing to save." });
            }
        }
        catch (Exception ex)
        {
            log.LogError(ex, "Saving to MySQL failed");
            await TrySend(browser, new { type = "error", message = "Database save failed: " + (ex.InnerException?.Message ?? ex.Message) });
        }

        // Close both sockets, but never wait longer than 2 seconds.
        using var closeCts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        try { await gemini.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "", closeCts.Token); } catch { }
        try { await browser.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "", closeCts.Token); } catch { }
    }

    static async Task TrySend(WebSocket ws, object o)
    {
        try { if (ws.State == WebSocketState.Open) await SendJson(ws, o, CancellationToken.None); } catch { }
    }

    static Task SendJson(WebSocket ws, object o, CancellationToken ct) =>
        ws.SendAsync(JsonSerializer.SerializeToUtf8Bytes(o), WebSocketMessageType.Text, true, ct);

    static async Task<string?> ReceiveText(WebSocket ws, byte[] buf, CancellationToken ct)
    {
        using var ms = new MemoryStream();
        WebSocketReceiveResult r;
        do
        {
            r = await ws.ReceiveAsync(buf, ct);
            if (r.MessageType == WebSocketMessageType.Close) return null;
            ms.Write(buf, 0, r.Count);
        } while (!r.EndOfMessage);
        return Encoding.UTF8.GetString(ms.ToArray());
    }
}
