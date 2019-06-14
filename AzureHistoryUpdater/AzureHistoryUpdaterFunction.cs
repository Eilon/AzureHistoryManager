using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Azure.Management.Fluent;
using Microsoft.Azure.Management.Monitor.Fluent.Models;
using Microsoft.Azure.Management.ResourceManager.Fluent;
using Microsoft.Azure.Management.ResourceManager.Fluent.Authentication;
using Microsoft.Azure.Management.ResourceManager.Fluent.Core;
using Microsoft.Azure.Management.ResourceManager.Fluent.Models;
using Microsoft.Azure.WebJobs;
using Microsoft.Extensions.Logging;
using Microsoft.Rest.Azure;

namespace AzureHistoryUpdater
{
    public static class AZHFunction
    {
        // Need to use "0 0 8 * * 0" --> Run every Sunday at 8:00am
        // Example: "0 */2 * * * *" --> Run every 2 minutes
        [FunctionName("HistoryUpdater")]
        public static async Task Run(
            [TimerTrigger("0 */1 * * * *")] TimerInfo myTimer,
            //[TimerTrigger("0 0 8 * * 0")] TimerInfo myTimer,
            ILogger log)
        {
            log.LogInformation($"C# Timer trigger function executed at: {DateTime.Now}");

            try
            {
                var customTokenProvider = new AzureCredentials(
                     new MSILoginInformation(MSIResourceType.AppService),
                     AzureEnvironment.AzureGlobalCloud);

                var restClient = RestClient
                    .Configure()
                    .WithEnvironment(AzureEnvironment.AzureGlobalCloud)
                    .WithCredentials(customTokenProvider)
                    .Build();

                var azure = Azure.Authenticate(restClient, customTokenProvider.TenantId).WithDefaultSubscription();

                var sub = azure.GetCurrentSubscription(); // has current sub info!
                log.LogInformation("Using Azure subscription {SUB}", sub.DisplayName);

                // Start reading info about resources in the subscription...
                var rmc = new ResourceManagementClient(restClient)
                {
                    SubscriptionId = sub.SubscriptionId,
                };

                // Update all info in Azure
                var resources = await rmc.Resources.ListAsync();
                await EnsureResourcesHaveCreatorInfo(azure, rmc, resources, log);
            }
            catch (Exception ex)
            {
                log.LogError(ex, $"Error doing stuff: {ex.Message}\r\n\r\n----\r\n{ex.StackTrace}\r\n-----");
                throw;
            }
        }

        private static async Task EnsureResourcesHaveCreatorInfo(IAzure azure, ResourceManagementClient rmc, IPage<GenericResourceInner> resources, ILogger log)
        {
            foreach (var res in resources)
            {
                await EnsureResourceHasCreatorInfo(azure, rmc, res, log);
            }
        }

        private static async Task EnsureResourceHasCreatorInfo(IAzure azure, ResourceManagementClient resourceManager, GenericResourceInner res, ILogger log)
        {
            // Info on which resources support tags:
            // https://docs.microsoft.com/en-us/azure/azure-resource-manager/tag-support

            // First, try to find a tag with the creator info
            if (res.Tags != null && res.Tags.ContainsKey("azh-creator"))
            {
                log.LogDebug("Resource {NAME} with ID {ID} created by {CREATOR} at {DATE} is already tagged", res.Name, res.Id, res.Tags["azh-creator"], res.Tags["azh-createddate"]);
                return;
            }

            // If the resource isn't tagged, look at the activity log to find activity with a caller
            var oldestLogWithPeople = GetOldestLogWithPeople(azure, res);

            var resourceCreator = "<unknown>";
            var resourceCreatedDate = "<unknown>";

            if (oldestLogWithPeople == null)
            {
                // Activity log didn't have creator info (e.g. resource is very old, and the log doesn't go far enough back)
            }
            else
            {
                resourceCreator = oldestLogWithPeople.Caller;
                if (oldestLogWithPeople.EventTimestamp != null)
                {
                    resourceCreatedDate = oldestLogWithPeople.EventTimestamp.Value.ToString("yyyy-MM-dd");
                }
            }

            // Info on tag naming rules:
            // https://docs.microsoft.com/en-us/azure/azure-resource-manager/resource-group-using-tags

            if (res.Tags == null)
            {
                res.Tags = new Dictionary<string, string>();
            }
            res.Tags.Add("azh-creator", resourceCreator);
            res.Tags.Add("azh-createddate", resourceCreatedDate);

            try
            {
                await resourceManager.Resources.UpdateByIdAsync(
                    resourceId: res.Id,
                    apiVersion: "2014-04-01", // Found this version by setting the wrong version and seeing the exception message, which tells you supported versions
                    parameters: res);
                log.LogDebug("Resource {NAME} with ID {ID} created by {CREATOR} at {DATE} is now tagged", res.Name, res.Id, res.Tags["azh-creator"], res.Tags["azh-createddate"]);
            }
            catch (Exception ex)
            {
                log.LogError(ex, "Resource {NAME} with ID {ID} created by {CREATOR} at {DATE} could not be tagged!", res.Name, res.Id, res.Tags["azh-creator"], res.Tags["azh-createddate"]);
            }
        }

        private static IEventData GetOldestLogWithPeople(IAzure azure, GenericResourceInner res)
        {
            var resourceId = res.Id;

            var logs = azure.ActivityLogs.DefineQuery()
                .StartingFrom(DateTime.Now.AddDays(-60))
                .EndsBefore(DateTime.Now)
                .WithResponseProperties(EventDataPropertyName.Parse("caller"), EventDataPropertyName.EventTimestamp, EventDataPropertyName.OperationName)
                .FilterByResource(resourceId)
                .Execute();

            var logsWithPeople = logs
                .OrderBy(log => log.EventTimestamp)
                .Where(log => HasCallerEmail(log))
                .ToList();

            var oldestLogWithPeople = logsWithPeople.FirstOrDefault();
            return oldestLogWithPeople;
        }

        private static bool HasCallerEmail(IEventData log)
        {
            // Not very scientific, but works in practice
            return
                log.Caller != null &&
                log.Caller.Contains('@', StringComparison.Ordinal);
        }
    }
}
