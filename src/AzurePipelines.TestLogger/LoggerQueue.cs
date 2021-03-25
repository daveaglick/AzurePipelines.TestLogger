using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace AzurePipelines.TestLogger
{
    internal class LoggerQueue
    {
        private readonly AsyncProducerConsumerCollection<ITestResult> _queue = new AsyncProducerConsumerCollection<ITestResult>();
        private readonly Task _consumeTask;
        private readonly CancellationTokenSource _consumeTaskCancellationSource = new CancellationTokenSource();

        private readonly IApiClient _apiClient;
        private readonly string _buildId;
        private readonly string _agentName;
        private readonly string _jobName;
        private readonly bool _groupTestResultsByClassName;

        // Internal for testing
        internal Dictionary<string, TestResultParent> Parents { get; } = new Dictionary<string, TestResultParent>();
        internal DateTime StartedDate { get; } = DateTime.UtcNow;
        internal int RunId { get; set; }
        internal string Source { get; set; }

        public LoggerQueue(IApiClient apiClient, string buildId, string agentName, string jobName, bool groupTestResultsByClassName = true)
        {
            _apiClient = apiClient;
            _buildId = buildId;
            _agentName = agentName;
            _jobName = jobName;
            _groupTestResultsByClassName = groupTestResultsByClassName;

            _consumeTask = ConsumeItemsAsync(_consumeTaskCancellationSource.Token);
        }

        public void Enqueue(ITestResult testResult) => _queue.Add(testResult);

        public void Flush(VstpTestRunComplete testRunComplete)
        {
            // Cancel any idle consumers and let them return
            _queue.Cancel();

            try
            {
                // Any active consumer will circle back around and batch post the remaining queue
                _consumeTask.Wait(TimeSpan.FromSeconds(60));

                // Update the run and parents to a completed state
                SendTestsCompleted(testRunComplete, _consumeTaskCancellationSource.Token).Wait(TimeSpan.FromSeconds(60));

                // Cancel any active HTTP requests if still hasn't finished flushing
                _consumeTaskCancellationSource.Cancel();
                if (!_consumeTask.Wait(TimeSpan.FromSeconds(10)))
                {
                    throw new TimeoutException("Cancellation didn't happen quickly");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine(ex);
            }
        }

        private async Task ConsumeItemsAsync(CancellationToken cancellationToken)
        {
            while (true)
            {
                ITestResult[] nextItems = await _queue.TakeAsync().ConfigureAwait(false);

                if (nextItems == null || nextItems.Length == 0)
                {
                    // Queue is canceling and is empty
                    return;
                }

                await SendResultsAsync(nextItems, cancellationToken).ConfigureAwait(false);

                if (cancellationToken.IsCancellationRequested)
                {
                    return;
                }
            }
        }

        private async Task SendResultsAsync(ITestResult[] testResults, CancellationToken cancellationToken)
        {
            try
            {
                // Create a test run if we need it
                if (RunId == 0)
                {
                    Source = GetSource(testResults);
                    RunId = await CreateTestRun(cancellationToken).ConfigureAwait(false);
                }

                // Group results by their parent
                IEnumerable<IGrouping<string, ITestResult>> testResultsByParent = GroupTestResultsByParent(testResults);

                // Create any required parent nodes
                await CreateParents(testResultsByParent, cancellationToken).ConfigureAwait(false);

                // Update parents with the test results
                await SendTestResults(testResultsByParent, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception)
            {
                // Eat any communications exceptions
            }
        }

        // Internal for testing
        internal static string GetSource(ITestResult[] testResults)
        {
            string source = Array.Find(testResults, x => !string.IsNullOrEmpty(x.Source))?.Source;
            if (source != null)
            {
                source = Path.GetFileName(source);
                if (source.EndsWith(".dll"))
                {
                    return source.Substring(0, source.Length - 4);
                }
            }
            return source;
        }

        // Internal for testing
        internal async Task<int> CreateTestRun(CancellationToken cancellationToken)
        {
            string runName = $"{(string.IsNullOrEmpty(Source) ? "Unknown Test Source" : Source)} (OS: {System.Runtime.InteropServices.RuntimeInformation.OSDescription}, Job: {_jobName}, Agent: {_agentName})";

            TestRun testRun = new TestRun
            {
                Name = runName,
                BuildId = _buildId,
                StartedDate = StartedDate,
                IsAutomated = true
            };

            return await _apiClient.AddTestRun(testRun, cancellationToken).ConfigureAwait(false);
        }

        // Internal for testing
        internal IEnumerable<IGrouping<string, ITestResult>> GroupTestResultsByParent(ITestResult[] testResults) =>
            testResults.GroupBy(x =>
            {
                // Namespace.ClassName.MethodName
                string name = x.FullyQualifiedName;

                if (Source != null && name.StartsWith(Source + "."))
                {
                    // remove the namespace
                    name = name.Substring(Source.Length + 1);
                }

                // At this point, name should always have at least one '.' to represent the Class.Method
                if (_groupTestResultsByClassName)
                {
                    // We need to start at the opening method if there is one
                    int startIndex = name.IndexOf('(');
                    if (startIndex < 0)
                    {
                        startIndex = name.Length - 1;
                    }

                    // remove the method name to get just the class name
                    return name.Substring(0, name.LastIndexOf('.', startIndex));
                }
                else
                {
                    // remove the class name to get just the method name
                    return name.Substring(name.IndexOf('.') + 1);
                }
            });

        // Internal for testing
        internal async Task CreateParents(IEnumerable<IGrouping<string, ITestResult>> testResultsByParent, CancellationToken cancellationToken)
        {
            // Find the parents that don't exist
            string[] parentsToAdd = testResultsByParent
                .Select(x => x.Key)
                .Where(x => !Parents.ContainsKey(x))
                .ToArray();

            // Batch an add operation and record the new parent IDs
            DateTime startedDate = DateTime.UtcNow;
            if (parentsToAdd.Length > 0)
            {
                int[] parents = await _apiClient.AddTestCases(RunId, parentsToAdd, startedDate, Source, cancellationToken).ConfigureAwait(false);
                for (int i = 0; i < parents.Length; i++)
                {
                    Parents.Add(parentsToAdd[i], new TestResultParent(parents[i], startedDate));
                }
            }
        }

        private Task SendTestResults(IEnumerable<IGrouping<string, ITestResult>> testResultsByParent, CancellationToken cancellationToken)
        {
            return _apiClient.UpdateTestResults(RunId, Parents, testResultsByParent, cancellationToken);
        }

        private async Task SendTestsCompleted(VstpTestRunComplete testRunComplete, CancellationToken cancellationToken)
        {
            DateTime completedDate = DateTime.UtcNow;

            // Mark all parents as completed (but only if we actually created a parent)
            if (RunId != 0)
            {
                await _apiClient.UpdateTestResults(RunId, testRunComplete, cancellationToken);

                if (Parents.Values.Count > 0)
                {
                    await _apiClient.MarkTestCasesCompleted(RunId, Parents.Values, completedDate, cancellationToken).ConfigureAwait(false);
                }

                await _apiClient.MarkTestRunCompleted(RunId, StartedDate, completedDate, cancellationToken).ConfigureAwait(false);
            }
        }
    }
}