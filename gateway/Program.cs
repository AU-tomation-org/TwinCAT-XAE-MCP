using System.IO.Pipes;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

// Local dashboard of the XAE MCP: read-only pages over the daemon's pipe, loopback only.
// It is a process of its own because ASP.NET Core does not run on the daemon's framework.
// This is also where authentication will live: the daemon's IAccessPolicy decides, and the
// operator identity established here is what it will be given.

string pipe = Environment.GetEnvironmentVariable("TE1000_DAEMON_PIPE") ?? "te1000-mcp";
int preferredPort = 5990;
for (int i = 0; i < args.Length - 1; i++)
{
    if (args[i] == "--pipe") pipe = args[i + 1];
    if (args[i] == "--port") preferredPort = int.Parse(args[i + 1]);
}
string progId = Environment.GetEnvironmentVariable("TE1000_PROGID") ?? "VisualStudio.DTE.17.0";

// One gateway per pipe, whoever starts it: the daemon does at every launch.
using var single = new Mutex(true, @"Global\XaeGateway_" + pipe, out bool first);
if (!first) return;

int port = FindFreePort(preferredPort);
string url = $"http://127.0.0.1:{port}";
var startedAt = DateTimeOffset.UtcNow;

var builder = WebApplication.CreateBuilder(args);
builder.Logging.ClearProviders();
builder.WebHost.UseUrls(url);
var app = builder.Build();

string presence = WritePresence(url, pipe);
app.Lifetime.ApplicationStopping.Register(() => { try { File.Delete(presence); } catch { } });

app.Use(async (ctx, next) =>
{
    if (ctx.Request.Host.Host is not ("127.0.0.1" or "localhost")) { ctx.Response.StatusCode = 403; return; }
    await next();
});

app.MapGet("/health", () => Results.Json(new
{
    name = "xae-gateway",
    status = "ok",
    version = typeof(Program).Assembly.GetName().Version?.ToString(),
    startedAt,
    pid = Environment.ProcessId,
    pipe,
    authentication = "none",
}));
app.MapGet("/api/state", async () => Results.Text(await Daemon("daemon_snapshot", new JsonObject { ["progId"] = progId }), "application/json"));
app.MapGet("/api/audit", async (int? max) => Results.Text(await Daemon("audit_query", new JsonObject { ["max"] = max ?? 300 }), "application/json"));
app.MapGet("/api/tools", async () => Results.Text(await Daemon("list_actions", new JsonObject()), "application/json"));
app.MapGet("/", () => Page("index.html", "text/html; charset=utf-8"));
app.MapGet("/aut.css", () => Page("aut.css", "text/css; charset=utf-8"));
app.MapGet("/logo.svg", () => Page("logo.svg", "image/svg+xml"));
app.Run();

IResult Page(string file, string type) => Results.Bytes(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "wwwroot", file)), type);

// A connection per request: the daemon answers each line on its own thread, so a poll never
// queues behind a long build, and a daemon that is gone is an answer, not a stuck page.
async Task<string> Daemon(string action, JsonObject payload)
{
    try
    {
        using var client = new NamedPipeClientStream(".", pipe, PipeDirection.InOut, PipeOptions.Asynchronous);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        await client.ConnectAsync(timeout.Token);
        var request = new JsonObject
        {
            ["id"] = "1",
            ["action"] = action,
            ["payload"] = payload,
            ["client"] = new JsonObject { ["kind"] = "gateway", ["pid"] = Environment.ProcessId },
        };
        byte[] line = Encoding.UTF8.GetBytes(request.ToJsonString() + "\n");
        await client.WriteAsync(line, timeout.Token);
        await client.FlushAsync(timeout.Token);
        using var reader = new StreamReader(client, new UTF8Encoding(false));
        return await reader.ReadLineAsync(timeout.Token) ?? Failure("empty reply");
    }
    catch (Exception ex) { return Failure(ex.Message); }
}

static string Failure(string message) =>
    new JsonObject { ["ok"] = false, ["error"] = "daemon not reachable: " + message }.ToJsonString();

static int FindFreePort(int from)
{
    for (int port = from; port < from + 20; port++)
    {
        try
        {
            var probe = new TcpListener(System.Net.IPAddress.Loopback, port);
            probe.Start();
            probe.Stop();
            return port;
        }
        catch (SocketException) { }
    }
    throw new InvalidOperationException("no free port from " + from);
}

// The console cannot start an MCP server, it can only find it: instances announce themselves.
static string WritePresence(string url, string pipe)
{
    string folder = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AUT", "presence");
    Directory.CreateDirectory(folder);
    string file = Path.Combine(folder, $"xae-gateway-{Environment.ProcessId}.json");
    File.WriteAllText(file, JsonSerializer.Serialize(new
    {
        service = "xae-gateway",
        title = "TwinCAT XAE MCP",
        url,
        pid = Environment.ProcessId,
        pipe,
    }));
    return file;
}
