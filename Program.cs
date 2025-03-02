using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading.Tasks;
using Akka.Actor;
using Akka.Configuration;
using Akka.Routing;
using CommandLine;
using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace LoadTester
{
    // Command line options
    public class Options
    {
        [Option('u', "url", Required = false, HelpText = "Base URL of the API to test.")]
        public string BaseUrl { get; set; }

        [Option('k', "key", Required = false, HelpText = "API key for authentication.")]
        public string ApiKey { get; set; }

        [Option('c', "concurrent-users", Default = 10, HelpText = "Number of concurrent users to simulate.")]
        public int ConcurrentUsers { get; set; }

        [Option('d', "duration", Default = 60, HelpText = "Duration of the test in seconds.")]
        public int Duration { get; set; }

        [Option('r', "ramp-up", Default = 10, HelpText = "Ramp-up period in seconds.")]
        public int RampUpPeriod { get; set; }

        [Option('s', "scenario", Required = false, HelpText = "Path to the scenario JSON file.")]
        public string ScenarioFile { get; set; }

        [Option('r',"convert-har",Required=false,HelpText="Convert HAR file to scenario JSON file")]
        public string HarFile {get;set;}

        [Option('o',"output",Required=false,HelpText="Output scenario file to create")]
        public string OutputScenarioFile {get;set;}
    }

    // Messages
    public class StartTest { }
    public class StopTest { }
    public class UserScenario
    {
        public List<ApiRequest> Requests { get; set; }
    }

    // public class ApiRequest
    // {
    //     public string Name { get; set; }
    //     public string Endpoint { get; set; }
    //     public string Method { get; set; }
    //     public Dictionary<string, string> Headers { get; set; }
    //     public string Body { get; set; }
    //     public int ThinkTimeMs { get; set; }
    // }

    public class RequestResult
    {
        public string RequestName { get; set; }
        public bool Success { get; set; }
        public int StatusCode { get; set; }
        public TimeSpan Duration { get; set; }
        public string Error { get; set; }
    }

    // Virtual User Actor
    public class VirtualUserActor : ReceiveActor
    {
        private readonly string _baseUrl;
        private readonly string _apiKey;
        private readonly UserScenario _scenario;
        private readonly HttpClient _httpClient;
        private readonly IActorRef _resultCollector;

        public VirtualUserActor(string baseUrl, string apiKey, UserScenario scenario, IActorRef resultCollector)
        {
            _baseUrl = baseUrl.TrimEnd('/');
            _apiKey = apiKey;
            _scenario = scenario;
            _resultCollector = resultCollector;
            _httpClient = new HttpClient(/*new LoggingHandler(new HttpClientHandler())*/);


            _httpClient.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            _httpClient.DefaultRequestHeaders.Add("Authorization","APIKEY " + _apiKey);
            ReceiveAsync<StartTest>(async _ => await ExecuteScenario());
        }

        private async Task ExecuteScenario()
        {
            try
            {
                foreach (var request in _scenario.Requests)
                {
                    var result = await ExecuteRequest(request);
                    _resultCollector.Tell(result);

                    if (request.ThinkTimeMs > 0)
                    {
                        await Task.Delay(request.ThinkTimeMs);
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error in user scenario: {ex.Message}");
            }
        }

        private async Task<RequestResult> ExecuteRequest(ApiRequest request)
        {
            var stopwatch = Stopwatch.StartNew();
            var result = new RequestResult { RequestName = request.Name };
            try
            {
                var url = $"{_baseUrl}/{request.Endpoint.TrimStart('/')}";
                var httpRequest = new HttpRequestMessage(new HttpMethod(request.Method), url);

                // Add custom headers
                var contentType = "application/json";
                if (request.Headers != null)
                {
                    
                    foreach (var header in request.Headers)
                    {
                        if (header.Key.Equals("Content-Type", StringComparison.OrdinalIgnoreCase))
                        {
                            contentType = header.Value;
                        }
                        else
                        httpRequest.Headers.Add(header.Key, header.Value);
                    }
                }
              

                // Add body for POST, PUT, PATCH
                if (request.HasBody && 
                    (request.Method.Equals("POST", StringComparison.OrdinalIgnoreCase) || 
                     request.Method.Equals("PUT", StringComparison.OrdinalIgnoreCase) || 
                     request.Method.Equals("PATCH", StringComparison.OrdinalIgnoreCase)))
                {
                    if (contentType.StartsWith("multipart/form-data"))
                    {
                        var mpContent = new MultipartFormDataContent();
                        foreach(var part in request.MultiPartContents)
                        {
                            if (!string.IsNullOrEmpty(part.FileName))
                            {
                                // For simplicity, this example assumes file path is in Value
                                // In real implementation, you might need to adapt this based on how you're storing file data
                                try
                                {
                                    byte[] fileBytes;

                                    // Check if the Value contains actual file content (base64 encoded)
                                    if (Common.IsBase64String(part.Value))
                                    {
                                        fileBytes = Convert.FromBase64String(part.Value);
                                    }
                                    // Otherwise, treat Value as a file path
                                    else if (File.Exists(part.Value))
                                    {
                                        fileBytes = File.ReadAllBytes(part.Value);
                                    }
                                    else
                                    {
                                        // If it's not a file path or base64, just use the string content
                                        fileBytes = System.Text.Encoding.UTF8.GetBytes(part.Value);
                                    }

                                    var fileContent = new ByteArrayContent(fileBytes);

                                    // Set content type if available
                                    if (!string.IsNullOrEmpty(part.ContentType))
                                    {
                                        fileContent.Headers.ContentType = MediaTypeHeaderValue.Parse(part.ContentType);
                                    }

                                    mpContent.Add(fileContent, part.Name, part.FileName);
                                }
                                catch (Exception ex)
                                {
                                    Console.WriteLine($"Error processing file content for {part.Name}: {ex.Message}");
                                    // Fallback to string content if file processing fails
                                    mpContent.Add(new StringContent(part.Value ?? string.Empty), part.Name);
                                }
                            }
                            // Handle string content
                            else
                            {
                                var stringContent = new StringContent(part.Value ?? string.Empty);

                                // Set content type if available
                                if (!string.IsNullOrEmpty(part.ContentType))
                                {
                                    stringContent.Headers.ContentType = MediaTypeHeaderValue.Parse(part.ContentType);
                                }

                                mpContent.Add(stringContent, part.Name);
                            }
                        }
                        httpRequest.Content = mpContent;
                        // byte[] bodyBytes = Encoding.UTF8.GetBytes(request.Body);
                        // httpRequest.Content = new ByteArrayContent(bodyBytes);
                        // httpRequest.Headers.TryAddWithoutValidation("Content-Type", contentType);

                    } 
                    else 
                    {
                        httpRequest.Content = new StringContent(request.Body, Encoding.UTF8, contentType);
                    }
                }

                var response = await _httpClient.SendAsync(httpRequest);
                
                result.StatusCode = (int)response.StatusCode;
                result.Success = response.IsSuccessStatusCode;
                var content = await response.Content.ReadAsStringAsync();
                // For debugging - can be removed for production
                if (!response.IsSuccessStatusCode)
                {
                    result.Error = content;
                    Console.Error.WriteLine("ERROR: " + content);
                }

            }
            catch (Exception ex)
            {
                Console.WriteLine("5. Making request to "+ex.Message);

                result.Success = false;
                result.Error = ex.Message;
            }
            finally
            {
                stopwatch.Stop();
                result.Duration = stopwatch.Elapsed;
            }

            return result;
        }
    }

    // Result Collector Actor
    public class ResultCollectorActor : ReceiveActor
    {
        private readonly List<RequestResult> _results = new List<RequestResult>();
        private readonly Dictionary<string, List<RequestResult>> _resultsByEndpoint = new Dictionary<string, List<RequestResult>>();
        private DateTime _startTime;
        private int _expectedUsers;
        private int _completedUsers;

        public ResultCollectorActor(int expectedUsers)
        {
            _expectedUsers = expectedUsers;
            _startTime = DateTime.Now;

            Receive<RequestResult>(result => {
                _results.Add(result);
                
                if (!_resultsByEndpoint.ContainsKey(result.RequestName))
                    _resultsByEndpoint[result.RequestName] = new List<RequestResult>();
                
                _resultsByEndpoint[result.RequestName].Add(result);
            });

            Receive<StopTest>(_ => {
                _completedUsers++;
                if (_completedUsers >= _expectedUsers)
                {
                    GenerateReport();
                    Context.System.Terminate();
                }
            });
        }

        private void GenerateReport()
        {
            Console.WriteLine("\n========== LOAD TEST REPORT ==========");
            Console.WriteLine($"Test Duration: {(DateTime.Now - _startTime).TotalSeconds:F2} seconds");
            Console.WriteLine($"Total Requests: {_results.Count}");
            Console.WriteLine($"Successful Requests: {_results.Count(r => r.Success)}");
            Console.WriteLine($"Failed Requests: {_results.Count(r => !r.Success)}");
            
            Console.WriteLine("\n---------- BY ENDPOINT ----------");
            foreach (var endpoint in _resultsByEndpoint.Keys)
            {
                var endpointResults = _resultsByEndpoint[endpoint];
                var avgResponse = endpointResults.Average(r => r.Duration.TotalMilliseconds);
                var minResponse = endpointResults.Min(r => r.Duration.TotalMilliseconds);
                var maxResponse = endpointResults.Max(r => r.Duration.TotalMilliseconds);
                var p95Response = endpointResults.OrderBy(r => r.Duration.TotalMilliseconds)
                    .ElementAt((int)(endpointResults.Count * 0.95)).Duration.TotalMilliseconds;
                var successRate = (double)endpointResults.Count(r => r.Success) / endpointResults.Count * 100;
                
                Console.WriteLine($"\nEndpoint: {endpoint}");
                Console.WriteLine($"  Requests: {endpointResults.Count}");
                Console.WriteLine($"  Success Rate: {successRate:F2}%");
                Console.WriteLine($"  Avg Response: {avgResponse:F2} ms");
                Console.WriteLine($"  Min Response: {minResponse:F2} ms");
                Console.WriteLine($"  Max Response: {maxResponse:F2} ms");
                Console.WriteLine($"  95th Percentile: {p95Response:F2} ms");
                
                if (endpointResults.Any(r => !r.Success))
                {
                    Console.WriteLine("  Error Samples:");
                    foreach (var errorResult in endpointResults.Where(r => !r.Success).Take(5))
                    {
                        Console.WriteLine($"    Status: {errorResult.StatusCode}, Error: {errorResult.Error}");
                    }
                }
            }
        }
    }

    // Load Test Coordinator Actor
    public class LoadTestCoordinatorActor : ReceiveActor
    {
        private readonly IActorRef _resultCollector;
        private readonly List<IActorRef> _virtualUsers = new List<IActorRef>();
        private readonly string _baseUrl;
        private readonly string _apiKey;
        private readonly UserScenario _scenario;
        private readonly int _concurrentUsers;
        private readonly int _durationSeconds;
        private readonly int _rampUpPeriodSeconds;
        private ICancelable _testTimer;

        public LoadTestCoordinatorActor(Options options, UserScenario scenario)
        {
            _baseUrl = options.BaseUrl;
            _apiKey = options.ApiKey;
            _scenario = scenario;
            _concurrentUsers = options.ConcurrentUsers;
            _durationSeconds = options.Duration;
            _rampUpPeriodSeconds = options.RampUpPeriod;

            // Create result collector
            _resultCollector = Context.ActorOf(Props.Create(() => new ResultCollectorActor(_concurrentUsers)), "resultCollector");

            // Schedule test end using a cancellable scheduler
            _testTimer = Context.System.Scheduler.ScheduleTellOnceCancelable(
                TimeSpan.FromSeconds(_durationSeconds),
                Self,
                new StopTest(),
                Self);

            Receive<StartTest>(_ => StartLoadTest());
            Receive<StopTest>(_ => StopLoadTest());
        }

        private void StartLoadTest()
        {
            Console.WriteLine($"Starting load test with {_concurrentUsers} concurrent users...");
            Console.WriteLine($"Base URL: {_baseUrl}");
            Console.WriteLine($"Test duration: {_durationSeconds} seconds");
            Console.WriteLine($"Ramp-up period: {_rampUpPeriodSeconds} seconds");

            // Create virtual users
            var usersPerSecond = (double)_concurrentUsers / _rampUpPeriodSeconds;
            
            for (int i = 0; i < _concurrentUsers; i++)
            {
                var user = Context.ActorOf(
                    Props.Create(() => new VirtualUserActor(_baseUrl, _apiKey, _scenario, _resultCollector)),
                    $"user-{i}");
                
                _virtualUsers.Add(user);
                
                // Stagger user starts during ramp-up period
                var delay = (int)(i / usersPerSecond * 1000);
                Context.System.Scheduler.ScheduleTellOnce(
                    TimeSpan.FromMilliseconds(delay),
                    user,
                    new StartTest(),
                    Self);
            }
        }

        private void StopLoadTest()
        {
            Console.WriteLine("Stopping load test...");
            foreach (var user in _virtualUsers)
            {
                _resultCollector.Tell(new StopTest());
            }
        }
    }

    // Program entry point
    class Program
    {
        static void Main(string[] args)
        {
            Parser.Default.ParseArguments<Options>(args)
                .WithParsed(RunLoadTest)
                .WithNotParsed(HandleParseError);
        }

        static void RunLoadTest(Options options)
        {
            try
            {
                if (options.HarFile != null)
                {
                    if (string.IsNullOrWhiteSpace(options.OutputScenarioFile))
                    {
                        Console.WriteLine("Error: Output scenario file is required when converting HAR file.");
                        return;
                    }
                    HarFileConverter.ConvertHarToScenario(options.HarFile,options.OutputScenarioFile,1500);
                    return;
                }
                if (string.IsNullOrWhiteSpace(options.ScenarioFile))
                {
                    Console.WriteLine("Error: Scenario file is required.");
                    return;
                }
                // Load scenario
                var scenarioJson = System.IO.File.ReadAllText(options.ScenarioFile);
                var scenario = JsonSerializer.Deserialize<UserScenario>(scenarioJson);

                // Configure Akka.NET
                var config = ConfigurationFactory.ParseString(@"
                    akka {
                        log-dead-letters = off
                        actor {
                            default-dispatcher {
                                throughput = 100
                                fork-join-executor {
                                    parallelism-min = 8
                                    parallelism-factor = 1.0
                                    parallelism-max = 64
                                }
                            }
                        }
                    }");

                // Create actor system
                using (var system = ActorSystem.Create("LoadTestSystem", config))
                {
                    // Create coordinator
                    var coordinator = system.ActorOf(
                        Props.Create(() => new LoadTestCoordinatorActor(options, scenario)),
                        "coordinator");

                    // Start the test
                    coordinator.Tell(new StartTest());

                    // Wait for the system to terminate
                    system.WhenTerminated.Wait();
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error: {ex.Message}");
                Console.WriteLine(ex.StackTrace);
            }
        }

        static void HandleParseError(IEnumerable<Error> errors)
        {
            Console.WriteLine("Command line argument errors:");
            foreach (var error in errors)
            {
                Console.WriteLine($"  {error}");
            }
        }
    }
    class LoggingHandler : DelegatingHandler
{
    public LoggingHandler(HttpMessageHandler innerHandler) : base(innerHandler) { }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Console.WriteLine("🚀 Final Request Headers Being Sent:");
        
        foreach (var header in request.Headers)
        {
            Console.WriteLine($"{header.Key}: {string.Join(", ", header.Value)}");
        }

        if (request.Content != null)
        {
            foreach (var header in request.Content.Headers)
            {
                Console.WriteLine($"{header.Key}: {string.Join(", ", header.Value)}");
            }
        }

        Console.WriteLine("=====================================\n");
        return await base.SendAsync(request, cancellationToken);
    }
}
public static class Common
{
    public static bool IsBase64String(string value)
        {
            if (string.IsNullOrEmpty(value))
                return false;

            // Remove any Base64 padding characters
            value = value.Trim();
            
            // Base64 strings are always divisible by 4 after padding
            if (value.Length % 4 != 0)
            {
                // Try to add padding
                value = value.PadRight(value.Length + (4 - value.Length % 4), '=');
            }

            try
            {
                // Try to decode it
                Convert.FromBase64String(value);
                return true;
            }
            catch
            {
                return false;
            }
        }
}

}