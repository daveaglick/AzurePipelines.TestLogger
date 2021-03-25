using System;
using System.Collections.Generic;
using Microsoft.VisualStudio.TestPlatform.ObjectModel;
using Microsoft.VisualStudio.TestPlatform.ObjectModel.Client;
using Microsoft.VisualStudio.TestPlatform.ObjectModel.Logging;

namespace AzurePipelines.TestLogger
{
    [FriendlyName(AzurePipelinesTestLogger.FriendlyName)]
    [ExtensionUri(AzurePipelinesTestLogger.ExtensionUri)]
    public class AzurePipelinesTestLogger : ITestLoggerWithParameters
    {
        /// <summary>
        /// Uri used to uniquely identify the logger.
        /// </summary>
        public const string ExtensionUri = "logger://Microsoft/TestPlatform/AzurePiplinesTestLogger/v1";

        /// <summary>
        /// Alternate user friendly string to uniquely identify the logger.
        /// </summary>
        public const string FriendlyName = "AzurePipelines";

        private readonly IEnvironmentVariableProvider _environmentVariableProvider;
        private readonly IApiClientFactory _apiClientFactory;
        private IApiClient _apiClient;
        private LoggerQueue _queue;
        private bool _groupTestResultsByClassName = true;

        public AzurePipelinesTestLogger()
        {
            _environmentVariableProvider = new EnvironmentVariableProvider();
            _apiClientFactory = new ApiClientFactory();
        }

        // Used for testing
        internal AzurePipelinesTestLogger(IEnvironmentVariableProvider environmentVariableProvider, IApiClientFactory apiClientFactory)
        {
            _environmentVariableProvider = environmentVariableProvider;
            _apiClientFactory = apiClientFactory;
        }

        public void Initialize(TestLoggerEvents events, string testRunDirectory)
        {
            Initialize(events, new Dictionary<string, string>());
        }

        public void Initialize(TestLoggerEvents events, Dictionary<string, string> parameters)
        {
            if (events == null)
            {
                throw new ArgumentNullException(nameof(events));
            }

            if (parameters == null)
            {
                throw new ArgumentNullException(nameof(parameters));
            }

            if (!GetRequiredVariable(EnvironmentVariableNames.TeamFoundationCollectionUri, out string collectionUri)
                || !GetRequiredVariable(EnvironmentVariableNames.TeamProject, out string teamProject)
                || !GetRequiredVariable(EnvironmentVariableNames.BuildId, out string buildId)
                || !GetRequiredVariable(EnvironmentVariableNames.BuildRequestedFor, out string buildRequestedFor)
                || !GetRequiredVariable(EnvironmentVariableNames.AgentName, out string agentName)
                || !GetRequiredVariable(EnvironmentVariableNames.AgentJobName, out string jobName))
            {
                return;
            }

            if (_apiClient == null)
            {
                string apiVersion = "5.0";

                if (parameters.TryGetValue(TestLoggerParameters.ApiVersion, out string apiVersionParameterValue))
                {
                    apiVersion = apiVersionParameterValue;
                }

                if (parameters.TryGetValue(TestLoggerParameters.UseDefaultCredentials, out string useDefaultCredentialsString)
                    && bool.TryParse(useDefaultCredentialsString, out bool useDefaultCredentials)
                    && useDefaultCredentials)
                {
                    _apiClient = _apiClientFactory.CreateWithDefaultCredentials(collectionUri, teamProject, apiVersion);
                }
                else if (GetRequiredVariable(EnvironmentVariableNames.AccessToken, out string accessToken))
                {
                    _apiClient = _apiClientFactory.CreateWithAccessToken(accessToken, collectionUri, teamProject, apiVersion);
                }
                else
                {
                    throw new ArgumentException($"Expected environment variable {EnvironmentVariableNames.AccessToken} or {TestLoggerParameters.UseDefaultCredentials} parameter", nameof(parameters));
                }

                if (parameters.TryGetValue(TestLoggerParameters.Verbose, out string verboseParameterValue))
                {
                    if (!bool.TryParse(verboseParameterValue, out bool verbose))
                    {
                        throw new ArgumentException($"Expected {TestLoggerParameters.Verbose} parameter to be boolean.", nameof(parameters));
                    }

                    _apiClient.Verbose = verbose;
                }

                _apiClient.BuildRequestedFor = buildRequestedFor;
            }

            if (parameters.TryGetValue(TestLoggerParameters.GroupTestResultsByClassName, out string groupTestResultsByClassNameString)
                && bool.TryParse(groupTestResultsByClassNameString, out bool groupTestResultsByClassName))
            {
                _groupTestResultsByClassName = groupTestResultsByClassName;
            }

            _queue = new LoggerQueue(_apiClient, buildId, agentName, jobName, _groupTestResultsByClassName);

            // Register for the events
            events.TestRunMessage += TestMessageHandler;
            events.TestResult += TestResultHandler;
            events.TestRunComplete += TestRunCompleteHandler;
        }

        private bool GetRequiredVariable(string name, out string value)
        {
            value = _environmentVariableProvider.GetEnvironmentVariable(name);
            if (string.IsNullOrEmpty(value))
            {
                Console.WriteLine($"AzurePipelines.TestLogger: Not an Azure Pipelines test run, environment variable {name} not set.");
                return false;
            }
            return true;
        }

        private void TestMessageHandler(object sender, TestRunMessageEventArgs e)
        {
            // Add code to handle message
        }

        private void TestResultHandler(object sender, TestResultEventArgs e) =>
            _queue.Enqueue(new VstpTestResult(e.Result));

        private void TestRunCompleteHandler(object sender, TestRunCompleteEventArgs e) =>
            _queue.Flush(new VstpTestRunComplete(e.AttachmentSets));
    }
}
