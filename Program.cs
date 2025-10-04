using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using HtmlAgilityPack;
using System.Text.Json;

namespace SWR701Tracker
{
    class Program
    {
        // === Configuration ===
        static readonly string BASE_URL = "https://www.realtimetrains.co.uk/search/handler";
        static readonly string SERVICE_URL = "https://www.realtimetrains.co.uk"; 
        static readonly string DISCORD_WEBHOOK_URL = Environment.GetEnvironmentVariable("DISCORD_WEBHOOK")
    ?? throw new InvalidOperationException("DISCORD_WEBHOOK not set.");

        static readonly Dictionary<string, string> HEADCODE_TO_LINE = new()
        {
            {"2U", "Windsor"}, {"1U", "Windsor"}, {"1J", "Hampton Court" }, {"2J", "Hampton Court"}, {"2H", "Shepperton"},
            {"1H", "Shepperton"}, {"2K", "Kingston Loop via Wimbledon"}, {"2O", "Kingston Loop via Richmond"},
            {"2C", "Reading"}, {"1C", "Reading"}, {"2V", "Hounslow Loop via Hounslow"}, {"2R", "Hounslow Loop via Richmond"},
            {"2S", "Weybridge"}, {"1S", "Weybridge"}, {"2G", "Guildford via Cobham"}, {"2D", "Guildford via Epsom"},
            {"2F", "Woking"}, {"2M", "Chessington South"}, {"1D", "Dorking"}
        };

        static readonly string[] ACTIVE_4585_UNITS = { "458529", "458530", "458533", "458535", "458536" };
        static readonly string[] ACTIVE_7015_UNITS = Enumerable.Range(501, 30).Select(i => $"701{i}").ToArray();

        static async Task Main(string[] args)
        {
            var handler = new HttpClientHandler { AllowAutoRedirect = false };
            using var client = new HttpClient(handler);

            var tasks701 = Enumerable.Range(1, 60)
                .Select(i => Check701Async(client, $"701{i:D3}")).ToArray();
            var results701 = await Task.WhenAll(tasks701);
            var nonNullableResults701 = results701
                .Select(r => (r.unit, r.status, r.headcode ?? string.Empty, r.identity ?? string.Empty, r.reversal ?? string.Empty, r.statusIndicator, r.statusColor))
                .ToArray();

            var seen = new HashSet<string>();
            var tasks458 = ACTIVE_4585_UNITS.Select(u => Check458Async(client, u, seen)).ToArray();
            var results458 = await Task.WhenAll(tasks458);

            var seen7015 = new HashSet<string>();
            var tasks7015 = ACTIVE_7015_UNITS.Select(u => Check458Async(client, u, seen7015)).ToArray();
            var results7015 = await Task.WhenAll(tasks7015);

            await NotifyDiscord(nonNullableResults701, results458, results7015);
        }

        static string ClassifyUnit(string? headcode)
        {
            if (!string.IsNullOrEmpty(headcode) && (headcode.StartsWith("5Q") || headcode.StartsWith("5T")))
                return "testing";
            return "in_service";
        }

        static string GetLineFromHeadcode(string headcode)
        {
            if (string.IsNullOrEmpty(headcode)) return "Depot";
            return HEADCODE_TO_LINE.TryGetValue(headcode[..2], out var line) ? line : "Other";
        }

        static string GetAnsiColor(string statusColor)
        {
            return statusColor switch
            {
                "LimeGreen" => "\u001b[32m",    // Green for on-time
                "DeepSkyBlue" => "\u001b[36m",  // Cyan for early
                "Orange" => "\u001b[33m",       // Yellow for minor delays
                "Red" => "\u001b[31m",          // Red for major delays
                "Gray" => "\u001b[37m",         // White for unknown
                _ => "\u001b[37m"               // Default to white
            };
        }

        static string ColorizeStatus(string statusIndicator, string statusColor)
        {
            if (string.IsNullOrEmpty(statusIndicator))
                return "";

            if (statusIndicator == "●")
            {
                // Use emoji circles instead of the bullet character
                return statusColor == "LimeGreen" ? " 🟢" : " ⚪";
            }

            var ansiColor = GetAnsiColor(statusColor);
            var resetColor = "\u001b[0m";
            return $" {ansiColor}{statusIndicator}{resetColor}";
        }

        // === 701s: one identity + possible reversal + status ===
        static async Task<(string unit, string status, string? headcode, string? identity, string? reversal, string statusIndicator, string statusColor)>
        Check701Async(HttpClient client, string unitNumber)

        {
            try
            {
                var url = $"{BASE_URL}?qsearch={unitNumber}&type=detailed";
                using var resp = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);
                if ((int)resp.StatusCode == 302 && resp.Headers.Location?.ToString().StartsWith("/service/") == true)
                {
                    var serviceUrl = SERVICE_URL + resp.Headers.Location.ToString();
                    var (headcode, identities, reversal, statusIndicator, statusColor) = await FetchHeadcodeAndIdentities(client, serviceUrl, is458: false);
                    var identity = identities.FirstOrDefault() ?? unitNumber;
                    var status = ClassifyUnit(headcode);
                    return (unitNumber, status, headcode, identity, reversal, statusIndicator, statusColor);
                }
                return (unitNumber, "not_running", null, null, null, "●", "Gray");
            }
            catch (Exception e)
            {
                Console.WriteLine($"701 {unitNumber}: Error - {e.Message}");
                return (unitNumber, "error", null, null, null, "●", "Gray");
            }
        }

        // === 458s: multiple identities + reversal + status ===
        static async Task<(string formation, string status, string headcode, string reversal, string statusIndicator, string statusColor)?> Check458Async(HttpClient client, string unitNumber, HashSet<string> seen)
        {
            if (seen.Contains(unitNumber)) return null;

            try
            {
                var url = $"{BASE_URL}?qsearch={unitNumber}&type=detailed";
                using var resp = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);
                if ((int)resp.StatusCode == 302 && resp.Headers.Location?.ToString().StartsWith("/service/") == true)
                {
                    var serviceUrl = SERVICE_URL + resp.Headers.Location.ToString();
                    var (headcode, identities, reversal, statusIndicator, statusColor) = await FetchHeadcodeAndIdentities(client, serviceUrl, is458: true);
                    var clean = SquashReversal(identities ?? new List<string>());
                    var formation = string.Join("+", clean);
                    var status = ClassifyUnit(headcode);
                    foreach (var id in identities ?? Enumerable.Empty<string>()) seen.Add(id);

                    // Ensure non-null values for headcode and reversal
                    return (formation, status, headcode ?? string.Empty, reversal ?? string.Empty, statusIndicator, statusColor);
                }
                return null;
            }
            catch (Exception e)
            {
                Console.WriteLine($"458 {unitNumber}: Error - {e.Message}");
                return null;
            }
        }

        // === Status determination logic ===
        static (string statusIndicator, string statusColor) DetermineUnitStatus(string pageHtml)
        {
            try
            {
                var doc = new HtmlDocument();
                doc.LoadHtml(pageHtml);

                // Parse the timetable to extract delay information
                var delays = new List<string>();
                var rows = doc.DocumentNode.SelectNodes("//div[contains(@class,'locationlist')]/div[contains(@class,'location')]");

                if (rows != null)
                {
                    foreach (var row in rows)
                    {
                        // Get delay information using the same logic as LiveStatusService
                        var delayNode = row.SelectSingleNode(".//div[contains(@class,'delay')]");
                        string delay = "";

                        if (delayNode != null && delayNode.GetClasses().Contains("nil"))
                            delay = "●";
                        else if (delayNode != null)
                        {
                            var span = delayNode.SelectSingleNode(".//span");
                            delay = span?.InnerText.Trim() ?? delayNode.InnerText.Trim();
                        }

                        delays.Add(delay);
                    }
                }

                // Apply the same status determination logic as LiveStatusPage
                var nonEmptyDelays = delays.Where(d => !string.IsNullOrWhiteSpace(d)).ToList();
                if (nonEmptyDelays.Count == 0)
                {
                    return ("●", "Gray");
                }

                string lastDelay = nonEmptyDelays.Last();

                if (lastDelay == "●")
                {
                    return ("●", "LimeGreen");
                }
                else if (lastDelay.StartsWith("-"))
                {
                    return (lastDelay, "DeepSkyBlue");
                }
                else if (lastDelay.StartsWith("+"))
                {
                    if (int.TryParse(lastDelay.TrimStart('+'), out int minsLate))
                    {
                        return (lastDelay, minsLate >= 5 ? "Red" : "Orange");
                    }
                    else
                    {
                        return (lastDelay, "Gray");
                    }
                }
                else
                {
                    return (lastDelay, "Gray");
                }
            }
            catch (Exception ex)
            {
                // Fallback to generic status if parsing fails
                Console.WriteLine($"Error determining status: {ex.Message}");
                return ("●", "Gray");
            }
        }

        // === Extract headcode, identities, reversal station, and status ===
        static async Task<(string? headcode, List<string> identities, string? reversal, string statusIndicator, string statusColor)>
        FetchHeadcodeAndIdentities(HttpClient client, string serviceUrl, bool is458)

        {
            try
            {
                var resp = await client.GetAsync(serviceUrl);
                var html = await resp.Content.ReadAsStringAsync();
                var doc = new HtmlDocument();
                doc.LoadHtml(html);

                var header = doc.DocumentNode.SelectSingleNode("//div[@class='header']");
                var headcode = header?.InnerText?.Trim()?.Split(' ').FirstOrDefault();

                List<string> identities = new();
                if (is458)
                {
                    identities = doc.DocumentNode
                        .SelectNodes("//div[@class='unit']//span[@class='identity']")
                        ?.Select(n => n.InnerText.Trim())
                        .ToList() ?? new List<string>();
                }
                else
                {
                    var span = doc.DocumentNode.SelectSingleNode("//div[@class='identity']//span[@class='identity']");
                    if (span != null) identities.Add(span.InnerText.Trim());
                }

                string? reversalStation = null;
                var addls = doc.DocumentNode.SelectNodes("//div[@class='addl']");
                if (addls != null)
                {
                    foreach (var addl in addls)
                    {
                        if (addl.InnerText.Contains("Service reverses here"))
                        {
                            var prevA = addl.SelectSingleNode("preceding-sibling::a[@class='name']");
                            if (prevA != null)
                            {
                                var raw = prevA.InnerText.Trim();
                                reversalStation = raw.Split(" [")[0];
                            }
                            break;
                        }
                    }
                }

                // Determine unit status from the HTML
                var (statusIndicator, statusColor) = DetermineUnitStatus(html);

                return (headcode, identities, reversalStation, statusIndicator, statusColor);
            }
            catch (Exception e)
            {
                Console.WriteLine($"Error fetching RTT data from {serviceUrl}: {e.Message}");
                return (null, new List<string>(), null, "●", "Gray");
            }
        }

        // === Helper to squash mirror reversals ===
        static List<string> SquashReversal(List<string> units)
        {
            int n = units.Count;
            if (n % 2 == 0)
            {
                int half = n / 2;
                if (Enumerable.SequenceEqual(units.Take(half), units.Skip(half).Reverse()))
                    return units.Take(half).ToList();
            }
            return units;
        }

        // === Format & send Discord message ===
        static async Task NotifyDiscord(
            (string unit, string status, string headcode, string identity, string reversal, string statusIndicator, string statusColor)[] results701,
            (string formation, string status, string headcode, string reversal, string statusIndicator, string statusColor)?[] results458,
            (string formation, string status, string headcode, string reversal, string statusIndicator, string statusColor)?[] results7015)
        {
            var inService701 = new Dictionary<string, List<string>>();
            var depot701 = new List<string>();
            var testing701 = new List<string>();
            var other701 = new List<string>();

            foreach (var (unit, status, headcode, identity, reversal, statusIndicator, statusColor) in results701)
            {
                var identityStr = identity ?? unit;
                var headcodeStr = !string.IsNullOrEmpty(headcode) ? $" ({headcode})" : "";
                var statusStr = ColorizeStatus(statusIndicator, statusColor);
                var part = $"{identityStr}{headcodeStr}{statusStr}";
                var label = string.IsNullOrEmpty(reversal) ? part : $"{part} – reverses at {reversal}";

                if (status == "in_service")
                {
                    var line = GetLineFromHeadcode(headcode);
                    if (line == "Depot") depot701.Add(label);
                    else if (line == "Other") other701.Add(label);
                    else
                    {
                        if (!inService701.ContainsKey(line)) inService701[line] = new List<string>();
                        inService701[line].Add(label);
                    }
                }
                else if (status == "testing")
                    testing701.Add(label);
            }

            var inService458 = new Dictionary<string, HashSet<string>>();
            var depot458 = new HashSet<string>();
            var seenForm = new HashSet<string>();
            foreach (var r in results458)
            {
                if (r == null) continue;
                var (formation, status, headcode, reversal, statusIndicator, statusColor) = r.Value;
                if (status == "in_service" && seenForm.Add(formation))
                {
                    var statusStr = ColorizeStatus(statusIndicator, statusColor);
                    var label = string.IsNullOrEmpty(reversal)
                        ? $"{formation} ({headcode}){statusStr}"
                        : $"{formation} ({headcode}){statusStr} – reverses at {reversal}";
                    var line = GetLineFromHeadcode(headcode);
                    if (line == "Depot")
                    {
                        depot458.Add(label);
                    }
                    else
                    {
                        if (!inService458.ContainsKey(line)) inService458[line] = new HashSet<string>();
                        inService458[line].Add(label);
                    }
                }
            }

            var now = DateTime.UtcNow.AddHours(1).ToString("yyyy-MM-dd HH:mm:ss");
            int total701 = inService701.Values.Sum(v => v.Count) + depot701.Count + testing701.Count + other701.Count;
            // Count individual units by splitting formations on '+'
            int total458 = inService458.Values.Sum(v => v.Sum(f => f.Split(' ')[0].Split('+').Length)) +
                          depot458.Sum(f => f.Split(' ')[0].Split('+').Length);

            var content = "```ansi\nSWR Fleet Report\n";
            content += $"701/0s: {total701}/60 active — {now}\n\n";

            if (inService701.Any())
            {
                content += $"🟢 In service ({inService701.Values.Sum(v => v.Count)}):\n";
                foreach (var (line, labels) in inService701.OrderBy(x => x.Key))
                {
                    content += $"{line} ({labels.Count}):\n";

                    var normals = labels.Where(l => !l.Contains("reverses at")).ToList();
                    var revs = labels.Where(l => l.Contains("reverses at")).ToList();

                    foreach (var normal in normals)
                        content += $"• {normal}\n";
                    foreach (var rev in revs)
                        content += $"• {rev}\n";

                    content += "\n"; // Line break between destinations
                }
                content += "\n";
            }
            if (depot701.Any())
            {
                content += $"🏠 Depot ({depot701.Count}):\n";
                foreach (var unit in depot701)
                    content += $"• {unit}\n";
                content += "\n";
            }
            if (testing701.Any())
            {
                content += $"🛠️ Testing ({testing701.Count}):\n";
                foreach (var unit in testing701)
                    content += $"• {unit}\n";
                content += "\n";
            }

            if (other701.Any())
            {
                content += $"❓ Other ({other701.Count}):\n";
                foreach (var unit in other701)
                    content += $"• {unit}\n";
                content += "\n";
            }

            var inService458Count = inService458.Values.Sum(v => v.Sum(f => f.Split(' ')[0].Split('+').Length));
            content += $"🚆 458/5s in service ({inService458Count}):\n";
            if (inService458.Any())
            {
                foreach (var (line, labels) in inService458.OrderBy(x => x.Key))
                {
                    var lineUnitCount = labels.Sum(f => f.Split(' ')[0].Split('+').Length);
                    content += $"{line} ({lineUnitCount}):\n";

                    var normals = labels.Where(l => !l.Contains("reverses at")).ToList();
                    var revs = labels.Where(l => l.Contains("reverses at")).ToList();

                    foreach (var normal in normals)
                        content += $"• {normal}\n";
                    foreach (var rev in revs)
                        content += $"• {rev}\n";

                    content += "\n"; // Line break between destinations
                }
                content += "\n";
            }
            else content += "None running today.\n";

            if (depot458.Any())
            {
                var depotUnitCount = depot458.Sum(f => f.Split(' ')[0].Split('+').Length);
                content += $"🏠 Depot ({depotUnitCount}):\n";
                foreach (var unit in depot458)
                    content += $"• {unit}\n";
                content += "\n";
            }

            // Process 701/5 results
            var inService7015 = new Dictionary<string, HashSet<string>>();
            var depot7015 = new HashSet<string>();
            var testing7015 = new HashSet<string>();
            var seenForm7015 = new HashSet<string>();
            foreach (var r in results7015)
            {
                if (r == null) continue;
                var (formation, status, headcode, reversal, statusIndicator, statusColor) = r.Value;
                if (seenForm7015.Add(formation))
                {
                    var statusStr = ColorizeStatus(statusIndicator, statusColor);
                    var label = string.IsNullOrEmpty(reversal)
                        ? $"{formation} ({headcode}){statusStr}"
                        : $"{formation} ({headcode}){statusStr} – reverses at {reversal}";

                    if (status == "in_service")
                    {
                        var line = GetLineFromHeadcode(headcode);
                        if (line == "Depot")
                        {
                            depot7015.Add(label);
                        }
                        else
                        {
                            if (!inService7015.ContainsKey(line)) inService7015[line] = new HashSet<string>();
                            inService7015[line].Add(label);
                        }
                    }
                    else if (status == "testing")
                    {
                        testing7015.Add(label);
                    }
                }
            }

            var inService7015Count = inService7015.Values.Sum(v => v.Sum(f => f.Split(' ')[0].Split('+').Length));
            content += $"🚊 701/5s in service ({inService7015Count}):\n";
            if (inService7015.Any())
            {
                foreach (var (line, labels) in inService7015.OrderBy(x => x.Key))
                {
                    var lineUnitCount = labels.Sum(f => f.Split(' ')[0].Split('+').Length);
                    content += $"{line} ({lineUnitCount}):\n";

                    var normals = labels.Where(l => !l.Contains("reverses at")).ToList();
                    var revs = labels.Where(l => l.Contains("reverses at")).ToList();

                    foreach (var normal in normals)
                        content += $"• {normal}\n";
                    foreach (var rev in revs)
                        content += $"• {rev}\n";

                    content += "\n"; // Line break between destinations
                }
                content += "\n";
            }
            else content += "None running today.\n";

            if (testing7015.Any())
            {
                var testingUnitCount = testing7015.Sum(f => f.Split(' ')[0].Split('+').Length);
                content += $"🛠️ Testing ({testingUnitCount}):\n";
                foreach (var unit in testing7015)
                    content += $"• {unit}\n";
                content += "\n";
            }

            if (depot7015.Any())
            {
                var depotUnitCount = depot7015.Sum(f => f.Split(' ')[0].Split('+').Length);
                content += $"🏠 Depot ({depotUnitCount}):\n";
                foreach (var unit in depot7015)
                    content += $"• {unit}\n";
                content += "\n";
            }

            content += "\nPowered by SWR Unit Tracker (Beta) v1.8.0\n```";

            Console.WriteLine("\n" + content);

            // Send to Discord
            using var client = new HttpClient();
            var payload = new { content };
            var json = JsonSerializer.Serialize(payload);
            var resp = await client.PostAsync(DISCORD_WEBHOOK_URL,
                new StringContent(json, System.Text.Encoding.UTF8, "application/json"));
            resp.EnsureSuccessStatusCode();
        }
    }
}
