using Microsoft.EntityFrameworkCore;
using SpeechToText.Data;
using SpeechToText.Models;
using SpeechToText.Services;

LoadDotEnv(
    Path.Combine(Directory.GetCurrentDirectory(), ".env"),
    Path.Combine(AppContext.BaseDirectory, ".env"));

var builder = WebApplication.CreateBuilder(args);
// Real secrets go in .env or appsettings.Local.json (both git-ignored). They override appsettings.json.
builder.Configuration.AddJsonFile("appsettings.Local.json", optional: true, reloadOnChange: true);
var cs = builder.Configuration.GetConnectionString("Default")!;
builder.Services.AddDbContext<AppDbContext>(o => o.UseMySql(cs, ServerVersion.AutoDetect(cs)));
builder.Services.AddSingleton<GeminiLiveBridge>();

var app = builder.Build();

// Create the database/table automatically on first run.
using (var scope = app.Services.CreateScope())
    scope.ServiceProvider.GetRequiredService<AppDbContext>().Database.EnsureCreated();

app.UseDefaultFiles();
app.UseStaticFiles();
app.UseWebSockets();

// ---- Real-time transcription endpoint ----
app.Map("/ws/transcribe", async (HttpContext ctx, GeminiLiveBridge bridge) =>
{
    if (!ctx.WebSockets.IsWebSocketRequest) { ctx.Response.StatusCode = 400; return; }
    using var ws = await ctx.WebSockets.AcceptWebSocketAsync();
    await bridge.RunAsync(ws, ctx.RequestAborted);
});

// ---- CRUD for transcription history ----
var api = app.MapGroup("/api/transcriptions");

api.MapGet("/", async (AppDbContext db) =>
    await db.Transcriptions.OrderByDescending(t => t.CreatedAt).ToListAsync());                      // Read all

api.MapGet("/{id:int}", async (int id, AppDbContext db) =>
    await db.Transcriptions.FindAsync(id) is { } t ? Results.Ok(t) : Results.NotFound());            // Read one

api.MapPost("/", async (Transcription input, AppDbContext db) =>                                      // Create
{
    input.Id = 0; input.CreatedAt = DateTime.UtcNow;
    db.Transcriptions.Add(input);
    await db.SaveChangesAsync();
    return Results.Created($"/api/transcriptions/{input.Id}", input);
});

api.MapPut("/{id:int}", async (int id, Transcription input, AppDbContext db) =>                       // Update
{
    var t = await db.Transcriptions.FindAsync(id);
    if (t is null) return Results.NotFound();
    t.Text = input.Text; t.UpdatedAt = DateTime.UtcNow;
    await db.SaveChangesAsync();
    return Results.Ok(t);
});

api.MapDelete("/{id:int}", async (int id, AppDbContext db) =>                                         // Delete
{
    var t = await db.Transcriptions.FindAsync(id);
    if (t is null) return Results.NotFound();
    db.Transcriptions.Remove(t);
    await db.SaveChangesAsync();
    return Results.NoContent();
});

app.Run();

/// <summary>Loads key=value pairs from the first existing .env file into process environment variables.</summary>
static void LoadDotEnv(params string[] candidatePaths)
{
    var envPath = candidatePaths.FirstOrDefault(File.Exists);
    if (envPath is null) return;

    foreach (var rawLine in File.ReadAllLines(envPath))
    {
        var line = rawLine.Trim();
        if (line.Length == 0 || line.StartsWith('#')) continue;
        if (line.StartsWith("export ", StringComparison.Ordinal)) line = line[7..].TrimStart();

        var sep = line.IndexOf('=');
        if (sep <= 0) continue;

        var key = line[..sep].Trim();
        var value = line[(sep + 1)..].Trim();
        if (value.Length >= 2 && ((value[0] == '"' && value[^1] == '"') || (value[0] == '\'' && value[^1] == '\'')))
            value = value[1..^1];

        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable(key)))
            Environment.SetEnvironmentVariable(key, value);
    }
}
