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
            _httpClient = new HttpClient();
            _httpClient.DefaultRequestHeaders.Add("Authorization", $"Bearer {_apiKey}");
            _httpClient.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

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
                if (request.Headers != null)
                {
                    foreach (var header in request.Headers)
                    {
                        httpRequest.Headers.Add(header.Key, header.Value);
                    }
                }

                // Add body for POST, PUT, PATCH
                if (!string.IsNullOrEmpty(request.Body) && 
                    (request.Method.Equals("POST", StringComparison.OrdinalIgnoreCase) || 
                     request.Method.Equals("PUT", StringComparison.OrdinalIgnoreCase) || 
                     request.Method.Equals("PATCH", StringComparison.OrdinalIgnoreCase)))
                {
                    httpRequest.Content = new StringContent(request.Body, Encoding.UTF8, "application/json");
                }

                var response = await _httpClient.SendAsync(httpRequest);
                
                result.StatusCode = (int)response.StatusCode;
                result.Success = response.IsSuccessStatusCode;
                
                // For debugging - can be removed for production
                if (!response.IsSuccessStatusCode)
                {
                    result.Error = await response.Content.ReadAsStringAsync();
                }
            }
            catch (Exception ex)
            {
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
}