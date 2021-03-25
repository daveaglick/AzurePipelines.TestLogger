using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using AzurePipelines.TestLogger.Json;
using Microsoft.VisualStudio.TestPlatform.ObjectModel;

namespace AzurePipelines.TestLogger
{
    internal class ApiClientV5 : ApiClient
    {
        public ApiClientV5(string collectionUri, string teamProject, string apiVersionString)
            : base(collectionUri, teamProject, apiVersionString)
        {
        }

        internal override string GetTestCasesAsCompleted(IEnumerable<TestResultParent> testCases, DateTime completedDate)
        {
            // https://docs.microsoft.com/en-us/rest/api/azure/devops/test/results/add?view=azure-devops-rest-5.0
            return "[ " + string.Join(", ", testCases.Select(x =>
                $@"{{
                ""id"": {x.Id},
                ""state"": ""Completed"",
                ""startedDate"": ""{x.StartedDate.ToString(_dateFormatString)}"",
                ""completedDate"": ""{completedDate.ToString(_dateFormatString)}""
            }}")) + " ]";
        }

        internal override string GetTestResults(
            Dictionary<string, TestResultParent> testCaseTestResults,
            IEnumerable<IGrouping<string, ITestResult>> testResultsByParent,
            DateTime completedDate)
        {
            // https://docs.microsoft.com/en-us/rest/api/azure/devops/test/results/update?view=azure-devops-rest-5.0
            return "[ " + string.Join(", ", testResultsByParent.Select(x =>
            {
                TestResultParent parent = testCaseTestResults[x.Key];
                string subResults = "[ " + string.Join(", ", x.Select(y => GetTestResultProperties(y).ToJson())) + " ]";
                string failedOutcome = x.Any(t => t.Outcome == TestOutcome.Failed) ? "\"outcome\": \"Failed\"," : null;

                return $@"{{
                    ""id"": {parent.Id},
                    ""completedDate"": ""{completedDate.ToString(_dateFormatString)}"",
                    {failedOutcome}
                    ""subResults"": {subResults}
                }}";
            })) + " ]";
        }

        internal override void AddAdditionalTestResultProperties(ITestResult testResult, Dictionary<string, object> properties)
        {
            properties.Add("displayName", testResult.DisplayName);
        }
    }
}