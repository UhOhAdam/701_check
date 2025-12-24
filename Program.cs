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
            {"2U", "Windsor"}, {"1U", "Windsor"},
            {"2J", "Hampton Court"},  // 1J is Lymington Shuttle (mainline), not suburban
            {"2H", "Shepperton"}, {"1H", "Shepperton"},
            {"2K", "Kingston Loop via Wimbledon"}, {"2O", "Kingston Loop via Richmond"},
            {"2C", "Reading"}, {"1C", "Reading"},
            {"2V", "Hounslow Loop via Hounslow"}, {"2R", "Hounslow Loop via Richmond"},
            {"2S", "Weybridge"}, {"1S", "Weybridge"},
            {"2G", "Guildford via Cobham"}, {"2D", "Guildford via Epsom"},
            {"2F", "Woking"}, {"1F", "Woking"},
            {"2M", "Chessington South"}, {"1M", "Chessington South"},
            {"1N", "Aldershot via Richmond"}, {"1D", "Dorking"}
        };

        static readonly string[] ACTIVE_4585_UNITS = { "458529", "458530", "458533", "458535" };
        static readonly string[] ACTIVE_7015_UNITS = Enumerable.Range(501, 30).Select(i => $"701{i}").ToArray();

        // Full 455 fleet to monitor (19 units)
        static readonly string[] ACTIVE_455_UNITS = {
            // 455/7 (14 units)
            "455701", "455709", "455710", "455712", "455716", "455717", "455719", "455720",
            "455721", "455727", "455729", "455732", "455734", "455737",
            // 455/8 (5 units)
            "455863", "455869", "455870", "455871", "455873"
        };

        // Known baseline units for dynamic total (12 units)
        static readonly HashSet<string> KNOWN_455_UNITS = new()
        {
            "455719", "455729", "455721", "455732", "455712", "455717",
            "455727", "455871", "455716", "455870", "455873", "455737"
        };

        static async Task Main(string[] args)
        {
            var handler = new HttpClientHandler { AllowAutoRedirect = false };
            using var client = new HttpClient(handler);

            var tasks701 = Enumerable.Range(1, 60)
                .Select(i => Check701Async(client, $"701{i:D3}")).ToArray();
            var results701 = await Task.WhenAll(tasks701);
            var nonNullableResults701 = results701
                .Select(r => (r.unit, r.status, r.headcode ?? string.Empty, r.identity ?? string.Empty, r.reversal ?? string.Empty, r.statusIndicator, r.statusColor, r.lastSeenLocation))
                .ToArray();

            var seen = new HashSet<string>();
            var tasks458 = ACTIVE_4585_UNITS.Select(u => Check458Async(client, u, seen)).ToArray();
            var results458 = await Task.WhenAll(tasks458);

            var seen455 = new HashSet<string>();
            var tasks455 = ACTIVE_455_UNITS.Select(u => Check458Async(client, u, seen455)).ToArray();
            var results455 = await Task.WhenAll(tasks455);

            var seen7015 = new HashSet<string>();
            var tasks7015 = ACTIVE_7015_UNITS.Select(u => Check458Async(client, u, seen7015, is458: false)).ToArray();
            var results7015 = await Task.WhenAll(tasks7015);

            await NotifyDiscord(nonNullableResults701, results458, results455, results7015);
        }

        static string ClassifyUnit(string? headcode)
        {
            if (string.IsNullOrEmpty(headcode)) return "depot";
            var prefix = headcode.Length >= 2 ? headcode[..2] : headcode;
            if (prefix == "5Q" || prefix == "5T" || prefix == "5X" || prefix == "5Z")
                return "testing";
            if (headcode.StartsWith("5") || headcode.StartsWith("0"))
                return "depot";
            return "in_service";
        }

        static string GetLineFromHeadcode(string headcode)
        {
            if (string.IsNullOrEmpty(headcode)) return "";
            return HEADCODE_TO_LINE.TryGetValue(headcode[..2], out var line) ? line : "Other";
        }

        // Calculate dynamic 455 total: base known count + any unexpected units seen
        static int GetDynamic455Total(IEnumerable<string> activeUnits)
        {
            int knownCount = KNOWN_455_UNITS.Count; // 12
            int extraUnits = activeUnits.Count(u => !KNOWN_455_UNITS.Contains(u));
            return knownCount + extraUnits;
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

        // === 701s: one identity + possible reversal + status + last seen ===
        static async Task<(string unit, string status, string? headcode, string? identity, string? reversal, string statusIndicator, string statusColor, string lastSeenLocation)>
        Check701Async(HttpClient client, string unitNumber)

        {
            try
            {
                var url = $"{BASE_URL}?qsearch={unitNumber}&type=detailed";
                using var resp = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);
                if ((int)resp.StatusCode == 302 && resp.Headers.Location?.ToString().StartsWith("/service/") == true)
                {
                    var serviceUrl = SERVICE_URL + resp.Headers.Location.ToString();
                    var (headcode, identities, reversal, statusIndicator, statusColor, lastSeenLocation) = await FetchHeadcodeAndIdentities(client, serviceUrl, is458: false);
                    var identity = identities.FirstOrDefault() ?? unitNumber;
                    var status = ClassifyUnit(headcode);
                    return (unitNumber, status, headcode, identity, reversal, statusIndicator, statusColor, lastSeenLocation);
                }
                return (unitNumber, "not_running", null, null, null, "●", "Gray", "");
            }
            catch (Exception e)
            {
                Console.WriteLine($"701 {unitNumber}: Error - {e.Message}");
                return (unitNumber, "error", null, null, null, "●", "Gray", "");
            }
        }

        // === 458s: multiple identities + reversal + status + last seen ===
        static async Task<(string formation, string status, string headcode, string reversal, string statusIndicator, string statusColor, string lastSeenLocation)?> Check458Async(HttpClient client, string unitNumber, HashSet<string> seen, bool is458 = true)
        {
            if (seen.Contains(unitNumber)) return null;

            try
            {
                var url = $"{BASE_URL}?qsearch={unitNumber}&type=detailed";
                using var resp = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);
                if ((int)resp.StatusCode == 302 && resp.Headers.Location?.ToString().StartsWith("/service/") == true)
                {
                    var serviceUrl = SERVICE_URL + resp.Headers.Location.ToString();
                    var (headcode, identities, reversal, statusIndicator, statusColor, lastSeenLocation) = await FetchHeadcodeAndIdentities(client, serviceUrl, is458: is458);
                    var clean = SquashReversal(identities ?? new List<string>());
                    var formation = string.Join("+", clean);
                    if (string.IsNullOrEmpty(formation)) formation = unitNumber;
                    var status = ClassifyUnit(headcode);
                    foreach (var id in identities ?? Enumerable.Empty<string>()) seen.Add(id);

                    // Ensure non-null values for headcode, reversal, and lastSeenLocation
                    return (formation, status, headcode ?? string.Empty, reversal ?? string.Empty, statusIndicator, statusColor, lastSeenLocation);
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
        static (string statusIndicator, string statusColor, string lastSeenLocation) DetermineUnitStatus(string pageHtml)
        {
            try
            {
                var doc = new HtmlDocument();
                doc.LoadHtml(pageHtml);

                // Parse the timetable to extract delay information and station names
                var delays = new List<string>();
                var stationNames = new List<string>();
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

                        // Get station name for this row
                        var stationNode = row.SelectSingleNode(".//a[@class='name']");
                        var stationName = stationNode?.InnerText.Trim() ?? "";
                        // Remove CRS code if present (e.g., "Reading [RDG]" -> "Reading")
                        if (!string.IsNullOrEmpty(stationName))
                        {
                            var crsMatch = System.Text.RegularExpressions.Regex.Match(stationName, @"\s*\[[A-Z]{3}\]$");
                            if (crsMatch.Success)
                                stationName = stationName.Substring(0, crsMatch.Index).Trim();
                        }
                        stationNames.Add(stationName);
                    }
                }

                // Find the last station that has delay data (i.e., the train has passed through)
                string lastSeenLocation = "";
                for (int i = stationNames.Count - 1; i >= 0; i--)
                {
                    if (!string.IsNullOrWhiteSpace(delays[i]) && !string.IsNullOrWhiteSpace(stationNames[i]))
                    {
                        lastSeenLocation = stationNames[i];
                        break;
                    }
                }

                // Apply the same status determination logic as LiveStatusPage
                var nonEmptyDelays = delays.Where(d => !string.IsNullOrWhiteSpace(d)).ToList();
                if (nonEmptyDelays.Count == 0)
                {
                    return ("●", "Gray", lastSeenLocation);
                }

                string lastDelay = nonEmptyDelays.Last();

                if (lastDelay == "●")
                {
                    return ("●", "LimeGreen", lastSeenLocation);
                }
                else if (lastDelay.StartsWith("-"))
                {
                    return (lastDelay, "DeepSkyBlue", lastSeenLocation);
                }
                else if (lastDelay.StartsWith("+"))
                {
                    if (int.TryParse(lastDelay.TrimStart('+'), out int minsLate))
                    {
                        return (lastDelay, minsLate >= 5 ? "Red" : "Orange", lastSeenLocation);
                    }
                    else
                    {
                        return (lastDelay, "Gray", lastSeenLocation);
                    }
                }
                else
                {
                    return (lastDelay, "Gray", lastSeenLocation);
                }
            }
            catch (Exception ex)
            {
                // Fallback to generic status if parsing fails
                Console.WriteLine($"Error determining status: {ex.Message}");
                return ("●", "Gray", "");
            }
        }

        // === Extract headcode, identities, reversal station, status, and last seen location ===
        static async Task<(string? headcode, List<string> identities, string? reversal, string statusIndicator, string statusColor, string lastSeenLocation)>
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

                // Determine unit status and last seen location from the HTML
                var (statusIndicator, statusColor, lastSeenLocation) = DetermineUnitStatus(html);

                return (headcode, identities, reversalStation, statusIndicator, statusColor, lastSeenLocation);
            }
            catch (Exception e)
            {
                Console.WriteLine($"Error fetching RTT data from {serviceUrl}: {e.Message}");
                return (null, new List<string>(), null, "●", "Gray", "");
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

        // === Helper to split messages for Discord's 2000 char limit ===
        static List<string> SplitMessageForDiscord(string content, int maxLength)
        {
            var messages = new List<string>();
            if (content.Length <= maxLength)
            {
                messages.Add(content);
                return messages;
            }

            // Split by double newlines (section breaks) to keep logical sections together
            var sections = content.Split(new[] { "\n\n" }, StringSplitOptions.None);
            var currentMessage = "";
            var isFirstMessage = true;

            foreach (var section in sections)
            {
                var sectionWithBreak = currentMessage.Length > 0 ? "\n\n" + section : section;

                if (currentMessage.Length + sectionWithBreak.Length > maxLength && currentMessage.Length > 0)
                {
                    // Close code block if we're splitting mid-message
                    if (isFirstMessage || !currentMessage.StartsWith("```"))
                    {
                        if (!currentMessage.TrimEnd().EndsWith("```"))
                            currentMessage += "\n```";
                    }
                    messages.Add(currentMessage);

                    // Start new message with code block continuation
                    currentMessage = "```ansi\n" + section;
                    isFirstMessage = false;
                }
                else
                {
                    currentMessage += sectionWithBreak;
                }
            }

            if (!string.IsNullOrWhiteSpace(currentMessage))
                messages.Add(currentMessage);

            return messages;
        }

        // === Format & send Discord message ===
        static async Task NotifyDiscord(
            (string unit, string status, string headcode, string identity, string reversal, string statusIndicator, string statusColor, string lastSeenLocation)[] results701,
            (string formation, string status, string headcode, string reversal, string statusIndicator, string statusColor, string lastSeenLocation)?[] results458,
            (string formation, string status, string headcode, string reversal, string statusIndicator, string statusColor, string lastSeenLocation)?[] results455,
            (string formation, string status, string headcode, string reversal, string statusIndicator, string statusColor, string lastSeenLocation)?[] results7015)
        {
            var inService701 = new Dictionary<string, List<string>>();
            var depot701 = new List<string>();
            var testing701 = new List<string>();
            var other701 = new List<string>();

            foreach (var (unit, status, headcode, identity, reversal, statusIndicator, statusColor, lastSeenLocation) in results701)
            {
                var identityStr = identity ?? unit;
                var headcodeStr = !string.IsNullOrEmpty(headcode) ? $" ({headcode})" : "";
                var statusStr = ColorizeStatus(statusIndicator, statusColor);
                var lastSeenStr = !string.IsNullOrEmpty(lastSeenLocation) ? $" – last seen at {lastSeenLocation}" : "";
                var part = $"{identityStr}{headcodeStr}{statusStr}{lastSeenStr}";
                var label = string.IsNullOrEmpty(reversal) ? part : $"{part} – reverses at {reversal}";

                if (status == "depot")
                    depot701.Add(label);
                else if (status == "testing")
                    testing701.Add(label);
                else if (status == "in_service")
                {
                    var line = GetLineFromHeadcode(headcode);
                    if (string.IsNullOrEmpty(line)) depot701.Add(label);
                    else if (line == "Other") other701.Add(label);
                    else
                    {
                        if (!inService701.ContainsKey(line)) inService701[line] = new List<string>();
                        inService701[line].Add(label);
                    }
                }
            }

            var inService458 = new Dictionary<string, HashSet<string>>();
            var depot458 = new HashSet<string>();
            var testing458 = new HashSet<string>();
            var seenForm458 = new HashSet<string>();
            foreach (var r in results458)
            {
                if (r == null) continue;
                var (formation, status, headcode, reversal, statusIndicator, statusColor, lastSeenLocation) = r.Value;
                if (!seenForm458.Add(formation)) continue;

                var statusStr = ColorizeStatus(statusIndicator, statusColor);
                var lastSeenStr = !string.IsNullOrEmpty(lastSeenLocation) ? $" – last seen at {lastSeenLocation}" : "";
                var label = string.IsNullOrEmpty(reversal)
                    ? $"{formation} ({headcode}){statusStr}{lastSeenStr}"
                    : $"{formation} ({headcode}){statusStr}{lastSeenStr} – reverses at {reversal}";

                if (status == "depot")
                    depot458.Add(label);
                else if (status == "testing")
                    testing458.Add(label);
                else if (status == "in_service")
                {
                    var line = GetLineFromHeadcode(headcode);
                    if (string.IsNullOrEmpty(line))
                        depot458.Add(label);
                    else
                    {
                        if (!inService458.ContainsKey(line)) inService458[line] = new HashSet<string>();
                        inService458[line].Add(label);
                    }
                }
            }

            // Process 455 results
            var inService455 = new Dictionary<string, HashSet<string>>();
            var depot455 = new HashSet<string>();
            var testing455 = new HashSet<string>();
            var seenForm455 = new HashSet<string>();
            var activeUnits455 = new List<string>();
            foreach (var r in results455)
            {
                if (r == null) continue;
                var (formation, status, headcode, reversal, statusIndicator, statusColor, lastSeenLocation) = r.Value;
                if (!seenForm455.Add(formation)) continue;

                // Track all active units for dynamic total
                foreach (var u in formation.Split('+'))
                    activeUnits455.Add(u.Trim());

                var statusStr = ColorizeStatus(statusIndicator, statusColor);
                var lastSeenStr = !string.IsNullOrEmpty(lastSeenLocation) ? $" – last seen at {lastSeenLocation}" : "";
                var label = string.IsNullOrEmpty(reversal)
                    ? $"{formation} ({headcode}){statusStr}{lastSeenStr}"
                    : $"{formation} ({headcode}){statusStr}{lastSeenStr} – reverses at {reversal}";

                if (status == "depot")
                    depot455.Add(label);
                else if (status == "testing")
                    testing455.Add(label);
                else if (status == "in_service")
                {
                    var line = GetLineFromHeadcode(headcode);
                    if (string.IsNullOrEmpty(line))
                        depot455.Add(label);
                    else
                    {
                        if (!inService455.ContainsKey(line)) inService455[line] = new HashSet<string>();
                        inService455[line].Add(label);
                    }
                }
            }

            var now = DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss");
            int total701 = inService701.Values.Sum(v => v.Count) + depot701.Count + testing701.Count + other701.Count;
            // Count individual units by splitting formations on '+'
            int total458 = inService458.Values.Sum(v => v.Sum(f => f.Split(' ')[0].Split('+').Length)) +
                          depot458.Sum(f => f.Split(' ')[0].Split('+').Length) +
                          testing458.Sum(f => f.Split(' ')[0].Split('+').Length);
            int total455 = inService455.Values.Sum(v => v.Sum(f => f.Split(' ')[0].Split('+').Length)) +
                          depot455.Sum(f => f.Split(' ')[0].Split('+').Length) +
                          testing455.Sum(f => f.Split(' ')[0].Split('+').Length);
            int dynamic455Total = GetDynamic455Total(activeUnits455);

            var content = "```ansi\nSWR Fleet Report\n";
            content += $"701/0s: {total701}/60 units seen today — {now}\n\n";

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

            // 458/5 section
            content += $"🚆 458/5s: {total458}/4 units seen today\n";
            if (inService458.Any())
            {
                var inService458Count = inService458.Values.Sum(v => v.Sum(f => f.Split(' ')[0].Split('+').Length));
                content += $"🟢 In service ({inService458Count}):\n";
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

                    content += "\n";
                }
            }
            if (depot458.Any())
            {
                var depotUnitCount = depot458.Sum(f => f.Split(' ')[0].Split('+').Length);
                content += $"🏠 Depot ({depotUnitCount}):\n";
                foreach (var unit in depot458)
                    content += $"• {unit}\n";
                content += "\n";
            }
            if (testing458.Any())
            {
                var testingUnitCount = testing458.Sum(f => f.Split(' ')[0].Split('+').Length);
                content += $"🛠️ Testing ({testingUnitCount}):\n";
                foreach (var unit in testing458)
                    content += $"• {unit}\n";
                content += "\n";
            }
            if (!inService458.Any() && !depot458.Any() && !testing458.Any())
                content += "None running today.\n";
            content += "\n";

            // 455 section
            content += $"🚃 455s: {total455}/{dynamic455Total} units seen today\n";
            if (inService455.Any())
            {
                var inService455Count = inService455.Values.Sum(v => v.Sum(f => f.Split(' ')[0].Split('+').Length));
                content += $"🟢 In service ({inService455Count}):\n";
                foreach (var (line, labels) in inService455.OrderBy(x => x.Key))
                {
                    var lineUnitCount = labels.Sum(f => f.Split(' ')[0].Split('+').Length);
                    content += $"{line} ({lineUnitCount}):\n";

                    var normals = labels.Where(l => !l.Contains("reverses at")).ToList();
                    var revs = labels.Where(l => l.Contains("reverses at")).ToList();

                    foreach (var normal in normals)
                        content += $"• {normal}\n";
                    foreach (var rev in revs)
                        content += $"• {rev}\n";

                    content += "\n";
                }
            }
            if (depot455.Any())
            {
                var depotUnitCount = depot455.Sum(f => f.Split(' ')[0].Split('+').Length);
                content += $"🏠 Depot ({depotUnitCount}):\n";
                foreach (var unit in depot455)
                    content += $"• {unit}\n";
                content += "\n";
            }
            if (testing455.Any())
            {
                var testingUnitCount = testing455.Sum(f => f.Split(' ')[0].Split('+').Length);
                content += $"🛠️ Testing ({testingUnitCount}):\n";
                foreach (var unit in testing455)
                    content += $"• {unit}\n";
                content += "\n";
            }
            if (!inService455.Any() && !depot455.Any() && !testing455.Any())
                content += "None running today.\n";
            content += "\n";

            // Process 701/5 results - group by service
            var grouped7015 = new Dictionary<string, List<string>>();
            foreach (var r in results7015)
            {
                if (r == null) continue;
                var (formation, status, headcode, reversal, statusIndicator, statusColor, lastSeenLocation) = r.Value;
                var serviceKey = $"{status}|{headcode}|{reversal}|{statusIndicator}|{statusColor}|{lastSeenLocation}";
                if (!grouped7015.ContainsKey(serviceKey)) grouped7015[serviceKey] = new List<string>();
                grouped7015[serviceKey].Add(formation);
            }

            var inService7015 = new Dictionary<string, HashSet<string>>();
            var depot7015 = new List<string>();
            var testing7015 = new List<string>();

            foreach (var (serviceKey, units) in grouped7015)
            {
                var parts = serviceKey.Split('|');
                var status = parts[0];
                var headcode = parts[1];
                var reversal = parts[2];
                var statusIndicator = parts[3];
                var statusColor = parts[4];
                var lastSeenLocation = parts.Length > 5 ? parts[5] : "";

                var formation = string.Join("+", units.OrderBy(u => u));
                var statusStr = ColorizeStatus(statusIndicator, statusColor);
                var lastSeenStr = !string.IsNullOrEmpty(lastSeenLocation) ? $" – last seen at {lastSeenLocation}" : "";
                var label = string.IsNullOrEmpty(reversal)
                    ? $"{formation} ({headcode}){statusStr}{lastSeenStr}"
                    : $"{formation} ({headcode}){statusStr}{lastSeenStr} – reverses at {reversal}";

                if (status == "depot")
                    depot7015.Add(label);
                else if (status == "testing")
                    testing7015.Add(label);
                else if (status == "in_service")
                {
                    var line = GetLineFromHeadcode(headcode);
                    if (string.IsNullOrEmpty(line))
                        depot7015.Add(label);
                    else
                    {
                        if (!inService7015.ContainsKey(line)) inService7015[line] = new HashSet<string>();
                        inService7015[line].Add(label);
                    }
                }
            }

            // Only show 701/5 section if any units are running
            bool has7015 = inService7015.Any() || depot7015.Any() || testing7015.Any();
            if (has7015)
            {
                int total7015 = inService7015.Values.Sum(v => v.Sum(f => f.Split(' ')[0].Split('+').Length)) +
                               depot7015.Sum(f => f.Split(' ')[0].Split('+').Length) +
                               testing7015.Sum(f => f.Split(' ')[0].Split('+').Length);
                content += $"🚊 701/5s: {total7015}/30 units seen today\n";

                if (inService7015.Any())
                {
                    var inService7015Count = inService7015.Values.Sum(v => v.Sum(f => f.Split(' ')[0].Split('+').Length));
                    content += $"🟢 In service ({inService7015Count}):\n";
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

                        content += "\n";
                    }
                }

                if (depot7015.Any())
                {
                    var depotUnitCount = depot7015.Sum(f => f.Split(' ')[0].Split('+').Length);
                    content += $"🏠 Depot ({depotUnitCount}):\n";
                    foreach (var unit in depot7015)
                        content += $"• {unit}\n";
                    content += "\n";
                }

                if (testing7015.Any())
                {
                    var testingUnitCount = testing7015.Sum(f => f.Split(' ')[0].Split('+').Length);
                    content += $"🛠️ Testing ({testingUnitCount}):\n";
                    foreach (var unit in testing7015)
                        content += $"• {unit}\n";
                    content += "\n";
                }
            }

            content += "\nPowered by SWR Unit Tracker v2.0.0\n```";

            Console.WriteLine("\n" + content);

            // Send to Discord - split into multiple messages if over 2000 chars
            using var client = new HttpClient();
            var messages = SplitMessageForDiscord(content, 1900); // Leave margin for safety
            foreach (var msg in messages)
            {
                var payload = new { content = msg };
                var json = JsonSerializer.Serialize(payload);
                var resp = await client.PostAsync(DISCORD_WEBHOOK_URL,
                    new StringContent(json, System.Text.Encoding.UTF8, "application/json"));
                resp.EnsureSuccessStatusCode();
                if (messages.Count > 1)
                    await Task.Delay(500); // Rate limit protection between messages
            }
        }
    }
}
