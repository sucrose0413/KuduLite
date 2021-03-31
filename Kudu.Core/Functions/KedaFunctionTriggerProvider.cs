﻿using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;

namespace Kudu.Core.Functions
{
    /// <summary>
    /// Returns "<see cref="IEnumerable<ScaleTrigger>"/> for KEDA scalers"
    /// </summary>
    public class KedaFunctionTriggerProvider
    {
        public IEnumerable<ScaleTrigger> GetFunctionTriggers(string zipFilePath)
        {
            if (!File.Exists(zipFilePath))
            {
                return null;
            }

            string hostJsonText = null;
            var triggerBindings = new List<FunctionTrigger>();
            using (var zip = ZipFile.OpenRead(zipFilePath))
            {
                var hostJsonEntry = zip.Entries.FirstOrDefault(e => IsHostJson(e.FullName));
                if (hostJsonEntry != null)
                {
                    using (var reader = new StreamReader(hostJsonEntry.Open()))
                    {
                        hostJsonText = reader.ReadToEnd();
                    }
                }

                var entries = zip.Entries
                    .Where(e => IsFunctionJson(e.FullName));

                foreach (var entry in entries)
                {
                    using (var stream = entry.Open())
                    {
                        using (var reader = new StreamReader(stream))
                        {
                            triggerBindings.AddRange(ParseFunctionJson(GetFunctionName(entry), reader.ReadToEnd()));
                        }
                    }
                }
            }

            var durableTriggers = triggerBindings.Where(b => IsDurable(b));
            var standardTriggers = triggerBindings.Where(b => !IsDurable(b));

            var kedaScaleTriggers = new List<ScaleTrigger>();
            kedaScaleTriggers.AddRange(GetStandardScaleTriggers(standardTriggers));

            // Durable Functions triggers are treated as a group and get configuration from host.json
            if (durableTriggers.Any() && TryGetDurableKedaTrigger(hostJsonText, out ScaleTrigger durableScaleTrigger))
            {
                kedaScaleTriggers.Add(durableScaleTrigger);
            }

            bool IsFunctionJson(string fullName)
            {
                return fullName.EndsWith(Constants.FunctionsConfigFile) &&
                       fullName.Count(c => c == '/' || c == '\\') == 1;
            }

            bool IsHostJson(string fullName)
            {
                return fullName.Equals(Constants.FunctionsHostConfigFile, StringComparison.OrdinalIgnoreCase);
            }

            bool IsDurable(FunctionTrigger function) =>
                function.Type.Equals("orchestrationTrigger", StringComparison.OrdinalIgnoreCase) ||
                function.Type.Equals("activityTrigger", StringComparison.OrdinalIgnoreCase) ||
                function.Type.Equals("entityTrigger", StringComparison.OrdinalIgnoreCase);

            return kedaScaleTriggers;
        }

        private IEnumerable<FunctionTrigger> ParseFunctionJson(string functionName, string functionJson)
        {
            var json = JObject.Parse(functionJson);
            if (json.TryGetValue("disabled", out JToken value))
            {
                string stringValue = value.ToString();
                if (!bool.TryParse(stringValue, out bool disabled))
                {
                    string expandValue = System.Environment.GetEnvironmentVariable(stringValue);
                    disabled = string.Equals(expandValue, "1", StringComparison.OrdinalIgnoreCase) ||
                               string.Equals(expandValue, "true", StringComparison.OrdinalIgnoreCase);
                }

                if (disabled)
                {
                    yield break;
                }
            }

            var excluded = json.TryGetValue("excluded", out value) && (bool)value;
            if (excluded)
            {
                yield break;
            }

            foreach (JObject binding in (JArray)json["bindings"])
            {
                var type = (string)binding["type"];
                if (type.EndsWith("Trigger", StringComparison.OrdinalIgnoreCase))
                {
                    yield return new FunctionTrigger(functionName, binding, type);
                }
            }
        }

        private static IEnumerable<ScaleTrigger> GetStandardScaleTriggers(IEnumerable<FunctionTrigger> standardTriggers)
        {
            foreach (FunctionTrigger function in standardTriggers)
            {
                var scaleTrigger = new ScaleTrigger
                {
                    Type = GetKedaTriggerType(function.Type),
                    Metadata = PopulateMetadataDictionary(function.Binding)
                };
                yield return scaleTrigger;
            }
        }

        private static string GetFunctionName(ZipArchiveEntry zipEntry)
        {
            if (string.IsNullOrWhiteSpace(zipEntry?.FullName))
            {
                return string.Empty;
            }

            return zipEntry.FullName.Split('/').Length == 2 ? zipEntry.FullName.Split('/')[0] : zipEntry.FullName.Split('\\')[0];
        }

        public static string GetKedaTriggerType(string triggerType)
        {
            if (string.IsNullOrEmpty(triggerType))
            {
                throw new ArgumentNullException(nameof(triggerType));
            }

            triggerType = triggerType.ToLower();

            switch (triggerType)
            {
                case "queuetrigger":
                    return "azure-queue";

                case "kafkatrigger":
                    return "kafka";

                case "blobtrigger":
                    return "azure-blob";

                case "servicebustrigger":
                    return "azure-servicebus";

                case "eventhubtrigger":
                    return "azure-eventhub";

                case "rabbitmqtrigger":
                    return "rabbitmq";

                case "httpTrigger":
                    return "httpTrigger";

                default:
                    return triggerType;
            }
        }

        private static bool TryGetDurableKedaTrigger(string hostJsonText, out ScaleTrigger scaleTrigger)
        {
            scaleTrigger = null;
            if (string.IsNullOrEmpty(hostJsonText))
            {
                return false;
            }

            JObject hostJson = JObject.Parse(hostJsonText);

            // Reference: https://docs.microsoft.com/azure/azure-functions/durable/durable-functions-bindings#durable-functions-2-0-host-json
            string durableStorageProviderPath = $"{Constants.Extensions}.{Constants.DurableTask}.{Constants.DurableTaskStorageProvider}";
            JObject storageProviderConfig = hostJson.SelectToken(durableStorageProviderPath) as JObject;
            string storageType = storageProviderConfig?["type"]?.ToString();

            // Custom storage types are supported starting in Durable Functions v2.4.2
            if (string.Equals(storageType, Constants.DurableTaskMicrosoftSqlProviderType, StringComparison.OrdinalIgnoreCase))
            {
                scaleTrigger = new ScaleTrigger
                {
                    // MSSQL scaler reference: https://keda.sh/docs/2.2/scalers/mssql/
                    Type = Constants.MicrosoftSqlScaler,
                    Metadata = new Dictionary<string, string>
                    {
                        ["query"] = "SELECT dt.GetScaleMetric()",
                        ["targetValue"] = "1", // super-conservative default
                        ["connectionStringFromEnv"] = storageProviderConfig?[Constants.DurableTaskSqlConnectionName]?.ToString(),
                    }
                };
            }
            else
            {
                // TODO: Support for the Azure Storage and Netherite backends
            }

            return scaleTrigger != null;
        }

        // match https://github.com/Azure/azure-functions-core-tools/blob/6bfab24b2743f8421475d996402c398d2fe4a9e0/src/Azure.Functions.Cli/Kubernetes/KEDA/V2/KedaV2Resource.cs#L91
        public static IDictionary<string, string> PopulateMetadataDictionary(JToken t)
        {
            const string ConnectionField = "connection";
            const string ConnectionFromEnvField = "connectionFromEnv";

            IDictionary<string, string> metadata = t.ToObject<Dictionary<string, JToken>>()
                .Where(i => i.Value.Type == JTokenType.String)
                .ToDictionary(k => k.Key, v => v.Value.ToString());

            var triggerType = t["type"].ToString().ToLower();

            switch (triggerType)
            {
                case TriggerTypes.AzureBlobStorage:
                case TriggerTypes.AzureStorageQueue:
                    metadata[ConnectionFromEnvField] = metadata[ConnectionField] ?? "AzureWebJobsStorage";
                    metadata.Remove(ConnectionField);
                    break;
                case TriggerTypes.AzureServiceBus:
                    metadata[ConnectionFromEnvField] = metadata[ConnectionField] ?? "AzureWebJobsServiceBus";
                    metadata.Remove(ConnectionField);
                    break;
                case TriggerTypes.AzureEventHubs:
                    metadata[ConnectionFromEnvField] = metadata[ConnectionField];
                    metadata.Remove(ConnectionField);
                    break;

                case TriggerTypes.Kafka:
                    metadata["bootstrapServers"] = metadata["brokerList"];
                    metadata.Remove("brokerList");
                    metadata.Remove("protocol");
                    metadata.Remove("authenticationMode");
                    break;

                case TriggerTypes.RabbitMq:
                    metadata["hostFromEnv"] = metadata["connectionStringSetting"];
                    metadata.Remove("connectionStringSetting");
                    break;
            }

            // Clean-up for all triggers

            metadata.Remove("type");
            metadata.Remove("name");

            return metadata;
        }

        private class FunctionTrigger
        {
            public FunctionTrigger(string functionName, JObject binding, string type)
            {
                this.FunctionName = functionName;
                this.Binding = binding;
                this.Type = type;
            }

            public string FunctionName { get; }
            public JObject Binding { get; }
            public string Type { get; }
        }

        public class TriggerTypes
        {
            public const string AzureBlobStorage = "blobtrigger";
            public const string AzureEventHubs = "eventhubtrigger";
            public const string AzureServiceBus = "servicebustrigger";
            public const string AzureStorageQueue = "queuetrigger";
            public const string Kafka = "kafkatrigger";
            public const string RabbitMq = "rabbitmqtrigger";
        }
    }
}