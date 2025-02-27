using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using CommandLine;

namespace LoadTester
{
    // HAR file classes
    public class HarFile
    {
        [JsonPropertyName("log")]
        public HarLog Log { get; set; }
    }

    public class HarLog
    {
        [JsonPropertyName("entries")]
        public List<HarEntry> Entries { get; set; }
    }

    public class HarEntry
    {
        [JsonPropertyName("request")]
        public HarRequest Request { get; set; }

        [JsonPropertyName("response")]
        public HarResponse Response { get; set; }

        [JsonPropertyName("startedDateTime")]
        public string StartedDateTime { get; set; }

        [JsonPropertyName("time")]
        public double Time { get; set; }
    }

    public class HarRequest
    {
        [JsonPropertyName("method")]
        public string Method { get; set; }

        [JsonPropertyName("url")]
        public string Url { get; set; }

        [JsonPropertyName("headers")]
        public List<HarHeader> Headers { get; set; }

        [JsonPropertyName("postData")]
        public HarPostData PostData { get; set; }
    }

    public class HarResponse
    {
        [JsonPropertyName("status")]
        public int Status { get; set; }

        [JsonPropertyName("headers")]
        public List<HarHeader> Headers { get; set; }
    }

    public class HarHeader
    {
        [JsonPropertyName("name")]
        public string Name { get; set; }

        [JsonPropertyName("value")]
        public string Value { get; set; }
    }

    public class HarPostData
    {
        [JsonPropertyName("text")]
        public string Text { get; set; }

        [JsonPropertyName("mimeType")]
        public string MimeType { get; set; }
    }

    // Scenario classes
    public class UserScenarioOutput
    {
        public List<ApiRequest> Requests { get; set; } = new List<ApiRequest>();
    }

    public class ApiRequest
    {
        public string Name { get; set; }
        public string Endpoint { get; set; }
        public string Method { get; set; }
        public Dictionary<string, string> Headers { get; set; } = new Dictionary<string, string>();
        public string Body { get; set; }
        public int ThinkTimeMs { get; set; }
    }

    public class HarFileConverter
    {
        // URL patterns to include
        private static readonly string[] UrlPatterns = new[] 
        { 
            "/api", 
            "/Lucy/", 
            "/hook/", 
            "/Services", 
            "/components/"
        };

        // Content types to include
        private static readonly string[] AllowedContentTypes = new[]
        {
            "application/json",
            "text/json",
            "application/xml",
            "text/xml",
            "text/plain"
        };

        public static void ConvertHarToScenario(string inputFile, string outputFile, int defaultThinkTimeMs)
        {
            try
            {
                Console.WriteLine($"Reading HAR file from {inputFile}...");
                string harJson = File.ReadAllText(inputFile);
                
                var harFile = JsonSerializer.Deserialize<HarFile>(harJson);
                if (harFile == null || harFile.Log == null || harFile.Log.Entries == null)
                {
                    Console.WriteLine("Invalid HAR file format");
                    return;
                }

                var scenario = new UserScenarioOutput();
                var filteredEntries = FilterRelevantEntries(harFile.Log.Entries);
                
                Console.WriteLine($"Found {filteredEntries.Count} relevant API requests");
                
                // Track timestamps for think time calculation
                DateTime? lastTimestamp = null;
                
                foreach (var entry in filteredEntries)
                {
                    var request = entry.Request;
                    
                    // Extract endpoint from URL
                    var endpoint = ExtractEndpoint(request.Url);
                    var name = CreateNameFromEndpoint(endpoint);
                    
                    // Calculate think time based on timestamp differences
                    int thinkTime = defaultThinkTimeMs;
                    if (lastTimestamp != null && DateTime.TryParse(entry.StartedDateTime, out DateTime currentTimestamp))
                    {
                        var timeDiff = (currentTimestamp - lastTimestamp.Value).TotalMilliseconds;
                        if (timeDiff > 0 && timeDiff < 30000) // Cap at 30 seconds
                        {
                            thinkTime = (int)timeDiff;
                        }
                        lastTimestamp = currentTimestamp;
                    }
                    else if (lastTimestamp == null && DateTime.TryParse(entry.StartedDateTime, out DateTime firstTimestamp))
                    {
                        lastTimestamp = firstTimestamp;
                    }
                    
                    // Create API request
                    var apiRequest = new ApiRequest
                    {
                        Name = name,
                        Endpoint = endpoint,
                        Method = request.Method,
                        Headers = ExtractRelevantHeaders(request.Headers),
                        Body = ExtractBody(request),
                        ThinkTimeMs = thinkTime
                    };
                    
                    scenario.Requests.Add(apiRequest);
                }
                
                // Set last request think time to 0 as it's the end of the scenario
                if (scenario.Requests.Count > 0)
                {
                    scenario.Requests[scenario.Requests.Count - 1].ThinkTimeMs = 0;
                }
                
                // Write the scenario to output file
                var options = new JsonSerializerOptions 
                { 
                    WriteIndented = true 
                };
                
                string scenarioJson = JsonSerializer.Serialize(scenario, options);
                File.WriteAllText(outputFile, scenarioJson);
                
                Console.WriteLine($"Scenario with {scenario.Requests.Count} requests written to {outputFile}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error: {ex.Message}");
                Console.WriteLine(ex.StackTrace);
            }
        }
        
        private static List<HarEntry> FilterRelevantEntries(List<HarEntry> entries)
        {
            return entries
                .Where(entry => 
                    entry.Request != null && 
                    UrlPatterns.Any(pattern => entry.Request.Url.Contains(pattern, StringComparison.OrdinalIgnoreCase)) &&
                    entry.Response != null && 
                    entry.Response.Status >= 200 && entry.Response.Status < 400 &&
                    HasAllowedContentType(entry.Response.Headers))
                .ToList();
        }
        
        private static bool HasAllowedContentType(List<HarHeader> headers)
        {
            if (headers == null) return false;
            
            var contentTypeHeader = headers.FirstOrDefault(h => 
                h.Name.Equals("Content-Type", StringComparison.OrdinalIgnoreCase));
            
            if (contentTypeHeader == null) return false;
            
            return AllowedContentTypes.Any(allowedType => 
                contentTypeHeader.Value.Contains(allowedType, StringComparison.OrdinalIgnoreCase));
        }
        
        private static string ExtractEndpoint(string url)
        {
            try
            {
                // Remove domain part
                var uri = new Uri(url);
                string endpoint = uri.PathAndQuery;
                
                return endpoint;
            }
            catch
            {
                // If URL parsing fails, return the original URL
                return url;
            }
        }
        
        private static string CreateNameFromEndpoint(string endpoint)
        {
            // Remove query parameters
            int queryIndex = endpoint.IndexOf('?');
            if (queryIndex > 0)
            {
                endpoint = endpoint.Substring(0, queryIndex);
            }
            
            // Convert to camel case name
            string[] parts = endpoint.Split(new[] { '/', '-', '_' }, StringSplitOptions.RemoveEmptyEntries);
            StringBuilder nameBuilder = new StringBuilder();
            
            foreach (var part in parts)
            {
                if (string.IsNullOrEmpty(part)) continue;
                
                if (nameBuilder.Length == 0)
                {
                    nameBuilder.Append(part);
                }
                else
                {
                    nameBuilder.Append(char.ToUpper(part[0]));
                    if (part.Length > 1)
                    {
                        nameBuilder.Append(part.Substring(1));
                    }
                }
            }
            
            string name = nameBuilder.ToString();
            
            // Ensure first letter is uppercase
            if (!string.IsNullOrEmpty(name))
            {
                name = char.ToUpper(name[0]) + name.Substring(1);
            }
            
            // Append HTTP method to make the name more descriptive
            return name;
        }
        
        private static Dictionary<string, string> ExtractRelevantHeaders(List<HarHeader> headers)
        {
            var relevantHeaders = new Dictionary<string, string>();
            
            if (headers != null)
            {
                foreach (var header in headers)
                {
                    // Keep only Authorization, Content-Type, and X-* headers
                    if (header.Name.Equals("Authorization", StringComparison.OrdinalIgnoreCase) ||
                        header.Name.Equals("Content-Type", StringComparison.OrdinalIgnoreCase) ||
                        header.Name.StartsWith("X-", StringComparison.OrdinalIgnoreCase))
                    {
                        relevantHeaders[header.Name] = header.Value;
                    }
                }
            }
            
            return relevantHeaders;
        }
        
        private static string ExtractBody(HarRequest request)
        {
            if (request.PostData != null && !string.IsNullOrEmpty(request.PostData.Text))
            {
                return request.PostData.Text;
            }
            
            return "";
        }
    }
}