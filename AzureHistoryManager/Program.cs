using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Azure.Management.Fluent;
using Microsoft.Azure.Management.Monitor.Fluent.Models;
using Microsoft.Azure.Management.ResourceManager.Fluent;
using Microsoft.Azure.Management.ResourceManager.Fluent.Core;
using Microsoft.Azure.Management.ResourceManager.Fluent.Models;
using Microsoft.Extensions.Configuration;
using Microsoft.Rest.Azure;

namespace AzureHistoryManager
{
    class Program
    {
        static async Task Main(string[] args)
        {
            // 0. More info here: https://docs.microsoft.com/en-us/dotnet/azure/dotnet-sdk-azure-authenticate?view=azure-dotnet#mgmt-auth
            // 1. Run 'az ad sp create-for-rbac --sdk-auth'
            // 2. Save JSON output to a notepad
            // 3. From the command line, in the project directory, run:
            //      dotnet user-secrets set ClientID CLIENT_ID
            //      dotnet user-secrets set ClientSecret CLIENT_SECRET
            //      dotnet user-secrets set TenantID TENANT_ID

            var config = new ConfigurationBuilder()
                .AddUserSecrets("AzureHistoryManager")
                .Build();
            var clientId = GetRequiredUserSecret(config, "ClientID");
            var clientSecret = GetRequiredUserSecret(config, "ClientSecret");
            var tenantId = GetRequiredUserSecret(config, "TenantId");

            var credentials = SdkContext.AzureCredentialsFactory
              .FromServicePrincipal(
                  clientId,
                  clientSecret,
                  tenantId,
                  AzureEnvironment.AzureGlobalCloud);

            var azure = Azure
                .Configure()
                .Authenticate(credentials)
                .WithDefaultSubscription();

            var sub = azure.GetCurrentSubscription();
            Console.WriteLine("SUBSCRIPTION: " + sub.DisplayName);


            var restClient = RestClient.Configure()
                .WithEnvironment(AzureEnvironment.AzureGlobalCloud)
                .WithCredentials(credentials)
                .Build();
            var rmc = new ResourceManagementClient(restClient)
            {
                SubscriptionId = sub.SubscriptionId,
            };

            // Update all info in Azure
            var resources = await rmc.Resources.ListAsync();
            await EnsureResorucesHaveCreatorInfo(azure, rmc, resources);

            // Retrieve info again and report results
            var updatedResources = await rmc.Resources.ListAsync();
            Console.WriteLine("Creator,CreatedDate,Lifetime,Resource Name,Resource ID,Kind");
            foreach (var resource in updatedResources)
            {
                Console.WriteLine($"\"{GetResourceCreator(resource)}\",\"{GetResourceCreatedDate(resource)}\",\"{GetResourceLifetime(resource)}\",\"{resource.Name}\",\"{resource.Id}\",\"{resource.Kind}\"");
            }

            Console.ReadKey();
        }

        private static string GetRequiredUserSecret(IConfigurationRoot config, string key)
        {
            var value = config[key];
            if (string.IsNullOrEmpty(value))
            {
                throw new InvalidOperationException($"Couldn't find User Secret named '{key}' in configuration. Run 'dotnet user-secrets set {key} VALUE' to set the value.");
            }
            return value;
        }

        private static string GetResourceLifetime(GenericResourceInner resource)
        {
            return GetResourceTag(resource, "azh-lifetime");
        }

        private static string GetResourceCreatedDate(GenericResourceInner resource)
        {
            return GetResourceTag(resource, "azh-createddate");
        }

        private static string GetResourceCreator(GenericResourceInner resource)
        {
            return GetResourceTag(resource, "azh-creator");
        }

        private static string GetResourceTag(GenericResourceInner resource,string tag)
        {
            if (resource.Tags == null || !resource.Tags.ContainsKey(tag))
            {
                return "<unknown>";
            }
            return resource.Tags[tag];
        }

        private static async Task EnsureResorucesHaveCreatorInfo(IAzure azure, ResourceManagementClient rmc, IPage<GenericResourceInner> resources)
        {
            foreach (var res in resources)
            {
                await EnsureHasCreatorInfo(azure, rmc, res);
            }
        }

        private static async Task EnsureHasCreatorInfo(IAzure azure, ResourceManagementClient resourceManager, GenericResourceInner res)
        {
            Console.WriteLine($"RESOURCE: {res.Name} ({res.Type})");

            // Info on which resources support tags:
            // https://docs.microsoft.com/en-us/azure/azure-resource-manager/tag-support

            // First, try to find a tag with the creator info
            if (res.Tags != null && res.Tags.ContainsKey("azh-creator"))
            {
                Console.WriteLine($"\tKnown owner: {res.Tags["azh-creator"]}");
                Console.WriteLine($"\tKnown created date: {res.Tags["azh-createddate"]}");
                return;
            }

            // If the resource isn't tagged, look at the activity log to find activity with a caller
            var oldestLogWithPeople = GetOldestLogWithPeople(azure, res);

            var resourceCreator = "<unknown>";
            var resourceCreatedDate = "<unknown>";

            if (oldestLogWithPeople == null)
            {
                Console.WriteLine("\tUnknown author and creation date");
            }
            else
            {
                resourceCreator = oldestLogWithPeople.Caller;
                if (oldestLogWithPeople.EventTimestamp != null)
                {
                    resourceCreatedDate = oldestLogWithPeople.EventTimestamp.Value.ToString("yyyy-MM-dd");
                }
                Console.WriteLine($"\tResource created by {resourceCreator} @ {resourceCreatedDate}");
            }

            // Info on tag naming rules:
            // https://docs.microsoft.com/en-us/azure/azure-resource-manager/resource-group-using-tags

            if (res.Tags == null)
            {
                res.Tags = new Dictionary<string, string>();
            }
            res.Tags.Add("azh-creator", resourceCreator);
            res.Tags.Add("azh-createddate", resourceCreatedDate);

            Console.Write($"\tUpdating resource tags...");
            try
            {
                await resourceManager.Resources.UpdateByIdAsync(
                    resourceId: res.Id,
                    apiVersion: "2014-04-01", // Found this version by setting the wrong version and seeing the exception message, which tells you supported versions
                    parameters: res);
                Console.WriteLine($" Done!");
            }
            catch (Exception ex)
            {
                Console.WriteLine($" Failed! {ex.Message}");
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
