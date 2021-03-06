﻿// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the MIT License. See License.txt in the project root for license information.

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Azure.WebJobs.Extensions;
using Microsoft.Azure.WebJobs.Extensions.ApiHub;
using Microsoft.Azure.WebJobs.Extensions.Bindings;
using Microsoft.Azure.WebJobs.Extensions.DocumentDB;
using Microsoft.Azure.WebJobs.Extensions.MobileApps;
using Microsoft.Azure.WebJobs.Extensions.NotificationHubs;
using Microsoft.Azure.WebJobs.Host;
using Microsoft.Azure.WebJobs.Host.Indexers;
using Microsoft.Azure.WebJobs.Script.Config;
using Microsoft.Azure.WebJobs.Script.Description;
using Microsoft.Azure.WebJobs.Script.Diagnostics;
using Microsoft.Azure.WebJobs.Script.Extensibility;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Microsoft.Azure.WebJobs.Script
{
    public class ScriptHost : JobHost
    {
        internal const int DebugModeTimeoutMinutes = 15;
        private const string HostAssemblyName = "ScriptHost";
        private readonly AutoResetEvent _restartEvent = new AutoResetEvent(false);
        private string _instanceId;
        private Action<FileSystemEventArgs> _restart;
        private FileSystemWatcher _scriptFileWatcher;
        private FileSystemWatcher _debugModeFileWatcher;
        private int _directoryCountSnapshot;
        private BlobLeaseManager _blobLeaseManager;
        private static readonly TimeSpan MinTimeout = TimeSpan.FromSeconds(1);
        private static readonly TimeSpan MaxTimeout = TimeSpan.FromMinutes(5);
        private static readonly Regex FunctionNameValidationRegex = new Regex(@"^[a-z][a-z0-9_\-]{0,127}$(?<!^host$)", RegexOptions.Compiled | RegexOptions.IgnoreCase);

        protected ScriptHost(ScriptHostConfiguration scriptConfig)
            : base(scriptConfig.HostConfig)
        {
            ScriptConfig = scriptConfig;
            FunctionErrors = new Dictionary<string, Collection<string>>(StringComparer.OrdinalIgnoreCase);
            NodeFunctionInvoker.UnhandledException += OnUnhandledException;
        }

        public static readonly string Version = GetAssemblyFileVersion(typeof(ScriptHost).Assembly);

        public event EventHandler IsPrimaryChanged;

        public string InstanceId
        {
            get
            {
                if (_instanceId == null)
                {
                    _instanceId = Environment.GetEnvironmentVariable(EnvironmentSettingNames.AzureWebsiteInstanceId)
                        ?? Environment.MachineName.GetHashCode().ToString("X").PadLeft(32, '0');

                    _instanceId = _instanceId.Substring(0, 32);
                }

                return _instanceId;
            }
        }

        public TraceWriter TraceWriter { get; private set; }

        public ScriptHostConfiguration ScriptConfig { get; private set; }

        public virtual Collection<FunctionDescriptor> Functions { get; private set; }

        public Dictionary<string, Collection<string>> FunctionErrors { get; private set; }

        public virtual bool IsPrimary
        {
            get
            {
                return _blobLeaseManager?.HasLease ?? false;
            }
        }

        public AutoResetEvent RestartEvent
        {
            get
            {
                return _restartEvent;
            }
        }

        /// <summary>
        /// Gets a value indicating whether the host is in debug mode.
        /// </summary>
        public virtual bool InDebugMode
        {
            get
            {
                return (DateTime.UtcNow - LastDebugNotify).TotalMinutes < DebugModeTimeoutMinutes;
            }
        }

        /// <summary>
        /// Gets a value indicating whether logs should be written to disk.
        /// </summary>
        internal virtual bool FileLoggingEnabled
        {
            get
            {
                return ScriptConfig.FileLoggingMode == FileLoggingMode.Always ||
                    (ScriptConfig.FileLoggingMode == FileLoggingMode.DebugOnly && InDebugMode);
            }
        }

        internal DateTime LastDebugNotify { get; set; }

        /// <summary>
        /// Notifies this host that it should be in debug mode.
        /// </summary>
        public void NotifyDebug()
        {
            // This is redundant, since we're also watching the debug marker
            // file. However, we leave this here for assurances.
            LastDebugNotify = DateTime.UtcNow;

            // create or update the debug sentinel file to trigger a
            // debug timeout update across all instances
            string debugSentinelFileName = Path.Combine(ScriptConfig.RootLogPath, "Host", ScriptConstants.DebugSentinelFileName);
            if (!File.Exists(debugSentinelFileName))
            {
                File.WriteAllText(debugSentinelFileName, "This is a system managed marker file used to control runtime debug mode behavior.");
            }
            else
            {
                File.SetLastWriteTimeUtc(debugSentinelFileName, DateTime.UtcNow);
            }
        }

        internal void AddFunctionError(string functionName, string error)
        {
            functionName = Utility.GetFunctionShortName(functionName);

            Collection<string> functionErrors = new Collection<string>();
            if (!FunctionErrors.TryGetValue(functionName, out functionErrors))
            {
                FunctionErrors[functionName] = functionErrors = new Collection<string>();
            }
            functionErrors.Add(error);
        }

        public virtual async Task CallAsync(string method, Dictionary<string, object> arguments, CancellationToken cancellationToken = default(CancellationToken))
        {
            // TODO: Don't hardcode Functions Type name
            // TODO: Validate inputs
            // TODO: Cache this lookup result
            string typeName = "Functions";
            method = method.ToLowerInvariant();
            Type type = ScriptConfig.HostConfig.TypeLocator.GetTypes().SingleOrDefault(p => p.Name == typeName);
            MethodInfo methodInfo = type.GetMethods().SingleOrDefault(p => p.Name.ToLowerInvariant() == method);

            await CallAsync(methodInfo, arguments, cancellationToken);
        }

        protected virtual void Initialize()
        {
            IMetricsLogger metricsLogger = ScriptConfig.HostConfig.GetService<IMetricsLogger>();
            if (metricsLogger == null)
            {
                metricsLogger = new MetricsLogger();
                ScriptConfig.HostConfig.AddService<IMetricsLogger>(metricsLogger);
            }

            using (metricsLogger.LatencyEvent(MetricEventNames.HostStartupLatency))
            {
                // read host.json and apply to JobHostConfiguration
                string hostConfigFilePath = Path.Combine(ScriptConfig.RootScriptPath, ScriptConstants.HostMetadataFileName);

                // If it doesn't exist, create an empty JSON file
                if (!File.Exists(hostConfigFilePath))
                {
                    File.WriteAllText(hostConfigFilePath, "{}");
                }

                if (ScriptConfig.HostConfig.IsDevelopment)
                {
                    ScriptConfig.HostConfig.UseDevelopmentSettings();
                }
                else
                {
                    // TEMP: Until https://github.com/Azure/azure-webjobs-sdk-script/issues/100 is addressed
                    // we're using some presets that are a good middle ground
                    ScriptConfig.HostConfig.Queues.MaxPollingInterval = TimeSpan.FromSeconds(10);
                    ScriptConfig.HostConfig.Singleton.ListenerLockPeriod = TimeSpan.FromSeconds(15);
                }

                string json = File.ReadAllText(hostConfigFilePath);

                JObject hostConfig;
                try
                {
                    hostConfig = JObject.Parse(json);
                }
                catch (JsonException ex)
                {
                    throw new FormatException(string.Format("Unable to parse {0} file.", ScriptConstants.HostMetadataFileName), ex);
                }

                ApplyConfiguration(hostConfig, ScriptConfig);

                // Set up a host level TraceMonitor that will receive notification
                // of ALL errors that occur. This allows us to inspect/log errors.
                var traceMonitor = new TraceMonitor()
                    .Filter(p => { return true; })
                    .Subscribe(HandleHostError);
                ScriptConfig.HostConfig.Tracing.Tracers.Add(traceMonitor);

                TraceWriter = ScriptConfig.TraceWriter;
                TraceLevel hostTraceLevel = ScriptConfig.HostConfig.Tracing.ConsoleLevel;
                if (ScriptConfig.FileLoggingMode != FileLoggingMode.Never)
                {
                    // Host file logging is only done conditionally
                    string hostLogFilePath = Path.Combine(ScriptConfig.RootLogPath, "Host");
                    TraceWriter fileTraceWriter = new FileTraceWriter(hostLogFilePath, hostTraceLevel).Conditional(p => FileLoggingEnabled);

                    if (TraceWriter != null)
                    {
                        // create a composite writer so our host logs are written to both
                        TraceWriter = new CompositeTraceWriter(new[] { TraceWriter, fileTraceWriter });
                    }
                    else
                    {
                        TraceWriter = fileTraceWriter;
                    }
                }

                if (TraceWriter != null)
                {
                    ScriptConfig.HostConfig.Tracing.Tracers.Add(TraceWriter);
                }
                else
                {
                    // if no TraceWriter has been configured, default it to Console
                    TraceWriter = new ConsoleTraceWriter(hostTraceLevel);
                }

                string hostLogPath = Path.Combine(ScriptConfig.RootLogPath, "Host");
                string debugSentinelFileName = Path.Combine(hostLogPath, ScriptConstants.DebugSentinelFileName);
                this.LastDebugNotify = File.GetLastWriteTime(debugSentinelFileName);

                _debugModeFileWatcher = new FileSystemWatcher(hostLogPath, ScriptConstants.DebugSentinelFileName)
                {
                    EnableRaisingEvents = true
                };
                _debugModeFileWatcher.Created += OnDebugModeFileChanged;
                _debugModeFileWatcher.Changed += OnDebugModeFileChanged;

                var bindingProviders = LoadBindingProviders(ScriptConfig, hostConfig, TraceWriter);
                ScriptConfig.BindingProviders = bindingProviders;

                TraceWriter.Info(string.Format(CultureInfo.InvariantCulture, "Reading host configuration file '{0}'", hostConfigFilePath));

                if (ScriptConfig.FileWatchingEnabled)
                {
                    _scriptFileWatcher = new FileSystemWatcher(ScriptConfig.RootScriptPath)
                    {
                        IncludeSubdirectories = true,
                        EnableRaisingEvents = true
                    };
                    _scriptFileWatcher.Changed += OnFileChanged;
                    _scriptFileWatcher.Created += OnFileChanged;
                    _scriptFileWatcher.Deleted += OnFileChanged;
                    _scriptFileWatcher.Renamed += OnFileChanged;
                }

                // If a file change should result in a restart, we debounce the event to
                // ensure that only a single restart is triggered within a specific time window.
                // This allows us to deal with a large set of file change events that might
                // result from a bulk copy/unzip operation. In such cases, we only want to
                // restart after ALL the operations are complete and there is a quiet period.
                _restart = (e) =>
                {
                    TraceWriter.Info(string.Format(CultureInfo.InvariantCulture, "File change of type '{0}' detected for '{1}'", e.ChangeType, e.FullPath));
                    TraceWriter.Info("Host configuration has changed. Signaling restart.");

                    // signal host restart
                    _restartEvent.Set();
                };
                _restart = _restart.Debounce(500);

                // take a snapshot so we can detect function additions/removals
                _directoryCountSnapshot = Directory.EnumerateDirectories(ScriptConfig.RootScriptPath).Count();

                var storageString = AmbientConnectionStringProvider.Instance.GetConnectionString(ConnectionStringNames.Storage);
                if (storageString == null)
                {
                    // Disable core storage 
                    ScriptConfig.HostConfig.StorageConnectionString = null;
                }
                else
                {
                    // Create the lease manager that will keep handle the primary host blob lease acquisition and renewal 
                    // and subscribe for change notifications.
                    _blobLeaseManager = BlobLeaseManager.Create(storageString, TimeSpan.FromSeconds(15), ScriptConfig.HostConfig.HostId, InstanceId, TraceWriter);
                    _blobLeaseManager.HasLeaseChanged += BlobLeaseManagerHasLeaseChanged;
                }

                List<FunctionDescriptorProvider> descriptionProviders = new List<FunctionDescriptorProvider>()
                {
                    new ScriptFunctionDescriptorProvider(this, ScriptConfig),
                    new NodeFunctionDescriptorProvider(this, ScriptConfig),
                    new DotNetFunctionDescriptorProvider(this, ScriptConfig),
                    new PowerShellFunctionDescriptorProvider(this, ScriptConfig)
                };

                // Allow BindingProviders to initialize
                foreach (var bindingProvider in ScriptConfig.BindingProviders)
                {
                    try
                    {
                        bindingProvider.Initialize();
                    }
                    catch (Exception ex)
                    {
                        // If we're unable to initialize a binding provider for any reason, log the error
                        // and continue
                        TraceWriter.Error(string.Format("Error initializing binding provider '{0}'", bindingProvider.GetType().FullName), ex);
                    }
                }

                // read all script functions and apply to JobHostConfiguration
                Collection<FunctionDescriptor> functions = ReadFunctions(ScriptConfig, descriptionProviders);
                Collection<CustomAttributeBuilder> typeAttributes = CreateTypeAttributes(ScriptConfig);
                string defaultNamespace = "Host";
                string typeName = string.Format(CultureInfo.InvariantCulture, "{0}.{1}", defaultNamespace, "Functions");
                TraceWriter.Info(string.Format(CultureInfo.InvariantCulture, "Generating {0} job function(s)", functions.Count));
                Type type = FunctionGenerator.Generate(HostAssemblyName, typeName, typeAttributes, functions);
                List<Type> types = new List<Type>();
                types.Add(type);

                ScriptConfig.HostConfig.TypeLocator = new TypeLocator(types);

                Functions = functions;

                if (ScriptConfig.FileLoggingMode != FileLoggingMode.Never)
                {
                    PurgeOldLogDirectories();
                }
            }
        }

        internal static Collection<CustomAttributeBuilder> CreateTypeAttributes(ScriptHostConfiguration scriptConfig)
        {
            Collection<CustomAttributeBuilder> customAttributes = new Collection<CustomAttributeBuilder>();

            // apply the timeout settings to our type
            if (scriptConfig.FunctionTimeout != null)
            {
                Type timeoutType = typeof(TimeoutAttribute);
                ConstructorInfo ctorInfo = timeoutType.GetConstructor(new[] { typeof(string) });

                PropertyInfo[] propertyInfos = new[]
                {
                    timeoutType.GetProperty("ThrowOnTimeout"),
                    timeoutType.GetProperty("TimeoutWhileDebugging")
                };

                // Hard-code these for now. Eventually elevate to config
                object[] propertyValues = new object[]
                {
                    true,
                    true
                };

                CustomAttributeBuilder timeoutBuilder = new CustomAttributeBuilder(
                    ctorInfo,
                    new object[] { scriptConfig.FunctionTimeout.ToString() },
                    propertyInfos,
                    propertyValues
                );

                customAttributes.Add(timeoutBuilder);
            }

            return customAttributes;
        }

        /// <summary>
        /// Whenever the debug marker file changes we update our debug timeout
        /// </summary>
        private void OnDebugModeFileChanged(object sender, FileSystemEventArgs e)
        {
            LastDebugNotify = DateTime.UtcNow;
        }

        private void BlobLeaseManagerHasLeaseChanged(object sender, EventArgs e)
        {
            IsPrimaryChanged?.Invoke(this, EventArgs.Empty);
        }

        /// <summary>
        /// Iterate through all function log directories and remove any that don't
        /// correspond to a function.
        /// </summary>
        private void PurgeOldLogDirectories()
        {
            try
            {
                if (!Directory.Exists(this.ScriptConfig.RootScriptPath))
                {
                    return;
                }

                // Create a lookup of all potential functions (whether they're valid or not)
                // It is important that we determine functions based on the presence of a folder,
                // not whether we've identified a valid function from that folder. This ensures
                // that we don't delete logs/secrets for functions that transition into/out of
                // invalid unparsable states.
                var functionLookup = Directory.EnumerateDirectories(this.ScriptConfig.RootScriptPath).ToLookup(p => Path.GetFileName(p), StringComparer.OrdinalIgnoreCase);

                string rootLogFilePath = Path.Combine(this.ScriptConfig.RootLogPath, "Function");
                if (!Directory.Exists(rootLogFilePath))
                {
                    return;
                }

                var logFileDirectory = new DirectoryInfo(rootLogFilePath);
                foreach (var logDir in logFileDirectory.GetDirectories())
                {
                    if (!functionLookup.Contains(logDir.Name))
                    {
                        // the directory no longer maps to a running function
                        // so delete it
                        try
                        {
                            logDir.Delete(recursive: true);
                        }
                        catch
                        {
                            // Purge is best effort
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                // Purge is best effort
                TraceWriter.Error("An error occurred while purging log files", ex);
            }
        }

        public static ScriptHost Create(ScriptHostConfiguration scriptConfig = null)
        {
            if (scriptConfig == null)
            {
                scriptConfig = new ScriptHostConfiguration();
            }

            if (!Path.IsPathRooted(scriptConfig.RootScriptPath))
            {
                scriptConfig.RootScriptPath = Path.Combine(Environment.CurrentDirectory, scriptConfig.RootScriptPath);
            }

            ScriptHost scriptHost = new ScriptHost(scriptConfig);
            try
            {
                scriptHost.Initialize();
            }
            catch (Exception ex)
            {
                if (scriptHost.TraceWriter != null)
                {
                    scriptHost.TraceWriter.Error("ScriptHost initialization failed", ex);
                }
                throw;
            }

            return scriptHost;
        }

        private static Collection<ScriptBindingProvider> LoadBindingProviders(ScriptHostConfiguration config, JObject hostMetadata, TraceWriter traceWriter)
        {
            JobHostConfiguration hostConfig = config.HostConfig;

            // Register our built in extensions
            var bindingProviderTypes = new Collection<Type>()
            {
                // binding providers defined in this assembly
                typeof(WebJobsCoreScriptBindingProvider),
                typeof(ServiceBusScriptBindingProvider),

                // binding providers defined in known extension assemblies
                typeof(CoreExtensionsScriptBindingProvider),
                typeof(ApiHubScriptBindingProvider),
                typeof(DocumentDBScriptBindingProvider),
                typeof(MobileAppsScriptBindingProvider),
                typeof(NotificationHubScriptBindingProvider),
                typeof(SendGridScriptBindingProvider),
                typeof(TwilioScriptBindingProvider)
            };

            // Create the binding providers
            var bindingProviders = new Collection<ScriptBindingProvider>();
            foreach (var bindingProviderType in bindingProviderTypes)
            {
                try
                {
                    var provider = (ScriptBindingProvider)Activator.CreateInstance(bindingProviderType, new object[] { hostConfig, hostMetadata, traceWriter });
                    bindingProviders.Add(provider);
                }
                catch (Exception ex)
                {
                    // If we're unable to load create a binding provider for any reason, log
                    // the error and continue
                    traceWriter.Error(string.Format("Unable to create binding provider '{0}'", bindingProviderType.FullName), ex);
                }
            }

            return bindingProviders;
        }

        private static FunctionMetadata ParseFunctionMetadata(string functionName, JObject configMetadata)
        {
            FunctionMetadata functionMetadata = new FunctionMetadata
            {
                Name = functionName
            };

            JValue triggerDisabledValue = null;
            JArray bindingArray = (JArray)configMetadata["bindings"];
            if (bindingArray == null || bindingArray.Count == 0)
            {
                throw new FormatException("At least one binding must be declared.");
            }

            if (bindingArray != null)
            {
                foreach (JObject binding in bindingArray)
                {
                    BindingMetadata bindingMetadata = BindingMetadata.Create(binding);
                    functionMetadata.Bindings.Add(bindingMetadata);
                    if (bindingMetadata.IsTrigger)
                    {
                        triggerDisabledValue = (JValue)binding["disabled"];
                    }
                }
            }

            // A function can be disabled at the trigger or function level
            if (IsDisabled(triggerDisabledValue) ||
                IsDisabled((JValue)configMetadata["disabled"]))
            {
                functionMetadata.IsDisabled = true;
            }

            JToken value = null;
            if (configMetadata.TryGetValue("excluded", StringComparison.OrdinalIgnoreCase, out value) &&
                value.Type == JTokenType.Boolean)
            {
                functionMetadata.IsExcluded = (bool)value;
            }

            return functionMetadata;
        }

        private Collection<FunctionDescriptor> ReadFunctions(ScriptHostConfiguration config, IEnumerable<FunctionDescriptorProvider> descriptorProviders)
        {
            string scriptRootPath = config.RootScriptPath;
            List<FunctionMetadata> metadatas = new List<FunctionMetadata>();

            foreach (var scriptDir in Directory.EnumerateDirectories(scriptRootPath))
            {
                string functionName = null;

                try
                {
                    // read the function config
                    string functionConfigPath = Path.Combine(scriptDir, ScriptConstants.FunctionMetadataFileName);
                    if (!File.Exists(functionConfigPath))
                    {
                        // not a function directory
                        continue;
                    }

                    functionName = Path.GetFileNameWithoutExtension(scriptDir);

                    if (ScriptConfig.Functions != null &&
                        !ScriptConfig.Functions.Contains(functionName, StringComparer.OrdinalIgnoreCase))
                    {
                        // a functions filter has been specified and the current function is
                        // not in the filter list
                        continue;
                    }

                    ValidateFunctionName(functionName);

                    // TODO: we need to define a json schema document and do
                    // schema validation and give more informative responses 
                    string json = File.ReadAllText(functionConfigPath);
                    JObject functionConfig = JObject.Parse(json);
                    FunctionMetadata metadata = ParseFunctionMetadata(functionName, functionConfig);

                    if (metadata.IsExcluded)
                    {
                        TraceWriter.Info(string.Format("Function '{0}' is marked as excluded", functionName));
                        continue;
                    }

                    if (metadata.IsDisabled)
                    {
                        TraceWriter.Info(string.Format("Function '{0}' is disabled", functionName));
                    }

                    // determine the primary script
                    string[] functionFiles = Directory.EnumerateFiles(scriptDir).Where(p => Path.GetFileName(p).ToLowerInvariant() != ScriptConstants.FunctionMetadataFileName).ToArray();
                    if (functionFiles.Length == 0)
                    {
                        AddFunctionError(functionName, "No function script files present.");
                        continue;
                    }
                    string scriptFile = DeterminePrimaryScriptFile(functionConfig, functionFiles);
                    if (string.IsNullOrEmpty(scriptFile))
                    {
                        AddFunctionError(functionName,
                            "Unable to determine the primary function script. Try renaming your entry point script to 'run' (or 'index' in the case of Node), " +
                            "or alternatively you can specify the name of the entry point script explicitly by adding a 'scriptFile' property to your function metadata.");
                        continue;
                    }
                    metadata.ScriptFile = scriptFile;

                    // determine the script type based on the primary script file extension
                    metadata.ScriptType = ParseScriptType(metadata.ScriptFile);

                    metadata.EntryPoint = (string)functionConfig["entryPoint"];

                    metadatas.Add(metadata);
                }
                catch (Exception ex)
                {
                    // log any unhandled exceptions and continue
                    AddFunctionError(functionName, ex.Message);
                }
            }

            return ReadFunctions(metadatas, descriptorProviders);
        }

        internal static void ValidateFunctionName(string functionName)
        {
            if (!FunctionNameValidationRegex.IsMatch(functionName))
            {
                throw new InvalidOperationException(string.Format("'{0}' is not a valid function name.", functionName));
            }
        }

        /// <summary>
        /// Determines which script should be considered the "primary" entry point script.
        /// </summary>
        internal static string DeterminePrimaryScriptFile(JObject functionConfig, string[] functionFiles)
        {
            if (functionFiles.Length == 1)
            {
                // if there is only a single file, that file is primary
                return functionFiles[0];
            }
            else
            {
                // First see if there is an explicit primary file indicated
                // in config. If so use that.
                string functionPrimary = null;
                string scriptFileName = (string)functionConfig["scriptFile"];
                if (!string.IsNullOrEmpty(scriptFileName))
                {
                    functionPrimary = functionFiles.FirstOrDefault(p =>
                        string.Compare(Path.GetFileName(p), scriptFileName, StringComparison.OrdinalIgnoreCase) == 0);
                }
                else
                {
                    // if there is a "run" file, that file is primary,
                    // for Node, any index.js file is primary
                    functionPrimary = functionFiles.FirstOrDefault(p =>
                        Path.GetFileNameWithoutExtension(p).ToLowerInvariant() == "run" ||
                        Path.GetFileName(p).ToLowerInvariant() == "index.js");
                }

                return functionPrimary;
            }
        }

        private static ScriptType ParseScriptType(string scriptFilePath)
        {
            string extension = Path.GetExtension(scriptFilePath).ToLowerInvariant().TrimStart('.');

            switch (extension)
            {
                case "csx":
                case "cs":
                    return ScriptType.CSharp;
                case "js":
                    return ScriptType.Javascript;
                case "ps1":
                    return ScriptType.PowerShell;
                case "cmd":
                case "bat":
                    return ScriptType.WindowsBatch;
                case "py":
                    return ScriptType.Python;
                case "php":
                    return ScriptType.PHP;
                case "sh":
                    return ScriptType.Bash;
                case "fsx":
                    return ScriptType.FSharp;
                default:
                    return ScriptType.Unknown;
            }
        }

        internal Collection<FunctionDescriptor> ReadFunctions(List<FunctionMetadata> metadatas, IEnumerable<FunctionDescriptorProvider> descriptorProviders)
        {
            Collection<FunctionDescriptor> functionDescriptors = new Collection<FunctionDescriptor>();
            foreach (FunctionMetadata metadata in metadatas)
            {
                try
                {
                    FunctionDescriptor descriptor = null;
                    foreach (var provider in descriptorProviders)
                    {
                        if (provider.TryCreate(metadata, out descriptor))
                        {
                            break;
                        }
                    }

                    if (descriptor != null)
                    {
                        functionDescriptors.Add(descriptor);
                    }
                }
                catch (Exception ex)
                {
                    // log any unhandled exceptions and continue
                    AddFunctionError(metadata.Name, ex.Message);
                }
            }

            return functionDescriptors;
        }

        internal static void ApplyConfiguration(JObject config, ScriptHostConfiguration scriptConfig)
        {
            JobHostConfiguration hostConfig = scriptConfig.HostConfig;

            JArray functions = (JArray)config["functions"];
            if (functions != null && functions.Count > 0)
            {
                scriptConfig.Functions = new Collection<string>();
                foreach (var function in functions)
                {
                    scriptConfig.Functions.Add((string)function);
                }
            }

            // We may already have a host id, but the one from the JSON takes precedence
            JToken hostId = (JToken)config["id"];
            if (hostId != null)
            {
                hostConfig.HostId = (string)hostId;
            }
            else if (hostConfig.HostId == null)
            {
                throw new InvalidOperationException("An 'id' must be specified in the host configuration.");
            }

            JToken fileWatchingEnabled = (JToken)config["fileWatchingEnabled"];
            if (fileWatchingEnabled != null && fileWatchingEnabled.Type == JTokenType.Boolean)
            {
                scriptConfig.FileWatchingEnabled = (bool)fileWatchingEnabled;
            }

            // Configure the set of watched directories, adding the standard built in
            // set to any the user may have specified
            if (scriptConfig.WatchDirectories == null)
            {
                scriptConfig.WatchDirectories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            }
            scriptConfig.WatchDirectories.Add("node_modules");
            JToken watchDirectories = config["watchDirectories"];
            if (watchDirectories != null && watchDirectories.Type == JTokenType.Array)
            {
                foreach (JToken directory in watchDirectories.Where(p => p.Type == JTokenType.String))
                {
                    scriptConfig.WatchDirectories.Add((string)directory);
                }
            }

            // Apply Singleton configuration
            JObject configSection = (JObject)config["singleton"];
            JToken value = null;
            if (configSection != null)
            {
                if (configSection.TryGetValue("lockPeriod", out value))
                {
                    hostConfig.Singleton.LockPeriod = TimeSpan.Parse((string)value, CultureInfo.InvariantCulture);
                }
                if (configSection.TryGetValue("listenerLockPeriod", out value))
                {
                    hostConfig.Singleton.ListenerLockPeriod = TimeSpan.Parse((string)value, CultureInfo.InvariantCulture);
                }
                if (configSection.TryGetValue("listenerLockRecoveryPollingInterval", out value))
                {
                    hostConfig.Singleton.ListenerLockRecoveryPollingInterval = TimeSpan.Parse((string)value, CultureInfo.InvariantCulture);
                }
                if (configSection.TryGetValue("lockAcquisitionTimeout", out value))
                {
                    hostConfig.Singleton.LockAcquisitionTimeout = TimeSpan.Parse((string)value, CultureInfo.InvariantCulture);
                }
                if (configSection.TryGetValue("lockAcquisitionPollingInterval", out value))
                {
                    hostConfig.Singleton.LockAcquisitionPollingInterval = TimeSpan.Parse((string)value, CultureInfo.InvariantCulture);
                }
            }

            // Apply Tracing/Logging configuration
            configSection = (JObject)config["tracing"];
            if (configSection != null)
            {
                if (configSection.TryGetValue("consoleLevel", out value))
                {
                    TraceLevel consoleLevel;
                    if (Enum.TryParse<TraceLevel>((string)value, true, out consoleLevel))
                    {
                        hostConfig.Tracing.ConsoleLevel = consoleLevel;
                    }
                }

                if (configSection.TryGetValue("fileLoggingMode", out value))
                {
                    FileLoggingMode fileLoggingMode;
                    if (Enum.TryParse<FileLoggingMode>((string)value, true, out fileLoggingMode))
                    {
                        scriptConfig.FileLoggingMode = fileLoggingMode;
                    }
                }
            }

            if (config.TryGetValue("functionTimeout", out value))
            {
                TimeSpan requestedTimeout = TimeSpan.Parse((string)value, CultureInfo.InvariantCulture);

                // Only apply limits if this is Dynamic.
                if (IsDynamicSku() && (requestedTimeout < MinTimeout || requestedTimeout > MaxTimeout))
                {
                    string message = $"{nameof(scriptConfig.FunctionTimeout)} must be between {MinTimeout} and {MaxTimeout}.";
                    throw new ArgumentException(message);
                }

                scriptConfig.FunctionTimeout = requestedTimeout;
            }
            else if (IsDynamicSku())
            {
                // Apply a default if this is running on Dynamic.
                scriptConfig.FunctionTimeout = MaxTimeout;
            }
        }

        private void OnUnhandledException(object sender, UnhandledExceptionEventArgs e)
        {
            HandleHostError((Exception)e.ExceptionObject);
        }

        private void HandleHostError(Microsoft.Azure.WebJobs.Extensions.TraceFilter traceFilter)
        {
            // TODO: figure out why sometimes we get null events
            var events = traceFilter.Events.Where(p => p != null).ToArray();

            foreach (TraceEvent traceEvent in events)
            {
                var exception = traceEvent.Exception ?? new InvalidOperationException(traceEvent.Message);
                HandleHostError(exception);
            }
        }

        private void HandleHostError(Exception exception)
        {
            if (exception == null)
            {
                throw new ArgumentNullException("exception");
            }

            // First, ensure that we've logged to the host log
            // Also ensure we flush immediately to ensure any buffered logs
            // are written
            TraceWriter.Error("A ScriptHost error has occurred", exception);
            TraceWriter.Flush();

            if (exception is FunctionInvocationException)
            {
                // For all function invocation errors, we notify the invoker so it can
                // log the error as needed to its function specific logs.
                FunctionInvocationException invocationException = exception as FunctionInvocationException;
                NotifyInvoker(invocationException.MethodName, invocationException);
            }
            else if (exception is FunctionIndexingException)
            {
                // For all startup time indexing errors, we accumulate them per function
                FunctionIndexingException indexingException = exception as FunctionIndexingException;
                string formattedError = Utility.FlattenException(indexingException);
                AddFunctionError(indexingException.MethodName, formattedError);

                // Also notify the invoker so the error can also be written to the function
                // log file
                NotifyInvoker(indexingException.MethodName, indexingException);

                // Mark the error as handled so indexing will continue
                indexingException.Handled = true;
            }
            else
            {
                // See if we can identify which function caused the error, and if we can
                // log the error as needed to its function specific logs.
                FunctionDescriptor function = null;
                if (TryGetFunctionFromException(Functions, exception, out function))
                {
                    NotifyInvoker(function.Name, exception);
                }
            }
        }

        internal static bool TryGetFunctionFromException(Collection<FunctionDescriptor> functions, Exception exception, out FunctionDescriptor function)
        {
            function = null;

            string errorStack = exception.ToString().ToLowerInvariant();
            foreach (var currFunction in functions)
            {
                // For each function, we search the entire error stack trace to see if it contains
                // the function entry/primary script path. If it does, we're virtually certain that
                // that function caused the error (e.g. as in the case of global unhandled exceptions
                // coming from Node.js scripts).
                // We use the directory name for the script rather than the full script path itself to ensure
                // that we handle cases where the error might be coming from some other script (e.g. an NPM
                // module) that is part of the function.
                string absoluteScriptPath = Path.GetFullPath(currFunction.Metadata.ScriptFile).ToLowerInvariant();
                string functionDirectory = Path.GetDirectoryName(absoluteScriptPath);
                if (errorStack.Contains(functionDirectory))
                {
                    function = currFunction;
                    return true;
                }
            }

            return false;
        }

        private void NotifyInvoker(string functionName, Exception ex)
        {
            functionName = Utility.GetFunctionShortName(functionName);

            FunctionDescriptor functionDescriptor = this.Functions.SingleOrDefault(p => string.Compare(functionName, p.Name, StringComparison.OrdinalIgnoreCase) == 0);
            if (functionDescriptor != null)
            {
                functionDescriptor.Invoker.OnError(ex);
            }
        }

        private void OnFileChanged(object sender, FileSystemEventArgs e)
        {
            string directory = GetRelativeDirectory(e.FullPath, ScriptConfig.RootScriptPath);
            bool isWatchedDirectory = ScriptConfig.WatchDirectories.Contains(directory);

            // We will perform a host restart in the following cases:
            // - the file change was under one of the configured watched directories (e.g. node_modules, shared code directories, etc.)
            // - the host.json file was changed
            // - a function.json file was changed
            // - a function directory was added/removed
            string fileName = Path.GetFileName(e.Name);
            if (isWatchedDirectory ||
                ((string.Compare(fileName, ScriptConstants.HostMetadataFileName, StringComparison.OrdinalIgnoreCase) == 0) ||
                string.Compare(fileName, ScriptConstants.FunctionMetadataFileName, StringComparison.OrdinalIgnoreCase) == 0) ||
                (Directory.EnumerateDirectories(ScriptConfig.RootScriptPath).Count() != _directoryCountSnapshot))
            {
                // a host level configuration change has been made which requires a
                // host restart
                _restart(e);
            }
        }

        internal static string GetRelativeDirectory(string path, string scriptRoot)
        {
            if (path.StartsWith(scriptRoot))
            {
                string directory = path.Substring(scriptRoot.Length).TrimStart(Path.DirectorySeparatorChar);
                int idx = directory.IndexOf(Path.DirectorySeparatorChar);
                if (idx != -1)
                {
                    directory = directory.Substring(0, idx);
                }

                return directory;
            }

            return string.Empty;
        }

        private static bool IsDisabled(JToken isDisabledValue)
        {
            if (isDisabledValue != null)
            {
                if (isDisabledValue.Type == JTokenType.Boolean && (bool)isDisabledValue)
                {
                    return true;
                }
                else
                {
                    string settingName = (string)isDisabledValue;
                    string value = Environment.GetEnvironmentVariable(settingName);
                    if (!string.IsNullOrEmpty(value) &&
                        (string.Compare(value, "1", StringComparison.OrdinalIgnoreCase) == 0 ||
                         string.Compare(value, "true", StringComparison.OrdinalIgnoreCase) == 0))
                    {
                        return true;
                    }
                }
            }

            return false;
        }

        private static bool IsDynamicSku()
        {
            string hostingPlan = Environment.GetEnvironmentVariable(EnvironmentSettingNames.AzureWebsiteSku);
            return hostingPlan != null && hostingPlan == "Dynamic";
        }

        internal static string GetAssemblyFileVersion(Assembly assembly)
        {
            AssemblyFileVersionAttribute fileVersionAttr = assembly.GetCustomAttribute<AssemblyFileVersionAttribute>();
            return fileVersionAttr?.Version ?? "Unknown";
        }

        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);

            if (disposing)
            {
                _scriptFileWatcher?.Dispose();
                _debugModeFileWatcher?.Dispose();

                foreach (var function in Functions)
                {
                    (function.Invoker as IDisposable)?.Dispose();
                }

                _blobLeaseManager?.Dispose();
                _restartEvent.Dispose();
                (TraceWriter as IDisposable)?.Dispose();

                NodeFunctionInvoker.UnhandledException -= OnUnhandledException;
            }
        }
    }
}
