using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using AzurePipelines.TestLogger.Json;
using Microsoft.VisualStudio.TestPlatform.ObjectModel;

namespace AzurePipelines.TestLogger
{
    internal abstract class ApiClient : IApiClient
    {
        private readonly string _baseUrl;
        private readonly string _apiVersionString;
        private HttpClient _client;

        protected const string _dateFormatString = "yyyy-MM-ddTHH:mm:ss.FFFZ";

        protected ApiClient(string collectionUri, string teamProject, string apiVersionString)
        {
            if (collectionUri == null)
            {
                throw new ArgumentNullException(nameof(collectionUri));
            }

            if (teamProject == null)
            {
                throw new ArgumentNullException(nameof(teamProject));
            }

            _baseUrl = $"{collectionUri}{teamProject}/_apis/test/runs";
            _apiVersionString = apiVersionString ?? throw new ArgumentNullException(nameof(apiVersionString));
        }

        public bool Verbose { get; set; }

        public string BuildRequestedFor { get; set; }

        public IApiClient WithAccessToken(string accessToken)
        {
            // The : character delimits username (which should be empty here) and password in basic auth headers
            _client = new HttpClient();
            _client.DefaultRequestHeaders.Authorization
                 = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.ASCII.GetBytes($":{accessToken}")));
            return this;
        }

        public IApiClient WithDefaultCredentials()
        {
            _client = new HttpClient(new HttpClientHandler
            {
                UseDefaultCredentials = true
            });
            return this;
        }

        public async Task<string> MarkTestCasesCompleted(int testRunId, IEnumerable<TestResultParent> testCases, DateTime completedDate, CancellationToken cancellationToken)
        {
            string requestBody = GetTestCasesAsCompleted(testCases, completedDate);

            return await SendAsync(new HttpMethod("PATCH"), $"/{testRunId}/results", requestBody, cancellationToken).ConfigureAwait(false);
        }

        public async Task<int> AddTestRun(TestRun testRun, CancellationToken cancellationToken)
        {
            string requestBody = new Dictionary<string, object>
            {
                { "name", testRun.Name },
                { "build", new Dictionary<string, object> { { "id", testRun.BuildId } } },
                { "startedDate", testRun.StartedDate.ToString(_dateFormatString) },
                { "isAutomated", true }
            }.ToJson();

            string responseString = await SendAsync(HttpMethod.Post, null, requestBody, cancellationToken).ConfigureAwait(false);
            using (StringReader reader = new StringReader(responseString))
            {
                JsonObject response = JsonDeserializer.Deserialize(reader) as JsonObject;
                return response.ValueAsInt("id");
            }
        }

        public async Task UpdateTestResults(int testRunId, Dictionary<string, TestResultParent> testCaseTestResults, IEnumerable<IGrouping<string, ITestResult>> testResultsByParent, CancellationToken cancellationToken)
        {
            DateTime completedDate = DateTime.UtcNow;

            string requestBody = GetTestResults(testCaseTestResults, testResultsByParent, completedDate);

            await SendAsync(new HttpMethod("PATCH"), $"/{testRunId}/results", requestBody, cancellationToken).ConfigureAwait(false);

            await UploadConsoleOutputsAndErrors(testRunId, testCaseTestResults, testResultsByParent, cancellationToken);

            await UploadTestResultFiles(testRunId, testCaseTestResults, testResultsByParent, cancellationToken);
        }

        public async Task UpdateTestResults(int testRunId, VstpTestRunComplete testRunComplete, CancellationToken cancellationToken)
        {
            await UploadTestResultFiles(testRunId, null, testRunComplete.Attachments, cancellationToken);
        }

        public async Task<int[]> AddTestCases(int testRunId, string[] testCaseNames, DateTime startedDate, string source, CancellationToken cancellationToken)
        {
            string requestBody = "[ " + string.Join(", ", testCaseNames.Select(x =>
            {
                Dictionary<string, object> properties = new Dictionary<string, object>
                {
                    { "testCaseTitle", x },
                    { "automatedTestName", x },
                    { "resultGroupType", "generic" },
                    { "outcome", "Passed" }, // Start with a passed outcome initially
                    { "state", "InProgress" },
                    { "startedDate", startedDate.ToString(_dateFormatString) },
                    { "automatedTestType", "UnitTest" },
                    { "automatedTestTypeId", "13cdc9d9-ddb5-4fa4-a97d-d965ccfc6d4b" } // This is used in the sample response and also appears in web searches
                };
                if (!string.IsNullOrEmpty(source))
                {
                    properties.Add("automatedTestStorage", source);
                }
                return properties.ToJson();
            })) + " ]";

            string responseString = await SendAsync(HttpMethod.Post, $"/{testRunId}/results", requestBody, cancellationToken).ConfigureAwait(false);
            using (StringReader reader = new StringReader(responseString))
            {
                JsonObject response = JsonDeserializer.Deserialize(reader) as JsonObject;
                JsonArray testCases = (JsonArray)response.Value("value");
                if (testCases.Length != testCaseNames.Length)
                {
                    throw new Exception("Unexpected number of test cases added");
                }

                List<int> testCaseIds = new List<int>();
                for (int c = 0; c < testCases.Length; c++)
                {
                    int id = ((JsonObject)testCases[c]).ValueAsInt("id");
                    testCaseIds.Add(id);
                }

                return testCaseIds.ToArray();
            }
        }

        public async Task MarkTestRunCompleted(int testRunId, DateTime startedDate, DateTime completedDate, CancellationToken cancellationToken)
        {
            // Mark the overall test run as completed
            string requestBody = $@"{{
                ""state"": ""Completed"",
                ""startedDate"": ""{startedDate.ToString(_dateFormatString)}"",
                ""completedDate"": ""{completedDate.ToString(_dateFormatString)}""
            }}";

            await SendAsync(new HttpMethod("PATCH"), $"/{testRunId}", requestBody, cancellationToken).ConfigureAwait(false);
        }

        protected Dictionary<string, object> GetTestResultProperties(ITestResult testResult)
        {
            Dictionary<string, object> properties = new Dictionary<string, object>
            {
                { "outcome", testResult.Outcome.ToString() },
                { "computerName", testResult.ComputerName },
                { "runBy", new Dictionary<string, object> { { "displayName", BuildRequestedFor } } }
            };

            AddAdditionalTestResultProperties(testResult, properties);

            if (testResult.Outcome == TestOutcome.Passed || testResult.Outcome == TestOutcome.Failed)
            {
                long duration = Convert.ToInt64(testResult.Duration.TotalMilliseconds);
                properties.Add("durationInMs", duration.ToString(CultureInfo.InvariantCulture));

                string errorStackTrace = testResult.ErrorStackTrace;
                if (!string.IsNullOrEmpty(errorStackTrace))
                {
                    properties.Add("stackTrace", errorStackTrace);
                }

                string errorMessage = testResult.ErrorMessage;

                if (!string.IsNullOrEmpty(errorMessage))
                {
                    properties.Add("errorMessage", errorMessage);
                }
            }
            else
            {
                // Handle output type skip, NotFound and None
            }

            return properties;
        }

        internal abstract string GetTestCasesAsCompleted(IEnumerable<TestResultParent> testCases, DateTime completedDate);

        internal abstract string GetTestResults(
            Dictionary<string, TestResultParent> testCaseTestResults,
            IEnumerable<IGrouping<string, ITestResult>> testResultsByParent,
            DateTime completedDate);

        internal virtual void AddAdditionalTestResultProperties(ITestResult testResult, Dictionary<string, object> properties)
        {
        }

        internal virtual async Task<string> SendAsync(HttpMethod method, string endpoint, string body, CancellationToken cancellationToken, string apiVersionString = null)
        {
            if (method == null)
            {
                throw new ArgumentNullException(nameof(method));
            }

            if (string.IsNullOrEmpty(apiVersionString))
            {
                apiVersionString = _apiVersionString;
            }

            string requestUri = $"{_baseUrl}{endpoint}?api-version={apiVersionString}";
            HttpRequestMessage request = new HttpRequestMessage(method, requestUri);
            if (body != null)
            {
                request.Content = new StringContent(body, Encoding.UTF8, "application/json");
            }

            HttpResponseMessage response = await _client.SendAsync(request, cancellationToken).ConfigureAwait(false);
            string responseBody = await response.Content.ReadAsStringAsync().ConfigureAwait(false);

            response.Content?.Dispose();

            if (Verbose)
            {
                Console.WriteLine($"Request:\n{method} {requestUri}\n{body}\n\nResponse:\n{response.StatusCode}\n{responseBody}");
            }

            try
            {
                response.EnsureSuccessStatusCode();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error from AzurePipelines logger while sending {method} to {requestUri}\nBody:\n{body}\nException:\n{ex}");
                throw;
            }

            return responseBody;
        }

        private async Task UploadConsoleOutputsAndErrors(int testRunId, Dictionary<string, TestResultParent> testCaseTestResults, IEnumerable<IGrouping<string, ITestResult>> testResultsByParent, CancellationToken cancellationToken)
        {
            foreach (IGrouping<string, ITestResult> testResultByParent in testResultsByParent)
            {
                TestResultParent parent = testCaseTestResults[testResultByParent.Key];

                foreach (ITestResult testResult in testResultByParent.Select(x => x))
                {
                    StringBuilder stdErr = new StringBuilder();
                    StringBuilder stdOut = new StringBuilder();
                    foreach (TestResultMessage m in testResult.Messages)
                    {
                        if (TestResultMessage.StandardOutCategory.Equals(m.Category, StringComparison.OrdinalIgnoreCase))
                        {
                            stdOut.AppendLine(m.Text);
                        }
                        else if (TestResultMessage.StandardErrorCategory.Equals(m.Category, StringComparison.OrdinalIgnoreCase))
                        {
                            stdErr.AppendLine(m.Text);
                        }
                    }

                    if (stdOut.Length > 0)
                    {
                        await AttachTextAsFile(testRunId, parent.Id, stdOut.ToString(), "console output.txt", null, cancellationToken);
                    }

                    if (stdErr.Length > 0)
                    {
                        await AttachTextAsFile(testRunId, parent.Id, stdErr.ToString(), "console error.txt", null, cancellationToken);
                    }
                }
            }
        }

        private async Task UploadTestResultFiles(int testRunId, Dictionary<string, TestResultParent> testCaseTestResults, IEnumerable<IGrouping<string, ITestResult>> testResultsByParent, CancellationToken cancellationToken)
        {
            foreach (IGrouping<string, ITestResult> testResultByParent in testResultsByParent)
            {
                TestResultParent parent = testCaseTestResults[testResultByParent.Key];

                foreach (ITestResult testResult in testResultByParent.Select(x => x))
                {
                    await UploadTestResultFiles(testRunId, parent.Id, testResult.Attachments, cancellationToken);
                }
            }
        }

        private async Task UploadTestResultFiles(int testRunId, int? testResultId, ICollection<AttachmentSet> attachmentSets, CancellationToken cancellationToken)
        {
            if (attachmentSets.Count > 0)
            {
                string message = $"Attaching files to test run {testRunId}";

                if (testResultId != null)
                {
                    message += $" and test result {testResultId}";
                }

                message += "...";

                Console.WriteLine(message);
            }

            foreach (AttachmentSet attachmentSet in attachmentSets)
            {
                if (attachmentSet.Attachments.Count > 0)
                {
                    Console.WriteLine($"Attaching files in set {attachmentSet.DisplayName} {attachmentSet.Uri}...");
                }

                foreach (UriDataAttachment attachment in attachmentSet.Attachments)
                {
                    Console.WriteLine($"Attaching file {attachment.Description} {attachment.Uri.LocalPath}...");

                    await AttachFile(testRunId, testResultId, attachment.Uri.LocalPath, attachment.Description, cancellationToken);
                }
            }
        }

        private async Task AttachTextAsFile(int testRunId, int testResultId, string fileContents, string fileName, string comment, CancellationToken cancellationToken)
        {
            byte[] contentAsBytes = Encoding.UTF8.GetBytes(fileContents);
            await AttachFile(testRunId, testResultId, contentAsBytes, fileName, comment, cancellationToken);
        }

        private async Task AttachFile(int testRunId, int? testResultId, string filePath, string comment, CancellationToken cancellationToken)
        {
            byte[] contentAsBytes = File.ReadAllBytes(filePath);
            string fileName = Path.GetFileName(filePath);
            await AttachFile(testRunId, testResultId, contentAsBytes, fileName, comment, cancellationToken);
        }

        private async Task AttachFile(int testRunId, int? testResultId, byte[] fileContents, string fileName, string comment, CancellationToken cancellationToken)
        {
            string contentAsBase64 = Convert.ToBase64String(fileContents);

            string attachmentType = "GeneralAttachment";

            if (fileName.EndsWith(".coverage", StringComparison.OrdinalIgnoreCase))
            {
                attachmentType = "CodeCoverage";
            }

            Dictionary<string, object> props = new Dictionary<string, object>
            {
                { "stream", contentAsBase64 },
                { "fileName", fileName },
                { "comment", comment },
                { "attachmentType", attachmentType }
            };

            string requestBody = props.ToJson();

            if (testResultId == null)
            {
                // https://docs.microsoft.com/en-us/rest/api/azure/devops/test/attachments/create%20test%20run%20attachment
                // https://docs.microsoft.com/en-us/previous-versions/azure/devops/integrate/previous-apis/test/attachments?view=tfs-2015#attach-a-file-to-a-test-run
                await SendAsync(new HttpMethod("POST"), $"/{testRunId}/attachments", requestBody, cancellationToken, "2.0-preview").ConfigureAwait(false);
            }
            else
            {
                // https://docs.microsoft.com/en-us/rest/api/azure/devops/test/attachments/create%20test%20result%20attachment
                // https://docs.microsoft.com/en-us/azure/devops/integrate/previous-apis/test/attachments?view=tfs-2015#attach-a-file-to-a-test-result
                await SendAsync(new HttpMethod("POST"), $"/{testRunId}/results/{testResultId}/attachments", requestBody, cancellationToken, "2.0-preview").ConfigureAwait(false);
            }
        }
    }
}