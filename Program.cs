// Rust Store Discord Webhook
//
// Build (developer, once):
//   dotnet publish -c Release
//   Output: bin\Release\net8.0\win-x64\publish\ruststore-webhook.exe
//
// End-user setup (run once, no install needed):
//   ruststore-webhook.exe --setup
//
// The exe runs on the configured schedule after --setup.
// Safe to run any time — skips if already ran this week on the configured day.

using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using HtmlAgilityPack;

// ── Paths (resolved relative to the exe so Task Scheduler works from any cwd) ──
var baseDir   = AppContext.BaseDirectory;
var stateFile = Path.Combine(baseDir, "last_run.txt");
var logFile   = Path.Combine(baseDir, "ruststore.log");
var envFile   = Path.Combine(baseDir, ".env");

// ── Constants ─────────────────────────────────────────────────────────────────
const int    RustAppId        = 252490;
const string SteamStoreUrl    = "https://store.steampowered.com/itemstore/252490/";
const int    EmbedColor       = 0xCE422A;
const int    EmbedsPerMessage = 10;

// ── Logging ───────────────────────────────────────────────────────────────────
void Log(string msg, string level = "INFO")
{
    var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} [{level}] {msg}";
    Console.WriteLine(line);
    File.AppendAllText(logFile, line + Environment.NewLine, Encoding.UTF8);
}

// ── .env loader ───────────────────────────────────────────────────────────────
Dictionary<string, string> LoadEnv()
{
    var env = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    if (!File.Exists(envFile)) return env;
    foreach (var line in File.ReadAllLines(envFile))
    {
        var trimmed = line.Trim();
        if (trimmed.StartsWith('#') || !trimmed.Contains('=')) continue;
        var idx = trimmed.IndexOf('=');
        env[trimmed[..idx].Trim()] = trimmed[(idx + 1)..].Trim();
    }
    return env;
}

var config = LoadEnv();

// ── Schedule config ───────────────────────────────────────────────────────────
// SEND_DAY  : MON | TUE | WED | THU | FRI | SAT | SUN  (default: THU)
// SEND_TIME : HH:MM in 24-hour format                   (default: 22:00)

static DayOfWeek ParseSendDay(string? raw) => raw?.ToUpperInvariant() switch
{
    "MON" => DayOfWeek.Monday,
    "TUE" => DayOfWeek.Tuesday,
    "WED" => DayOfWeek.Wednesday,
    "THU" => DayOfWeek.Thursday,
    "FRI" => DayOfWeek.Friday,
    "SAT" => DayOfWeek.Saturday,
    "SUN" => DayOfWeek.Sunday,
    _     => DayOfWeek.Thursday   // default
};

config.TryGetValue("SEND_DAY",  out var rawDay);
config.TryGetValue("SEND_TIME", out var rawTime);

var sendDay  = ParseSendDay(rawDay);
var sendTime = string.IsNullOrWhiteSpace(rawTime) ? "22:00" : rawTime.Trim();

// ── Schedule guard ────────────────────────────────────────────────────────────
// Returns the date of the most recent occurrence of sendDay (today if today matches).
// Handles "missed day" automatically: if the machine was off and turns on later,
// GetLastOccurrence() returns the missed date and AlreadyRan() will be false → the job fires.
DateTime GetLastOccurrence()
{
    var today    = DateTime.Today;
    int daysBack = ((int)today.DayOfWeek - (int)sendDay + 7) % 7;
    return today.AddDays(-daysBack);
}

bool AlreadyRan()
{
    if (!File.Exists(stateFile)) return false;
    return File.ReadAllText(stateFile).Trim() == GetLastOccurrence().ToString("yyyy-MM-dd");
}

void MarkRan() =>
    File.WriteAllText(stateFile, GetLastOccurrence().ToString("yyyy-MM-dd"), Encoding.UTF8);

// ── --setup mode ──────────────────────────────────────────────────────────────
if (args.Contains("--setup"))
{
    var exePath = Environment.ProcessPath
        ?? Process.GetCurrentProcess().MainModule?.FileName
        ?? throw new InvalidOperationException("Cannot determine exe path.");

    // Map DayOfWeek to schtasks /d abbreviation
    var dayAbbrev = sendDay switch
    {
        DayOfWeek.Monday    => "MON",
        DayOfWeek.Tuesday   => "TUE",
        DayOfWeek.Wednesday => "WED",
        DayOfWeek.Thursday  => "THU",
        DayOfWeek.Friday    => "FRI",
        DayOfWeek.Saturday  => "SAT",
        DayOfWeek.Sunday    => "SUN",
        _                   => "THU"
    };

    Console.WriteLine($"Registering scheduled tasks (day: {dayAbbrev}, time: {sendTime})...");
    Console.WriteLine();

    // Weekly: every configured day at the configured time.
    // /f overwrites if the task already exists.
    Schtasks($"/create /tn \"RustStoreWebhook\" " +
             $"/tr \"\\\"{exePath}\\\"\" " +
             $"/sc weekly /d {dayAbbrev} /st {sendTime} /f",
             $"Weekly {dayAbbrev} at {sendTime}");

    // At logon: catches the case where the machine was off on the scheduled day.
    // The exe's own AlreadyRan() check prevents double-posting.
    Schtasks($"/create /tn \"RustStoreWebhook_Startup\" " +
             $"/tr \"\\\"{exePath}\\\"\" " +
             $"/sc onlogon /delay 0001:00 /f",
             "At every logon (missed-day catch-up)");

    Console.WriteLine();
    Console.WriteLine($"Done. The webhook will run every {dayAbbrev} at {sendTime}.");
    Console.WriteLine("If the machine is off on that day, it runs at the next login.");
    return;

    static void Schtasks(string arguments, string label)
    {
        var psi = new ProcessStartInfo("schtasks.exe", arguments)
        {
            RedirectStandardOutput = true,
            RedirectStandardError  = true,
            UseShellExecute        = false,
        };
        using var p = Process.Start(psi)!;
        p.WaitForExit();
        var status = p.ExitCode == 0 ? "OK " : "ERR";
        var detail = p.ExitCode != 0 ? $" — {p.StandardError.ReadToEnd().Trim()}" : "";
        Console.WriteLine($"  [{status}] {label}{detail}");
    }
}

// ── Main run ──────────────────────────────────────────────────────────────────
Log("=== Rust Store Webhook — starting ===");

// Load webhook URL from .env
config.TryGetValue("DISCORD_WEBHOOK_URL", out var webhookUrl);

if (string.IsNullOrWhiteSpace(webhookUrl) || webhookUrl.Contains("YOUR_"))
{
    Log("DISCORD_WEBHOOK_URL is not configured in .env", "ERROR");
    return;
}

if (AlreadyRan())
{
    Log($"Already ran for this {sendDay} ({GetLastOccurrence():yyyy-MM-dd}). Nothing to do.");
    return;
}

Log("Fetching items from Steam store...");
var items = await FetchItemsAsync();

if (items.Count == 0)
{
    Log("No items fetched. Aborting.", "ERROR");
    return;
}

Log($"Fetched {items.Count} items. Sending to Discord...");
await SendStoreUpdateAsync(items, webhookUrl);

MarkRan();
Log("=== Done ===");

// ── Steam fetcher ─────────────────────────────────────────────────────────────
async Task<List<StoreItem>> FetchItemsAsync()
{
    var handler = new HttpClientHandler { UseCookies = true };
    var steamUri = new Uri("https://store.steampowered.com");
    // Age-verification cookies — prevents Steam returning an HTML gate page instead of JSON
    handler.CookieContainer.Add(steamUri, new Cookie("birthtime",       "283996801", "/", "store.steampowered.com"));
    handler.CookieContainer.Add(steamUri, new Cookie("lastagecheckage", "1-0-1990",  "/", "store.steampowered.com"));
    handler.CookieContainer.Add(steamUri, new Cookie("mature_content",  "1",         "/", "store.steampowered.com"));

    using var http = new HttpClient(handler);
    http.DefaultRequestHeaders.Add("User-Agent",
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 " +
        "(KHTML, like Gecko) Chrome/124.0.0.0 Safari/537.36");
    http.DefaultRequestHeaders.Add("Accept-Language", "en-US,en;q=0.9");
    http.Timeout = TimeSpan.FromSeconds(30);

    // Step 1: GET main page to establish session cookies
    try
    {
        Log("Fetching Steam store page for session...");
        (await http.GetAsync(SteamStoreUrl)).EnsureSuccessStatusCode();
    }
    catch (Exception ex)
    {
        Log($"Failed to load Steam store page: {ex.Message}", "ERROR");
        return [];
    }

    // Parse items directly from the main store page
    Log("Parsing store page...");
    try
    {
        var html  = await http.GetStringAsync(SteamStoreUrl);
        var items = ParseItems(html);
        Log($"Parsed {items.Count} items.");
        return items;
    }
    catch (Exception ex)
    {
        Log($"Failed to parse store page: {ex.Message}", "ERROR");
        return [];
    }
}

List<StoreItem> ParseItems(string html)
{
    var doc = new HtmlDocument();
    doc.LoadHtml(html);

    var nodes = doc.DocumentNode.SelectNodes(
        "//a[contains(@class,'item_def_grid_item')]" +
        " | //div[contains(@class,'item_def_grid_item')]" +
        " | //a[@data-item-def-id]");

    if (nodes is null)
    {
        Log("No item elements found in HTML — Steam may have changed their markup.", "WARN");
        return [];
    }

    var items = new List<StoreItem>();
    foreach (var node in nodes)
    {
        try
        {
            var nameNode = node.SelectSingleNode(
                ".//*[contains(@class,'item_def_name')" +
                " or contains(@class,'itemstore_item_name')" +
                " or contains(@class,'name')]");
            var name = nameNode is not null
                ? HtmlEntity.DeEntitize(nameNode.InnerText.Trim())
                : node.GetAttributeValue("data-item-def-id", "Unknown");

            var priceNode = node.SelectSingleNode(
                ".//*[contains(@class,'price')" +
                " or contains(@class,'item_def_price')" +
                " or contains(@class,'itemstore_item_price')]");
            var price = priceNode is not null
                ? HtmlEntity.DeEntitize(priceNode.InnerText.Trim())
                : "Free";
            if (string.IsNullOrWhiteSpace(price)) price = "Free";

            var imgNode   = node.SelectSingleNode(".//img");
            var imageUrl  = imgNode?.GetAttributeValue("src", null)
                         ?? imgNode?.GetAttributeValue("data-src", "")
                         ?? "";

            var href      = node.GetAttributeValue("href", "");
            var itemDefId = node.GetAttributeValue("data-item-def-id", "");
            var url = href.StartsWith("http")          ? href
                    : !string.IsNullOrEmpty(href)       ? $"https://store.steampowered.com{href}"
                    : !string.IsNullOrEmpty(itemDefId)  ? $"https://store.steampowered.com/itemstore/{RustAppId}/detail/{itemDefId}/"
                    : SteamStoreUrl;

            items.Add(new StoreItem(name, price, imageUrl, url));
        }
        catch { /* skip malformed item */ }
    }

    return items;
}

// ── Discord sender ────────────────────────────────────────────────────────────
async Task SendStoreUpdateAsync(List<StoreItem> items, string webhookUrl)
{
    using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };

    // Header embed
    Log("Sending header message...");
    var headerEmbed = new JsonObject
    {
        ["title"]       = "Rust Store — Weekly Items",
        ["url"]         = SteamStoreUrl,
        ["description"] = $"Here are the **{items.Count}** items currently available in the Rust item store.\nUpdated: {DateTime.UtcNow:yyyy-MM-dd}",
        ["color"]       = EmbedColor,
        ["thumbnail"]   = new JsonObject { ["url"] = "https://store.steampowered.com/public/images/v6/acct/acct_billing_rust.jpg" }
    };
    await PostWebhookAsync(http, webhookUrl, new JsonObject { ["embeds"] = new JsonArray(headerEmbed) });
    await Task.Delay(500);

    // Build item embeds — thumbnail only added when an image URL exists (no null fields)
    var allEmbeds = items.Select(item =>
    {
        var embed = new JsonObject
        {
            ["title"]  = item.Name,
            ["color"]  = EmbedColor,
            ["fields"] = new JsonArray(new JsonObject
            {
                ["name"]   = "Price",
                ["value"]  = string.IsNullOrWhiteSpace(item.Price) ? "Free" : item.Price,
                ["inline"] = true
            })
        };
        if (!string.IsNullOrEmpty(item.ImageUrl))
            embed["thumbnail"] = new JsonObject { ["url"] = item.ImageUrl };
        // Only add url when it's item-specific — Discord deduplicates embeds that share the same url
        if (item.Url != SteamStoreUrl)
            embed["url"] = item.Url;
        return embed;
    }).ToList();

    for (int i = 0; i < allEmbeds.Count; i += EmbedsPerMessage)
    {
        var batch = allEmbeds.Skip(i).Take(EmbedsPerMessage).Select(e => (JsonNode)e).ToArray();
        Log($"Sending items {i + 1}–{Math.Min(i + EmbedsPerMessage, allEmbeds.Count)} of {allEmbeds.Count}...");
        await PostWebhookAsync(http, webhookUrl, new JsonObject { ["embeds"] = new JsonArray(batch) });
        await Task.Delay(500);
    }

    Log("All messages sent to Discord.");
}

async Task PostWebhookAsync(HttpClient http, string webhookUrl, JsonObject payload)
{
    var json    = payload.ToJsonString();
    var content = new StringContent(json, Encoding.UTF8, "application/json");
    var resp    = await http.PostAsync(webhookUrl, content);

    if ((int)resp.StatusCode == 429)
    {
        var body       = await resp.Content.ReadAsStringAsync();
        var retryAfter = (double)(JsonNode.Parse(body)?["retry_after"] ?? 1.0);
        Log($"Rate limited. Waiting {retryAfter}s...", "WARN");
        await Task.Delay(TimeSpan.FromSeconds(retryAfter));
        resp = await http.PostAsync(webhookUrl, new StringContent(json, Encoding.UTF8, "application/json"));
    }

    resp.EnsureSuccessStatusCode();
}

// ── Types ─────────────────────────────────────────────────────────────────────
record StoreItem(string Name, string Price, string ImageUrl, string Url);
