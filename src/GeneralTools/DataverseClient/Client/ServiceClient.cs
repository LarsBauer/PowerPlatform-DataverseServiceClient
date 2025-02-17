#region using
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security;
using System.ServiceModel;
using System.ServiceModel.Description;
using System.Xml;
using Microsoft.Crm.Sdk.Messages;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Discovery;
using Microsoft.Xrm.Sdk.Messages;
using Microsoft.Xrm.Sdk.Metadata;
using Microsoft.Xrm.Sdk.Query;
using Microsoft.Xrm.Sdk.WebServiceClient;
using System.Security.Cryptography.X509Certificates;
using System.Threading.Tasks;
using System.Net.Http;
using Microsoft.PowerPlatform.Dataverse.Client.Utils;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.PowerPlatform.Dataverse.Client.Auth;
using System.Threading;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.PowerPlatform.Dataverse.Client.Model;
using System.Reflection;
using Microsoft.Extensions.Caching.Memory;
#endregion

namespace Microsoft.PowerPlatform.Dataverse.Client
{
    /// <summary>
    /// Primary implementation of the API interface for Dataverse.
    /// </summary>
    public class ServiceClient : IOrganizationService, IOrganizationServiceAsync2, IDisposable
    {
        #region Vars


        /// <summary>
        /// Cached Object collection, used for pick lists and such.
        /// </summary>
        private Dictionary<string, Dictionary<string, object>> _CachObject; //Cache object.

        /// <summary>
        /// List of Dataverse Language ID's
        /// </summary>
        private List<int> _loadedLCIDList;

        /// <summary>
        /// Name of the cache object.
        /// </summary>
        private string _cachObjecName = ".LookupCache";

        /// <summary>
        /// Logging object for the Dataverse Interface.
        /// </summary>
        internal DataverseTraceLogger _logEntry;

        /// <summary>
        /// Dataverse Web Service Connector layer
        /// </summary>
        internal ConnectionService _connectionSvc;

        /// <summary>
        /// Dynamic app utility
        /// </summary>
        private DynamicEntityUtility _dynamicAppUtility = null;

        /// <summary>
        /// Configuration
        /// </summary>
        private IOptions<AppSettingsConfiguration> _configuration = ClientServiceProviders.Instance.GetService<IOptions<AppSettingsConfiguration>>();

        /// <summary>
        /// Metadata Utility
        /// </summary>
        private MetadataUtility _metadataUtlity = null;

        /// <summary>
        /// This is an internal Lock object,  used to sync communication with Dataverse.
        /// </summary>
        internal object _lockObject = new object();

        /// <summary>
        /// BatchManager for Execute Multiple.
        /// </summary>
        private BatchManager _batchManager = null;

        ///// <summary>
        ///// To cache the token
        ///// </summary>
        //private static CdsServiceClientTokenCache _CdsServiceClientTokenCache;

        private bool _disableConnectionLocking = false;

        /// <summary>
        /// SDK Version property backer.
        /// </summary>
        public string _sdkVersionProperty = null;

        /// <summary>
        /// Value used by the retry system while the code is running,
        /// this value can scale up and down based on throttling limits.
        /// </summary>
        private TimeSpan _retryPauseTimeRunning;

        /// <summary>
        /// Internal Organization Service Interface used for Testing
        /// </summary>
        internal IOrganizationService _testOrgSvcInterface { get; set; }

        #endregion

        #region Properties

        /// <summary>
        ///  Exposed OrganizationWebProxyClient for consumers
        /// </summary>
        internal OrganizationWebProxyClientAsync OrganizationWebProxyClient
        {
            get
            {
                if (_connectionSvc != null)
                {
                    if (_connectionSvc.WebClient == null)
                    {
                        if (_logEntry != null)
                            _logEntry.Log("OrganizationWebProxyClientAsync is null", TraceEventType.Error);
                        return null;
                    }
                    else
                        return _connectionSvc.WebClient;
                }
                else
                {
                    if (_logEntry != null)
                        _logEntry.Log("OrganizationWebProxyClientAsync is null", TraceEventType.Error);
                    return null;
                }
            }
        }

        /// <summary>
        /// Enabled Log Capture in memory
        /// This capability enables logs that would normally be sent to your configured
        /// </summary>
        public static bool InMemoryLogCollectionEnabled { get; set; } = Utils.AppSettingsHelper.GetAppSetting<bool>("InMemoryLogCollectionEnabled", false);

        /// <summary>
        /// This is the number of minuets that logs will be retained before being purged from memory. Default is 5 min.
        /// This capability controls how long the log cache is kept in memory.
        /// </summary>
        public static TimeSpan InMemoryLogCollectionTimeOutMinutes { get; set; } = Utils.AppSettingsHelper.GetAppSettingTimeSpan("InMemoryLogCollectionTimeOutMinutes", Utils.AppSettingsHelper.TimeSpanFromKey.Minutes, TimeSpan.FromMinutes(5));

        /// <summary>
        /// Gets or sets max retry count.
        /// </summary>
        public int MaxRetryCount
        {
            get { return _configuration.Value.MaxRetryCount; }
            set { _configuration.Value.MaxRetryCount = value; }
        }

        /// <summary>
        /// Gets or sets retry pause time.
        /// </summary>
        public TimeSpan RetryPauseTime
        {
            get { return _configuration.Value.RetryPauseTime; }
            set { _configuration.Value.RetryPauseTime = value; }
        }

        /// <summary>
        /// if true the service is ready to accept requests.
        /// </summary>
        public bool IsReady { get; private set; }

        /// <summary>
        /// if true then Batch Operations are available.
        /// </summary>
        public bool IsBatchOperationsAvailable
        {
            get
            {
                if (_batchManager != null)
                    return true;
                else
                    return false;
            }
        }

        /// <summary>
        /// OAuth Authority.
        /// </summary>
        public string Authority
        {
            get
            {   //Restricting to only OAuth login
                if (_connectionSvc != null && (
                    _connectionSvc.AuthenticationTypeInUse == AuthenticationType.OAuth ||
                    _connectionSvc.AuthenticationTypeInUse == AuthenticationType.Certificate ||
                    _connectionSvc.AuthenticationTypeInUse == AuthenticationType.ExternalTokenManagement ||
                    _connectionSvc.AuthenticationTypeInUse == AuthenticationType.ClientSecret))
                    return _connectionSvc.Authority;
                else
                    return string.Empty;
            }
        }

        /// <summary>
        /// Logged in Office365 UserId using OAuth.
        /// </summary>
        public string OAuthUserId
        {
            get
            {   //Restricting to only OAuth login
                if (_connectionSvc != null && _connectionSvc.AuthenticationTypeInUse == AuthenticationType.OAuth)
                    return _connectionSvc.UserId;
                else
                    return string.Empty;
            }
        }

        /// <summary>
        /// Gets or Sets the Max Connection Timeout for the connection.
        /// Default setting is 2 min,
        /// this property can also be set via app.config/app.settings with the property MaxConnectionTimeOutMinutes
        /// </summary>
        public static TimeSpan MaxConnectionTimeout
        {
            get
            {
                return ConnectionService.MaxConnectionTimeout;
            }
            set
            {
                ConnectionService.MaxConnectionTimeout = value;
            }
        }

        /// <summary>
        /// Authentication Type to use
        /// </summary>
        public AuthenticationType ActiveAuthenticationType
        {
            get
            {
                if (_connectionSvc != null)
                    return _connectionSvc.AuthenticationTypeInUse;
                else
                    return AuthenticationType.InvalidConnection;
            }
        }

        /// <summary>
        /// Returns the current access token in Use to connect to Dataverse.
        /// Note: this is only available when a token based authentication process is in use.
        /// </summary>
        public string CurrentAccessToken
        {
            get
            {
                if (_connectionSvc != null && (
                    _connectionSvc.AuthenticationTypeInUse == AuthenticationType.OAuth ||
                    _connectionSvc.AuthenticationTypeInUse == AuthenticationType.Certificate ||
                    _connectionSvc.AuthenticationTypeInUse == AuthenticationType.ExternalTokenManagement ||
                    _connectionSvc.AuthenticationTypeInUse == AuthenticationType.ClientSecret))
                {
                    return _connectionSvc.RefreshClientTokenAsync().Result;
                }
                else
                    return string.Empty;
            }
        }

        /// <summary>
        /// Defaults to True.
        /// <para>When true, this setting applies the default connection routing strategy to connections to Dataverse.</para>
        /// <para>This will 'prefer' a given node when interacting with Dataverse which improves overall connection performance.</para>
        /// <para>When set to false, each call to Dataverse will be routed to any given node supporting your organization. </para>
        /// <para>See https://docs.microsoft.com/en-us/powerapps/developer/data-platform/api-limits#remove-the-affinity-cookie for proper use.</para>
        /// </summary>
        public bool EnableAffinityCookie
        {
            get
            {
                if (_connectionSvc != null)
                    return _connectionSvc.EnableCookieRelay;
                else
                    return true;
            }
            set
            {
                if (_connectionSvc != null)
                    _connectionSvc.EnableCookieRelay = value;
            }
        }

        /// <summary>
        /// Pointer to Dataverse Service.
        /// </summary>
        internal IOrganizationService DataverseService
        {
            get
            {
                // Added to support testing of ServiceClient direct code.
                if (_testOrgSvcInterface != null)
                    return _testOrgSvcInterface;

                if (_connectionSvc != null)
                {
                    return _connectionSvc.WebClient;
                }
                else return null;
            }
        }

        /// <summary>
        /// Pointer to Dataverse Service.
        /// </summary>
        internal IOrganizationServiceAsync DataverseServiceAsync
        {
            get
            {
                // Added to support testing of ServiceClient direct code.
                //if (_testOrgSvcInterface != null)
                //    return _testOrgSvcInterface;

                if (_connectionSvc != null)
                {
                    return _connectionSvc.WebClient;
                }
                else return null;
            }
        }

        /// <summary>
        /// Current user Record.
        /// </summary>
        internal WhoAmIResponse SystemUser
        {
            get
            {
                if (_connectionSvc != null)
                {
                    if (_connectionSvc.CurrentUser != null)
                        return _connectionSvc.CurrentUser;
                    else
                    {
                        WhoAmIResponse resp = _connectionSvc.GetWhoAmIDetails(this).ConfigureAwait(false).GetAwaiter().GetResult();
                        _connectionSvc.CurrentUser = resp;
                        return resp;
                    }
                }
                else
                    return null;
            }
            set
            {
                _connectionSvc.CurrentUser = value;
            }
        }

        /// <summary>
        /// Returns the Last String Error that was created by the Dataverse Connection
        /// </summary>
        public string LastError { get { if (_logEntry != null) return _logEntry.LastError; else return string.Empty; } }

        /// <summary>
        /// Returns the Last Exception from Dataverse.
        /// </summary>
        public Exception LastException { get { if (_logEntry != null) return _logEntry.LastException; else return null; } }

        /// <summary>
        /// Returns the Actual URI used to connect to Dataverse.
        /// this URI could be influenced by user defined variables.
        /// </summary>
        public Uri ConnectedOrgUriActual { get { if (_connectionSvc != null) return _connectionSvc.ConnectOrgUriActual; else return null; } }

        /// <summary>
        /// Returns the friendly name of the connected Dataverse instance.
        /// </summary>
        public string ConnectedOrgFriendlyName { get { if (_connectionSvc != null) return _connectionSvc.ConnectedOrgFriendlyName; else return null; } }
        /// <summary>
        ///
        /// Returns the unique name for the org that has been connected.
        /// </summary>
        public string ConnectedOrgUniqueName { get { if (_connectionSvc != null) return _connectionSvc.CustomerOrganization; else return null; } }
        /// <summary>
        /// Returns the endpoint collection for the connected org.
        /// </summary>
        public EndpointCollection ConnectedOrgPublishedEndpoints { get { if (_connectionSvc != null) return _connectionSvc.ConnectedOrgPublishedEndpoints; else return null; } }

        /// <summary>
        /// OrganizationDetails for the currently connected environment.
        /// </summary>
        public OrganizationDetail OrganizationDetail { get { if (_connectionSvc != null) return _connectionSvc.ConnectedOrganizationDetail; else return null; } }

        /// <summary>
        /// This is the connection lock object that is used to control connection access for various threads. This should be used if you are using the Datavers queries via Linq to lock the connection
        /// </summary>
        internal object ConnectionLockObject { get { return _lockObject; } }

        /// <summary>
        /// Returns the Version Number of the connected Dataverse organization.
        /// If access before the Organization is connected, value returned will be null or 0.0
        /// </summary>
        public Version ConnectedOrgVersion { get { if (_connectionSvc != null) return _connectionSvc?.OrganizationVersion; else return new Version(0, 0); } }

        /// <summary>
        /// ID of the connected organization.
        /// </summary>
        public Guid ConnectedOrgId { get { if (_connectionSvc != null) return _connectionSvc.OrganizationId; else return Guid.Empty; } }

        /// <summary>
        /// Disabled internal cross thread safeties, this will gain much higher performance, however it places the requirements of thread safety on you, the developer.
        /// </summary>
        public bool DisableCrossThreadSafeties { get { return _disableConnectionLocking; } set { _disableConnectionLocking = value; } }

        /// <summary>
        /// Returns the access token from the attached function.
        /// This is set via the ServiceContructor that accepts a target url and a function to return an access token.
        /// </summary>
        internal Func<string, Task<string>> GetAccessToken { get; set; }

        /// <summary>
        /// Gets or Sets the current caller ID
        /// </summary>
        public Guid CallerId
        {
            get
            {
                if (OrganizationWebProxyClient != null)
                    return OrganizationWebProxyClient.CallerId;
                return Guid.Empty;
            }
            set
            {
                if (OrganizationWebProxyClient != null)
                    OrganizationWebProxyClient.CallerId = value;
            }
        }

        /// <summary>
        /// Gets or Sets the AAD Object ID of the caller.
        /// This is supported for Xrm 8.1 + only
        /// </summary>
        public Guid? CallerAADObjectId
        {
            get
            {
                if (_connectionSvc != null)
                {
                    return _connectionSvc.CallerAADObjectId;
                }
                return null;
            }
            set
            {
                if (Utilities.FeatureVersionMinimums.IsFeatureValidForEnviroment(_connectionSvc?.OrganizationVersion, Utilities.FeatureVersionMinimums.AADCallerIDSupported))
                    _connectionSvc.CallerAADObjectId = value;
                else
                {
                    if (_connectionSvc?.OrganizationVersion != null)
                    {
                        _connectionSvc.CallerAADObjectId = null; // Null value as this is not supported for this version.
                        _logEntry.Log($"Setting CallerAADObject ID not supported in version {_connectionSvc?.OrganizationVersion}");
                    }
                }
            }
        }

        /// <summary>
        /// This ID is used to support Dataverse Telemetry when trouble shooting SDK based errors.
        /// When Set by the caller, all Dataverse API Actions executed by this client will be tracked under a single session id for later troubleshooting.
        /// For example, you are able to group all actions in a given run of your client ( several creates / reads and such ) under a given tracking id that is shared on all requests.
        /// providing this ID when reporting a problem will aid in trouble shooting your issue.
        /// </summary>
        public Guid? SessionTrackingId
        {
            get
            {
                if (_connectionSvc != null)
                {
                    return _connectionSvc.SessionTrackingId;
                }
                return null;
            }

            set
            {
                if (_connectionSvc != null && Utilities.FeatureVersionMinimums.IsFeatureValidForEnviroment(ConnectedOrgVersion, Utilities.FeatureVersionMinimums.SessionTrackingSupported))
                    _connectionSvc.SessionTrackingId = value;
                else
                {
                    if (_connectionSvc?.OrganizationVersion != null)
                    {
                        _connectionSvc.SessionTrackingId = null; // Null value as this is not supported for this version.
                        _logEntry.Log($"Setting SessionTrackingId ID not supported in version {_connectionSvc?.OrganizationVersion}");
                    }
                }
            }

        }

        /// <summary>
        /// This will force the Dataverse server to refresh the current metadata cache with current DB config.
        /// Note, that this is a performance impacting property.
        /// Use of this flag will slow down operations server side as the server is required to check for consistency of the platform metadata against disk on each API call executed.
        /// It is recommended to use this ONLY in conjunction with solution import or delete operations.
        /// </summary>
        public bool ForceServerMetadataCacheConsistency
        {
            get
            {
                if (_connectionSvc != null)
                {
                    return _connectionSvc.ForceServerCacheConsistency;
                }
                return false;
            }
            set
            {
                if (_connectionSvc != null && Utilities.FeatureVersionMinimums.IsFeatureValidForEnviroment(_connectionSvc?.OrganizationVersion, Utilities.FeatureVersionMinimums.ForceConsistencySupported))
                    _connectionSvc.ForceServerCacheConsistency = value;
                else
                {
                    if (_connectionSvc?.OrganizationVersion != null)
                    {
                        _connectionSvc.ForceServerCacheConsistency = false; // Null value as this is not supported for this version.
                        _logEntry.Log($"Setting ForceServerMetadataCacheConsistency not supported in version {_connectionSvc?.OrganizationVersion}");
                    }
                }
            }

        }

        /// <summary>
        /// Get the Client SDK version property
        /// </summary>
        public string SdkVersionProperty
        {
            get
            {
                if (string.IsNullOrEmpty(_sdkVersionProperty))
                {
                    _sdkVersionProperty = typeof(OrganizationDetail).Assembly.GetCustomAttribute<AssemblyFileVersionAttribute>().Version ?? FileVersionInfo.GetVersionInfo(typeof(OrganizationDetail).Assembly.Location).FileVersion;
                }
                return _sdkVersionProperty;
            }
        }

        /// <summary>
        /// Gets the Tenant Id of the current connection.
        /// </summary>
        public Guid TenantId
        {
            get
            {
                if (_connectionSvc != null)
                {
                    return _connectionSvc.TenantId;
                }
                else
                    return Guid.Empty;
            }
        }

        /// <summary>
        /// Gets the PowerPlatform Environment Id of the environment that is hosting this instance of Dataverse
        /// </summary>
        public string EnvironmentId
        {
            get
            {
                if (_connectionSvc != null)
                {
                    return _connectionSvc.EnvironmentId;
                }
                else
                    return string.Empty;
            }
        }

        /// <summary>
        /// Use Web API instead of org service where possible.
        /// WARNING. THEASE ARE TEMPORARY SETTINGS AND WILL BE REMOVED IN THE FUTURE
        /// </summary>
        public bool UseWebApi
        {
            get => _configuration.Value.UseWebApi;
            set => _configuration.Value.UseWebApi = value;
        }

        /// <summary>
        /// Server Hint for the number of concurrent threads that would provbide optimal processing. 
        /// </summary>
        public int RecommendedDegreesOfParallelism => _connectionSvc.RecommendedDegreesOfParallelism;

        #endregion

        #region Constructor and Setup methods

        /// <summary>
        /// Default / Non accessible constructor
        /// </summary>
        private ServiceClient()
        { }

        /// <summary>
        /// Internal constructor used for testing.
        /// </summary>
        /// <param name="orgSvc"></param>
        /// <param name="httpClient"></param>
        /// <param name="targetVersion"></param>
        /// <param name="baseConnectUrl"></param>
        /// <param name="logger">Logging provider <see cref="ILogger"/></param>
        internal ServiceClient(IOrganizationService orgSvc, HttpClient httpClient, string baseConnectUrl, Version targetVersion = null, ILogger logger = null)
        {
            _testOrgSvcInterface = orgSvc;
            _logEntry = new DataverseTraceLogger(logger)
            {
                LogRetentionDuration = new TimeSpan(0, 10, 0),
                EnabledInMemoryLogCapture = true
            };
            _connectionSvc = new ConnectionService(orgSvc, baseConnectUrl , httpClient, logger);

            if (targetVersion != null)
                _connectionSvc.OrganizationVersion = targetVersion;

            _batchManager = new BatchManager(_logEntry);
            _metadataUtlity = new MetadataUtility(this);
            _dynamicAppUtility = new DynamicEntityUtility(this, _metadataUtlity);
        }

        /// <summary>
        /// ServiceClient to accept the connectionstring as a parameter
        /// </summary>
        /// <param name="dataverseConnectionString"></param>
        /// <param name="logger">Logging provider <see cref="ILogger"/></param>
        public ServiceClient(string dataverseConnectionString, ILogger logger = null)
        {
            if (string.IsNullOrEmpty(dataverseConnectionString))
                throw new ArgumentNullException("Dataverse ConnectionString", "Dataverse ConnectionString cannot be null or empty.");

            ConnectToService(dataverseConnectionString, logger);
        }

        //REMOVED FROM BUILD FOR NOW
        ///// <summary>
        ///// Uses the Organization Web proxy Client provided by the user
        ///// </summary>
        ///// <param name="externalOrgWebProxyClient">User Provided Organization Web Proxy Client</param>
        ///// <param name="logger">Logging provider <see cref="ILogger"/></param>
        //internal ServiceClient(OrganizationWebProxyClient externalOrgWebProxyClient, ILogger logger = null)
        //{
        //    // Disabled for this build as we determine how to support server side Native Client

        //    //CreateServiceConnection(null, AuthenticationType.OAuth, string.Empty, string.Empty, string.Empty, null, string.Empty,
        //    //    MakeSecureString(string.Empty), string.Empty, string.Empty, string.Empty, false, false, null, string.Empty, null,
        //    //    PromptBehavior.Auto, externalOrgWebProxyClient, externalLogger: logger);
        //}

        /// <summary>
        /// Creates an instance of ServiceClient who's authentication is managed by the caller.
        /// This requires the caller to implement a function that will accept the InstanceURI as a string will return the access token as a string on demand when the ServiceClient requires it.
        /// This approach is recommended when working with WebApplications or applications that are required to implement an on Behalf of flow for user authentication.
        /// </summary>
        /// <param name="instanceUrl">URL of the Dataverse instance to connect too.</param>
        /// <param name="tokenProviderFunction">Function that will be called when the access token is require for interaction with Dataverse.  This function must accept a string (InstanceURI) and return a string (accesstoken) </param>
        /// <param name="useUniqueInstance">A value of "true" Forces the ServiceClient to create a new connection to the Dataverse instance vs reusing an existing connection, Defaults to true.</param>
        /// <param name="logger">Logging provider <see cref="ILogger"/></param>
        public ServiceClient(Uri instanceUrl, Func<string, Task<string>> tokenProviderFunction, bool useUniqueInstance = true, ILogger logger = null)
        {
            GetAccessToken = tokenProviderFunction ??
                throw new DataverseConnectionException("tokenProviderFunction required for this constructor", new ArgumentNullException("tokenProviderFunction"));  // Set the function pointer or access.

            CreateServiceConnection(
                   null, AuthenticationType.ExternalTokenManagement, string.Empty, string.Empty, string.Empty, null,
                   string.Empty, null, string.Empty, string.Empty, string.Empty, true, useUniqueInstance, null,
                   string.Empty, null, PromptBehavior.Never, null, string.Empty, StoreName.My, null, instanceUrl, externalLogger: logger);
        }

        /// <summary>
        /// Log in with OAuth for online connections,
        /// <para>
        /// Utilizes the discovery system to resolve the correct endpoint to use given the provided server orgName, user name and password.
        /// </para>
        /// </summary>
        /// <param name="userId">User Id supplied</param>
        /// <param name="password">Password for login</param>
        /// <param name="regionGeo">Region where server is provisioned in for login</param>
        /// <param name="orgName">Name of the organization to connect</param>
        /// <param name="useUniqueInstance">if set, will force the system to create a unique connection</param>
        /// <param name="orgDetail">Dataverse Org Detail object, this is is returned from a query to the Dataverse Discovery Server service. not required.</param>
        /// <param name="clientId">The registered client Id on Azure portal.</param>
        /// <param name="redirectUri">The redirect URI application will be redirected post OAuth authentication.</param>
        /// <param name="promptBehavior">The prompt Behavior.</param>
        /// <param name="useDefaultCreds">(optional) If true attempts login using current user ( Online ) </param>
        /// <param name="tokenCacheStorePath">(Optional)The token cache path where token cache file is placed. if string.empty, will use default cache file store, if null, will use in memory cache</param>
        /// <param name="logger">Logging provider <see cref="ILogger"/></param>
        public ServiceClient(string userId, SecureString password, string regionGeo, string orgName, bool useUniqueInstance, OrganizationDetail orgDetail,
                string clientId, Uri redirectUri, PromptBehavior promptBehavior = PromptBehavior.Auto, bool useDefaultCreds = false, string tokenCacheStorePath = null, ILogger logger = null)
        {
            CreateServiceConnection(
                    null, AuthenticationType.OAuth, string.Empty, string.Empty, orgName, null,
                    userId, password, string.Empty, regionGeo, string.Empty, true, useUniqueInstance, orgDetail,
                    clientId, redirectUri, promptBehavior, null, useDefaultCreds: useDefaultCreds, externalLogger: logger, tokenCacheStorePath: tokenCacheStorePath);
        }

        /// <summary>
        /// Log in with OAuth for online connections,
        /// <para>
        /// Will attempt to connect directly to the URL provided for the API endpoint.
        /// </para>
        /// </summary>
        /// <param name="userId">User Id supplied</param>
        /// <param name="password">Password for login</param>
        /// <param name="useUniqueInstance">if set, will force the system to create a unique connection</param>
        /// <param name="clientId">The registered client Id on Azure portal.</param>
        /// <param name="redirectUri">The redirect URI application will be redirected post OAuth authentication.</param>
        /// <param name="promptBehavior">The prompt Behavior.</param>
        /// <param name="useDefaultCreds">(optional) If true attempts login using current user ( Online ) </param>
        /// <param name="hostUri">API or Instance URI to access the Dataverse environment.</param>
        /// <param name="tokenCacheStorePath">(Optional)The token cache path where token cache file is placed. if string.empty, will use default cache file store, if null, will use in memory cache</param>
        /// <param name="logger">Logging provider <see cref="ILogger"/></param>
        public ServiceClient(string userId, SecureString password, Uri hostUri, bool useUniqueInstance,
                string clientId, Uri redirectUri, PromptBehavior promptBehavior = PromptBehavior.Auto, bool useDefaultCreds = false, string tokenCacheStorePath = null, ILogger logger = null)
        {
            CreateServiceConnection(
                    null, AuthenticationType.OAuth, string.Empty, string.Empty, null, null,
                    userId, password, string.Empty, null, string.Empty, true, useUniqueInstance, null,
                    clientId, redirectUri, promptBehavior, null, useDefaultCreds: useDefaultCreds, instanceUrl: hostUri, externalLogger: logger, tokenCacheStorePath: tokenCacheStorePath);
        }

        /// <summary>
        /// Log in with OAuth for On-Premises connections.
        /// </summary>
        /// <param name="userId">User Id supplied</param>
        /// <param name="password">Password for login</param>
        /// <param name="domain">Domain</param>
        /// <param name="hostName">Host name of the server that is hosting the Dataverse web service</param>
        /// <param name="port">Port number on the Dataverse Host Server ( usually 444 )</param>
        /// <param name="orgName">Organization name for the Dataverse Instance.</param>
        /// <param name="useSsl">if true, https:// used</param>
        /// <param name="useUniqueInstance">if set, will force the system to create a unique connection</param>
        /// <param name="orgDetail">Dataverse Org Detail object, this is returned from a query to the Dataverse Discovery Server service. not required.</param>
        /// <param name="clientId">The registered client Id on Azure portal.</param>
        /// <param name="redirectUri">The redirect URI application will be redirected post OAuth authentication.</param>
        /// <param name="promptBehavior">The prompt Behavior.</param>
        /// <param name="tokenCacheStorePath">(Optional)The token cache path where token cache file is placed. if string.empty, will use default cache file store, if null, will use in memory cache</param>
        /// <param name="logger">Logging provider <see cref="ILogger"/></param>
        public ServiceClient(string userId, SecureString password, string domain, string hostName, string port, string orgName, bool useSsl, bool useUniqueInstance,
                OrganizationDetail orgDetail, string clientId, Uri redirectUri, PromptBehavior promptBehavior = PromptBehavior.Auto, string tokenCacheStorePath = null, ILogger logger = null)
        {
            CreateServiceConnection(
                    null, AuthenticationType.OAuth, hostName, port, orgName, null,
                    userId, password, domain, string.Empty, string.Empty, useSsl, useUniqueInstance, orgDetail,
                    clientId, redirectUri, promptBehavior, null, externalLogger: logger, tokenCacheStorePath: tokenCacheStorePath);
        }

        /// <summary>
        /// Log in with Certificate Auth On-Premises connections.
        /// </summary>
        /// <param name="certificate">Certificate to use during login</param>
        /// <param name="certificateStoreName">StoreName to look in for certificate identified by certificateThumbPrint</param>
        /// <param name="certificateThumbPrint">ThumbPrint of the Certificate to load</param>
        /// <param name="instanceUrl">URL of the Dataverse instance to connect too</param>
        /// <param name="orgName">Organization name for the Dataverse Instance.</param>
        /// <param name="useSsl">if true, https:// used</param>
        /// <param name="useUniqueInstance">if set, will force the system to create a unique connection</param>
        /// <param name="orgDetail">Dataverse Org Detail object, this is is returned from a query to the Dataverse Discovery Server service. not required.</param>
        /// <param name="clientId">The registered client Id on Azure portal.</param>
        /// <param name="redirectUri">The redirect URI application will be redirected post OAuth authentication.</param>
        /// <param name="logger">Logging provider <see cref="ILogger"/></param>
        public ServiceClient(X509Certificate2 certificate, StoreName certificateStoreName, string certificateThumbPrint, Uri instanceUrl, string orgName, bool useSsl, bool useUniqueInstance,
                OrganizationDetail orgDetail, string clientId, Uri redirectUri, ILogger logger = null)
        {
            if ((string.IsNullOrEmpty(clientId) || redirectUri == null))
            {
                throw new ArgumentOutOfRangeException("authType",
                    "When using Certificate Authentication you have to specify clientId and redirectUri.");
            }

            if (string.IsNullOrEmpty(certificateThumbPrint) && certificate == null)
            {
                throw new ArgumentOutOfRangeException("authType",
                    "When using Certificate Authentication you have to specify either a certificate thumbprint or provide a certificate to use.");
            }

            CreateServiceConnection(
                    null, AuthenticationType.Certificate, string.Empty, string.Empty, orgName, null,
                    string.Empty, null, string.Empty, string.Empty, string.Empty, useSsl, useUniqueInstance, orgDetail,
                    clientId, redirectUri, PromptBehavior.Never, null, certificateThumbPrint, certificateStoreName, certificate, instanceUrl, externalLogger: logger);
        }


        /// <summary>
        /// Log in with Certificate Auth OnLine connections.
        /// This requires the org API URI.
        /// </summary>
        /// <param name="certificate">Certificate to use during login</param>
        /// <param name="certificateStoreName">StoreName to look in for certificate identified by certificateThumbPrint</param>
        /// <param name="certificateThumbPrint">ThumbPrint of the Certificate to load</param>
        /// <param name="instanceUrl">API URL of the Dataverse instance to connect too</param>
        /// <param name="useUniqueInstance">if set, will force the system to create a unique connection</param>
        /// <param name="orgDetail">Dataverse Org Detail object, this is is returned from a query to the Dataverse Discovery Server service. not required.</param>
        /// <param name="clientId">The registered client Id on Azure portal.</param>
        /// <param name="redirectUri">The redirect URI application will be redirected post OAuth authentication.</param>
        /// <param name="logger">Logging provider <see cref="ILogger"/></param>
        public ServiceClient(X509Certificate2 certificate, StoreName certificateStoreName, string certificateThumbPrint, Uri instanceUrl, bool useUniqueInstance, OrganizationDetail orgDetail,
                string clientId, Uri redirectUri, ILogger logger = null)
        {
            if ((string.IsNullOrEmpty(clientId)))
            {
                throw new ArgumentOutOfRangeException("authType",
                    "When using Certificate Authentication you have to specify clientId.");
            }

            if (string.IsNullOrEmpty(certificateThumbPrint) && certificate == null)
            {
                throw new ArgumentOutOfRangeException("authType",
                    "When using Certificate Authentication you have to specify either a certificate thumbprint or provide a certificate to use.");
            }

            CreateServiceConnection(
                    null, AuthenticationType.Certificate, string.Empty, string.Empty, string.Empty, null,
                    string.Empty, null, string.Empty, string.Empty, string.Empty, true, useUniqueInstance, orgDetail,
                    clientId, redirectUri, PromptBehavior.Never, null, certificateThumbPrint, certificateStoreName, certificate, instanceUrl, externalLogger: logger);
        }


        /// <summary>
        /// ClientID \ ClientSecret Based Authentication flow.
        /// </summary>
        /// <param name="instanceUrl">Direct URL of Dataverse instance to connect too.</param>
        /// <param name="clientId">The registered client Id on Azure portal.</param>
        /// <param name="clientSecret">Client Secret for Client Id.</param>
        /// <param name="useUniqueInstance">Use unique instance or reuse current connection.</param>
        /// <param name="logger">Logging provider <see cref="ILogger"/></param>
        public ServiceClient(Uri instanceUrl, string clientId, string clientSecret, bool useUniqueInstance, ILogger logger = null)
        {
            CreateServiceConnection(null,
                AuthenticationType.ClientSecret,
                string.Empty, string.Empty, string.Empty, null, string.Empty,
                MakeSecureString(clientSecret), string.Empty, string.Empty, string.Empty, true, useUniqueInstance,
                null, clientId, null, PromptBehavior.Never, null, null, instanceUrl: instanceUrl, externalLogger: logger);
        }

        /// <summary>
        /// ClientID \ ClientSecret Based Authentication flow, allowing for Secure Client ID passing.
        /// </summary>
        /// <param name="instanceUrl">Direct URL of Dataverse instance to connect too.</param>
        /// <param name="clientId">The registered client Id on Azure portal.</param>
        /// <param name="clientSecret">Client Secret for Client Id.</param>
        /// <param name="useUniqueInstance">Use unique instance or reuse current connection.</param>
        /// <param name="logger">Logging provider <see cref="ILogger"/></param>
        public ServiceClient(Uri instanceUrl, string clientId, SecureString clientSecret, bool useUniqueInstance, ILogger logger = null)
        {
            CreateServiceConnection(null,
                AuthenticationType.ClientSecret,
                string.Empty, string.Empty, string.Empty, null, string.Empty,
                clientSecret, string.Empty, string.Empty, string.Empty, true, useUniqueInstance,
                null, clientId, null, PromptBehavior.Never, null, null, instanceUrl: instanceUrl, externalLogger: logger);
        }

        /// <summary>
        /// Parse the given connection string
        /// Connects to Dataverse using CreateWebServiceConnection
        /// </summary>
        /// <param name="connectionString"></param>
        /// <param name="logger">Logging provider <see cref="ILogger"/></param>
        internal void ConnectToService(string connectionString, ILogger logger = null)
        {
            var parsedConnStr = DataverseConnectionStringProcessor.Parse(connectionString, logger);

            if (parsedConnStr.AuthenticationType == AuthenticationType.InvalidConnection)
                throw new ArgumentException("AuthType is invalid.  Please see Microsoft.PowerPlatform.Dataverse.Client.AuthenticationType for supported authentication types.", "AuthType")
                { HelpLink = "https://docs.microsoft.com/powerapps/developer/data-platform/xrm-tooling/use-connection-strings-xrm-tooling-connect" };

            var serviceUri = parsedConnStr.ServiceUri;

            var networkCredentials = parsedConnStr.ClientCredentials != null && parsedConnStr.ClientCredentials.Windows != null ?
                parsedConnStr.ClientCredentials.Windows.ClientCredential : System.Net.CredentialCache.DefaultNetworkCredentials;

            string orgName = parsedConnStr.Organization;

            if ((parsedConnStr.SkipDiscovery && parsedConnStr.ServiceUri != null) && string.IsNullOrEmpty(orgName))
                // Orgname is mandatory if skip discovery is not passed
                throw new ArgumentNullException("Dataverse Instance Name or URL name Required",
                        parsedConnStr.IsOnPremOauth ?
                        $"Unable to determine instance name to connect to from passed instance Uri, Uri does not match known online deployments." :
                        $"Unable to determine instance name to connect to from passed instance Uri. Uri does not match specification for OnPrem instances.");

            string homesRealm = parsedConnStr.HomeRealmUri != null ? parsedConnStr.HomeRealmUri.AbsoluteUri : string.Empty;

            string userId = parsedConnStr.UserId;
            string password = parsedConnStr.Password;
            string domainname = parsedConnStr.DomainName;
            string onlineRegion = parsedConnStr.Geo;
            string clientId = parsedConnStr.ClientId;
            string hostname = serviceUri.Host;
            string port = Convert.ToString(serviceUri.Port);

            Uri redirectUri = parsedConnStr.RedirectUri;

            bool useSsl = serviceUri.Scheme == "https" ? true : false;

            switch (parsedConnStr.AuthenticationType)
            {
                case AuthenticationType.OAuth:
                    hostname = parsedConnStr.IsOnPremOauth ? hostname : string.Empty; //
                    port = parsedConnStr.IsOnPremOauth ? port : string.Empty;

                    if (string.IsNullOrEmpty(clientId) && redirectUri == null)
                    {
                        throw new ArgumentNullException("ClientId and Redirect Name", "ClientId or Redirect uri cannot be null or empty.");
                    }


                    CreateServiceConnection(null, parsedConnStr.AuthenticationType, hostname, port, orgName, networkCredentials, userId,
                                                MakeSecureString(password), domainname, onlineRegion, homesRealm, useSsl, parsedConnStr.UseUniqueConnectionInstance,
                                                    null, clientId, redirectUri, parsedConnStr.PromptBehavior, instanceUrl: parsedConnStr.SkipDiscovery ? parsedConnStr.ServiceUri : null,
                                                    useDefaultCreds: parsedConnStr.UseCurrentUser, externalLogger: logger, tokenCacheStorePath: parsedConnStr.TokenCacheStorePath);
                    break;
                case AuthenticationType.Certificate:
                    hostname = parsedConnStr.IsOnPremOauth ? hostname : string.Empty; //
                    port = parsedConnStr.IsOnPremOauth ? port : string.Empty;

                    if (string.IsNullOrEmpty(clientId) || string.IsNullOrEmpty(parsedConnStr.CertThumbprint))
                    {
                        throw new ArgumentNullException("ClientId or Certificate Thumbprint must be populated for Certificate Auth Type.");
                    }

                    StoreName targetStoreName = StoreName.My;
                    if (!string.IsNullOrEmpty(parsedConnStr.CertStoreName))
                    {
                        Enum.TryParse<StoreName>(parsedConnStr.CertStoreName, out targetStoreName);
                    }

                    CreateServiceConnection(null, parsedConnStr.AuthenticationType, hostname, port, orgName, null, string.Empty,
                                                null, string.Empty, onlineRegion, string.Empty, useSsl, parsedConnStr.UseUniqueConnectionInstance,
                                                    null, clientId, redirectUri, PromptBehavior.Never, null, parsedConnStr.CertThumbprint, targetStoreName, instanceUrl: parsedConnStr.ServiceUri, externalLogger: logger);

                    break;
                case AuthenticationType.ClientSecret:
                    hostname = parsedConnStr.IsOnPremOauth ? hostname : string.Empty;
                    port = parsedConnStr.IsOnPremOauth ? port : string.Empty;

                    if (string.IsNullOrEmpty(clientId) && string.IsNullOrEmpty(parsedConnStr.ClientSecret))
                    {
                        throw new ArgumentNullException("ClientId and ClientSecret must be populated for ClientSecret Auth Type.",
                            $"Client Id={(string.IsNullOrEmpty(clientId) ? "Not Specfied and Required." : clientId)} | Client Secret={(string.IsNullOrEmpty(parsedConnStr.ClientSecret) ? "Not Specfied and Required." : "Specfied")}");
                    }

                    CreateServiceConnection(null, parsedConnStr.AuthenticationType, hostname, port, orgName, null, string.Empty,
                                                 MakeSecureString(parsedConnStr.ClientSecret), string.Empty, onlineRegion, string.Empty, useSsl, parsedConnStr.UseUniqueConnectionInstance,
                                                    null, clientId, redirectUri, PromptBehavior.Never, null, null, instanceUrl: parsedConnStr.ServiceUri, externalLogger: logger);
                    break;
            }
        }


        /// <summary>
        /// Uses the Organization Web proxy Client provided by the user
        /// </summary>
        /// <param name="externalOrgWebProxyClient">User Provided Organization Web Proxy Client</param>
        /// <param name="isCloned">when true, skips init</param>
        /// <param name="orginalAuthType">Auth type of source connection</param>
        /// <param name="sourceOrgVersion">source organization version</param>
        /// <param name="logger">Logging provider <see cref="ILogger"/></param>
        internal ServiceClient(OrganizationWebProxyClientAsync externalOrgWebProxyClient, bool isCloned = true, AuthenticationType orginalAuthType = AuthenticationType.OAuth, Version sourceOrgVersion = null, ILogger logger = null)
        {
            CreateServiceConnection(null, orginalAuthType, string.Empty, string.Empty, string.Empty, null, string.Empty,
                MakeSecureString(string.Empty), string.Empty, string.Empty, string.Empty, false, false, null, string.Empty, null,
                PromptBehavior.Auto, externalOrgWebProxyClient, isCloned: isCloned, incomingOrgVersion: sourceOrgVersion, externalLogger: logger);
        }


        /// <summary>
        /// Sets up the Dataverse Web Service Connection
        ///  For Connecting via AD
        /// </summary>
        /// <param name="externalOrgServiceProxy">if populated, is the org service to use to connect to Dataverse</param>
        /// <param name="requestedAuthType">Authentication Type requested</param>
        /// <param name="hostName">Host name of the server that is hosting the Dataverse web service</param>
        /// <param name="port">Port number on the Dataverse Host Server ( usually 5555 )</param>
        /// <param name="orgName">Organization name for the Dataverse Instance.</param>
        /// <param name="credential">Network Credential Object used to login with</param>
        /// <param name="userId">Live ID to connect with</param>
        /// <param name="password">Live ID Password to connect with</param>
        /// <param name="domain">Name of the Domain where the Dataverse is deployed</param>
        /// <param name="Geo">Region hosting the Dataverse online Server, can be NA, EMEA, APAC</param>
        /// <param name="claimsHomeRealm">HomeRealm Uri for the user</param>
        /// <param name="useSsl">if true, https:// used</param>
        /// <param name="useUniqueInstance">if set, will force the system to create a unique connection</param>
        /// <param name="orgDetail">Dataverse Org Detail object, this is is returned from a query to the Dataverse Discovery Server service. not required.</param>
        /// <param name="clientId">Registered Client Id on Azure</param>
        /// <param name="promptBehavior">Default Prompt Behavior</param>
        /// <param name="redirectUri">Registered redirect uri for ADAL to return</param>
        /// <param name="externalOrgWebProxyClient">OAuth related web proxy client</param>
        /// <param name="certificate">Certificate to use during login</param>
        /// <param name="certificateStoreName">StoreName to look in for certificate identified by certificateThumbPrint</param>
        /// <param name="certificateThumbPrint">ThumbPrint of the Certificate to load</param>
        /// <param name="instanceUrl">Actual URI of the Organization Instance</param>
        /// <param name="isCloned">When True, Indicates that the construction request is coming from a clone operation. </param>
        /// <param name="useDefaultCreds">(optional) If true attempts login using current user ( Online ) </param>
        /// <param name="incomingOrgVersion">Incoming Org Version, used as part of clone.</param>
        /// <param name="externalLogger">Logging provider <see cref="ILogger"/></param>
        /// <param name="tokenCacheStorePath">path for token file storage</param>
        internal void CreateServiceConnection(
            object externalOrgServiceProxy,
            AuthenticationType requestedAuthType,
            string hostName,
            string port,
            string orgName,
            System.Net.NetworkCredential credential,
            string userId,
            SecureString password,
            string domain,
            string Geo,
            string claimsHomeRealm,
            bool useSsl,
            bool useUniqueInstance,
            OrganizationDetail orgDetail,
            string clientId = "",
            Uri redirectUri = null,
            PromptBehavior promptBehavior = PromptBehavior.Auto,
            OrganizationWebProxyClientAsync externalOrgWebProxyClient = null,
            string certificateThumbPrint = "",
            StoreName certificateStoreName = StoreName.My,
            X509Certificate2 certificate = null,
            Uri instanceUrl = null,
            bool isCloned = false,
            bool useDefaultCreds = false,
            Version incomingOrgVersion = null,
            ILogger externalLogger = null,
            string tokenCacheStorePath = null
            )
        {

            _logEntry = new DataverseTraceLogger(externalLogger)
            {
                // Set initial properties
                EnabledInMemoryLogCapture = InMemoryLogCollectionEnabled,
                LogRetentionDuration = InMemoryLogCollectionTimeOutMinutes
            };

            _connectionSvc = null;

            // Handel Direct Set from Login control.
            if (instanceUrl == null && orgDetail != null)
            {
                if (orgDetail.FriendlyName.Equals("DIRECTSET", StringComparison.OrdinalIgnoreCase)
                    && orgDetail.OrganizationId.Equals(Guid.Empty)
                    && !string.IsNullOrEmpty(orgDetail.OrganizationVersion) && orgDetail.OrganizationVersion.Equals("0.0.0.0")
                    && orgDetail.Endpoints != null
                    && orgDetail.Endpoints.ContainsKey(EndpointType.OrganizationService))
                {
                    if (Uri.TryCreate(orgDetail.Endpoints[EndpointType.OrganizationService], UriKind.RelativeOrAbsolute, out instanceUrl))
                    {
                        orgDetail = null;
                        _logEntry.Log(string.Format("DIRECTSET URL detected via Login OrgDetails Property, Setting Connect URI to {0}", instanceUrl.ToString()));
                    }
                }
            }

            try
            {
                // Support for things like Excel that do not run from a local directory.
                Version fileVersion = new Version(SdkVersionProperty);
                if (!(Utilities.FeatureVersionMinimums.IsFeatureValidForEnviroment(fileVersion, Utilities.FeatureVersionMinimums.DataverseVersionForThisAPI)))
                {
                    _logEntry.Log("!!WARNING!!! The version of the Dataverse product assemblies is less than the recommend version for this API; you must use version 5.0.9688.1533 or newer (Newer then the Oct-2011 service release)", TraceEventType.Warning);
                    _logEntry.Log(string.Format(CultureInfo.InvariantCulture, "Dataverse Version found is {0}", SdkVersionProperty), TraceEventType.Warning);
                }
            }
            catch
            {
                _logEntry.Log("!!WARNING!!! Failed to determine the version of the Dataverse SDK Present", TraceEventType.Warning);
            }
            _metadataUtlity = new MetadataUtility(this);
            _dynamicAppUtility = new DynamicEntityUtility(this, _metadataUtlity);

            // doing a direct Connect,  use Connection Manager to do the connect.
            // if using an user provided connection,.
            if (externalOrgWebProxyClient != null)
            {
                _connectionSvc = new ConnectionService(externalOrgWebProxyClient, requestedAuthType , _logEntry);
                _connectionSvc.IsAClone = isCloned;
                if (isCloned && incomingOrgVersion != null)
                {
                    _connectionSvc.OrganizationVersion = incomingOrgVersion;
                }
            }
            else
            {
                if (requestedAuthType == AuthenticationType.ExternalTokenManagement)
                {
                    _connectionSvc = new ConnectionService(
                            requestedAuthType,
                            instanceUrl,
                            useUniqueInstance,
                            orgDetail, clientId,
                            redirectUri, certificateThumbPrint,
                            certificateStoreName, certificate, hostName, port, false, logSink: _logEntry, tokenCacheStorePath: tokenCacheStorePath);

                    if (GetAccessToken != null)
                        _connectionSvc.GetAccessTokenAsync = GetAccessToken;
                    else
                    {
                        // Should not get here,  however..
                        throw new DataverseConnectionException("tokenProviderFunction required for ExternalTokenManagement Auth type, You must use the appropriate constructor for this auth type.", new ArgumentNullException("tokenProviderFunction"));
                    }
                }
                else
                {
                    // check to see what sort of login this is.
                    if (requestedAuthType == AuthenticationType.OAuth)
                    {
                        if (!String.IsNullOrEmpty(hostName))
                            _connectionSvc = new ConnectionService(requestedAuthType, orgName, userId, password, Geo, useUniqueInstance, orgDetail, clientId, redirectUri, promptBehavior, hostName, port, true, instanceToConnectToo: instanceUrl, logSink: _logEntry, useDefaultCreds: useDefaultCreds, tokenCacheStorePath: tokenCacheStorePath);
                        else
                            _connectionSvc = new ConnectionService(requestedAuthType, orgName, userId, password, Geo, useUniqueInstance, orgDetail, clientId, redirectUri, promptBehavior, hostName, port, false, instanceToConnectToo: instanceUrl, logSink: _logEntry, useDefaultCreds: useDefaultCreds, tokenCacheStorePath: tokenCacheStorePath);
                    }
                    else if (requestedAuthType == AuthenticationType.Certificate)
                    {
                        _connectionSvc = new ConnectionService(requestedAuthType, instanceUrl, useUniqueInstance, orgDetail, clientId, redirectUri, certificateThumbPrint, certificateStoreName, certificate, hostName, port, !String.IsNullOrEmpty(hostName), logSink: _logEntry, tokenCacheStorePath: tokenCacheStorePath);
                    }
                    else if (requestedAuthType == AuthenticationType.ClientSecret)
                    {
                        if (!String.IsNullOrEmpty(hostName))
                            _connectionSvc = new ConnectionService(requestedAuthType, orgName, userId, password, Geo, useUniqueInstance, orgDetail, clientId, redirectUri, promptBehavior, hostName, port, true, instanceToConnectToo: instanceUrl, logSink: _logEntry, useDefaultCreds: useDefaultCreds, tokenCacheStorePath: tokenCacheStorePath);
                        else
                            _connectionSvc = new ConnectionService(requestedAuthType, orgName, userId, password, Geo, useUniqueInstance, orgDetail, clientId, redirectUri, promptBehavior, hostName, port, false, instanceToConnectToo: instanceUrl, logSink: _logEntry, useDefaultCreds: useDefaultCreds, tokenCacheStorePath: tokenCacheStorePath);
                    }
                }
            }

            if (_connectionSvc != null)
            {
                try
                {
                    // Assign the log entry host to the ConnectionService engine
                    ConnectionService tempConnectService = null;
                    _connectionSvc.InternetProtocalToUse = useSsl ? "https" : "http";
                    if (!_connectionSvc.DoLogin(out tempConnectService))
                    {
                        _logEntry.Log("Unable to Login to Dataverse", TraceEventType.Error);
                        IsReady = false;
                        return;
                    }
                    else
                    {
                        if (tempConnectService != null)
                        {
                            _connectionSvc.Dispose();  // Clean up temp version and unassign assets.
                            _connectionSvc = tempConnectService;
                        }
                        _cachObjecName = _connectionSvc.ServiceCACHEName + ".LookupCache";

                        // Min supported version for batch operations.
                        if (_connectionSvc?.OrganizationVersion != null &&
                            Utilities.FeatureVersionMinimums.IsFeatureValidForEnviroment(_connectionSvc?.OrganizationVersion, Utilities.FeatureVersionMinimums.BatchOperations))
                            _batchManager = new BatchManager(_logEntry);
                        else
                            _logEntry.Log("Batch System disabled, Dataverse Server does not support this message call", TraceEventType.Information);

                        IsReady = true;

                    }
                }
                catch (Exception ex)
                {
                    throw new DataverseConnectionException("Failed to connect to Dataverse", ex);
                }
            }
        }

        #endregion

        #region Public General Interfaces

        /// <summary>
        /// Enabled only if InMemoryLogCollectionEnabled is true.
        /// Return all logs currently stored for the ServiceClient in queue.
        /// </summary>
        public IEnumerable<Tuple<DateTime, string>> GetAllLogs()
        {
            var source1 = _logEntry == null ? Enumerable.Empty<Tuple<DateTime, string>>() : _logEntry.Logs;
            var source2 = _connectionSvc == null ? Enumerable.Empty<Tuple<DateTime, string>>() : _connectionSvc.GetAllLogs();
            return source1.Union(source2);
        }

        /// <summary>
        /// Enabled only if InMemoryLogCollectionEnabled is true.
        /// Return all logs currently stored for the ServiceClient in queue in string list format with [UTCDateTime][LogEntry].
        /// </summary>
        public string[] GetAllLogsAsStringList()
        {
            return GetAllLogs().OrderBy(x => x.Item1).Select(x => $"[{x.Item1:yyyy-MM-dd HH:mm:ss:fffffff}]{x.Item2}").ToArray();
        }

        /// <summary>
        /// Clone, 'Clones" the current Dataverse ServiceClient with a new connection to Dataverse.
        /// Clone only works for connections creating using OAuth Protocol.
        /// </summary>
        /// <param name="logger">Logging provider <see cref="ILogger"/></param>
        /// <returns>returns an active ServiceClient or null</returns>
        public ServiceClient Clone(ILogger logger = null)
        {
            return Clone(null, logger: logger);
        }

        /// <summary>
        /// Clone, 'Clones" the current Dataverse Service client with a new connection to Dataverse.
        /// Clone only works for connections creating using OAuth Protocol.
        /// </summary>
        /// <param name="strongTypeAsm">Strong Type Assembly to reference as part of the create of the clone.</param>
        /// <param name="logger">Logging provider <see cref="ILogger"/></param>
        /// <returns></returns>
        public ServiceClient Clone(System.Reflection.Assembly strongTypeAsm, ILogger logger = null)
        {
            if (_connectionSvc == null || IsReady == false)
            {
                _logEntry.Log("You must have successfully created a connection to Dataverse before it can be cloned.", TraceEventType.Error);
                return null;
            }

            OrganizationWebProxyClientAsync proxy = null;
            if (_connectionSvc.ConnectOrgUriActual != null)
            {
                if (strongTypeAsm == null)
                    proxy = new OrganizationWebProxyClientAsync(_connectionSvc.ConnectOrgUriActual, true);
                else
                    proxy = new OrganizationWebProxyClientAsync(_connectionSvc.ConnectOrgUriActual, strongTypeAsm);
            }
            else
            {
                var orgWebClient = _connectionSvc.WebClient;
                if (orgWebClient != null)
                {
                    if (strongTypeAsm == null)
                        proxy = new OrganizationWebProxyClientAsync(orgWebClient.Endpoint.Address.Uri, true);
                    else
                        proxy = new OrganizationWebProxyClientAsync(orgWebClient.Endpoint.Address.Uri, strongTypeAsm);
                }
                else
                {
                    _logEntry.Log("Connection cannot be cloned.  There is currently no OAuth based connection active.");
                    return null;
                }
            }
            if (proxy != null)
            {
                try
                {
                    // Get Current Access Token.
                    // This will get the current access token
                    proxy.HeaderToken = this.CurrentAccessToken;
                    var SvcClient = new ServiceClient(proxy, true, _connectionSvc.AuthenticationTypeInUse, _connectionSvc?.OrganizationVersion, logger: logger);
                    SvcClient._connectionSvc.SetClonedProperties(this);
                    SvcClient.CallerAADObjectId = CallerAADObjectId;
                    SvcClient.CallerId = CallerId;
                    SvcClient.MaxRetryCount = _configuration.Value.MaxRetryCount;
                    SvcClient.RetryPauseTime = _configuration.Value.RetryPauseTime;
                    SvcClient.GetAccessToken = GetAccessToken;

                    return SvcClient;
                }
                catch (DataverseConnectionException)
                {
                    // rethrow the Connection exception coming from the initial call.
                    throw;
                }
                catch (Exception ex)
                {
                    _logEntry.Log(ex);
                    throw new DataverseConnectionException("Failed to Clone Connection", ex);
                }
            }
            else
            {
                _logEntry.Log("Connection cannot be cloned.  There is currently no OAuth based connection active or it is mis-configured in the ServiceClient.");
                return null;
            }
        }

        #region Dataverse DiscoveryServerMethods

        /// <summary>
        /// Discovers the organizations against an On-Premises deployment.
        /// </summary>
        /// <param name="discoveryServiceUri">The discovery service URI.</param>
        /// <param name="clientCredentials">The client credentials.</param>
        /// <param name="clientId">The client Id.</param>
        /// <param name="redirectUri">The redirect uri.</param>
        /// <param name="promptBehavior">The prompt behavior.</param>
        /// <param name="authority">The authority provider for OAuth tokens. Unique if any already known.</param>
        /// <param name="useDefaultCreds">(Optional) if specified, tries to use the current user</param>
        /// <param name="tokenCacheStorePath">(optional) path to log store</param>
        /// <param name="logger">Logging provider <see cref="ILogger"/></param>
        /// <returns>A collection of organizations</returns>
        public static async Task<DiscoverOrganizationsResult> DiscoverOnPremiseOrganizationsAsync(Uri discoveryServiceUri, ClientCredentials clientCredentials, string clientId, Uri redirectUri, string authority, PromptBehavior promptBehavior = PromptBehavior.Auto, bool useDefaultCreds = false, string tokenCacheStorePath = null, ILogger logger = null)
        {
            return await ConnectionService.DiscoverOrganizationsAsync(discoveryServiceUri, clientCredentials, clientId, redirectUri, promptBehavior, isOnPrem: true, authority, useDefaultCreds: useDefaultCreds, externalLogger: logger, tokenCacheStorePath: tokenCacheStorePath).ConfigureAwait(false);
        }

        /// <summary>
        /// Discovers the organizations, used for OAuth.
        /// </summary>
        /// <param name="discoveryServiceUri">The discovery service URI.</param>
        /// <param name="clientCredentials">The client credentials.</param>
        /// <param name="clientId">The client Id.</param>
        /// <param name="redirectUri">The redirect uri.</param>
        /// <param name="promptBehavior">The prompt behavior.</param>
        /// <param name="isOnPrem">The deployment type: OnPrem or Online.</param>
        /// <param name="authority">The authority provider for OAuth tokens. Unique if any already known.</param>
        /// <param name="useDefaultCreds">(Optional) if specified, tries to use the current user</param>
        /// <param name="tokenCacheStorePath">(optional) path to log store</param>
        /// <param name="logger">Logging provider <see cref="ILogger"/></param>
        /// <returns>A collection of organizations</returns>
        public static async Task<DiscoverOrganizationsResult> DiscoverOnlineOrganizationsAsync(Uri discoveryServiceUri, ClientCredentials clientCredentials, string clientId, Uri redirectUri, bool isOnPrem, string authority, PromptBehavior promptBehavior = PromptBehavior.Auto, bool useDefaultCreds = false, string tokenCacheStorePath = null, ILogger logger = null)
        {
            return await ConnectionService.DiscoverOrganizationsAsync(discoveryServiceUri, clientCredentials, clientId, redirectUri, promptBehavior, isOnPrem, authority, useGlobalDisco: true, useDefaultCreds: useDefaultCreds, externalLogger: logger, tokenCacheStorePath: tokenCacheStorePath).ConfigureAwait(false);
        }

        /// <summary>
        ///  Discovers Organizations Using the global discovery service.
        ///  <para>Provides a User ID / Password flow for authentication to the online discovery system.
        ///  You can also provide the discovery instance you wish to use, or not pass it.  If you do not specify a discovery region, the commercial global region is used</para>
        /// </summary>
        /// <param name="userId">User ID to login with</param>
        /// <param name="password">Password to use to login with</param>
        /// <param name="discoServer">(Optional) URI of the discovery server</param>
        /// <param name="clientId">The client Id.</param>
        /// <param name="redirectUri">The redirect uri.</param>
        /// <param name="promptBehavior">The prompt behavior.</param>
        /// <param name="isOnPrem">The deployment type: OnPrem or Online.</param>
        /// <param name="authority">The authority provider for OAuth tokens. Unique if any already known.</param>
        /// <param name="useDefaultCreds">(Optional) if specified, tries to use the current user</param>
        /// <param name="tokenCacheStorePath">(optional) path to log store</param>
        /// <param name="logger">Logging provider <see cref="ILogger"/></param>
        /// <returns>A collection of organizations</returns>
        public static async Task<DiscoverOrganizationsResult> DiscoverOnlineOrganizationsAsync(string userId, string password, string clientId, Uri redirectUri, bool isOnPrem, string authority, PromptBehavior promptBehavior = PromptBehavior.Auto, bool useDefaultCreds = false, Model.DiscoveryServer discoServer = null, string tokenCacheStorePath = null, ILogger logger = null)
        {
            Uri discoveryUriToUse = null;
            if (discoServer != null && discoServer.RequiresRegionalDiscovery)
            {
                // use the specified regional discovery server.
                discoveryUriToUse = discoServer.RegionalGlobalDiscoveryServer;
            }
            else
            {
                // default commercial cloud discovery server
                discoveryUriToUse = new Uri(ConnectionService.GlobalDiscoveryAllInstancesUri);
            }

            // create credentials.
            ClientCredentials clientCredentials = new ClientCredentials();
            clientCredentials.UserName.UserName = userId;
            clientCredentials.UserName.Password = password;

            return await ConnectionService.DiscoverOrganizationsAsync(discoveryUriToUse, clientCredentials, clientId, redirectUri, promptBehavior, isOnPrem, authority, useGlobalDisco: true, useDefaultCreds: useDefaultCreds, externalLogger: logger, tokenCacheStorePath: tokenCacheStorePath).ConfigureAwait(false);
        }

        /// <summary>
        /// Discovers Organizations Using the global discovery service and an external source for access tokens
        /// </summary>
        /// <param name="discoveryServiceUri">Global discovery base URI to use to connect too,  if null will utilize the commercial Global Discovery Server.</param>
        /// <param name="tokenProviderFunction">Function that will provide access token to the discovery call.</param>
        /// <param name="tokenCacheStorePath">(optional) path to log store</param>
        /// <param name="logger">Logging provider <see cref="ILogger"/></param>
        /// <returns></returns>
        public static async Task<OrganizationDetailCollection> DiscoverOnlineOrganizationsAsync(Func<string, Task<string>> tokenProviderFunction, Uri discoveryServiceUri = null, string tokenCacheStorePath = null, ILogger logger = null)
        {
            if (discoveryServiceUri == null)
                discoveryServiceUri = new Uri(ConnectionService.GlobalDiscoveryAllInstancesUri); // use commercial GD

            return await ConnectionService.DiscoverGlobalOrganizationsAsync(discoveryServiceUri, tokenProviderFunction, externalLogger: logger, tokenCacheStorePath: tokenCacheStorePath).ConfigureAwait(false);
        }

        #endregion

        #region Dataverse Service Methods

        #region Batch Interface methods.
        /// <summary>
        /// Create a Batch Request for executing batch operations.  This returns an ID that will be used to identify a request as a batch request vs a "normal" request.
        /// </summary>
        /// <param name="batchName">Name of the Batch</param>
        /// <param name="returnResults">Should Results be returned</param>
        /// <param name="continueOnError">Should the process continue on an error.</param>
        /// <returns></returns>
        public Guid CreateBatchOperationRequest(string batchName, bool returnResults = true, bool continueOnError = false)
        {
            #region PreChecks
            _logEntry.ResetLastError();
            if (DataverseService == null)
            {
                _logEntry.Log("Dataverse Service not initialized", TraceEventType.Error);
                return Guid.Empty;
            }

            if (!IsBatchOperationsAvailable)
            {
                _logEntry.Log("Batch Operations are not available", TraceEventType.Error);
                return Guid.Empty;
            }
            #endregion

            Guid guBatchId = Guid.Empty;
            if (_batchManager != null)
            {
                // Try to create a new Batch here.
                guBatchId = _batchManager.CreateNewBatch(batchName, returnResults, continueOnError);
            }
            return guBatchId;
        }

        /// <summary>
        /// Returns the batch id for a given batch name.
        /// </summary>
        /// <param name="batchName">Name of Batch</param>
        /// <returns></returns>
        public Guid GetBatchOperationIdRequestByName(string batchName)
        {
            #region PreChecks
            _logEntry.ResetLastError();
            if (DataverseService == null)
            {
                _logEntry.Log("Dataverse Service not initialized", TraceEventType.Error);
                return Guid.Empty;
            }

            if (!IsBatchOperationsAvailable)
            {
                _logEntry.Log("Batch Operations are not available", TraceEventType.Error);
                return Guid.Empty;
            }
            #endregion

            if (_batchManager != null)
            {
                var b = _batchManager.GetRequestBatchByName(batchName);
                if (b != null)
                    return b.BatchId;
            }
            return Guid.Empty;
        }


        /// <summary>
        /// Returns the organization request at a give position
        /// </summary>
        /// <param name="batchId">ID of the batch</param>
        /// <param name="position">Position</param>
        /// <returns></returns>
        public OrganizationRequest GetBatchRequestAtPosition(Guid batchId, int position)
        {
            #region PreChecks
            _logEntry.ResetLastError();
            if (DataverseService == null)
            {
                _logEntry.Log("Dataverse Service not initialized", TraceEventType.Error);
                return null;
            }

            if (!IsBatchOperationsAvailable)
            {
                _logEntry.Log("Batch Operations are not available", TraceEventType.Error);
                return null;
            }
            #endregion

            RequestBatch b = GetBatchById(batchId);
            if (b != null)
            {
                if (b.BatchItems.Count >= position)
                    return b.BatchItems[position].Request;
            }
            return null;
        }

        /// <summary>
        /// Release a batch from the stack
        /// Once you have completed using a batch, you must release it from the system.
        /// </summary>
        /// <param name="batchId">ID of the batch</param>
        public void ReleaseBatchInfoById(Guid batchId)
        {
            #region PreChecks
            _logEntry.ResetLastError();
            if (DataverseService == null)
            {
                _logEntry.Log("Dataverse Service not initialized", TraceEventType.Error);
                return;
            }

            if (!IsBatchOperationsAvailable)
            {
                _logEntry.Log("Batch Operations are not available", TraceEventType.Error);
                return;
            }
            #endregion

            if (_batchManager != null)
                _batchManager.RemoveBatch(batchId);

        }

        /// <summary>
        /// TEMP
        /// </summary>
        /// <param name="batchId">ID of the batch</param>
        /// <returns></returns>
        public RequestBatch GetBatchById(Guid batchId)
        {
            #region PreChecks
            _logEntry.ResetLastError();
            if (DataverseService == null)
            {
                _logEntry.Log("Dataverse Service not initialized", TraceEventType.Error);
                return null;
            }

            if (!IsBatchOperationsAvailable)
            {
                _logEntry.Log("Batch Operations are not available", TraceEventType.Error);
                return null;
            }
            #endregion

            if (_batchManager != null)
            {
                return _batchManager.GetRequestBatchById(batchId);
            }
            return null;
        }

        /// <summary>
        /// Executes the batch command and then parses the retrieved items into a list.
        /// If there exists a exception then the LastException would be filled with the first item that has the exception.
        /// </summary>
        /// <param name="batchId">ID of the batch to run</param>
        /// <returns>results which is a list of responses(type <![CDATA[ List<Dictionary<string, Dictionary<string, object>>> ]]>) in the order of each request or null or complete failure  </returns>
        public List<Dictionary<string, Dictionary<string, object>>> RetrieveBatchResponse(Guid batchId)
        {
            ExecuteMultipleResponse results = ExecuteBatch(batchId);
            if (results == null)
            {
                return null;
            }
            if (results.IsFaulted)
            {
                foreach (var response in results.Responses)
                {
                    if (response.Fault != null)
                    {
                        FaultException<OrganizationServiceFault> ex = new FaultException<OrganizationServiceFault>(response.Fault, new FaultReason(new FaultReasonText(response.Fault.Message)));

                        _logEntry.Log(string.Format(CultureInfo.InvariantCulture, "Failed to Execute Batch - {0}", batchId), TraceEventType.Verbose);
                        _logEntry.Log(string.Format(CultureInfo.InvariantCulture, "************ BatchExecution failed - : {0}\n\r{1}", response.Fault.Message, response.Fault.ErrorDetails.ToString()), TraceEventType.Error, ex);
                        break;
                    }
                }
            }
            List<Dictionary<string, Dictionary<string, object>>> retrieveMultipleResponseList = new List<Dictionary<string, Dictionary<string, object>>>();
            foreach (var response in results.Responses)
            {
                if (response.Response != null)
                {
                    retrieveMultipleResponseList.Add(CreateResultDataSet(((RetrieveMultipleResponse)response.Response).EntityCollection));
                }
            }
            return retrieveMultipleResponseList;
        }


        /// <summary>
        /// Begins running the Batch command.
        /// </summary>
        /// <param name="batchId">ID of the batch to run</param>
        /// <returns>true if the batch begins, false if not. </returns>
        public ExecuteMultipleResponse ExecuteBatch(Guid batchId)
        {
            #region PreChecks
            _logEntry.ResetLastError();
            if (DataverseService == null)
            {
                _logEntry.Log("Dataverse Service not initialized", TraceEventType.Error);
                return null;
            }

            if (!IsBatchOperationsAvailable)
            {
                _logEntry.Log("Batch Operations are not available", TraceEventType.Error);
                return null;
            }
            #endregion

            if (_batchManager != null)
            {
                var b = _batchManager.GetRequestBatchById(batchId);
                if (b.Status == BatchStatus.Complete || b.Status == BatchStatus.Running)
                {
                    _logEntry.Log("Batch is not in the correct state to run", TraceEventType.Error);
                    return null;
                }

                if (!(b.BatchItems.Count > 0))
                {
                    _logEntry.Log("No Items in the batch", TraceEventType.Error);
                    return null;
                }

                // Ready to run the batch.
                _logEntry.Log(string.Format(CultureInfo.InvariantCulture, "Executing Batch {0}|{1}, Sending {2} events.", b.BatchId, b.BatchName, b.BatchItems.Count), TraceEventType.Verbose);
                ExecuteMultipleRequest req = new ExecuteMultipleRequest();
                req.Settings = b.BatchRequestSettings;
                OrganizationRequestCollection reqstList = new OrganizationRequestCollection();

                // Make sure the batch is ordered.
                reqstList.AddRange(b.BatchItems.Select(s => s.Request));

                req.Requests = reqstList;
                b.Status = BatchStatus.Running;
                ExecuteMultipleResponse resp = (ExecuteMultipleResponse)Command_Execute(req, "Execute Batch Command");
                // Need to add retry logic here to deal with a "server busy" status.
                b.Status = BatchStatus.Complete;
                if (resp != null)
                {
                    if (resp.IsFaulted)
                        _logEntry.Log("Batch request faulted.", TraceEventType.Warning);
                    b.BatchResults = resp;
                    return b.BatchResults;
                }
                _logEntry.Log("Batch request faulted - No Results.", TraceEventType.Warning);
            }
            return null;
        }

        // Need methods here to work with the batch now,
        // get items out by id,
        // get batch request.


        #endregion

        /// <summary>
        /// Uses the dynamic entity patter to create a new entity
        /// </summary>
        /// <param name="entityName">Name of Entity To create</param>
        /// <param name="valueArray">Initial Values</param>
        /// <param name="applyToSolution">Optional: Applies the update with a solution by Unique name</param>
        /// <param name="enabledDuplicateDetection">Optional: if true, enabled Dataverse onboard duplicate detection</param>
        /// <param name="batchId">Optional: if set to a valid GUID, generated by the Create Batch Request Method, will assigned the request to the batch for later execution, on fail, runs the request immediately </param>
		/// <param name="bypassPluginExecution">Adds the bypass plugin behavior to this request. Note: this will only apply if the caller has the prvBypassPlugins permission to bypass plugins.  If its attempted without the permission the request will fault.</param>
        /// <returns>Guid on Success, Guid.Empty on fail</returns>
        public Guid CreateNewRecord(string entityName, Dictionary<string, DataverseDataTypeWrapper> valueArray, string applyToSolution = "", bool enabledDuplicateDetection = false, Guid batchId = default(Guid), bool bypassPluginExecution = false)
        {
            _logEntry.ResetLastError();  // Reset Last Error

            if (DataverseService == null)
            {
                _logEntry.Log("Dataverse Service not initialized", TraceEventType.Error);
                return Guid.Empty;
            }

            if (string.IsNullOrEmpty(entityName))
                return Guid.Empty;

            if ((valueArray == null) || (valueArray.Count == 0))
                return Guid.Empty;


            // Create the New Entity Type.
            Entity NewEnt = new Entity();
            NewEnt.LogicalName = entityName;

            AttributeCollection propList = new AttributeCollection();
            foreach (KeyValuePair<string, DataverseDataTypeWrapper> i in valueArray)
            {
                AddValueToPropertyList(i, propList);
            }

            NewEnt.Attributes.AddRange(propList);

            CreateRequest createReq = new CreateRequest();
            createReq.Target = NewEnt;
            createReq.Parameters.Add("SuppressDuplicateDetection", !enabledDuplicateDetection);
            if (!string.IsNullOrWhiteSpace(applyToSolution))
                createReq.Parameters.Add(Utilities.RequestHeaders.SOLUTIONUNIQUENAME, applyToSolution);

            CreateResponse createResp = null;

            if (AddRequestToBatch(batchId, createReq, entityName, string.Format(CultureInfo.InvariantCulture, "Request for Create on {0} queued", entityName), bypassPluginExecution))
                return Guid.Empty;

            createResp = (CreateResponse)ExecuteOrganizationRequestImpl(createReq, entityName, useWebAPI: true, bypassPluginExecution: bypassPluginExecution);
            if (createResp != null)
            {
                return createResp.id;
            }
            else
                return Guid.Empty;

        }

        /// <summary>
        /// Generic update entity
        /// </summary>
        /// <param name="entityName">String version of the entity name</param>
        /// <param name="keyFieldName">Key fieldname of the entity </param>
        /// <param name="id">Guid ID of the entity to update</param>
        /// <param name="fieldList">Fields to update</param>
        /// <param name="applyToSolution">Optional: Applies the update with a solution by Unique name</param>
        /// <param name="enabledDuplicateDetection">Optional: if true, enabled Dataverse onboard duplicate detection</param>
        /// <param name="batchId">Optional: if set to a valid GUID, generated by the Create Batch Request Method, will assigned the request to the batch for later execution, on fail, runs the request immediately </param>
		/// <param name="bypassPluginExecution">Adds the bypass plugin behavior to this request. Note: this will only apply if the caller has the prvBypassPlugins permission to bypass plugins.  If its attempted without the permission the request will fault.</param>
        /// <returns>true on success, false on fail</returns>
        public bool UpdateEntity(string entityName, string keyFieldName, Guid id, Dictionary<string, DataverseDataTypeWrapper> fieldList, string applyToSolution = "", bool enabledDuplicateDetection = false, Guid batchId = default(Guid), bool bypassPluginExecution = false)
        {
            _logEntry.ResetLastError();  // Reset Last Error
            if (DataverseService == null || id == Guid.Empty)
            {
                return false;
            }

            if (fieldList == null || fieldList.Count == 0)
                return false;

            Entity uEnt = new Entity();
            uEnt.LogicalName = entityName;


            AttributeCollection PropertyList = new AttributeCollection();

            #region MapCode
            foreach (KeyValuePair<string, DataverseDataTypeWrapper> field in fieldList)
            {
                AddValueToPropertyList(field, PropertyList);
            }

            // Add the key...
            // check to see if the key is in the import set already
            if (!fieldList.ContainsKey(keyFieldName))
                PropertyList.Add(new KeyValuePair<string, object>(keyFieldName, id));

            #endregion

            uEnt.Attributes.AddRange(PropertyList.ToArray());
            uEnt.Id = id;

            UpdateRequest req = new UpdateRequest();
            req.Target = uEnt;

            req.Parameters.Add("SuppressDuplicateDetection", !enabledDuplicateDetection);
            if (!string.IsNullOrWhiteSpace(applyToSolution))
                req.Parameters.Add("SolutionUniqueName", applyToSolution);


            if (AddRequestToBatch(batchId, req, string.Format(CultureInfo.InvariantCulture, "Updating {0} : {1}", entityName, id.ToString()), string.Format(CultureInfo.InvariantCulture, "Request for update on {0} queued", entityName), bypassPluginExecution))
                return false;

            UpdateResponse resp = (UpdateResponse)ExecuteOrganizationRequestImpl(req, string.Format(CultureInfo.InvariantCulture, "Updating {0} : {1}", entityName, id.ToString()), useWebAPI: true, bypassPluginExecution: bypassPluginExecution);
            if (resp == null)
                return false;
            else
                return true;
        }


        /// <summary>
        /// Updates the State and Status of the Entity passed in.
        /// </summary>
        /// <param name="entName">Name of the entity</param>
        /// <param name="id">Guid ID of the entity you are updating</param>
        /// <param name="stateCode">String version of the new state</param>
        /// <param name="statusCode">String Version of the new status</param>
        /// <param name="batchId">Optional : Batch ID  to attach this request too.</param>
		/// <param name="bypassPluginExecution">Adds the bypass plugin behavior to this request. Note: this will only apply if the caller has the prvBypassPlugins permission to bypass plugins.  If its attempted without the permission the request will fault.</param>
        /// <returns>true on success. </returns>
        public bool UpdateStateAndStatusForEntity(string entName, Guid id, string stateCode, string statusCode, Guid batchId = default(Guid), bool bypassPluginExecution = false)
        {
            return UpdateStateStatusForEntity(entName, id, stateCode, statusCode, batchId: batchId, bypassPluginExecution: bypassPluginExecution);
        }

        /// <summary>
        /// Updates the State and Status of the Entity passed in.
        /// </summary>
        /// <param name="entName">Name of the entity</param>
        /// <param name="id">Guid ID of the entity you are updating</param>
        /// <param name="stateCode">Int version of the new state</param>
        /// <param name="statusCode">Int Version of the new status</param>
        /// <param name="batchId">Optional : Batch ID  to attach this request too.</param>
        /// <param name="bypassPluginExecution">Adds the bypass plugin behavior to this request. Note: this will only apply if the caller has the prvBypassPlugins permission to bypass plugins.  If its attempted without the permission the request will fault.</param>
        /// <returns>true on success. </returns>
        public bool UpdateStateAndStatusForEntity(string entName, Guid id, int stateCode, int statusCode, Guid batchId = default(Guid), bool bypassPluginExecution = false)
        {
            return UpdateStateStatusForEntity(entName, id, string.Empty, string.Empty, stateCode, statusCode, batchId, bypassPluginExecution);
        }

        /// <summary>
        /// Deletes an entity from the Dataverse
        /// </summary>
        /// <param name="entityType">entity type name</param>
        /// <param name="entityId">entity id</param>
        /// <param name="batchId">Optional : Batch ID  to attach this request too.</param>
        /// <param name="bypassPluginExecution">Adds the bypass plugin behavior to this request. Note: this will only apply if the caller has the prvBypassPlugins permission to bypass plugins.  If its attempted without the permission the request will fault.</param>
        /// <returns>true on success, false on failure</returns>
        public bool DeleteEntity(string entityType, Guid entityId, Guid batchId = default(Guid), bool bypassPluginExecution = false)
        {
            _logEntry.ResetLastError();  // Reset Last Error
            if (DataverseService == null)
            {
                _logEntry.Log("Dataverse Service not initialized", TraceEventType.Error);
                return false;
            }

            DeleteRequest req = new DeleteRequest();
            req.Target = new EntityReference(entityType, entityId);

            if (batchId != Guid.Empty)
            {
                if (IsBatchOperationsAvailable)
                {
                    if (_batchManager.AddNewRequestToBatch(batchId, req, string.Format(CultureInfo.InvariantCulture, "Trying to Delete. Entity = {0}, ID = {1}", entityType, entityId)))
                    {
                        _logEntry.Log(string.Format(CultureInfo.InvariantCulture, "Request for Delete on {0} queued", entityType), TraceEventType.Verbose);
                        return false;
                    }
                    else
                        _logEntry.Log("Unable to add request to batch queue, Executing normally", TraceEventType.Warning);
                }
                else
                {
                    // Error and fall though.
                    _logEntry.Log("Unable to add request to batch, Batching is not currently available, Executing normally", TraceEventType.Warning);
                }
            }

            if (batchId != Guid.Empty)
            {
                if (IsBatchOperationsAvailable)
                {
                    if (_batchManager.AddNewRequestToBatch(batchId, req, string.Format(CultureInfo.InvariantCulture, "Delete Entity = {0}, ID = {1}  queued", entityType, entityId)))
                    {
                        _logEntry.Log(string.Format(CultureInfo.InvariantCulture, "Request for Delete. Entity = {0}, ID = {1}  queued", entityType, entityId), TraceEventType.Verbose);
                        return false;
                    }
                    else
                        _logEntry.Log("Unable to add request to batch queue, Executing normally", TraceEventType.Warning);
                }
                else
                {
                    // Error and fall though.
                    _logEntry.Log("Unable to add request to batch, Batching is not currently available, Executing normally", TraceEventType.Warning);
                }
            }

            if (AddRequestToBatch(batchId, req, string.Format(CultureInfo.InvariantCulture, "Trying to Delete. Entity = {0}, ID = {1}", entityType, entityId), string.Format(CultureInfo.InvariantCulture, "Request to Delete. Entity = {0}, ID = {1} Queued", entityType, entityId), bypassPluginExecution))
                return false;

            DeleteResponse resp = (DeleteResponse)ExecuteOrganizationRequestImpl(req, string.Format(CultureInfo.InvariantCulture, "Trying to Delete. Entity = {0}, ID = {1}", entityType, entityId), useWebAPI: true, bypassPluginExecution: bypassPluginExecution);
            if (resp != null)
            {
                // Clean out the cache if the account happens to be stored in there.
                if ((_CachObject != null) && (_CachObject.ContainsKey(entityType)))
                {
                    while (_CachObject[entityType].ContainsValue(entityId))
                    {
                        foreach (KeyValuePair<string, Guid> v in _CachObject[entityType].Values)
                        {
                            if (v.Value == entityId)
                            {
                                _CachObject[entityType].Remove(v.Key);
                                break;
                            }
                        }
                    }
                }
                return true;
            }
            return false;
        }

        /// <summary>
        /// Gets a list of accounts based on the search parameters.
        /// </summary>
        /// <param name="entityName">Dataverse Entity Type Name to search</param>
        /// <param name="searchParameters">Array of Search Parameters</param>
        /// <param name="fieldList">List of fields to retrieve, Null indicates all Fields</param>
        /// <param name="searchOperator">Logical Search Operator</param>
        /// <param name="bypassPluginExecution">Adds the bypass plugin behavior to this request. Note: this will only apply if the caller has the prvBypassPlugins permission to bypass plugins.  If its attempted without the permission the request will fault.</param>
        /// <param name="batchId">Optional: if set to a valid GUID, generated by the Create Batch Request Method, will assigned the request to the batch for later execution, on fail, runs the request immediately </param>
        /// <returns>List of matching Entity Types. </returns>

        [SuppressMessage("Microsoft.Naming", "CA1704:IdentifiersShouldBeSpelledCorrectly", Scope = "member")]
        public Dictionary<string, Dictionary<string, object>> GetEntityDataBySearchParams(string entityName,
            Dictionary<string, string> searchParameters,
            LogicalSearchOperator searchOperator,
            List<string> fieldList,
            Guid batchId = default(Guid),
            bool bypassPluginExecution = false)
        {
            List<DataverseSearchFilter> searchList = new List<DataverseSearchFilter>();
            BuildSearchFilterListFromSearchTerms(searchParameters, searchList);

            string pgCookie = string.Empty;
            bool moreRec = false;
            return GetEntityDataBySearchParams(entityName, searchList, searchOperator, fieldList, null, -1, -1, string.Empty, out pgCookie, out moreRec, batchId, bypassPluginExecution: bypassPluginExecution);
        }


        /// <summary>
        /// Gets a list of accounts based on the search parameters.
        /// </summary>
        /// <param name="entityName">Dataverse Entity Type Name to search</param>
        /// <param name="searchParameters">Array of Search Parameters</param>
        /// <param name="fieldList">List of fields to retrieve, Null indicates all Fields</param>
        /// <param name="searchOperator">Logical Search Operator</param>
        /// <param name="batchId">Optional: if set to a valid GUID, generated by the Create Batch Request Method, will assigned the request to the batch for later execution, on fail, runs the request immediately </param>
		/// <param name="bypassPluginExecution">Adds the bypass plugin behavior to this request. Note: this will only apply if the caller has the prvBypassPlugins permission to bypass plugins.  If its attempted without the permission the request will fault.</param>
        /// <returns>List of matching Entity Types. </returns>
        [SuppressMessage("Microsoft.Naming", "CA1704:IdentifiersShouldBeSpelledCorrectly", Scope = "member")]
        public Dictionary<string, Dictionary<string, object>> GetEntityDataBySearchParams(string entityName,
            List<DataverseSearchFilter> searchParameters,
            LogicalSearchOperator searchOperator,
            List<string> fieldList, Guid batchId = default(Guid),
            bool bypassPluginExecution = false)
        {
            string pgCookie = string.Empty;
            bool moreRec = false;
            return GetEntityDataBySearchParams(entityName, searchParameters, searchOperator, fieldList, null, -1, -1, string.Empty, out pgCookie, out moreRec, batchId, bypassPluginExecution);
        }

        /// <summary>
        /// Searches for data from an entity based on the search parameters.
        /// </summary>
        /// <param name="entityName">Name of the entity to search </param>
        /// <param name="searchParameters">Array of Search Parameters</param>
        /// <param name="fieldList">List of fields to retrieve, Null indicates all Fields</param>
        /// <param name="searchOperator">Logical Search Operator</param>
        /// <param name="pageCount">Number records per Page</param>
        /// <param name="pageNumber">Current Page number</param>
        /// <param name="pageCookie">inbound place holder cookie</param>
        /// <param name="outPageCookie">outbound place holder cookie</param>
        /// <param name="isMoreRecords">is there more records or not</param>
        /// <param name="sortParameters">Sort order</param>
        /// <param name="batchId">Optional: if set to a valid GUID, generated by the Create Batch Request Method, will assigned the request to the batch for later execution, on fail, runs the request immediately </param>
		/// <param name="bypassPluginExecution">Adds the bypass plugin behavior to this request. Note: this will only apply if the caller has the prvBypassPlugins permission to bypass plugins.  If its attempted without the permission the request will fault.</param>
        /// <returns>List of matching Entity Types. </returns>
        [SuppressMessage("Microsoft.Naming", "CA1704:IdentifiersShouldBeSpelledCorrectly", Scope = "member")]
        public Dictionary<string, Dictionary<string, object>> GetEntityDataBySearchParams(string entityName,
            List<DataverseSearchFilter> searchParameters,
            LogicalSearchOperator searchOperator,
            List<string> fieldList,
            Dictionary<string, LogicalSortOrder> sortParameters,
            int pageCount,
            int pageNumber,
            string pageCookie,
            out string outPageCookie,
            out bool isMoreRecords,
            Guid batchId = default(Guid),
            bool bypassPluginExecution = false
            )
        {
            _logEntry.ResetLastError();  // Reset Last Error

            outPageCookie = string.Empty;
            isMoreRecords = false;

            if (DataverseService == null)
            {
                _logEntry.Log("Dataverse Service not initialized", TraceEventType.Error);
                return null;
            }

            if (searchParameters == null)
                searchParameters = new List<DataverseSearchFilter>();

            // Build the query here.
            QueryExpression query = BuildQueryFilter(entityName, searchParameters, fieldList, searchOperator);

            if (pageCount != -1)
            {
                PagingInfo pgInfo = new PagingInfo();
                pgInfo.Count = pageCount;
                pgInfo.PageNumber = pageNumber;
                pgInfo.PagingCookie = pageCookie;
                query.PageInfo = pgInfo;
            }

            if (sortParameters != null)
                if (sortParameters.Count > 0)
                {
                    List<OrderExpression> qExpressList = new List<OrderExpression>();
                    foreach (KeyValuePair<string, LogicalSortOrder> itm in sortParameters)
                    {
                        OrderExpression ordBy = new OrderExpression();
                        ordBy.AttributeName = itm.Key;
                        if (itm.Value == LogicalSortOrder.Ascending)
                            ordBy.OrderType = OrderType.Ascending;
                        else
                            ordBy.OrderType = OrderType.Descending;

                        qExpressList.Add(ordBy);
                    }

                    query.Orders.AddRange(qExpressList.ToArray());
                }


            RetrieveMultipleRequest retrieve = new RetrieveMultipleRequest();
            //retrieve.ReturnDynamicEntities = true;
            retrieve.Query = query;


            if (AddRequestToBatch(batchId, retrieve, "Running GetEntityDataBySearchParms", "Request For GetEntityDataBySearchParms Queued", bypassPluginExecution))
                return null;


            RetrieveMultipleResponse retrieved;
            retrieved = (RetrieveMultipleResponse)Command_Execute(retrieve, "GetEntityDataBySearchParms", bypassPluginExecution);
            if (retrieved != null)
            {
                outPageCookie = retrieved.EntityCollection.PagingCookie;
                isMoreRecords = retrieved.EntityCollection.MoreRecords;

                return CreateResultDataSet(retrieved.EntityCollection);
            }
            else
                return null;
        }


        /// <summary>
        /// Searches for data based on a FetchXML query
        /// </summary>
        /// <param name="fetchXml">Fetch XML query data.</param>
        /// <param name="batchId">Optional: if set to a valid GUID, generated by the Create Batch Request Method, will assigned the request to the batch for later execution, on fail, runs the request immediately </param>
        /// <param name="bypassPluginExecution">Adds the bypass plugin behavior to this request. Note: this will only apply if the caller has the prvBypassPlugins permission to bypass plugins.  If its attempted without the permission the request will fault.</param>
        /// <returns>results or null</returns>
        public Dictionary<string, Dictionary<string, object>> GetEntityDataByFetchSearch(string fetchXml, Guid batchId = default(Guid), bool bypassPluginExecution = false)
        {
            EntityCollection ec = GetEntityDataByFetchSearchEC(fetchXml, batchId, bypassPluginExecution);
            if (ec != null)
                return CreateResultDataSet(ec);
            else
                return null;
        }


        /// <summary>
        /// Searches for data based on a FetchXML query
        /// </summary>
        /// <param name="fetchXml">Fetch XML query data.</param>
        /// <param name="batchId">Optional: if set to a valid GUID, generated by the Create Batch Request Method, will assigned the request to the batch for later execution, on fail, runs the request immediately </param>
        /// <param name="bypassPluginExecution">Adds the bypass plugin behavior to this request. Note: this will only apply if the caller has the prvBypassPlugins permission to bypass plugins.  If its attempted without the permission the request will fault.</param>
        /// <returns>results as an entity collection or null</returns>
        public EntityCollection GetEntityDataByFetchSearchEC(string fetchXml, Guid batchId = default(Guid), bool bypassPluginExecution = false)
        {
            _logEntry.ResetLastError();  // Reset Last Error

            if (DataverseService == null)
            {
                _logEntry.Log("Dataverse Service not initialized", TraceEventType.Error);
                return null;
            }

            if (string.IsNullOrWhiteSpace(fetchXml))
                return null;

            // This model directly requests the via FetchXML
            RetrieveMultipleRequest req = new RetrieveMultipleRequest() { Query = new FetchExpression(fetchXml) };
            RetrieveMultipleResponse retrieved;

            if (AddRequestToBatch(batchId, req, "Running GetEntityDataByFetchSearchEC", "Request For GetEntityDataByFetchSearchEC Queued", bypassPluginExecution))
                return null;

            retrieved = (RetrieveMultipleResponse)Command_Execute(req, "GetEntityDataByFetchSearch - Direct", bypassPluginExecution);
            if (retrieved != null)
            {
                return retrieved.EntityCollection;
            }
            else
                return null;
        }

        /// <summary>
        /// Searches for data based on a FetchXML query
        /// </summary>
        /// <param name="fetchXml">Fetch XML query data.</param>
        /// <param name="pageCount">Number records per Page</param>
        /// <param name="pageNumber">Current Page number</param>
        /// <param name="pageCookie">inbound place holder cookie</param>
        /// <param name="outPageCookie">outbound place holder cookie</param>
        /// <param name="isMoreRecords">is there more records or not</param>
        /// <param name="bypassPluginExecution">Adds the bypass plugin behavior to this request. Note: this will only apply if the caller has the prvBypassPlugins permission to bypass plugins.  If its attempted without the permission the request will fault.</param>
        /// <param name="batchId">Optional: if set to a valid GUID, generated by the Create Batch Request Method, will assigned the request to the batch for later execution, on fail, runs the request immediately </param>
        /// <returns>results or null</returns>
        public Dictionary<string, Dictionary<string, object>> GetEntityDataByFetchSearch(
                string fetchXml,
                int pageCount,
                int pageNumber,
                string pageCookie,
                out string outPageCookie,
                out bool isMoreRecords,
                Guid batchId = default(Guid),
                bool bypassPluginExecution = false)
        {
            EntityCollection ec = GetEntityDataByFetchSearchEC(fetchXml, pageCount, pageNumber, pageCookie, out outPageCookie, out isMoreRecords, bypassPluginExecution: bypassPluginExecution);
            if (ec != null)
                return CreateResultDataSet(ec);
            else
                return null;
        }

        /// <summary>
        /// Searches for data based on a FetchXML query
        /// </summary>
        /// <param name="fetchXml">Fetch XML query data.</param>
        /// <param name="pageCount">Number records per Page</param>
        /// <param name="pageNumber">Current Page number</param>
        /// <param name="pageCookie">inbound place holder cookie</param>
        /// <param name="outPageCookie">outbound place holder cookie</param>
        /// <param name="batchId">Optional: if set to a valid GUID, generated by the Create Batch Request Method, will assigned the request to the batch for later execution, on fail, runs the request immediately </param>
        /// <param name="bypassPluginExecution">Adds the bypass plugin behavior to this request. Note: this will only apply if the caller has the prvBypassPlugins permission to bypass plugins.  If its attempted without the permission the request will fault.</param>
        /// <param name="isMoreRecords">is there more records or not</param>
        /// <returns>results as an Entity Collection or null</returns>
        public EntityCollection GetEntityDataByFetchSearchEC(
            string fetchXml,
            int pageCount,
            int pageNumber,
            string pageCookie,
            out string outPageCookie,
            out bool isMoreRecords,
            Guid batchId = default(Guid),
            bool bypassPluginExecution = false)
        {

            _logEntry.ResetLastError();  // Reset Last Error

            outPageCookie = string.Empty;
            isMoreRecords = false;

            if (DataverseService == null)
            {
                _logEntry.Log("Dataverse Service not initialized", TraceEventType.Error);
                return null;
            }

            if (string.IsNullOrWhiteSpace(fetchXml))
                return null;

            if (pageCount != -1)
            {
                // Add paging related parameter to fetch xml.
                fetchXml = AddPagingParametersToFetchXml(fetchXml, pageCount, pageNumber, pageCookie);
            }

            RetrieveMultipleRequest retrieve = new RetrieveMultipleRequest() { Query = new FetchExpression(fetchXml) };
            RetrieveMultipleResponse retrieved;

            if (AddRequestToBatch(batchId, retrieve, "Running GetEntityDataByFetchSearchEC", "Request For GetEntityDataByFetchSearchEC Queued", bypassPluginExecution))
                return null;

            retrieved = (RetrieveMultipleResponse)Command_Execute(retrieve, "GetEntityDataByFetchSearch", bypassPluginExecution);
            if (retrieved != null)
            {
                outPageCookie = retrieved.EntityCollection.PagingCookie;
                isMoreRecords = retrieved.EntityCollection.MoreRecords;
                return retrieved.EntityCollection;
            }

            return null;
        }


        /// <summary>
        /// Queries an Object via a M to M Link
        /// </summary>
        /// <param name="returnEntityName">Name of the entity you want return data from</param>
        /// <param name="primarySearchParameters">Search Prams for the Return Entity</param>
        /// <param name="linkedEntityName">Name of the entity you are linking too</param>
        /// <param name="linkedSearchParameters">Search Prams for the Entity you are linking too</param>
        /// <param name="linkedEntityLinkAttribName">Key field on the Entity you are linking too</param>
        /// <param name="m2MEntityName">Dataverse Name of the Relationship </param>
        /// <param name="returnEntityPrimaryId">Key field on the Entity you want to return data from</param>
        /// <param name="searchOperator">Search Operator to apply</param>
        /// <param name="batchId">Optional: if set to a valid GUID, generated by the Create Batch Request Method, will assigned the request to the batch for later execution, on fail, runs the request immediately </param>
		/// <param name="bypassPluginExecution">Adds the bypass plugin behavior to this request. Note: this will only apply if the caller has the prvBypassPlugins permission to bypass plugins.  If its attempted without the permission the request will fault.</param>
        /// <param name="fieldList">List of Fields from the Returned Entity you want</param>
        /// <returns></returns>
        public Dictionary<string, Dictionary<string, object>> GetEntityDataByLinkedSearch(
                string returnEntityName,
                Dictionary<string, string> primarySearchParameters,
                string linkedEntityName,
                Dictionary<string, string> linkedSearchParameters,
                string linkedEntityLinkAttribName,
                string m2MEntityName,
                string returnEntityPrimaryId,
                LogicalSearchOperator searchOperator,
                List<string> fieldList,
            Guid batchId = default(Guid),
            bool bypassPluginExecution = false)
        {
            List<DataverseSearchFilter> primarySearchList = new List<DataverseSearchFilter>();
            BuildSearchFilterListFromSearchTerms(primarySearchParameters, primarySearchList);

            List<DataverseSearchFilter> linkedSearchList = new List<DataverseSearchFilter>();
            BuildSearchFilterListFromSearchTerms(linkedSearchParameters, linkedSearchList);

            return GetEntityDataByLinkedSearch(returnEntityName, primarySearchList, linkedEntityName, linkedSearchList, linkedEntityLinkAttribName,
                        m2MEntityName, returnEntityPrimaryId, searchOperator, fieldList, bypassPluginExecution: bypassPluginExecution);

        }

        /// <summary>
        /// Queries an Object via a M to M Link
        /// </summary>
        /// <param name="returnEntityName">Name of the entity you want return data from</param>
        /// <param name="primarySearchParameters">Search Prams for the Return Entity</param>
        /// <param name="linkedEntityName">Name of the entity you are linking too</param>
        /// <param name="linkedSearchParameters">Search Prams for the Entity you are linking too</param>
        /// <param name="linkedEntityLinkAttribName">Key field on the Entity you are linking too</param>
        /// <param name="m2MEntityName">Dataverse Name of the Relationship </param>
        /// <param name="returnEntityPrimaryId">Key field on the Entity you want to return data from</param>
        /// <param name="searchOperator">Search Operator to apply</param>
        /// <param name="batchId">Optional: if set to a valid GUID, generated by the Create Batch Request Method, will assigned the request to the batch for later execution, on fail, runs the request immediately </param>
        /// <param name="fieldList">List of Fields from the Returned Entity you want</param>
        /// <param name="bypassPluginExecution">Adds the bypass plugin behavior to this request. Note: this will only apply if the caller has the prvBypassPlugins permission to bypass plugins.  If its attempted without the permission the request will fault.</param>
        /// <param name="isReflexiveRelationship">If the relationship is defined as Entity:Entity or Account N:N Account, this parameter should be set to true</param>
        /// <returns></returns>
        public Dictionary<string, Dictionary<string, object>> GetEntityDataByLinkedSearch(
            string returnEntityName,
            List<DataverseSearchFilter> /*Dictionary<string, string>*/ primarySearchParameters,
            string linkedEntityName,
            List<DataverseSearchFilter> /*Dictionary<string, string>*/ linkedSearchParameters,
            string linkedEntityLinkAttribName,
            string m2MEntityName,
            string returnEntityPrimaryId,
            LogicalSearchOperator searchOperator,
            List<string> fieldList,
            Guid batchId = default(Guid),
            bool isReflexiveRelationship = false,
            bool bypassPluginExecution = false)
        {
            _logEntry.ResetLastError();  // Reset Last Error
            if (DataverseService == null)
            {
                _logEntry.Log("Dataverse Service not initialized", TraceEventType.Error);
                return null;
            }

            if (primarySearchParameters == null && linkedSearchParameters == null)
                return null;

            if (primarySearchParameters == null)
                primarySearchParameters = new List<DataverseSearchFilter>(); // new Dictionary<string, string>();

            if (linkedSearchParameters == null)
                linkedSearchParameters = new List<DataverseSearchFilter>(); //new Dictionary<string, string>();



            #region Primary QueryFilter and Conditions

            FilterExpression primaryFilter = new FilterExpression();
            primaryFilter.Filters.AddRange(BuildFilterList(primarySearchParameters));

            #endregion

            #region Secondary QueryFilter and conditions

            FilterExpression linkedEntityFilter = new FilterExpression();
            linkedEntityFilter.Filters.AddRange(BuildFilterList(linkedSearchParameters));

            #endregion

            // Create Link Object for LinkedEnitty Name and add the filter info
            LinkEntity nestedLinkEntity = new LinkEntity();  // this is the Secondary
            nestedLinkEntity.LinkToEntityName = linkedEntityName; // what Entity are we linking too...
            nestedLinkEntity.LinkToAttributeName = linkedEntityLinkAttribName; // what Attrib are we linking To on that Entity
            nestedLinkEntity.LinkFromAttributeName = isReflexiveRelationship ? string.Format("{0}two", linkedEntityLinkAttribName) : linkedEntityLinkAttribName;  // what Attrib on the primary object are we linking too.
            nestedLinkEntity.LinkCriteria = linkedEntityFilter; // Filtered query

            //Create Link Object for Primary
            LinkEntity m2mLinkEntity = new LinkEntity();
            m2mLinkEntity.LinkToEntityName = m2MEntityName; // this is the M2M table
            m2mLinkEntity.LinkToAttributeName = isReflexiveRelationship ? string.Format("{0}one", returnEntityPrimaryId) : returnEntityPrimaryId; // this is the name of the other side.
            m2mLinkEntity.LinkFromAttributeName = returnEntityPrimaryId;
            m2mLinkEntity.LinkEntities.AddRange(new LinkEntity[] { nestedLinkEntity });


            // Return Cols
            // Create ColumnSet
            ColumnSet cols = null;
            if (fieldList != null && fieldList.Count > 0)
            {
                cols = new ColumnSet();
                cols.Columns.AddRange(fieldList.ToArray());
            }

            // Build Query
            QueryExpression query = new QueryExpression();
            query.NoLock = false;  // Added to remove the Locks.

            query.EntityName = returnEntityName; // Set to the requested entity Type
            if (cols != null)
                query.ColumnSet = cols;
            else
                query.ColumnSet = new ColumnSet(true);// new AllColumns();

            query.Criteria = primaryFilter;
            query.LinkEntities.AddRange(new LinkEntity[] { m2mLinkEntity });

            //Dictionary<string, Dictionary<string, object>> Results = new Dictionary<string, Dictionary<string, object>>();


            RetrieveMultipleRequest req = new RetrieveMultipleRequest();
            req.Query = query;
            RetrieveMultipleResponse retrieved;


            if (AddRequestToBatch(batchId, req, string.Format(CultureInfo.InvariantCulture, "Running Get Linked data, returning {0}", returnEntityName), string.Format(CultureInfo.InvariantCulture, "Request for Get Linked data, returning {0}", returnEntityName), bypassPluginExecution))
                return null;

            retrieved = (RetrieveMultipleResponse)Command_Execute(req, "Search On Linked Data", bypassPluginExecution);

            if (retrieved != null)
            {

                return CreateResultDataSet(retrieved.EntityCollection);
            }
            else
                return null;

        }

        /// <summary>
        /// Gets a List of variables from the account based on the list of field specified in the Fields List
        /// </summary>
        /// <param name="searchEntity">The entity to be searched.</param>
        /// <param name="entityId">ID of Entity to query </param>
        /// <param name="batchId">Optional: if set to a valid GUID, generated by the Create Batch Request Method, will assigned the request to the batch for later execution, on fail, runs the request immediately </param>
        /// <param name="bypassPluginExecution">Adds the bypass plugin behavior to this request. Note: this will only apply if the caller has the prvBypassPlugins permission to bypass plugins.  If its attempted without the permission the request will fault.</param>
        /// <param name="fieldList">Populated Array of Key value pairs with the Results of the Search</param>
        /// <returns></returns>
        public Dictionary<string, object> GetEntityDataById(string searchEntity, Guid entityId, List<string> fieldList, Guid batchId = default(Guid), bool bypassPluginExecution = false)
        {
            _logEntry.ResetLastError();  // Reset Last Error
            if (DataverseService == null || entityId == Guid.Empty)
            {
                return null;
            }

            EntityReference re = new EntityReference(searchEntity, entityId);
            if (re == null)
                return null;

            RetrieveRequest req = new RetrieveRequest();

            // Create ColumnSet
            ColumnSet cols = null;
            if (fieldList != null)
            {
                cols = new ColumnSet();
                cols.Columns.AddRange(fieldList.ToArray());
            }

            if (cols != null)
                req.ColumnSet = cols;
            else
                req.ColumnSet = new ColumnSet(true);// new AllColumns();

            req.Target = re; //getEnt;

            if (AddRequestToBatch(batchId, req, string.Format(CultureInfo.InvariantCulture, "Trying to Read a Record. Entity = {0} , ID = {1}", searchEntity, entityId.ToString()),
                string.Format(CultureInfo.InvariantCulture, "Request to Read a Record. Entity = {0} , ID = {1} queued", searchEntity, entityId.ToString()), bypassPluginExecution))
                return null;

            RetrieveResponse resp = (RetrieveResponse)Command_Execute(req, string.Format(CultureInfo.InvariantCulture, "Trying to Read a Record. Entity = {0} , ID = {1}", searchEntity, entityId.ToString()), bypassPluginExecution);
            if (resp == null)
                return null;

            if (resp.Entity == null)
                return null;

            try
            {
                // Not really doing an update here... just turning it into something I can walk.
                Dictionary<string, object> resultSet = new Dictionary<string, object>();
                AddDataToResultSet(ref resultSet, resp.Entity);
                return resultSet;
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// This creates a annotation [note] entry, related to a an existing entity
        /// <para>Required Properties in the fieldList</para>
        /// <para>notetext (string) = Text of the note,  </para>
        /// <para>subject (string) = this is the title of the note</para>
        /// </summary>
        /// <param name="targetEntityTypeName">Target Entity TypeID</param>
        /// <param name="targetEntityId">Target Entity ID</param>
        /// <param name="fieldList">Fields to populate</param>
        /// <param name="batchId">Optional: if set to a valid GUID, generated by the Create Batch Request Method, will assigned the request to the batch for later execution, on fail, runs the request immediately </param>
		/// <param name="bypassPluginExecution">Adds the bypass plugin behavior to this request. Note: this will only apply if the caller has the prvBypassPlugins permission to bypass plugins.  If its attempted without the permission the request will fault.</param>
        /// <returns></returns>
        public Guid CreateAnnotation(string targetEntityTypeName, Guid targetEntityId, Dictionary<string, DataverseDataTypeWrapper> fieldList, Guid batchId = default(Guid), bool bypassPluginExecution = false)
        {
            _logEntry.ResetLastError();  // Reset Last Error

            if (string.IsNullOrEmpty(targetEntityTypeName))
                return Guid.Empty;

            if (targetEntityId == Guid.Empty)
                return Guid.Empty;

            if (fieldList == null)
                fieldList = new Dictionary<string, DataverseDataTypeWrapper>();

            fieldList.Add("objecttypecode", new DataverseDataTypeWrapper(targetEntityTypeName, DataverseFieldType.String));
            fieldList.Add("objectid", new DataverseDataTypeWrapper(targetEntityId, DataverseFieldType.Lookup, targetEntityTypeName));
            fieldList.Add("ownerid", new DataverseDataTypeWrapper(SystemUser.UserId, DataverseFieldType.Lookup, "systemuser"));

            return CreateNewRecord("annotation", fieldList, batchId: batchId, bypassPluginExecution: bypassPluginExecution);

        }

        /// <summary>
        /// Creates a new activity against the target entity type
        /// </summary>
        /// <param name="activityEntityTypeName">Type of Activity you would like to create</param>
        /// <param name="regardingEntityTypeName">Entity type of the Entity you want to associate with.</param>
        /// <param name="subject">Subject Line of the Activity</param>
        /// <param name="description">Description Text of the Activity </param>
        /// <param name="regardingId">ID of the Entity to associate the Activity too</param>
        /// <param name="creatingUserId">User ID that Created the Activity *Calling user must have necessary permissions to assign to another user</param>
        /// <param name="fieldList">Additional fields to add as part of the activity creation</param>
        /// <param name="batchId">Optional: if set to a valid GUID, generated by the Create Batch Request Method, will assigned the request to the batch for later execution, on fail, runs the request immediately </param>
		/// <param name="bypassPluginExecution">Adds the bypass plugin behavior to this request. Note: this will only apply if the caller has the prvBypassPlugins permission to bypass plugins.  If its attempted without the permission the request will fault.</param>
        /// <returns>Guid of Activity ID or Guid.empty</returns>
        public Guid CreateNewActivityEntry(string activityEntityTypeName,
            string regardingEntityTypeName,
            Guid regardingId,
            string subject,
            string description,
            string creatingUserId,
            Dictionary<string, DataverseDataTypeWrapper> fieldList = null,
            Guid batchId = default(Guid),
            bool bypassPluginExecution = false
            )
        {

            #region PreChecks
            _logEntry.ResetLastError();  // Reset Last Error
            if (DataverseService == null)
            {
                _logEntry.Log("Dataverse Service not initialized", TraceEventType.Error);
                return Guid.Empty;
            }
            if (string.IsNullOrWhiteSpace(activityEntityTypeName))
            {
                _logEntry.Log("You must specify the activity type name to create", TraceEventType.Error);
                return Guid.Empty;
            }
            if (string.IsNullOrWhiteSpace(subject))
            {
                _logEntry.Log(string.Format(CultureInfo.InvariantCulture, "A Subject is required to create an activity of type {0}", regardingEntityTypeName), TraceEventType.Error);
                return Guid.Empty;
            }
            #endregion

            Guid activityId = Guid.Empty;
            try
            {
                // reuse the passed in field list if its available, else punt and create a new one.
                if (fieldList == null)
                    fieldList = new Dictionary<string, DataverseDataTypeWrapper>();

                fieldList.Add("subject", new DataverseDataTypeWrapper(subject, DataverseFieldType.String));
                if (regardingId != Guid.Empty)
                    fieldList.Add("regardingobjectid", new DataverseDataTypeWrapper(regardingId, DataverseFieldType.Lookup, regardingEntityTypeName));
                if (!string.IsNullOrWhiteSpace(description))
                    fieldList.Add("description", new DataverseDataTypeWrapper(description, DataverseFieldType.String));

                // Create the base record.
                activityId = CreateNewRecord(activityEntityTypeName, fieldList, bypassPluginExecution: bypassPluginExecution);

                // if I have a user ID,  try to assign it to that user.
                if (!string.IsNullOrWhiteSpace(creatingUserId))
                {
                    Guid userId = GetLookupValueForEntity("systemuser", creatingUserId);

                    if (userId != Guid.Empty)
                    {
                        EntityReference newAction = new EntityReference(activityEntityTypeName, activityId);
                        EntityReference principal = new EntityReference("systemuser", userId);

                        AssignRequest arRequest = new AssignRequest();
                        arRequest.Assignee = principal;
                        arRequest.Target = newAction;
                        if (AddRequestToBatch(batchId, arRequest, string.Format(CultureInfo.InvariantCulture, "Trying to Assign a Record. Entity = {0} , ID = {1}", newAction.LogicalName, principal.LogicalName),
                                                string.Format(CultureInfo.InvariantCulture, "Request to Assign a Record. Entity = {0} , ID = {1} Queued", newAction.LogicalName, principal.LogicalName), bypassPluginExecution))
                            return Guid.Empty;
                        Command_Execute(arRequest, "Assign Activity", bypassPluginExecution);
                    }
                }
            }
            catch (Exception exp)
            {
                this._logEntry.Log(exp);
            }
            return activityId;
        }

        /// <summary>
        /// Closes the Activity type specified.
        /// The Activity Entity type supports fax , letter , and phonecall
        /// <para>*Note: This will default to using English names for Status. if you need to use Non-English, you should populate the names for completed for the status and state.</para>
        /// </summary>
        /// <param name="activityEntityType">Type of Activity you would like to close.. Supports fax, letter, phonecall</param>
        /// <param name="activityId">ID of the Activity you want to close</param>
        /// <param name="stateCode">State Code configured on the activity</param>
        /// <param name="statusCode">Status code on the activity </param>
        /// <param name="batchId">Optional: if set to a valid GUID, generated by the Create Batch Request Method, will assigned the request to the batch for later execution, on fail, runs the request immediately </param>
        /// <param name="bypassPluginExecution">Adds the bypass plugin behavior to this request. Note: this will only apply if the caller has the prvBypassPlugins permission to bypass plugins.  If its attempted without the permission the request will fault.</param>
        /// <returns>true if success false if not.</returns>
        public bool CloseActivity(string activityEntityType, Guid activityId, string stateCode = "completed", string statusCode = "completed", Guid batchId = default(Guid), bool bypassPluginExecution = false)
        {
            return UpdateStateStatusForEntity(activityEntityType, activityId, stateCode, statusCode, batchId: batchId, bypassPluginExecution: bypassPluginExecution);
        }

        /// <summary>
        /// Updates the state of an activity
        /// </summary>
        /// <param name="entName"></param>
        /// <param name="entId"></param>
        /// <param name="newState"></param>
        /// <param name="newStatus"></param>
        /// <param name="newStateid">ID for the new State ( Skips metadata lookup )</param>
        /// <param name="newStatusid">ID for new Status ( Skips Metadata Lookup)</param>
        /// <param name="batchId">Optional: if set to a valid GUID, generated by the Create Batch Request Method, will assigned the request to the batch for later execution, on fail, runs the request immediately </param>
		/// <param name="bypassPluginExecution">Adds the bypass plugin behavior to this request. Note: this will only apply if the caller has the prvBypassPlugins permission to bypass plugins.  If its attempted without the permission the request will fault.</param>
        /// <returns></returns>
        private bool UpdateStateStatusForEntity(string entName, Guid entId, string newState, string newStatus, int newStateid = -1, int newStatusid = -1, Guid batchId = default(Guid), bool bypassPluginExecution = false)
        {
            _logEntry.ResetLastError();  // Reset Last Error
            SetStateRequest req = new SetStateRequest();
            req.EntityMoniker = new EntityReference(entName, entId);

            int istatuscode = -1;
            int istatecode = -1;

            // Modified to prefer IntID's first... this is in support of multi languages.

            if (newStatusid != -1)
                istatuscode = newStatusid;
            else
            {
                if (!String.IsNullOrWhiteSpace(newStatus))
                {
                    PickListMetaElement picItem = GetPickListElementFromMetadataEntity(entName, "statuscode");
                    if (picItem != null)
                    {
                        var statusOption = picItem.Items.FirstOrDefault(s => s.DisplayLabel.Equals(newStatus, StringComparison.CurrentCultureIgnoreCase));
                        if (statusOption != null)
                            istatuscode = statusOption.PickListItemId;
                    }
                }
            }

            if (newStateid != -1)
                istatecode = newStateid;
            else
            {
                if (!string.IsNullOrWhiteSpace(newState))
                {
                    PickListMetaElement picItem2 = GetPickListElementFromMetadataEntity(entName, "statecode");
                    var stateOption = picItem2.Items.FirstOrDefault(s => s.DisplayLabel.Equals(newState, StringComparison.CurrentCultureIgnoreCase));
                    if (stateOption != null)
                        istatecode = stateOption.PickListItemId;
                }
            }

            if (istatecode == -1 && istatuscode == -1)
            {
                _logEntry.Log(string.Format(CultureInfo.InvariantCulture, "Cannot set status on {0}, State and Status codes not found, State = {1}, Status = {2}", entName, newState, newStatus), TraceEventType.Information);
                return false;
            }

            if (istatecode != -1)
                req.State = new OptionSetValue(istatecode);// "Completed";
            if (istatuscode != -1)
                req.Status = new OptionSetValue(istatuscode); //Status = 2;


            if (AddRequestToBatch(batchId, req, string.Format(CultureInfo.InvariantCulture, "Setting Activity State in Dataverse... {0}", entName), string.Format(CultureInfo.InvariantCulture, "Request for SetState on {0} queued", entName), bypassPluginExecution))
                return false;

            SetStateResponse resp = (SetStateResponse)Command_Execute(req, string.Format(CultureInfo.InvariantCulture, "Setting Activity State in Dataverse... {0}", entName), bypassPluginExecution);
            if (resp != null)
                return true;
            else
                return false;
        }

        /// <summary>
        /// Returns all Activities Related to a given Entity ID.
        /// Only Account, Contact and Opportunity entities are supported.
        /// </summary>
        /// <param name="searchEntity">Type of Entity to search against</param>
        /// <param name="entityId">ID of the entity to search against. </param>
        /// <param name="fieldList">List of Field to return for the entity , null indicates all fields.</param>
        /// <param name="searchOperator">Search Operator to use</param>
        /// <param name="searchParameters">Filters responses based on search prams.</param>
        /// <param name="sortParameters">Sort order</param>
        /// <param name="pageCount">Number of Pages</param>
        /// <param name="pageNumber">Current Page number</param>
        /// <param name="pageCookie">inbound place holder cookie</param>
        /// <param name="outPageCookie">outbound place holder cookie</param>
        /// <param name="isMoreRecords">is there more records or not</param>
        /// <param name="batchId">Optional: if set to a valid GUID, generated by the Create Batch Request Method, will assigned the request to the batch for later execution, on fail, runs the request immediately </param>
        /// <returns>Array of Activities</returns>
        public Dictionary<string, Dictionary<string, object>> GetActivitiesBy(
            string searchEntity,
            Guid entityId,
            List<string> fieldList,
            LogicalSearchOperator searchOperator,
            Dictionary<string, string> searchParameters,
            Dictionary<string, LogicalSortOrder> sortParameters,
            int pageCount,
            int pageNumber,
            string pageCookie,
            out string outPageCookie,
            out bool isMoreRecords,
            Guid batchId = default(Guid)
            )
        {
            List<DataverseSearchFilter> searchList = new List<DataverseSearchFilter>();
            BuildSearchFilterListFromSearchTerms(searchParameters, searchList);

            return GetEntityDataByRollup(searchEntity, entityId, "activitypointer", fieldList, searchOperator, searchList, sortParameters, pageCount, pageNumber, pageCookie, out outPageCookie, out isMoreRecords, batchId);
        }

        /// <summary>
        /// Returns all Activities Related to a given Entity ID.
        /// Only Account, Contact and Opportunity entities are supported.
        /// </summary>
        /// <param name="searchEntity">Type of Entity to search against</param>
        /// <param name="entityId">ID of the entity to search against. </param>
        /// <param name="fieldList">List of Field to return for the entity , null indicates all fields.</param>
        /// <param name="searchOperator">Search Operator to use</param>
        /// <param name="searchParameters">Filters responses based on search prams.</param>
        /// <param name="sortParameters">Sort order</param>
        /// <param name="pageCount">Number of Pages</param>
        /// <param name="pageNumber">Current Page number</param>
        /// <param name="pageCookie">inbound place holder cookie</param>
        /// <param name="outPageCookie">outbound place holder cookie</param>
        /// <param name="isMoreRecords">is there more records or not</param>
        /// <param name="batchId">Optional: if set to a valid GUID, generated by the Create Batch Request Method, will assigned the request to the batch for later execution, on fail, runs the request immediately </param>
        /// <returns>Array of Activities</returns>
        public Dictionary<string, Dictionary<string, object>> GetActivitiesBy(
           string searchEntity,
           Guid entityId,
           List<string> fieldList,
           LogicalSearchOperator searchOperator,
           List<DataverseSearchFilter> searchParameters,
           Dictionary<string, LogicalSortOrder> sortParameters,
           int pageCount,
           int pageNumber,
           string pageCookie,
           out string outPageCookie,
           out bool isMoreRecords,
            Guid batchId = default(Guid)
           )
        {
            return GetEntityDataByRollup(searchEntity, entityId, "activitypointer", fieldList, searchOperator, searchParameters, sortParameters, pageCount, pageNumber, pageCookie, out outPageCookie, out isMoreRecords, batchId: batchId);
        }

        /// <summary>
        /// Returns all Activities Related to a given Entity ID.
        /// Only Account, Contact and Opportunity entities are supported.
        /// </summary>
        /// <param name="searchEntity">Type of Entity to search against</param>
        /// <param name="entityId">ID of the entity to search against. </param>
        /// <param name="fieldList">List of Field to return for the entity , null indicates all fields.</param>
        /// <param name="searchOperator"></param>
        /// <param name="searchParameters">Filters responses based on search prams.</param>
        /// <returns>Array of Activities</returns>
        /// <param name="sortParameters">Sort Order</param>
        /// <param name="batchId">Optional: if set to a valid GUID, generated by the Create Batch Request Method, will assigned the request to the batch for later execution, on fail, runs the request immediately </param>
        /// <param name="rollupfromEntity">Entity to Rollup from</param>
        public Dictionary<string, Dictionary<string, object>> GetEntityDataByRollup(
            string searchEntity,
            Guid entityId,
            string rollupfromEntity,
            List<string> fieldList,
            LogicalSearchOperator searchOperator,
            Dictionary<string, string> searchParameters,
            Dictionary<string, LogicalSortOrder> sortParameters,
            Guid batchId = default(Guid))
        {

            List<DataverseSearchFilter> searchList = new List<DataverseSearchFilter>();
            BuildSearchFilterListFromSearchTerms(searchParameters, searchList);


            string pgCookie = string.Empty;
            bool moreRec = false;

            return GetEntityDataByRollup(
                searchEntity, entityId, rollupfromEntity, fieldList,
                searchOperator, searchList, sortParameters, -1, -1, string.Empty,
                out pgCookie, out moreRec, batchId: batchId);
        }


        /// <summary>
        /// Returns all Activities Related to a given Entity ID.
        /// Only Account, Contact and Opportunity entities are supported.
        /// </summary>
        /// <param name="searchEntity">Type of Entity to search against</param>
        /// <param name="entityId">ID of the entity to search against. </param>
        /// <param name="fieldList">List of Field to return for the entity , null indicates all fields.</param>
        /// <param name="rollupfromEntity">Entity to Rollup from</param>
        /// <param name="searchOperator">Search Operator to user</param>
        /// <param name="searchParameters">Dataverse Filter list to apply</param>
        /// <param name="sortParameters">Sort by</param>
        /// <param name="pageCount">Number of Pages</param>
        /// <param name="pageNumber">Current Page number</param>
        /// <param name="pageCookie">inbound place holder cookie</param>
        /// <param name="outPageCookie">outbound place holder cookie</param>
        /// <param name="isMoreRecords">is there more records or not</param>
        /// <param name="batchId">Optional: if set to a valid GUID, generated by the Create Batch Request Method, will assigned the request to the batch for later execution, on fail, runs the request immediately </param>
		/// <param name="bypassPluginExecution">Adds the bypass plugin behavior to this request. Note: this will only apply if the caller has the prvBypassPlugins permission to bypass plugins.  If its attempted without the permission the request will fault.</param>
        /// <returns></returns>
        public Dictionary<string, Dictionary<string, object>> GetEntityDataByRollup(
            string searchEntity,
            Guid entityId,
            string rollupfromEntity,
            List<string> fieldList,
            LogicalSearchOperator searchOperator,
            List<DataverseSearchFilter> searchParameters,
            Dictionary<string, LogicalSortOrder> sortParameters,
            int pageCount,
            int pageNumber,
            string pageCookie,
            out string outPageCookie,
            out bool isMoreRecords,
            Guid batchId = default(Guid),
            bool bypassPluginExecution = false
            )
        {
            _logEntry.ResetLastError();  // Reset Last Error
            outPageCookie = string.Empty;
            isMoreRecords = false;

            if (DataverseService == null)
            {
                _logEntry.Log("Dataverse Service not initialized", TraceEventType.Error);
                return null;
            }

            QueryExpression query = BuildQueryFilter(rollupfromEntity, searchParameters, fieldList, searchOperator);

            if (pageCount != -1)
            {
                PagingInfo pgInfo = new PagingInfo();
                pgInfo.Count = pageCount;
                pgInfo.PageNumber = pageNumber;
                pgInfo.PagingCookie = pageCookie;
                query.PageInfo = pgInfo;

            }

            if (sortParameters != null)
                if (sortParameters.Count > 0)
                {
                    List<OrderExpression> qExpressList = new List<OrderExpression>();
                    foreach (KeyValuePair<string, LogicalSortOrder> itm in sortParameters)
                    {
                        OrderExpression ordBy = new OrderExpression();
                        ordBy.AttributeName = itm.Key;
                        if (itm.Value == LogicalSortOrder.Ascending)
                            ordBy.OrderType = OrderType.Ascending;
                        else
                            ordBy.OrderType = OrderType.Descending;

                        qExpressList.Add(ordBy);
                    }

                    query.Orders.AddRange(qExpressList.ToArray());
                }

            if (query.Orders == null)
            {
                OrderExpression ordBy = new OrderExpression();
                ordBy.AttributeName = "createdon";
                ordBy.OrderType = OrderType.Descending;
                query.Orders.AddRange(new OrderExpression[] { ordBy });
            }

            EntityReference ro = new EntityReference(searchEntity, entityId);
            if (ro == null)
                return null;

            RollupRequest req = new RollupRequest();
            req.Query = query;
            req.RollupType = RollupType.Related;
            req.Target = ro;

            if (AddRequestToBatch(batchId, req, string.Format(CultureInfo.InvariantCulture, "Running Get entitydatabyrollup... {0}", searchEntity), string.Format(CultureInfo.InvariantCulture, "Request for GetEntityDataByRollup on {0} queued", searchEntity), bypassPluginExecution))
                return null;

            RollupResponse resp = (RollupResponse)Command_Execute(req, string.Format(CultureInfo.InvariantCulture, "Locating {0} by ID in Dataverse GetActivitesBy", searchEntity), bypassPluginExecution);
            if (resp == null)
                return null;

            if ((resp.EntityCollection != null) ||
                (resp.EntityCollection.Entities != null) ||
                (resp.EntityCollection.Entities.Count > 0)
                )
            {
                isMoreRecords = resp.EntityCollection.MoreRecords;
                outPageCookie = resp.EntityCollection.PagingCookie;
                return CreateResultDataSet(resp.EntityCollection);
            }
            else
                return null;
        }

        /// <summary>
        /// This function gets data from a Dictionary object, where "string" identifies the field name, and Object contains the data,
        /// this method then attempts to cast the result to the Type requested, if it cannot be cast an empty object is returned.
        /// </summary>
        /// <param name="results">Results from the query</param>
        /// <param name="key">key name you want</param>
        /// <typeparam name="T">Type if object to return</typeparam>
        /// <returns>object</returns>
        public T GetDataByKeyFromResultsSet<T>(Dictionary<string, object> results, string key)
        {
            try
            {
                if (results != null)
                {
                    if (results.ContainsKey(key))
                    {

                        if ((typeof(T) == typeof(int)) || (typeof(T) == typeof(string)))
                        {
                            try
                            {
                                string s = (string)results[key];
                                if (s.Contains("PICKLIST:"))
                                {
                                    try
                                    {
                                        //parse the PickList bit for what is asked for
                                        Collection<string> eleList = new Collection<string>(s.Split(':'));
                                        if (typeof(T) == typeof(int))
                                        {
                                            return (T)(object)Convert.ToInt32(eleList[1], CultureInfo.InvariantCulture);
                                        }
                                        else
                                            return (T)(object)eleList[3];
                                    }
                                    catch
                                    {
                                        // try to do the basic return
                                        return (T)results[key];
                                    }
                                }
                            }
                            catch
                            {
                                if (results[key] is T)
                                    // try to do the basic return
                                    return (T)results[key];
                            }
                        }

                        // MSB :: Added this method in light of new features in CDS 2011..
                        if (results[key] is T)
                            // try to do the basic return
                            return (T)results[key];
                        else
                        {
                            if (results != null && results.ContainsKey(key))  // Specific To CDS 2011..
                            {
                                if (results.ContainsKey(key + "_Property"))
                                {
                                    // Check for the property entry - CDS 2011 Specific
                                    KeyValuePair<string, object> property = (KeyValuePair<string, object>)results[key + "_Property"];
                                    // try to return the casted value.
                                    if (property.Value is T)
                                        return (T)property.Value;
                                }
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _logEntry.Log("Error In GetDataByKeyFromResultsSet (Non-Fatal)", TraceEventType.Verbose, ex);
            }
            return default(T);

        }

        /// <summary>
        /// Executes a named workflow on an object.
        /// </summary>
        /// <param name="workflowName">name of the workflow to run</param>
        /// <param name="id">ID to exec against</param>
        /// <param name="batchId">Optional: if set to a valid GUID, generated by the Create Batch Request Method, will assigned the request to the batch for later execution, on fail, runs the request immediately </param>
        /// <param name="bypassPluginExecution">Adds the bypass plugin behavior to this request. Note: this will only apply if the caller has the prvBypassPlugins permission to bypass plugins.  If its attempted without the permission the request will fault.</param>
        /// <returns>Async Op ID of the WF or Guid.Empty</returns>
        public Guid ExecuteWorkflowOnEntity(string workflowName, Guid id, Guid batchId = default(Guid), bool bypassPluginExecution = false)
        {
            _logEntry.ResetLastError();  // Reset Last Error
            if (DataverseService == null)
            {
                _logEntry.Log("Dataverse Service not initialized", TraceEventType.Error);
                return Guid.Empty;
            }

            if (id == Guid.Empty)
            {
                this._logEntry.Log(string.Format(CultureInfo.InvariantCulture, "************ Exception Executing workflow ({0}) on ID {1} in Dataverse  : " + "Target Entity Was not provided", workflowName, id), TraceEventType.Error);
                return Guid.Empty;
            }

            if (string.IsNullOrEmpty(workflowName))
            {
                this._logEntry.Log(string.Format(CultureInfo.InvariantCulture, "************ Exception Executing workflow ({0}) on ID {1} in Dataverse  : " + "Workflow Name Was not provided", workflowName, id), TraceEventType.Error);
                return Guid.Empty;
            }

            Dictionary<string, string> SearchParm = new Dictionary<string, string>();
            SearchParm.Add("name", workflowName);

            Dictionary<string, Dictionary<string, object>> rslts =
                    GetEntityDataBySearchParams("workflow", SearchParm, LogicalSearchOperator.None, null, bypassPluginExecution: bypassPluginExecution);

            if (rslts != null)
            {
                if (rslts.Count > 0)
                {
                    foreach (Dictionary<string, object> row in rslts.Values)
                    {
                        if (GetDataByKeyFromResultsSet<Guid>(row, "parentworkflowid") != Guid.Empty)
                            continue;
                        Guid guWorkflowID = GetDataByKeyFromResultsSet<Guid>(row, "workflowid");
                        if (guWorkflowID != Guid.Empty)
                        {
                            // Ok try to exec the workflow request
                            ExecuteWorkflowRequest wfRequest = new ExecuteWorkflowRequest();
                            wfRequest.EntityId = id;
                            wfRequest.WorkflowId = guWorkflowID;

                            if (AddRequestToBatch(batchId, wfRequest, string.Format(CultureInfo.InvariantCulture, "Executing workflow ({0}) on ID {1}", workflowName, id),
                                string.Format(CultureInfo.InvariantCulture, "Request to Execute workflow ({0}) on ID {1} Queued", workflowName, id), bypassPluginExecution))
                                return Guid.Empty;

                            ExecuteWorkflowResponse wfResponse = (ExecuteWorkflowResponse)Command_Execute(wfRequest, string.Format(CultureInfo.InvariantCulture, "Executing workflow ({0}) on ID {1}", workflowName, id), bypassPluginExecution);
                            if (wfResponse != null)
                                return wfResponse.Id;
                            else
                                return Guid.Empty;
                        }
                        else
                        {
                            this._logEntry.Log(string.Format(CultureInfo.InvariantCulture, "************ Exception Executing workflow ({0}) on ID {1} in Dataverse  : " + "Unable to Find Workflow by ID", workflowName, id), TraceEventType.Error);
                        }
                    }
                }
                else
                {
                    this._logEntry.Log(string.Format(CultureInfo.InvariantCulture, "************ Exception Executing workflow ({0}) on ID {1} in Dataverse  : " + "Unable to Find Workflow by Name", workflowName, id), TraceEventType.Error);
                }
            }
            this._logEntry.Log(string.Format(CultureInfo.InvariantCulture, "************ Exception Executing workflow ({0}) on ID {1} in Dataverse  : " + "Unable to Find Workflow by Name Search", workflowName, id), TraceEventType.Error);
            return Guid.Empty;
        }

        #region Solution and Data Import Methods
        /// <summary>
        /// Starts an Import request for CDS.
        /// <para>Supports a single file per Import request.</para>
        /// </summary>
        /// <param name="delayUntil">Delays the import jobs till specified time - Use DateTime.MinValue to Run immediately </param>
        /// <param name="importRequest">Import Data Request</param>
        /// <returns>Guid of the Import Request, or Guid.Empty.  If Guid.Empty then request failed.</returns>
        public Guid SubmitImportRequest(ImportRequest importRequest, DateTime delayUntil)
        {
            _logEntry.ResetLastError();  // Reset Last Error
            if (DataverseService == null)
            {
                _logEntry.Log("Dataverse Service not initialized", TraceEventType.Error);
                return Guid.Empty;
            }

            // Error checking
            if (importRequest == null)
            {
                this._logEntry.Log("************ Exception on SubmitImportRequest, importRequest is required", TraceEventType.Error);
                return Guid.Empty;
            }

            if (importRequest.Files == null || (importRequest.Files != null && importRequest.Files.Count == 0))
            {
                this._logEntry.Log("************ Exception on SubmitImportRequest, importRequest.Files is required and must have at least one file listed to import.", TraceEventType.Error);
                return Guid.Empty;
            }

            // Done error checking
            if (string.IsNullOrWhiteSpace(importRequest.ImportName))
                importRequest.ImportName = "User Requested Import";


            Guid ImportId = Guid.Empty;
            Guid ImportMap = Guid.Empty;
            Guid ImportFile = Guid.Empty;
            List<Guid> ImportFileIds = new List<Guid>();

            // Create Import Object
            // The Import Object is the anchor for the Import job in Dataverse.
            Dictionary<string, DataverseDataTypeWrapper> importFields = new Dictionary<string, DataverseDataTypeWrapper>();
            importFields.Add("name", new DataverseDataTypeWrapper(importRequest.ImportName, DataverseFieldType.String));
            importFields.Add("modecode", new DataverseDataTypeWrapper(importRequest.Mode, DataverseFieldType.Picklist));  // 0 == Create , 1 = Update..
            ImportId = CreateNewRecord("import", importFields);

            if (ImportId == Guid.Empty)
                // Error here;
                return Guid.Empty;

            #region Determin Map to Use
            //Guid guDataMapId = Guid.Empty;
            if (string.IsNullOrWhiteSpace(importRequest.DataMapFileName) && importRequest.DataMapFileId == Guid.Empty)
                // User Requesting to use System Mapping here.
                importRequest.UseSystemMap = true;  // Override whatever setting they had here.
            else
            {
                // User providing information on a map to use.
                // Query to get the map from the system
                List<string> fldList = new List<string>();
                fldList.Add("name");
                fldList.Add("source");
                fldList.Add("importmapid");
                Dictionary<string, object> MapData = null;
                if (importRequest.DataMapFileId != Guid.Empty)
                {
                    // Have the id here... get the map based on the ID.
                    MapData = GetEntityDataById("importmap", importRequest.DataMapFileId, fldList);
                }
                else
                {
                    // Search by name... exact match required.
                    List<DataverseSearchFilter> filters = new List<DataverseSearchFilter>();
                    DataverseSearchFilter filter = new DataverseSearchFilter();
                    filter.FilterOperator = Microsoft.Xrm.Sdk.Query.LogicalOperator.And;
                    filter.SearchConditions.Add(new DataverseFilterConditionItem() { FieldName = "name", FieldOperator = Microsoft.Xrm.Sdk.Query.ConditionOperator.Equal, FieldValue = importRequest.DataMapFileName });
                    filters.Add(filter);

                    // Search by Name..
                    Dictionary<string, Dictionary<string, object>> rslts = GetEntityDataBySearchParams("importmap", filters, LogicalSearchOperator.None, fldList);
                    if (rslts != null && rslts.Count > 0)
                    {
                        // if there is more then one record returned.. throw an error ( should not happen )
                        if (rslts.Count > 1)
                        {
                            // log error here.
                            this._logEntry.Log(string.Format(CultureInfo.InvariantCulture, "************ Exception on SubmitImportRequest, More then one mapping file was found for {0}, Specifiy the ID of the Mapfile to use", importRequest.DataMapFileName), TraceEventType.Error);
                            return Guid.Empty;
                        }
                        else
                        {
                            // Get my single record and move on..
                            MapData = rslts.First().Value;
                            // Update the Guid for the mapID.
                            importRequest.DataMapFileId = GetDataByKeyFromResultsSet<Guid>(MapData, "importmapid");
                        }
                    }
                }
                ImportMap = importRequest.DataMapFileId;


                // Now get the entity import mapping info,  We need this to get the source entity name from the map XML file.
                if (ImportMap != Guid.Empty)
                {
                    // Iterate over the import files and update the entity names.

                    fldList.Clear();
                    fldList.Add("sourceentityname");
                    List<DataverseSearchFilter> filters = new List<DataverseSearchFilter>();
                    DataverseSearchFilter filter = new DataverseSearchFilter();
                    filter.FilterOperator = Microsoft.Xrm.Sdk.Query.LogicalOperator.And;
                    filter.SearchConditions.Add(new DataverseFilterConditionItem() { FieldName = "importmapid", FieldOperator = Microsoft.Xrm.Sdk.Query.ConditionOperator.Equal, FieldValue = ImportMap });
                    filters.Add(filter);
                    Dictionary<string, Dictionary<string, object>> al = GetEntityDataBySearchParams("importentitymapping", filters, LogicalSearchOperator.None, null);
                    if (al != null && al.Count > 0)
                    {
                        foreach (var row in al.Values)
                        {
                            importRequest.Files.ForEach(fi =>
                            {
                                if (fi.TargetEntityName.Equals(GetDataByKeyFromResultsSet<string>(row, "targetentityname"), StringComparison.OrdinalIgnoreCase))
                                    fi.SourceEntityName = GetDataByKeyFromResultsSet<string>(row, "sourceentityname");
                            });
                        }
                    }
                    else
                    {
                        if (ImportId != Guid.Empty)
                            DeleteEntity("import", ImportId);

                        // Failed to find mapping entry error , Map not imported properly
                        this._logEntry.Log("************ Exception on SubmitImportRequest, Cannot find mapping file information found MapFile Provided.", TraceEventType.Error);
                        return Guid.Empty;
                    }
                }
                else
                {
                    if (ImportId != Guid.Empty)
                        DeleteEntity("import", ImportId);

                    // Failed to find mapping entry error , Map not imported properly
                    this._logEntry.Log("************ Exception on SubmitImportRequest, Cannot find ImportMappingsFile Provided.", TraceEventType.Error);
                    return Guid.Empty;
                }

            }
            #endregion

            #region Create Import File for each File in array
            bool continueImport = true;
            Dictionary<string, DataverseDataTypeWrapper> importFileFields = new Dictionary<string, DataverseDataTypeWrapper>();
            foreach (var FileItem in importRequest.Files)
            {
                // Create the Import File Object - Loop though file objects and create as many as necessary.
                // This is the row that has the data being imported as well as the status of the import file.
                importFileFields.Add("name", new DataverseDataTypeWrapper(FileItem.FileName, DataverseFieldType.String));
                importFileFields.Add("source", new DataverseDataTypeWrapper(FileItem.FileName, DataverseFieldType.String));
                importFileFields.Add("filetypecode", new DataverseDataTypeWrapper(FileItem.FileType, DataverseFieldType.Picklist)); // File Type is either : 0 = CSV , 1 = XML , 2 = Attachment
                importFileFields.Add("content", new DataverseDataTypeWrapper(FileItem.FileContentToImport, DataverseFieldType.String));
                importFileFields.Add("enableduplicatedetection", new DataverseDataTypeWrapper(FileItem.EnableDuplicateDetection, DataverseFieldType.Boolean));
                importFileFields.Add("usesystemmap", new DataverseDataTypeWrapper(importRequest.UseSystemMap, DataverseFieldType.Boolean)); // Use the System Map to get somthing done.
                importFileFields.Add("sourceentityname", new DataverseDataTypeWrapper(FileItem.SourceEntityName, DataverseFieldType.String));
                importFileFields.Add("targetentityname", new DataverseDataTypeWrapper(FileItem.TargetEntityName, DataverseFieldType.String));
                importFileFields.Add("datadelimitercode", new DataverseDataTypeWrapper(FileItem.DataDelimiter, DataverseFieldType.Picklist));   // 1 = " | 2 =   | 3 = '
                importFileFields.Add("fielddelimitercode", new DataverseDataTypeWrapper(FileItem.FieldDelimiter, DataverseFieldType.Picklist));  // 1 = : | 2 = , | 3 = '
                importFileFields.Add("isfirstrowheader", new DataverseDataTypeWrapper(FileItem.IsFirstRowHeader, DataverseFieldType.Boolean));
                importFileFields.Add("processcode", new DataverseDataTypeWrapper(1, DataverseFieldType.Picklist));
                if (FileItem.IsRecordOwnerATeam)
                    importFileFields.Add("recordsownerid", new DataverseDataTypeWrapper(FileItem.RecordOwner, DataverseFieldType.Lookup, "team"));
                else
                    importFileFields.Add("recordsownerid", new DataverseDataTypeWrapper(FileItem.RecordOwner, DataverseFieldType.Lookup, "systemuser"));

                importFileFields.Add("importid", new DataverseDataTypeWrapper(ImportId, DataverseFieldType.Lookup, "import"));
                if (ImportMap != Guid.Empty)
                    importFileFields.Add("importmapid", new DataverseDataTypeWrapper(ImportMap, DataverseFieldType.Lookup, "importmap"));

                ImportFile = CreateNewRecord("importfile", importFileFields);
                if (ImportFile == Guid.Empty)
                {
                    continueImport = false;
                    break;
                }
                ImportFileIds.Add(ImportFile);
                importFileFields.Clear();
            }

            #endregion


            // if We have an Import File... Activate Import.
            if (continueImport)
            {
                ParseImportResponse parseResp = (ParseImportResponse)Command_Execute(new ParseImportRequest() { ImportId = ImportId },
                    string.Format(CultureInfo.InvariantCulture, "************ Exception Executing ParseImportRequest for ImportJob ({0})", importRequest.ImportName));
                if (parseResp.AsyncOperationId != Guid.Empty)
                {
                    if (delayUntil != DateTime.MinValue)
                    {
                        importFileFields.Clear();
                        importFileFields.Add("postponeuntil", new DataverseDataTypeWrapper(delayUntil, DataverseFieldType.DateTime));
                        UpdateEntity("asyncoperation", "asyncoperationid", parseResp.AsyncOperationId, importFileFields);
                    }

                    TransformImportResponse transformResp = (TransformImportResponse)Command_Execute(new TransformImportRequest() { ImportId = ImportId },
                        string.Format(CultureInfo.InvariantCulture, "************ Exception Executing TransformImportRequest for ImportJob ({0})", importRequest.ImportName));
                    if (transformResp != null)
                    {
                        if (delayUntil != DateTime.MinValue)
                        {
                            importFileFields.Clear();
                            importFileFields.Add("postponeuntil", new DataverseDataTypeWrapper(delayUntil.AddSeconds(1), DataverseFieldType.DateTime));
                            UpdateEntity("asyncoperation", "asyncoperationid", transformResp.AsyncOperationId, importFileFields);
                        }

                        ImportRecordsImportResponse importResp = (ImportRecordsImportResponse)Command_Execute(new ImportRecordsImportRequest() { ImportId = ImportId },
                            string.Format(CultureInfo.InvariantCulture, "************ Exception Executing ImportRecordsImportRequest for ImportJob ({0})", importRequest.ImportName));
                        if (importResp != null)
                        {
                            if (delayUntil != DateTime.MinValue)
                            {
                                importFileFields.Clear();
                                importFileFields.Add("postponeuntil", new DataverseDataTypeWrapper(delayUntil.AddSeconds(2), DataverseFieldType.DateTime));
                                UpdateEntity("asyncoperation", "asyncoperationid", importResp.AsyncOperationId, importFileFields);
                            }

                            return ImportId;
                        }
                    }
                }
            }
            else
            {
                // Error.. Clean up the other records.
                string err = LastError;
                Exception ex = LastException;

                if (ImportFileIds.Count > 0)
                {
                    ImportFileIds.ForEach(i =>
                    {
                        DeleteEntity("importfile", i);
                    });
                    ImportFileIds.Clear();
                }

                if (ImportId != Guid.Empty)
                    DeleteEntity("import", ImportId);

                // This is done to allow the error to be available to the user after the class cleans things up.
                if (ex != null)
                    _logEntry.Log(err, TraceEventType.Error, ex);
                else
                    _logEntry.Log(err, TraceEventType.Error);

                return Guid.Empty;
            }
            return ImportId;
        }

        /// <summary>
        /// Used to upload a data map to the Dataverse
        /// </summary>
        /// <param name="dataMapXml">XML of the datamap in string form</param>
        /// <param name="replaceIds">True to have Dataverse replace ID's on inbound data, False to have inbound data retain its ID's</param>
        /// <param name="dataMapXmlIsFilePath">if true, dataMapXml is expected to be a File name and path to load.</param>
        /// <returns>Returns ID of the datamap or Guid.Empty</returns>
        public Guid ImportDataMap(string dataMapXml, bool replaceIds = true, bool dataMapXmlIsFilePath = false)
        {
            _logEntry.ResetLastError();  // Reset Last Error
            if (DataverseService == null)
            {
                _logEntry.Log("Dataverse Service not initialized", TraceEventType.Error);
                return Guid.Empty;
            }

            if (string.IsNullOrWhiteSpace(dataMapXml))
            {
                this._logEntry.Log("************ Exception on ImportDataMap, dataMapXml is required", TraceEventType.Error);
                return Guid.Empty;
            }

            if (dataMapXmlIsFilePath)
            {
                // try to load the file from the file system
                if (File.Exists(dataMapXml))
                {
                    try
                    {
                        string sContent = "";
                        using (var a = File.OpenText(dataMapXml))
                        {
                            sContent = a.ReadToEnd();
                        }

                        dataMapXml = sContent;
                    }
                    #region Exception handlers for files
                    catch (UnauthorizedAccessException ex)
                    {
                        this._logEntry.Log(string.Format(CultureInfo.InvariantCulture, "************ Exception on ImportDataMap, Unauthorized Access to file: {0}", dataMapXml), TraceEventType.Error, ex);
                        return Guid.Empty;
                    }
                    catch (ArgumentNullException ex)
                    {
                        this._logEntry.Log("************ Exception on ImportDataMap, File path not specified", TraceEventType.Error, ex);
                        return Guid.Empty;
                    }
                    catch (ArgumentException ex)
                    {
                        this._logEntry.Log(string.Format(CultureInfo.InvariantCulture, "************ Exception on ImportDataMap, File path is invalid: {0}", dataMapXml), TraceEventType.Error, ex);
                        return Guid.Empty;
                    }
                    catch (PathTooLongException ex)
                    {
                        this._logEntry.Log(string.Format(CultureInfo.InvariantCulture, "************ Exception on ImportDataMap, File path is too long. Paths must be less than 248 characters, and file names must be less than 260 characters\n{0}", dataMapXml), TraceEventType.Error, ex);
                        return Guid.Empty;
                    }
                    catch (DirectoryNotFoundException ex)
                    {
                        this._logEntry.Log(string.Format(CultureInfo.InvariantCulture, "************ Exception on ImportDataMap, File path is invalid: {0}", dataMapXml), TraceEventType.Error, ex);
                        return Guid.Empty;
                    }
                    catch (FileNotFoundException ex)
                    {
                        this._logEntry.Log(string.Format(CultureInfo.InvariantCulture, "************ Exception on ImportDataMap, File Not Found: {0}", dataMapXml), TraceEventType.Error, ex);
                        return Guid.Empty;
                    }
                    catch (NotSupportedException ex)
                    {
                        this._logEntry.Log(string.Format(CultureInfo.InvariantCulture, "************ Exception on ImportDataMap, File path or name is invalid: {0}", dataMapXml), TraceEventType.Error, ex);
                        return Guid.Empty;
                    }
                    #endregion
                }
                else
                {
                    this._logEntry.Log(string.Format(CultureInfo.InvariantCulture, "************ Exception on ImportDataMap, File path specified in dataMapXml is not found: {0}", dataMapXml), TraceEventType.Error);
                    return Guid.Empty;
                }

            }

            ImportMappingsImportMapResponse resp = (ImportMappingsImportMapResponse)Command_Execute(new ImportMappingsImportMapRequest() { MappingsXml = dataMapXml, ReplaceIds = replaceIds },
                "************ Exception Executing ImportMappingsImportMapResponse for ImportDataMap");
            if (resp != null)
            {
                if (resp.ImportMapId != Guid.Empty)
                {
                    return resp.ImportMapId;
                }
            }

            return Guid.Empty;
        }


        /// <summary>
        /// Import Solution Async used Execute Async pattern to run a solution import.
        /// </summary>
        /// <param name="solutionPath">Path to the Solution File</param>
        /// <param name="activatePlugIns">Activate Plugin's and workflows on the Solution </param>
        /// <param name="importId"><para>This will populate with the Import ID even if the request failed.
        /// You can use this ID to request status on the import via a request to the ImportJob entity.</para></param>
        /// <param name="overwriteUnManagedCustomizations">Forces an overwrite of unmanaged customizations of the managed solution you are installing, defaults to false</param>
        /// <param name="skipDependancyOnProductUpdateCheckOnInstall">Skips dependency against dependencies flagged as product update, defaults to false</param>
        /// <param name="importAsHoldingSolution">Applies only on Dataverse organizations version 7.2 or higher.  This imports the Dataverse solution as a holding solution utilizing the “As Holding” capability of ImportSolution </param>
        /// <param name="isInternalUpgrade">Internal Microsoft use only</param>
        /// <param name="extraParameters">Extra parameters</param>
        /// <returns>Returns the Async Job ID.  To find the status of the job, query the AsyncOperation Entity using GetEntityDataByID using the returned value of this method</returns>
        public Guid ImportSolutionAsync(string solutionPath, out Guid importId, bool activatePlugIns = true, bool overwriteUnManagedCustomizations = false, bool skipDependancyOnProductUpdateCheckOnInstall = false, bool importAsHoldingSolution = false, bool isInternalUpgrade = false, Dictionary<string, object> extraParameters = null)
        {
            return ImportSolutionToImpl(solutionPath, out importId, activatePlugIns, overwriteUnManagedCustomizations, skipDependancyOnProductUpdateCheckOnInstall, importAsHoldingSolution, isInternalUpgrade, true, extraParameters);
        }


        /// <summary>
        /// <para>
        /// Imports a Dataverse solution to the Dataverse Server currently connected.
        /// <para>*** Note: this is a blocking call and will take time to Import to Dataverse ***</para>
        /// </para>
        /// </summary>
        /// <param name="solutionPath">Path to the Solution File</param>
        /// <param name="activatePlugIns">Activate Plugin's and workflows on the Solution </param>
        /// <param name="importId"><para>This will populate with the Import ID even if the request failed.
        /// You can use this ID to request status on the import via a request to the ImportJob entity.</para></param>
        /// <param name="overwriteUnManagedCustomizations">Forces an overwrite of unmanaged customizations of the managed solution you are installing, defaults to false</param>
        /// <param name="skipDependancyOnProductUpdateCheckOnInstall">Skips dependency against dependencies flagged as product update, defaults to false</param>
        /// <param name="importAsHoldingSolution">Applies only on Dataverse organizations version 7.2 or higher.  This imports the Dataverse solution as a holding solution utilizing the “As Holding” capability of ImportSolution </param>
        /// <param name="isInternalUpgrade">Internal Microsoft use only</param>
        /// <param name="extraParameters">Extra parameters</param>
        public Guid ImportSolution(string solutionPath, out Guid importId, bool activatePlugIns = true, bool overwriteUnManagedCustomizations = false, bool skipDependancyOnProductUpdateCheckOnInstall = false, bool importAsHoldingSolution = false, bool isInternalUpgrade = false, Dictionary<string, object> extraParameters = null)
        {
            return ImportSolutionToImpl(solutionPath, out importId, activatePlugIns, overwriteUnManagedCustomizations, skipDependancyOnProductUpdateCheckOnInstall, importAsHoldingSolution, isInternalUpgrade, false, extraParameters);
        }

        /// <summary>
        /// Executes a Delete and Propmote Request against Dataverse using the Async Pattern.
        /// </summary>
        /// <param name="uniqueName">Unique Name of solution to be upgraded</param>
        /// <returns>Returns the Async Job ID.  To find the status of the job, query the AsyncOperation Entity using GetEntityDataByID using the returned value of this method</returns>
        public Guid DeleteAndPromoteSolutionAsync(string uniqueName)
        {
            _logEntry.ResetLastError();  // Reset Last Error
            if (DataverseService == null)
            {
                _logEntry.Log("Dataverse Service not initialized", TraceEventType.Error);
                return Guid.Empty;
            }
            // Test for non blank unique name.
            if (string.IsNullOrEmpty(uniqueName))
            {
                _logEntry.Log("Solution UniqueName is required.", TraceEventType.Error);
                return Guid.Empty;
            }

            DeleteAndPromoteRequest delReq = new DeleteAndPromoteRequest()
            {
                UniqueName = uniqueName
            };

            // Assign Tracking ID
            Guid requestTrackingId = Guid.NewGuid();
            delReq.RequestId = requestTrackingId;

            // Execute Async here
            ExecuteAsyncRequest req = new ExecuteAsyncRequest() { Request = delReq };
            _logEntry.Log(string.Format(CultureInfo.InvariantCulture, "{1} - Created Async DeleteAndPromoteSolutionRequest : RequestID={0} ",
            requestTrackingId.ToString(), uniqueName), TraceEventType.Verbose);
            ExecuteAsyncResponse resp = (ExecuteAsyncResponse)Command_Execute(req, "Submitting DeleteAndPromoteSolution Async Request");
            if (resp != null)
            {
                if (resp.AsyncJobId != Guid.Empty)
                {
                    _logEntry.Log(string.Format("{1} - AsyncJobID for DeleteAndPromoteSolution {0}.", resp.AsyncJobId, uniqueName), TraceEventType.Verbose);
                    return resp.AsyncJobId;
                }
            }

            _logEntry.Log(string.Format("{0} - Failed execute Async Job for DeleteAndPromoteSolution.", uniqueName), TraceEventType.Error);
            return Guid.Empty;
        }

        /// <summary>
        /// <para>
        /// Request Dataverse to install sample data shipped with Dataverse. Note this is process will take a few moments to execute.
        /// <para>This method will return once the request has been submitted.</para>
        /// </para>
        /// </summary>
        /// <returns>ID of the Async job executing the request</returns>
        public Guid InstallSampleData()
        {
            _logEntry.ResetLastError();  // Reset Last Error
            if (DataverseService == null)
            {
                _logEntry.Log("Dataverse Service not initialized", TraceEventType.Error);
                return Guid.Empty;
            }

            if (ImportStatus.NotImported != IsSampleDataInstalled())
            {
                _logEntry.Log("************ InstallSampleData failed, sample data is already installed on Dataverse", TraceEventType.Error);
                return Guid.Empty;
            }

            // Create Request to Install Sample data.
            InstallSampleDataRequest loadSampledataRequest = new InstallSampleDataRequest() { RequestId = Guid.NewGuid() };
            InstallSampleDataResponse resp = (InstallSampleDataResponse)Command_Execute(loadSampledataRequest, "Executing InstallSampleDataRequest for InstallSampleData");
            if (resp == null)
                return Guid.Empty;
            else
                return loadSampledataRequest.RequestId.Value;
        }

        /// <summary>
        /// <para>
        /// Request Dataverse to remove sample data shipped with Dataverse. Note this is process will take a few moments to execute.
        /// This method will return once the request has been submitted.
        /// </para>
        /// </summary>
        /// <returns>ID of the Async job executing the request</returns>
        public Guid UninstallSampleData()
        {
            _logEntry.ResetLastError();  // Reset Last Error
            if (DataverseService == null)
            {
                _logEntry.Log("Dataverse Service not initialized", TraceEventType.Error);
                return Guid.Empty;
            }

            if (ImportStatus.NotImported == IsSampleDataInstalled())
            {
                _logEntry.Log("************ UninstallSampleData failed, sample data is not installed on Dataverse", TraceEventType.Error);
                return Guid.Empty;
            }

            UninstallSampleDataRequest removeSampledataRequest = new UninstallSampleDataRequest() { RequestId = Guid.NewGuid() };
            UninstallSampleDataResponse resp = (UninstallSampleDataResponse)Command_Execute(removeSampledataRequest, "Executing UninstallSampleDataRequest for UninstallSampleData");
            if (resp == null)
                return Guid.Empty;
            else
                return removeSampledataRequest.RequestId.Value;
        }

        /// <summary>
        /// Determines if the Dataverse sample data has been installed
        /// </summary>
        /// <returns>True if the sample data is installed, False if not. </returns>
        public ImportStatus IsSampleDataInstalled()
        {
            try
            {
                // Query the Org I'm connected to to get the sample data import info.
                Dictionary<string, Dictionary<string, object>> theOrg =
                GetEntityDataBySearchParams("organization",
                    new Dictionary<string, string>(), LogicalSearchOperator.None, new List<string>() { "sampledataimportid" });

                if (theOrg != null && theOrg.Count > 0)
                {
                    var v = theOrg.FirstOrDefault();
                    if (v.Value != null && v.Value.Count > 0)
                    {
                        if (GetDataByKeyFromResultsSet<Guid>(v.Value, "sampledataimportid") != Guid.Empty)
                        {
                            string sampledataimportid = GetDataByKeyFromResultsSet<Guid>(v.Value, "sampledataimportid").ToString();
                            _logEntry.Log(string.Format("sampledataimportid = {0}", sampledataimportid), TraceEventType.Verbose);
                            Dictionary<string, string> basicSearch = new Dictionary<string, string>();
                            basicSearch.Add("importid", sampledataimportid);
                            Dictionary<string, Dictionary<string, object>> importSampleData = GetEntityDataBySearchParams("import", basicSearch, LogicalSearchOperator.None, new List<string>() { "statuscode" });

                            if (importSampleData != null && importSampleData.Count > 0)
                            {
                                var import = importSampleData.FirstOrDefault();
                                if (import.Value != null)
                                {
                                    OptionSetValue ImportStatusResult = GetDataByKeyFromResultsSet<OptionSetValue>(import.Value, "statuscode");
                                    if (ImportStatusResult != null)
                                    {
                                        _logEntry.Log(string.Format("sampledata import job result = {0}", ImportStatusResult.Value), TraceEventType.Verbose);
                                        //This Switch Case needs to be in Sync with the Dataverse Import StatusCode.
                                        switch (ImportStatusResult.Value)
                                        {
                                            // 4 is the Import Status Code for Complete Import
                                            case 4: return ImportStatus.Completed;
                                            // 5 is the Import Status Code for the Failed Import
                                            case 5: return ImportStatus.Failed;
                                            // Rest (Submitted, Parsing, Transforming, Importing) are different stages of Inprogress Import hence putting them under same case.
                                            default: return ImportStatus.InProgress;
                                        }
                                    }
                                }
                            }
                        }
                    }
                }
            }
            catch { }
            return ImportStatus.NotImported;
            //return false;
        }

        /// <summary>
        /// ImportStatus Reasons
        /// </summary>
        public enum ImportStatus
        {
            /// <summary> Not Yet Imported </summary>
            NotImported = 0,
            /// <summary> Import is in Progress </summary>
            InProgress = 1,
            /// <summary> Import has Completed </summary>
            Completed = 2,
            /// <summary> Import has Failed </summary>
            Failed = 3
        };

        #endregion

        /// <summary>
        /// Associates one Entity to another where an M2M Relationship Exists.
        /// </summary>
        /// <param name="entityName1">Entity on one side of the relationship</param>
        /// <param name="entity1Id">The Id of the record on the first side of the relationship</param>
        /// <param name="entityName2">Entity on the second side of the relationship</param>
        /// <param name="entity2Id">The Id of the record on the second side of the relationship</param>
        /// <param name="relationshipName">Relationship name between the 2 entities</param>
        /// <param name="batchId">Optional: if set to a valid GUID, generated by the Create Batch Request Method, will assigned the request to the batch for later execution, on fail, runs the request immediately </param>
		/// <param name="bypassPluginExecution">Adds the bypass plugin behavior to this request. Note: this will only apply if the caller has the prvBypassPlugins permission to bypass plugins.  If its attempted without the permission the request will fault.</param>
        /// <returns>true on success, false on fail</returns>
        public bool CreateEntityAssociation(string entityName1, Guid entity1Id, string entityName2, Guid entity2Id, string relationshipName, Guid batchId = default(Guid), bool bypassPluginExecution = false)
        {
            _logEntry.ResetLastError();  // Reset Last Error
            if (DataverseService == null)
            {
                _logEntry.Log("Dataverse Service not initialized", TraceEventType.Error);
                return false;
            }

            if (string.IsNullOrEmpty(entityName1) || string.IsNullOrEmpty(entityName2) || entity1Id == Guid.Empty || entity2Id == Guid.Empty)
            {
                _logEntry.Log(string.Format(CultureInfo.InvariantCulture, "************ Exception in CreateEntityAssociation, all parameters must be populated"), TraceEventType.Error);
                return false;
            }

            AssociateEntitiesRequest req = new AssociateEntitiesRequest();
            req.Moniker1 = new EntityReference(entityName1, entity1Id);
            req.Moniker2 = new EntityReference(entityName2, entity2Id);
            req.RelationshipName = relationshipName;


            if (AddRequestToBatch(batchId, req, string.Format(CultureInfo.InvariantCulture, "Creating association between({0}) and {1}", entityName1, entityName2),
                    string.Format(CultureInfo.InvariantCulture, "Request to Create association between({0}) and {1} Queued", entityName1, entityName2), bypassPluginExecution))
                return true;

            AssociateEntitiesResponse resp = (AssociateEntitiesResponse)Command_Execute(req, "Executing CreateEntityAssociation", bypassPluginExecution);
            if (resp != null)
                return true;

            return false;
        }

        /// <summary>
        /// Associates multiple entities of the same time to a single entity
        /// </summary>
        /// <param name="targetEntity">Entity that things will be related too.</param>
        /// <param name="targetEntity1Id">ID of entity that things will be related too</param>
        /// <param name="sourceEntityName">Entity that you are relating from</param>
        /// <param name="sourceEntitieIds">ID's of the entities you are relating from</param>
        /// <param name="relationshipName">Name of the relationship between the target and the source entities.</param>
        /// <param name="batchId">Optional: if set to a valid GUID, generated by the Create Batch Request Method, will assigned the request to the batch for later execution, on fail, runs the request immediately </param>
        /// <param name="isReflexiveRelationship">Optional: if set to true, indicates that this is a N:N using a reflexive relationship</param>
		/// <param name="bypassPluginExecution">Adds the bypass plugin behavior to this request. Note: this will only apply if the caller has the prvBypassPlugins permission to bypass plugins.  If its attempted without the permission the request will fault.</param>
        /// <returns>true on success, false on fail</returns>
        public bool CreateMultiEntityAssociation(string targetEntity, Guid targetEntity1Id, string sourceEntityName, List<Guid> sourceEntitieIds, string relationshipName, Guid batchId = default(Guid), bool bypassPluginExecution = false, bool isReflexiveRelationship = false)
        {
            _logEntry.ResetLastError();  // Reset Last Error
            if (DataverseService == null)
            {
                _logEntry.Log("Dataverse Service not initialized", TraceEventType.Error);
                return false;
            }

            if (string.IsNullOrEmpty(targetEntity) || string.IsNullOrEmpty(sourceEntityName) || targetEntity1Id == Guid.Empty || sourceEntitieIds == null || sourceEntitieIds.Count == 0)
            {
                _logEntry.Log(string.Format(CultureInfo.InvariantCulture, "************ Exception in CreateMultiEntityAssociation, all parameters must be populated"), TraceEventType.Error);
                return false;
            }

            AssociateRequest req = new AssociateRequest();
            req.Relationship = new Relationship(relationshipName);
            if (isReflexiveRelationship) // used to determine if the relationship role is reflexive.
                req.Relationship.PrimaryEntityRole = EntityRole.Referenced;
            req.RelatedEntities = new EntityReferenceCollection();
            foreach (Guid g in sourceEntitieIds)
            {
                req.RelatedEntities.Add(new EntityReference(sourceEntityName, g));
            }
            req.Target = new EntityReference(targetEntity, targetEntity1Id);

            if (AddRequestToBatch(batchId, req, string.Format(CultureInfo.InvariantCulture, "Creating multi association between({0}) and {1}", targetEntity, sourceEntityName),
                    string.Format(CultureInfo.InvariantCulture, "Request to Create multi association between({0}) and {1} queued", targetEntity, sourceEntityName), bypassPluginExecution))
                return true;

            AssociateResponse resp = (AssociateResponse)Command_Execute(req, "Executing CreateMultiEntityAssociation", bypassPluginExecution);
            if (resp != null)
                return true;

            return false;
        }

        /// <summary>
        /// Removes the Association between 2 entity items where an M2M Relationship Exists.
        /// </summary>
        /// <param name="entityName1">Entity on one side of the relationship</param>
        /// <param name="entity1Id">The Id of the record on the first side of the relationship</param>
        /// <param name="entityName2">Entity on the second side of the relationship</param>
        /// <param name="entity2Id">The Id of the record on the second side of the relationship</param>
        /// <param name="relationshipName">Relationship name between the 2 entities</param>
        /// <param name="batchId">Optional: if set to a valid GUID, generated by the Create Batch Request Method, will assigned the request to the batch for later execution, on fail, runs the request immediately </param>
		/// <param name="bypassPluginExecution">Adds the bypass plugin behavior to this request. Note: this will only apply if the caller has the prvBypassPlugins permission to bypass plugins.  If its attempted without the permission the request will fault.</param>
        /// <returns>true on success, false on fail</returns>
        public bool DeleteEntityAssociation(string entityName1, Guid entity1Id, string entityName2, Guid entity2Id, string relationshipName, Guid batchId = default(Guid), bool bypassPluginExecution = false)
        {
            _logEntry.ResetLastError();  // Reset Last Error
            if (DataverseService == null)
            {
                _logEntry.Log("Dataverse Service not initialized", TraceEventType.Error);
                return false;
            }

            if (string.IsNullOrEmpty(entityName1) || string.IsNullOrEmpty(entityName2) || entity1Id == Guid.Empty || entity2Id == Guid.Empty)
            {
                _logEntry.Log(string.Format(CultureInfo.InvariantCulture, "************ Exception in DeleteEntityAssociation, all parameters must be populated"), TraceEventType.Error);
                return false;
            }

            DisassociateEntitiesRequest req = new DisassociateEntitiesRequest();
            req.Moniker1 = new EntityReference(entityName1, entity1Id);
            req.Moniker2 = new EntityReference(entityName2, entity2Id);
            req.RelationshipName = relationshipName;

            if (AddRequestToBatch(batchId, req, string.Format(CultureInfo.InvariantCulture, "Executing DeleteEntityAssociation between ({0}) and {1}", entityName1, entityName2),
                              string.Format(CultureInfo.InvariantCulture, "Request to Execute DeleteEntityAssociation between ({0}) and {1} Queued", entityName1, entityName2), bypassPluginExecution))
                return true;

            DisassociateEntitiesResponse resp = (DisassociateEntitiesResponse)Command_Execute(req, "Executing DeleteEntityAssociation", bypassPluginExecution);
            if (resp != null)
                return true;

            return false;
        }

        /// <summary>
        /// Assign an Entity to the specified user ID
        /// </summary>
        /// <param name="userId">User ID to assign too</param>
        /// <param name="entityName">Target entity Name</param>
        /// <param name="entityId">Target entity id</param>
        /// <param name="batchId">Batch ID of to use, Optional</param>
		/// <param name="bypassPluginExecution">Adds the bypass plugin behavior to this request. Note: this will only apply if the caller has the prvBypassPlugins permission to bypass plugins.  If its attempted without the permission the request will fault.</param>
        /// <returns></returns>
        public bool AssignEntityToUser(Guid userId, string entityName, Guid entityId, Guid batchId = default(Guid), bool bypassPluginExecution = false)
        {
            _logEntry.ResetLastError();  // Reset Last Error
            if (DataverseService == null || userId == Guid.Empty || entityId == Guid.Empty)
            {
                return false;
            }

            AssignRequest assignRequest = new AssignRequest();
            assignRequest.Assignee = new EntityReference("systemuser", userId);
            assignRequest.Target = new EntityReference(entityName, entityId);

            if (AddRequestToBatch(batchId, assignRequest, string.Format(CultureInfo.InvariantCulture, "Assigning entity ({0}) to {1}", entityName, userId.ToString()),
                  string.Format(CultureInfo.InvariantCulture, "Request to Assign entity ({0}) to {1} Queued", entityName, userId.ToString()), bypassPluginExecution))
                return true;

            AssignResponse arResp = (AssignResponse)Command_Execute(assignRequest, "Assigning Entity to User", bypassPluginExecution);
            if (arResp != null)
                return true;

            return false;
        }

        /// <summary>
        /// This will route a Entity to a public queue,
        /// </summary>
        /// <param name="entityId">ID of the Entity to route</param>
        /// <param name="entityName">Name of the Entity that the Id describes</param>
        /// <param name="queueName">Name of the Queue to Route Too</param>
        /// <param name="workingUserId">ID of the user id to set as the working system user</param>
        /// <param name="setWorkingByUser">if true Set the worked by when doing the assign</param>
        /// <param name="batchId">Optional: if set to a valid GUID, generated by the Create Batch Request Method, will assigned the request to the batch for later execution, on fail, runs the request immediately </param>
        /// <param name="bypassPluginExecution">Adds the bypass plugin behavior to this request. Note: this will only apply if the caller has the prvBypassPlugins permission to bypass plugins.  If its attempted without the permission the request will fault.</param>
        /// <returns>true on success</returns>
        public bool AddEntityToQueue(Guid entityId, string entityName, string queueName, Guid workingUserId, bool setWorkingByUser = false, Guid batchId = default(Guid), bool bypassPluginExecution = false)
        {
            _logEntry.ResetLastError();  // Reset Last Error
            if (DataverseService == null || entityId == Guid.Empty)
            {
                return false;
            }

            Dictionary<string, string> SearchParams = new Dictionary<string, string>();
            SearchParams.Add("name", queueName);

            // Get the Target QUeue
            Dictionary<string, Dictionary<string, object>> rslts = GetEntityDataBySearchParams("queue", SearchParams, LogicalSearchOperator.None, null);
            if (rslts != null)
                if (rslts.Count > 0)
                {
                    Guid guQueueID = Guid.Empty;
                    foreach (Dictionary<string, object> row in rslts.Values)
                    {
                        // got something
                        guQueueID = GetDataByKeyFromResultsSet<Guid>(row, "queueid");
                        break;
                    }

                    if (guQueueID != Guid.Empty)
                    {


                        AddToQueueRequest req = new AddToQueueRequest();
                        req.DestinationQueueId = guQueueID;
                        req.Target = new EntityReference(entityName, entityId);

                        // Set the worked by user if the request includes it.
                        if (setWorkingByUser)
                        {
                            Entity queItm = new Entity("queueitem");
                            queItm.Attributes.Add("workerid", new EntityReference("systemuser", workingUserId));
                            req.QueueItemProperties = queItm;
                        }

                        if (AddRequestToBatch(batchId, req, string.Format(CultureInfo.InvariantCulture, "Assigning entity to queue ({0}) to {1}", entityName, guQueueID.ToString()),
                                    string.Format(CultureInfo.InvariantCulture, "Request to Assign entity to queue ({0}) to {1} Queued", entityName, guQueueID.ToString()), bypassPluginExecution))
                            return true;

                        AddToQueueResponse resp = (AddToQueueResponse)Command_Execute(req, string.Format(CultureInfo.InvariantCulture, "Adding a item to queue {0} in CDS", queueName), bypassPluginExecution);
                        if (resp != null)
                            return true;
                        else
                            return false;
                    }
                }
            return false;
        }

        /// <summary>
        /// this will send an Email to the
        /// </summary>
        /// <param name="emailid">ID of the Email activity</param>
        /// <param name="token">Tracking Token or Null</param>
        /// <param name="batchId">Optional: if set to a valid GUID, generated by the Create Batch Request Method, will assigned the request to the batch for later execution, on fail, runs the request immediately </param>
		/// <param name="bypassPluginExecution">Adds the bypass plugin behavior to this request. Note: this will only apply if the caller has the prvBypassPlugins permission to bypass plugins.  If its attempted without the permission the request will fault.</param>
        /// <returns></returns>
        public bool SendSingleEmail(Guid emailid, string token, Guid batchId = default(Guid), bool bypassPluginExecution = false)
        {
            _logEntry.ResetLastError();  // Reset Last Error
            if (DataverseService == null || emailid == Guid.Empty)
            {
                return false;
            }

            if (token == null)
                token = string.Empty;

            // Send the mail now.
            SendEmailRequest req = new SendEmailRequest();
            req.EmailId = emailid;
            req.TrackingToken = token;
            req.IssueSend = true; // Send it now.

            if (AddRequestToBatch(batchId, req, string.Format(CultureInfo.InvariantCulture, "Send Direct email ({0}) tracking token {1}", emailid.ToString(), token),
                    string.Format(CultureInfo.InvariantCulture, "Request to Send Direct email ({0}) tracking token {1} Queued", emailid.ToString(), token), bypassPluginExecution))
                return true;

            SendEmailResponse sendresp = (SendEmailResponse)Command_Execute(req, string.Format(CultureInfo.InvariantCulture, "Sending email ({0}) from Dataverse", emailid), bypassPluginExecution);
            if (sendresp != null)
                return true;
            else
                return false;
        }

        /// <summary>
        /// Returns the user ID of the currently logged in user.
        /// </summary>
        /// <returns></returns>
        public Guid GetMyUserId()
        {
            return SystemUser.UserId;
        }

        #endregion

        #region Dataverse MetadataService methods


        /// <summary>
        /// Gets a PickList, Status List or StateList from the metadata of an attribute
        /// </summary>
        /// <param name="targetEntity">text name of the entity to query</param>
        /// <param name="attribName">name of the attribute to query</param>
        /// <returns></returns>
        public PickListMetaElement GetPickListElementFromMetadataEntity(string targetEntity, string attribName)
        {
            _logEntry.ResetLastError();  // Reset Last Error
            if (DataverseService != null)
            {
                List<AttributeData> attribDataList = _dynamicAppUtility.GetAttributeDataByEntity(targetEntity, attribName);
                if (attribDataList.Count > 0)
                {
                    // have data..
                    // need to make sure its really a pick list.
                    foreach (AttributeData attributeData in attribDataList)
                    {
                        switch (attributeData.AttributeType)
                        {
                            case AttributeTypeCode.Picklist:
                            case AttributeTypeCode.Status:
                            case AttributeTypeCode.State:
                                PicklistAttributeData pick = (PicklistAttributeData)attributeData;
                                PickListMetaElement resp = new PickListMetaElement((string)pick.ActualValue, pick.AttributeLabel, pick.DisplayValue);
                                if (pick.PicklistOptions != null)
                                {
                                    foreach (OptionMetadata opt in pick.PicklistOptions)
                                    {
                                        PickListItem itm = null;
                                        itm = new PickListItem((string)GetLocalLabel(opt.Label), (int)opt.Value.Value);
                                        resp.Items.Add(itm);
                                    }
                                }
                                return resp;
                            default:
                                break;
                        }
                    }
                }
            }
            return null;
        }

        /// <summary>
        /// Gets a global option set from Dataverse.
        /// </summary>
        /// <param name="globalOptionSetName">Name of the Option Set To get</param>
        /// <returns>OptionSetMetadata or null</returns>
        public OptionSetMetadata GetGlobalOptionSetMetadata(string globalOptionSetName)
        {
            _logEntry.ResetLastError();  // Reset Last Error
            if (DataverseService == null)
            {
                _logEntry.Log("Dataverse Service not initialized", TraceEventType.Error);
                return null;
            }

            try
            {
                return _metadataUtlity.GetGlobalOptionSetMetadata(globalOptionSetName);
            }
            catch (Exception ex)
            {
                this._logEntry.Log("************ Exception getting optionset metadata info from Dataverse   : " + ex.Message, TraceEventType.Error);
            }
            return null;
        }


        /// <summary>
        /// Returns a list of entities with basic data from Dataverse
        /// </summary>
        /// <param name="onlyPublished">defaults to true, will only return published information</param>
        /// <param name="filter">EntityFilter to apply to this request, note that filters other then Default will consume more time.</param>
        /// <returns></returns>
        public List<EntityMetadata> GetAllEntityMetadata(bool onlyPublished = true, EntityFilters filter = EntityFilters.Default)
        {
            _logEntry.ResetLastError();  // Reset Last Error
            #region Basic Checks
            if (DataverseService == null)
            {
                _logEntry.Log("Dataverse Service not initialized", TraceEventType.Error);
                return null;
            }
            #endregion

            try
            {
                return _metadataUtlity.GetAllEntityMetadata(onlyPublished, filter);
            }
            catch (Exception ex)
            {
                this._logEntry.Log("************ Exception getting metadata info from CDS   : " + ex.Message, TraceEventType.Error);
            }
            return null;
        }

        /// <summary>
        /// Returns the Metadata for an entity from Dataverse, defaults to basic data only.
        /// </summary>
        /// <param name="entityLogicalname">Logical name of the entity</param>
        /// <param name="queryFilter">filter to apply to the query, defaults to default entity data.</param>
        /// <returns></returns>
        public EntityMetadata GetEntityMetadata(string entityLogicalname, EntityFilters queryFilter = EntityFilters.Default)
        {
            _logEntry.ResetLastError();  // Reset Last Error
            #region Basic Checks
            if (DataverseService == null)
            {
                _logEntry.Log("Dataverse Service not initialized", TraceEventType.Error);
                return null;
            }
            #endregion

            try
            {
                return _metadataUtlity.GetEntityMetadata(queryFilter, entityLogicalname);
            }
            catch (Exception ex)
            {
                this._logEntry.Log("************ Exception getting metadata info from Dataverse   : " + ex.Message, TraceEventType.Error);
            }
            return null;
        }

        /// <summary>
        /// Returns the Form Entity References for a given form type.
        /// </summary>
        /// <param name="entityLogicalname">logical name of the entity you are querying for form data.</param>
        /// <param name="formTypeId">Form Type you want</param>
        /// <returns>List of Entity References for the form type requested.</returns>
        public List<EntityReference> GetEntityFormIdListByType(string entityLogicalname, FormTypeId formTypeId)
        {
            _logEntry.ResetLastError();
            #region Basic Checks
            if (DataverseService == null)
            {
                _logEntry.Log("Dataverse Service not initialized", TraceEventType.Error);
                return null;
            }
            if (string.IsNullOrWhiteSpace(entityLogicalname))
            {
                _logEntry.Log("An Entity Name must be supplied", TraceEventType.Error);
                return null;
            }
            #endregion

            try
            {
                RetrieveFilteredFormsRequest req = new RetrieveFilteredFormsRequest();
                req.EntityLogicalName = entityLogicalname;
                req.FormType = new OptionSetValue((int)formTypeId);
                RetrieveFilteredFormsResponse resp = (RetrieveFilteredFormsResponse)Command_Execute(req, "GetEntityFormIdListByType");
                if (resp != null)
                    return resp.SystemForms.ToList();
                else
                    return null;
            }
            catch (Exception ex)
            {
                this._logEntry.Log("************ Exception getting metadata info from Dataverse   : " + ex.Message, TraceEventType.Error);
            }
            return null;
        }

        /// <summary>
        /// Returns all attributes on a entity
        /// </summary>
        /// <param name="entityLogicalname">returns all attributes on a entity</param>
        /// <returns></returns>
        public List<AttributeMetadata> GetAllAttributesForEntity(string entityLogicalname)
        {
            _logEntry.ResetLastError();  // Reset Last Error
            #region Basic Checks
            if (DataverseService == null)
            {
                _logEntry.Log("Dataverse Service not initialized", TraceEventType.Error);
                return null;
            }
            if (string.IsNullOrWhiteSpace(entityLogicalname))
            {
                _logEntry.Log("An Entity Name must be supplied", TraceEventType.Error);
                return null;
            }
            #endregion

            try
            {
                return _metadataUtlity.GetAllAttributesMetadataByEntity(entityLogicalname);
            }
            catch (Exception ex)
            {
                this._logEntry.Log("************ Exception getting metadata info from Dataverse   : " + ex.Message, TraceEventType.Error);
            }
            return null;
        }

        /// <summary>
        /// Gets metadata for a specific entity's attribute.
        /// </summary>
        /// <param name="entityLogicalname">Name of the entity</param>
        /// <param name="attribName">Attribute Name</param>
        /// <returns></returns>
        public AttributeMetadata GetEntityAttributeMetadataForAttribute(string entityLogicalname, string attribName)
        {
            _logEntry.ResetLastError();  // Reset Last Error
            #region Basic Checks
            if (DataverseService == null)
            {
                _logEntry.Log("Dataverse Service not initialized", TraceEventType.Error);
                return null;
            }
            if (string.IsNullOrWhiteSpace(entityLogicalname))
            {
                _logEntry.Log("An Entity Name must be supplied", TraceEventType.Error);
                return null;
            }
            #endregion

            try
            {
                return _metadataUtlity.GetAttributeMetadata(entityLogicalname, attribName);
            }
            catch (Exception ex)
            {
                this._logEntry.Log("************ Exception getting metadata info from Dataverse   : " + ex.Message, TraceEventType.Error);
            }
            return null;
        }

        /// <summary>
        /// Gets an Entity Name by Logical name or Type code.
        /// </summary>
        /// <param name="entityName">logical name of the entity </param>
        /// <param name="entityTypeCode">Type code for the entity </param>
        /// <returns>Localized name for the entity in the current users language</returns>
        public string GetEntityDisplayName(string entityName, int entityTypeCode = -1)
        {
            return GetEntityDisplayNameImpl(entityName, entityTypeCode);
        }

        /// <summary>
        /// Gets an Entity Name by Logical name or Type code.
        /// </summary>
        /// <param name="entityName">logical name of the entity </param>
        /// <param name="entityTypeCode">Type code for the entity </param>
        /// <returns>Localized plural name for the entity in the current users language</returns>
        public string GetEntityDisplayNamePlural(string entityName, int entityTypeCode = -1)
        {
            return GetEntityDisplayNameImpl(entityName, entityTypeCode, true);
        }

        /// <summary>
        /// This will clear the Metadata cache for either all entities or the specified entity
        /// </summary>
        /// <param name="entityName">Optional: name of the entity to clear cached info for</param>
        public void ResetLocalMetadataCache(string entityName = "")
        {
            if (_metadataUtlity != null)
                _metadataUtlity.ClearCachedEntityMetadata(entityName);
        }

        /// <summary>
        /// Gets the Entity Display Name.
        /// </summary>
        /// <param name="entityName"></param>
        /// <param name="entityTypeCode"></param>
        /// <param name="getPlural"></param>
        /// <returns></returns>
        private string GetEntityDisplayNameImpl(string entityName, int entityTypeCode = -1, bool getPlural = false)
        {
            _logEntry.ResetLastError();  // Reset Last Error
            #region Basic Checks
            if (DataverseService == null)
            {
                _logEntry.Log("Dataverse Service not initialized", TraceEventType.Error);
                return string.Empty;
            }

            if (entityTypeCode == -1 && string.IsNullOrWhiteSpace(entityName))
            {
                _logEntry.Log("Target entity or Type code is required", TraceEventType.Error);
                return string.Empty;
            }
            #endregion

            try
            {
                // Get the entity by type code if necessary.
                if (entityTypeCode != -1)
                    entityName = _metadataUtlity.GetEntityLogicalName(entityTypeCode);

                if (string.IsNullOrWhiteSpace(entityName))
                {
                    _logEntry.Log("Target entity or Type code is required", TraceEventType.Error);
                    return string.Empty;
                }



                // Pull Object type code for this object.
                EntityMetadata entData =
                    _metadataUtlity.GetEntityMetadata(EntityFilters.Entity, entityName);

                if (entData != null)
                {
                    if (getPlural)
                    {
                        if (entData.DisplayCollectionName != null && entData.DisplayCollectionName.UserLocalizedLabel != null)
                            return entData.DisplayCollectionName.UserLocalizedLabel.Label;
                        else
                            return entityName; // Default to echo the same name back
                    }
                    else
                    {
                        if (entData.DisplayName != null && entData.DisplayName.UserLocalizedLabel != null)
                            return entData.DisplayName.UserLocalizedLabel.Label;
                        else
                            return entityName; // Default to echo the same name back
                    }
                }

            }
            catch (Exception ex)
            {
                this._logEntry.Log("************ Exception getting metadata info from Dataverse   : " + ex.Message, TraceEventType.Error);
            }
            return string.Empty;
        }

        /// <summary>
        /// Gets the typecode of an entity by name.
        /// </summary>
        /// <param name="entityName">name of the entity to get the type code on</param>
        /// <returns></returns>
        public string GetEntityTypeCode(string entityName)
        {
            _logEntry.ResetLastError();  // Reset Last Error
            #region Basic Checks
            if (DataverseService == null)
            {
                _logEntry.Log("Dataverse Service not initialized", TraceEventType.Error);
                return string.Empty;
            }

            if (string.IsNullOrEmpty(entityName))
            {
                _logEntry.Log("Target entity is required", TraceEventType.Error);
                return string.Empty;
            }
            #endregion

            try
            {

                // Pull Object type code for this object.
                EntityMetadata entData =
                    _metadataUtlity.GetEntityMetadata(EntityFilters.Entity, entityName);

                if (entData != null)
                {
                    if (entData.ObjectTypeCode != null && entData.ObjectTypeCode.HasValue)
                    {
                        return entData.ObjectTypeCode.Value.ToString(CultureInfo.InvariantCulture);
                    }
                }
            }
            catch (Exception ex)
            {
                this._logEntry.Log("************ Exception getting metadata info from Dataverse   : " + ex.Message, TraceEventType.Error);
            }
            return string.Empty;
        }


        /// <summary>
        /// Returns the Entity name for the given Type code
        /// </summary>
        /// <param name="entityTypeCode"></param>
        /// <returns></returns>
        public string GetEntityName(int entityTypeCode)
        {
            return _metadataUtlity.GetEntityLogicalName(entityTypeCode);
        }


        /// <summary>
        /// Adds an option to a pick list on an entity.
        /// </summary>
        /// <param name="targetEntity">Entity Name to Target</param>
        /// <param name="attribName">Attribute Name on the Entity</param>
        /// <param name="locLabelList">List of Localized Labels</param>
        /// <param name="valueData">integer Value</param>
        /// <param name="publishOnComplete">Publishes the Update to the Live system.. note this is a time consuming process.. if you are doing a batch up updates, call PublishEntity Separately when you are finished.</param>
        /// <returns>true on success, on fail check last error.</returns>
        public bool CreateOrUpdatePickListElement(string targetEntity, string attribName, List<LocalizedLabel> locLabelList, int valueData, bool publishOnComplete)
        {
            _logEntry.ResetLastError();  // Reset Last Error
            #region Basic Checks
            if (DataverseService == null)
            {
                _logEntry.Log("Dataverse Service not initialized", TraceEventType.Error);
                return false;
            }

            if (string.IsNullOrEmpty(targetEntity))
            {
                _logEntry.Log("Target entity is required", TraceEventType.Error);
                return false;
            }

            if (string.IsNullOrEmpty(attribName))
            {
                _logEntry.Log("Target attribute name is required", TraceEventType.Error);
                return false;
            }

            if (locLabelList == null || locLabelList.Count == 0)
            {
                _logEntry.Log("Target Labels are required", TraceEventType.Error);
                return false;
            }

            LoadLCIDs(); // Load current languages .

            // Clear out the Metadata for this object.
            if (_metadataUtlity != null)
                _metadataUtlity.ClearCachedEntityMetadata(targetEntity);

            EntityMetadata entData =
                _metadataUtlity.GetEntityMetadata(targetEntity);

            if (!entData.IsCustomEntity.Value)
            {
                // Only apply this if the entity is not a custom entity
                if (valueData <= 199999)
                {
                    _logEntry.Log("Option Value must exceed 200000", TraceEventType.Error);
                    return false;
                }
            }


            #endregion

            // get the values for the requested attribute.
            PickListMetaElement listData = GetPickListElementFromMetadataEntity(targetEntity, attribName);
            if (listData == null)
            {
                // error here.
            }

            bool isUpdate = false;
            if (listData.Items != null && listData.Items.Count != 0)
            {
                // Check to see if the value we are looking to insert already exists by name or value.
                List<string> DisplayLabels = new List<string>();
                foreach (LocalizedLabel loclbl in locLabelList)
                {
                    if (DisplayLabels.Contains(loclbl.Label))
                        continue;
                    else
                        DisplayLabels.Add(loclbl.Label);
                }

                foreach (PickListItem pItem in listData.Items)
                {
                    // check the value by id.
                    if (pItem.PickListItemId == valueData)
                    {
                        if (DisplayLabels.Contains(pItem.DisplayLabel))
                        {
                            DisplayLabels.Clear();
                            _logEntry.Log("PickList Element exists, No Change required.", TraceEventType.Error);
                            return false;
                        }
                        isUpdate = true;
                        break;
                    }

                    //// Check the value by name...  by putting this hear, we will handle a label update vs a Duplicate label.
                    if (DisplayLabels.Contains(pItem.DisplayLabel))
                    {
                        // THis is an ERROR State... While Dataverse will allow 2 labels with the same text, it looks weird.
                        DisplayLabels.Clear();
                        _logEntry.Log("Label Name exists, Please use a different display name for the label.", TraceEventType.Error);
                        return false;
                    }
                }

                DisplayLabels.Clear();
            }

            if (isUpdate)
            {
                // update request
                UpdateOptionValueRequest updateReq = new UpdateOptionValueRequest();
                updateReq.AttributeLogicalName = attribName;
                updateReq.EntityLogicalName = targetEntity;
                updateReq.Label = new Label();
                List<LocalizedLabel> lblList = new List<LocalizedLabel>();
                foreach (LocalizedLabel loclbl in locLabelList)
                {
                    if (_loadedLCIDList.Contains(loclbl.LanguageCode))
                    {
                        LocalizedLabel lbl = new LocalizedLabel()
                        {
                            Label = loclbl.Label,
                            LanguageCode = loclbl.LanguageCode
                        };
                        lblList.Add(lbl);
                    }
                }
                updateReq.Label.LocalizedLabels.AddRange(lblList.ToArray());
                updateReq.Value = valueData;
                updateReq.MergeLabels = true;

                UpdateOptionValueResponse UpdateResp = (UpdateOptionValueResponse)Command_Execute(updateReq, "Updating a PickList Element in Dataverse");
                if (UpdateResp == null)
                    return false;
            }
            else
            {
                // create request.
                // Create a new insert request
                InsertOptionValueRequest req = new InsertOptionValueRequest();

                req.AttributeLogicalName = attribName;
                req.EntityLogicalName = targetEntity;
                req.Label = new Label();
                List<LocalizedLabel> lblList = new List<LocalizedLabel>();
                foreach (LocalizedLabel loclbl in locLabelList)
                {
                    if (_loadedLCIDList.Contains(loclbl.LanguageCode))
                    {
                        LocalizedLabel lbl = new LocalizedLabel()
                        {
                            Label = loclbl.Label,
                            LanguageCode = loclbl.LanguageCode
                        };
                        lblList.Add(lbl);
                    }
                }
                req.Label.LocalizedLabels.AddRange(lblList.ToArray());
                req.Value = valueData;


                InsertOptionValueResponse resp = (InsertOptionValueResponse)Command_Execute(req, "Creating a PickList Element in Dataverse");
                if (resp == null)
                    return false;

            }

            // Publish the update if asked to.
            if (publishOnComplete)
                return PublishEntity(targetEntity);
            else
                return true;
        }

        /// <summary>
        /// Publishes an entity to the production system,
        /// used in conjunction with the Metadata services.
        /// </summary>
        /// <param name="entityName">Name of the entity to publish</param>
        /// <returns>True on success</returns>
        public bool PublishEntity(string entityName)
        {
            // Now Publish the update.
            string sPublishUpdateXml =
                           string.Format(CultureInfo.InvariantCulture, "<importexportxml><entities><entity>{0}</entity></entities><nodes /><securityroles/><settings/><workflows/></importexportxml>",
                           entityName);

            PublishXmlRequest pubReq = new PublishXmlRequest();
            pubReq.ParameterXml = sPublishUpdateXml;

            PublishXmlResponse rsp = (PublishXmlResponse)Command_Execute(pubReq, "Publishing a PickList Element in Dataverse");
            if (rsp != null)
                return true;
            else
                return false;
        }

        /// <summary>
        /// Loads the Currently loaded languages for Dataverse
        /// </summary>
        /// <returns></returns>
        private bool LoadLCIDs()
        {
            // Now Publish the update.
            // Check to see if the Language ID's are loaded.
            if (_loadedLCIDList == null)
            {
                _loadedLCIDList = new List<int>();

                // load the Dataverse Language List.
                RetrieveAvailableLanguagesRequest lanReq = new RetrieveAvailableLanguagesRequest();
                RetrieveAvailableLanguagesResponse rsp = (RetrieveAvailableLanguagesResponse)Command_Execute(lanReq, "Reading available languages from Dataverse");
                if (rsp == null)
                    return false;
                if (rsp.LocaleIds != null)
                {
                    foreach (int iLCID in rsp.LocaleIds)
                    {
                        if (_loadedLCIDList.Contains(iLCID))
                            continue;
                        else
                            _loadedLCIDList.Add(iLCID);
                    }
                }
            }
            return true;
        }

        #endregion

        #endregion

        #region OAuth Token Cache

        /// <summary>
        /// Clear the persistent and in-memory store cache
        /// </summary>
        /// <param name="tokenCachePath"></param>
        /// <returns></returns>
        public static bool RemoveOAuthTokenCache(string tokenCachePath = "")
        {
            throw new NotImplementedException();
            //If tokenCachePath is not supplied it will take from the constructor  of token cache and delete the file.
            //if (_CdsServiceClientTokenCache == null)
            //    _CdsServiceClientTokenCache = new CdsServiceClientTokenCache(tokenCachePath);
            //return _CdsServiceClientTokenCache.Clear(tokenCachePath);
            //TODO: Update for new Token cache providers.
            //return false;
        }

        #endregion

        #region DataverseUtilites

        /// <summary>
        /// Adds paging related parameter to the input fetchXml
        /// </summary>
        /// <param name="fetchXml">Input fetch Xml</param>
        /// <param name="pageCount">The number of records to be fetched</param>
        /// <param name="pageNum">The page number</param>
        /// <param name="pageCookie">Page cookie</param>
        /// <returns></returns>
        private String AddPagingParametersToFetchXml(string fetchXml, int pageCount, int pageNum, string pageCookie)
        {
            if (String.IsNullOrWhiteSpace(fetchXml))
            {
                return fetchXml;
            }

            XmlDocument fetchdoc = XmlUtil.CreateXmlDocument(fetchXml);
            XmlElement fetchroot = fetchdoc.DocumentElement;

            XmlAttribute pageAttribute = fetchdoc.CreateAttribute("page");
            pageAttribute.Value = pageNum.ToString(CultureInfo.InvariantCulture);

            XmlAttribute countAttribute = fetchdoc.CreateAttribute("count");
            countAttribute.Value = pageCount.ToString(CultureInfo.InvariantCulture);

            XmlAttribute pagingCookieAttribute = fetchdoc.CreateAttribute("paging-cookie");
            pagingCookieAttribute.Value = pageCookie;

            fetchroot.Attributes.Append(pageAttribute);
            fetchroot.Attributes.Append(countAttribute);
            fetchroot.Attributes.Append(pagingCookieAttribute);

            return fetchdoc.DocumentElement.OuterXml;
        }

        /// <summary>
        ///  Makes a secure string
        /// </summary>
        /// <param name="pass"></param>
        /// <returns></returns>
        public static SecureString MakeSecureString(string pass)
        {
            SecureString _pass = new SecureString();
            if (!string.IsNullOrEmpty(pass))
            {
                foreach (char c in pass)
                {
                    _pass.AppendChar(c);
                }
                _pass.MakeReadOnly(); // Lock it down.
                return _pass;
            }
            return null;
        }

        /// <summary>
        /// Builds the Query expression to use with a Search.
        /// </summary>
        /// <param name="entityName"></param>
        /// <param name="searchParams"></param>
        /// <param name="fieldList"></param>
        /// <param name="searchOperator"></param>
        /// <returns></returns>
        private static QueryExpression BuildQueryFilter(string entityName, List<DataverseSearchFilter> searchParams, List<string> fieldList, LogicalSearchOperator searchOperator)
        {
            // Create ColumnSet
            ColumnSet cols = null;
            if (fieldList != null)
            {
                cols = new ColumnSet();
                cols.Columns.AddRange(fieldList.ToArray());
            }

            List<FilterExpression> filters = BuildFilterList(searchParams);

            // Link Filter.
            FilterExpression Queryfilter = new FilterExpression();
            Queryfilter.Filters.AddRange(filters);

            // Add Logical relationship.
            if (searchOperator == LogicalSearchOperator.Or)
                Queryfilter.FilterOperator = LogicalOperator.Or;
            else
                Queryfilter.FilterOperator = LogicalOperator.And;


            // Build Query
            QueryExpression query = new QueryExpression();
            query.EntityName = entityName; // Set to the requested entity Type
            if (cols != null)
                query.ColumnSet = cols;
            else
                query.ColumnSet = new ColumnSet(true);// new AllColumns();

            query.Criteria = Queryfilter;
            query.NoLock = true; // Added to remove locking on queries.
            return query;
        }

        /// <summary>
        /// Creates a SearchFilterList from a Search string Dictionary
        /// </summary>
        /// <param name="inSearchParams">Inbound Search Strings</param>
        /// <param name="outSearchList">List that will be populated</param>
        private static void BuildSearchFilterListFromSearchTerms(Dictionary<string, string> inSearchParams, List<DataverseSearchFilter> outSearchList)
        {
            if (inSearchParams != null)
            {
                foreach (var item in inSearchParams)
                {
                    DataverseSearchFilter f = new DataverseSearchFilter();
                    f.FilterOperator = LogicalOperator.And;
                    f.SearchConditions.Add(new DataverseFilterConditionItem()
                    {
                        FieldName = item.Key,
                        FieldValue = item.Value,
                        FieldOperator = string.IsNullOrWhiteSpace(item.Value) ? ConditionOperator.Null : item.Value.Contains("%") ? ConditionOperator.Like : ConditionOperator.Equal
                    });
                    outSearchList.Add(f);
                }
            }
        }

        /// <summary>
        /// Builds the filter list for a query
        /// </summary>
        /// <param name="searchParams"></param>
        /// <returns></returns>
        private static List<FilterExpression> BuildFilterList(List<DataverseSearchFilter> searchParams)
        {
            List<FilterExpression> filters = new List<FilterExpression>();
            // Create Conditions
            foreach (DataverseSearchFilter conditionItemList in searchParams)
            {
                FilterExpression filter = new FilterExpression();
                foreach (DataverseFilterConditionItem conditionItem in conditionItemList.SearchConditions)
                {
                    ConditionExpression condition = new ConditionExpression();
                    condition.AttributeName = conditionItem.FieldName;
                    condition.Operator = conditionItem.FieldOperator;
                    if (!(condition.Operator == ConditionOperator.NotNull || condition.Operator == ConditionOperator.Null))
                        condition.Values.Add(conditionItem.FieldValue);

                    filter.AddCondition(condition);
                }
                if (filter.Conditions.Count > 0)
                {
                    filter.FilterOperator = conditionItemList.FilterOperator;
                    filters.Add(filter);
                }
            }
            return filters;
        }

        /// <summary>
        /// Get the localize label from a Dataverse Label.
        /// </summary>
        /// <param name="localLabel"></param>
        /// <returns></returns>
        private static string GetLocalLabel(Label localLabel)
        {
            foreach (LocalizedLabel lbl in localLabel.LocalizedLabels)
            {
                // try to get the current display langue code.
                if (lbl.LanguageCode == CultureInfo.CurrentUICulture.LCID)
                {
                    return lbl.Label;
                }
            }
            return localLabel.UserLocalizedLabel.Label;
        }

        /// <summary>
        /// Adds data from a Entity to result set
        /// </summary>
        /// <param name="resultSet"></param>
        /// <param name="dataEntity"></param>
        private static void AddDataToResultSet(ref Dictionary<string, object> resultSet, Entity dataEntity)
        {
            if (dataEntity == null)
                return;
            if (resultSet == null)
                return;
            try
            {
                foreach (var p in dataEntity.Attributes)
                {
                    resultSet.Add(p.Key + "_Property", p);
                    resultSet.Add(p.Key, dataEntity.FormattedValues.ContainsKey(p.Key) ? dataEntity.FormattedValues[p.Key] : p.Value);
                }

            }
            catch { }
        }

        /// <summary>
        /// Gets the Lookup Value GUID for any given entity name
        /// </summary>
        /// <param name="entName">Entity you are looking for</param>
        /// <param name="Value">Value you are looking for</param>
        /// <returns>ID of the lookup value in the entity</returns>
        private Guid GetLookupValueForEntity(string entName, string Value)
        {
            // Check for existence of cached list.
            if (_CachObject == null)
            {
                object objc = _connectionSvc.LocalMemoryCache.Get(_cachObjecName);
                if (objc is Dictionary<string, Dictionary<string, object>> workingObj)
                    _CachObject = workingObj;

                if (_CachObject == null)
                    _CachObject = new Dictionary<string, Dictionary<string, object>>();
            }

            Guid guResultID = Guid.Empty;

            if ((_CachObject.ContainsKey(entName.ToString())) && (_CachObject[entName.ToString()].ContainsKey(Value)))
                return (Guid)_CachObject[entName.ToString()][Value];

            switch (entName)
            {
                case "transactioncurrency":
                    guResultID = LookupEntitiyID(Value, entName, "transactioncurrencyid", "currencyname");
                    break;
                case "subject":
                    guResultID = LookupEntitiyID(Value, entName, "subjectid", "title"); //LookupSubjectIDForName(Value);
                    break;
                case "systemuser":
                    guResultID = LookupEntitiyID(Value, entName, "systemuserid", "domainname");
                    break;
                case "pricelevel":
                    guResultID = LookupEntitiyID(Value, entName, "pricelevelid", "name");
                    break;

                case "product":
                    guResultID = LookupEntitiyID(Value, entName, "productid", "productnumber");
                    break;
                case "uom":
                    guResultID = LookupEntitiyID(Value, entName, "uomid", "name");
                    break;
                default:
                    return Guid.Empty;
            }


            // High effort objects that are generally not changed during the live cycle of a connection are cached here.
            if (guResultID != Guid.Empty)
            {
                if (!_CachObject.ContainsKey(entName.ToString()))
                    _CachObject.Add(entName.ToString(), new Dictionary<string, object>());
                _CachObject[entName.ToString()].Add(Value, guResultID);

                _connectionSvc.LocalMemoryCache.Set(_cachObjecName, _CachObject, DateTime.Now.AddMinutes(5));
            }

            return guResultID;

        }

        /// <summary>
        /// Lookup a entity ID by a single search element.
        /// Used for Lookup Lists.
        /// </summary>
        /// <param name="SearchValue">Text to search for</param>
        /// <param name="ent">Entity Type to Search in </param>
        /// <param name="IDField">Field that contains the id</param>
        /// <param name="SearchField">Field to Search against</param>
        /// <returns>Guid of Entity or Empty Guid</returns>
        private Guid LookupEntitiyID(string SearchValue, string ent, string IDField, string SearchField)
        {
            try
            {
                Guid guID = Guid.Empty;
                List<string> FieldList = new List<string>();
                FieldList.Add(IDField);

                Dictionary<string, string> SearchList = new Dictionary<string, string>();
                SearchList.Add(SearchField, SearchValue);

                Dictionary<string, Dictionary<string, object>> rslts = GetEntityDataBySearchParams(ent, SearchList, LogicalSearchOperator.None, FieldList);

                if (rslts != null)
                {
                    foreach (Dictionary<string, object> rsl in rslts.Values)
                    {
                        if (rsl.ContainsKey(IDField))
                        {
                            guID = (Guid)rsl[IDField];
                        }
                    }
                }
                return guID;
            }
            catch
            {
                return Guid.Empty;
            }
        }

        /// <summary>
        /// Adds values for an update to a Dataverse propertyList
        /// </summary>
        /// <param name="Field"></param>
        /// <param name="PropertyList"></param>
        /// <returns></returns>
        internal void AddValueToPropertyList(KeyValuePair<string, DataverseDataTypeWrapper> Field, AttributeCollection PropertyList)
        {
            if (string.IsNullOrEmpty(Field.Key))
                // throw exception
                throw new System.ArgumentOutOfRangeException("valueArray", "Missing Dataverse field name");

            try
            {
                switch (Field.Value.Type)
                {

                    case DataverseFieldType.Boolean:
                        PropertyList.Add(new KeyValuePair<string, object>(Field.Key, (bool)Field.Value.Value));
                        break;

                    case DataverseFieldType.DateTime:
                        PropertyList.Add(new KeyValuePair<string, object>(Field.Key, (DateTime)Field.Value.Value));
                        break;

                    case DataverseFieldType.Decimal:
                        PropertyList.Add(new KeyValuePair<string, object>(Field.Key, Convert.ToDecimal(Field.Value.Value)));
                        break;

                    case DataverseFieldType.Float:
                        PropertyList.Add(new KeyValuePair<string, object>(Field.Key, Convert.ToDouble(Field.Value.Value)));
                        break;

                    case DataverseFieldType.Money:
                        PropertyList.Add(new KeyValuePair<string, object>(Field.Key, new Money(Convert.ToDecimal(Field.Value.Value))));
                        break;

                    case DataverseFieldType.Number:
                        PropertyList.Add(new KeyValuePair<string, object>(Field.Key, (int)Field.Value.Value));
                        break;

                    case DataverseFieldType.Customer:
                        PropertyList.Add(new KeyValuePair<string, object>(Field.Key, new EntityReference(Field.Value.ReferencedEntity, (Guid)Field.Value.Value)));
                        break;

                    case DataverseFieldType.Lookup:
                        PropertyList.Add(new KeyValuePair<string, object>(Field.Key, new EntityReference(Field.Value.ReferencedEntity, (Guid)Field.Value.Value)));
                        break;

                    case DataverseFieldType.Picklist:
                        PropertyList.Add(new KeyValuePair<string, object>(Field.Key, new OptionSetValue((int)Field.Value.Value)));
                        break;

                    case DataverseFieldType.String:
                        PropertyList.Add(new KeyValuePair<string, object>(Field.Key, (string)Field.Value.Value));
                        break;

                    case DataverseFieldType.Raw:
                        PropertyList.Add(new KeyValuePair<string, object>(Field.Key, Field.Value.Value));
                        break;

                    case DataverseFieldType.UniqueIdentifier:
                        PropertyList.Add(new KeyValuePair<string, object>(Field.Key, (Guid)Field.Value.Value));
                        break;
                }
            }
            catch (InvalidCastException castEx)
            {
                _logEntry.Log(string.Format(CultureInfo.InvariantCulture, "Failed when casting DataverseDataTypeWrapper wrapped objects to the Dataverse Type. Field : {0}", Field.Key), TraceEventType.Error, castEx);
                throw;
            }
            catch (System.Exception ex)
            {
                _logEntry.Log(string.Format(CultureInfo.InvariantCulture, "Failed when casting DataverseDataTypeWrapper wrapped objects to the Dataverse Type. Field : {0}", Field.Key), TraceEventType.Error, ex);
                throw;
            }

        }

        /// <summary>
        /// Creates and Returns a Search Result Set
        /// </summary>
        /// <param name="resp"></param>
        /// <returns></returns>
        private static Dictionary<string, Dictionary<string, object>> CreateResultDataSet(EntityCollection resp)
        {
            Dictionary<string, Dictionary<string, object>> Results = new Dictionary<string, Dictionary<string, object>>();
            foreach (Entity bEnt in resp.Entities)
            {
                // Not really doing an update here... just turning it into something I can walk.
                Dictionary<string, object> SearchRstls = new Dictionary<string, object>();
                AddDataToResultSet(ref SearchRstls, bEnt);
                // Add Ent name and ID
                SearchRstls.Add("ReturnProperty_EntityName", bEnt.LogicalName);
                SearchRstls.Add("ReturnProperty_Id ", bEnt.Id);
                Results.Add(Guid.NewGuid().ToString(), SearchRstls);
            }
            if (Results.Count > 0)
                return Results;
            else
                return null;
        }

        /// <summary>
        /// Adds a request to a batch with display and handling logic
        /// will fail out if batching is not enabled.
        /// </summary>
        /// <param name="batchId">ID of the batch to add too</param>
        /// <param name="req">Organization request to Add</param>
        /// <param name="batchTagText">Batch Add Text, this is the text that will be reflected when the batch is added - appears in the batch diags</param>
        /// <param name="successText">Success Added Batch - appears in webSvcActions diag</param>
        /// <param name="bypassPluginExecution">Adds the bypass plugin behavior to this request. Note: this will only apply if the caller has the prvBypassPlugins permission to bypass plugins.  If its attempted without the permission the request will fault.</param>
        /// <returns></returns>
        internal bool AddRequestToBatch(Guid batchId, OrganizationRequest req, string batchTagText, string successText, bool bypassPluginExecution)
        {
            if (batchId != Guid.Empty)
            {
                // if request should bypass plugin exec.
                if (bypassPluginExecution &&
                    Utilities.FeatureVersionMinimums.IsFeatureValidForEnviroment(ConnectedOrgVersion, Utilities.FeatureVersionMinimums.AllowBypassCustomPlugin))
                    req.Parameters.Add(Utilities.RequestHeaders.BYPASSCUSTOMPLUGINEXECUTION, true);

                if (IsBatchOperationsAvailable)
                {
                    if (_batchManager.AddNewRequestToBatch(batchId, req, batchTagText))
                    {
                        _logEntry.Log(successText, TraceEventType.Verbose);
                        return true;
                    }
                    else
                        _logEntry.Log("Unable to add request to batch queue, Executing normally", TraceEventType.Warning);
                }
                else
                {
                    // Error and fall though.
                    _logEntry.Log("Unable to add request to batch, Batching is not currently available, Executing normally", TraceEventType.Warning);
                }
            }
            return false;
        }

        #region XRM Commands and handlers

        #region Public Access to direct commands.

        /// <summary>
        /// Executes a web request against Xrm WebAPI.
        /// </summary>
        /// <param name="queryString">Here you would pass the path and query parameters that you wish to pass onto the WebAPI.
        /// The format used here is as follows:
        ///   {APIURI}/api/data/v{instance version}/querystring.
        /// For example,
        ///     if you wanted to get data back from an account,  you would pass the following:
        ///         accounts(id)
        ///         which creates:  get - https://myinstance.crm.dynamics.com/api/data/v9.0/accounts(id)
        ///     if you were creating an account, you would pass the following:
        ///         accounts
        ///         which creates:  post - https://myinstance.crm.dynamics.com/api/data/v9.0/accounts - body contains the data.
        ///         </param>
        /// <param name="method">Method to use for the request</param>
        /// <param name="body">Content your passing to the request</param>
        /// <param name="customHeaders">Headers in addition to the default headers added by for Executing a web request</param>
        /// <param name="contentType">Content Type attach to the request.  this defaults to application/json if not set.</param>
        /// <param name="cancellationToken">Cancellation token for the request</param>
        /// <returns></returns>
        public HttpResponseMessage ExecuteWebRequest(HttpMethod method, string queryString, string body, Dictionary<string, List<string>> customHeaders, string contentType = default, CancellationToken cancellationToken = default)
        {
            _logEntry.ResetLastError();  // Reset Last Error
            if (DataverseService == null)
            {
                _logEntry.Log("Dataverse Service not initialized", TraceEventType.Error);
                return null;
            }

            if (string.IsNullOrEmpty(queryString) && string.IsNullOrEmpty(body))
            {
                _logEntry.Log("Execute Web Request failed, queryString and body cannot be null", TraceEventType.Error);
                return null;
            }

            if (Uri.TryCreate(queryString, UriKind.Absolute, out var urlPath))
            {
                // Was able to create a URL here... Need to make sure that we strip out everything up to the last segment.
                string baseQueryString = urlPath.Segments.Last();
                if (!string.IsNullOrEmpty(urlPath.Query))
                    queryString = baseQueryString + urlPath.Query;
                else
                    queryString = baseQueryString;
            }

            var result = _connectionSvc.Command_WebExecuteAsync(queryString, body, method, customHeaders, contentType, string.Empty, CallerId, _disableConnectionLocking, MaxRetryCount, RetryPauseTime, cancellationToken: cancellationToken).Result;
            if (result == null)
                throw LastException;
            else
                return result;
        }

        /// <summary>
        /// Executes a Dataverse Organization Request (thread safe) and returns the organization response object. Also adds metrics for logging support.
        /// </summary>
        /// <param name="req">Organization Request  to run</param>
        /// <param name="logMessageTag">Message identifying what this request in logging.</param>
        /// <param name="useWebAPI">When True, uses the webAPI to execute the organization Request.  This works for only Create at this time.</param>
        /// <returns>Result of request or null.</returns>
        public OrganizationResponse ExecuteOrganizationRequest(OrganizationRequest req, string logMessageTag = "User Defined", bool useWebAPI = false)
        {
            return ExecuteOrganizationRequestImpl(req, logMessageTag, useWebAPI, false);
        }

        private OrganizationResponse ExecuteOrganizationRequestImpl(OrganizationRequest req, string logMessageTag = "User Defined", bool useWebAPI = false, bool bypassPluginExecution = false)
        {
            if (req != null)
            {
                useWebAPI = Utilities.IsRequestValidForTranslationToWebAPI(req);
                if (!useWebAPI)
                {
                    return Command_Execute(req, logMessageTag, bypassPluginExecution);
                }
                else
                {
                    // use Web API.
                    return _connectionSvc.Command_WebAPIProcess_ExecuteAsync(req, logMessageTag, bypassPluginExecution, _metadataUtlity, CallerId, _disableConnectionLocking, MaxRetryCount, RetryPauseTime, new CancellationToken()).Result;
                }
            }
            else
            {
                _logEntry.Log("Execute Organization Request failed, Organization Request cannot be null", TraceEventType.Error);
                return null;
            }
        }

        private async Task<OrganizationResponse> ExecuteOrganizationRequestAsyncImpl(OrganizationRequest req, CancellationToken cancellationToken, string logMessageTag = "User Defined", bool useWebAPI = false, bool bypassPluginExecution = false)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (req != null)
            {
                useWebAPI = Utilities.IsRequestValidForTranslationToWebAPI(req);
                if (!useWebAPI)
                {
                    return await Command_ExecuteAsync(req, logMessageTag, cancellationToken, bypassPluginExecution).ConfigureAwait(false);
                }
                else
                {
                    // use Web API.
                    return await _connectionSvc.Command_WebAPIProcess_ExecuteAsync(req, logMessageTag, bypassPluginExecution, _metadataUtlity, CallerId, _disableConnectionLocking, MaxRetryCount, RetryPauseTime, cancellationToken).ConfigureAwait(false);
                }
            }
            else
            {
                _logEntry.Log("Execute Organization Request failed, Organization Request cannot be null", TraceEventType.Error);
                return null;
            }
        }

        /// <summary>
        /// Executes a row level delete on a Dataverse entity ( thread safe ) and returns true or false. Also adds metrics for logging support.
        /// </summary>
        /// <param name="entName">Name of the Entity to delete from</param>
        /// <param name="entId">ID of the row to delete</param>
        /// <param name="logMessageTag">Message identifying what this request in logging</param>
        /// <returns>True on success, False on fail. </returns>
        public bool ExecuteEntityDeleteRequest(string entName, Guid entId, string logMessageTag = "User Defined")
        {
            if (string.IsNullOrWhiteSpace(entName))
            {
                _logEntry.Log("Execute Delete Request failed, Entity Name cannot be null or empty", TraceEventType.Error);
                return false;
            }
            if (entId == Guid.Empty)
            {
                _logEntry.Log("Execute Delete Request failed, Guid to delete cannot be null or empty", TraceEventType.Error);
                return false;
            }

            DeleteRequest req = new DeleteRequest();
            req.Target = new EntityReference(entName, entId);

            DeleteResponse resp = (DeleteResponse)Command_Execute(req, string.Format(CultureInfo.InvariantCulture, "Trying to Delete. Entity = {0}, ID = {1}", entName, entId));
            if (resp != null)
            {
                return true;
            }
            return false;
        }

        #endregion


        /// <summary>
        /// <para>
        /// Imports a Dataverse solution to the Dataverse Server currently connected.
        /// <para>*** Note: this is a blocking call and will take time to Import to Dataverse ***</para>
        /// </para>
        /// </summary>
        /// <param name="solutionPath">Path to the Solution File</param>
        /// <param name="activatePlugIns">Activate Plugin's and workflows on the Solution </param>
        /// <param name="importId"><para>This will populate with the Import ID even if the request failed.
        /// You can use this ID to request status on the import via a request to the ImportJob entity.</para></param>
        /// <param name="overwriteUnManagedCustomizations">Forces an overwrite of unmanaged customizations of the managed solution you are installing, defaults to false</param>
        /// <param name="skipDependancyOnProductUpdateCheckOnInstall">Skips dependency against dependencies flagged as product update, defaults to false</param>
        /// <param name="importAsHoldingSolution">Applies only on Dataverse organizations version 7.2 or higher.  This imports the Dataverse solution as a holding solution utilizing the “As Holding” capability of ImportSolution </param>
        /// <param name="isInternalUpgrade">Internal Microsoft use only</param>
        /// <param name="useAsync">Requires the use of an Async Job to do the import. </param>
        /// <param name="extraParameters">Extra parameters</param>
        /// <returns>Returns the Import Solution Job ID.  To find the status of the job, query the ImportJob Entity using GetEntityDataByID using the returned value of this method</returns>
        internal Guid ImportSolutionToImpl(string solutionPath, out Guid importId, bool activatePlugIns, bool overwriteUnManagedCustomizations, bool skipDependancyOnProductUpdateCheckOnInstall, bool importAsHoldingSolution, bool isInternalUpgrade, bool useAsync, Dictionary<string, object> extraParameters)
        {
            _logEntry.ResetLastError();  // Reset Last Error
            importId = Guid.Empty;
            if (DataverseService == null)
            {
                _logEntry.Log("Dataverse Service not initialized", TraceEventType.Error);
                return Guid.Empty;
            }

            if (string.IsNullOrWhiteSpace(solutionPath))
            {
                this._logEntry.Log("************ Exception on ImportSolutionToImpl, SolutionPath is required", TraceEventType.Error);
                return Guid.Empty;
            }

            // determine if the system is connected to OnPrem
            bool isConnectedToOnPrem = (_connectionSvc.ConnectedOrganizationDetail != null && string.IsNullOrEmpty(_connectionSvc.ConnectedOrganizationDetail.Geo));

            //Extract extra parameters if they exist
            string solutionName = string.Empty;
            LayerDesiredOrder desiredLayerOrder = null;
            bool? asyncRibbonProcessing = null;
            EntityCollection componetsToProcess = null;
            bool? convertToManaged = null;
            bool? isTemplateModeImport = null;
            string templateSuffix = null;

            if (extraParameters != null)
            {
                solutionName = extraParameters.ContainsKey(ImportSolutionProperties.SOLUTIONNAMEPARAM) ? extraParameters[ImportSolutionProperties.SOLUTIONNAMEPARAM].ToString() : string.Empty;
                desiredLayerOrder = extraParameters.ContainsKey(ImportSolutionProperties.DESIREDLAYERORDERPARAM) ? extraParameters[ImportSolutionProperties.DESIREDLAYERORDERPARAM] as LayerDesiredOrder : null;
                componetsToProcess = extraParameters.ContainsKey(ImportSolutionProperties.COMPONENTPARAMETERSPARAM) ? extraParameters[ImportSolutionProperties.COMPONENTPARAMETERSPARAM] as EntityCollection : null;
                convertToManaged = extraParameters.ContainsKey(ImportSolutionProperties.CONVERTTOMANAGED) ? extraParameters[ImportSolutionProperties.CONVERTTOMANAGED] as bool? : null;
                isTemplateModeImport = extraParameters.ContainsKey(ImportSolutionProperties.ISTEMPLATEMODE) ? extraParameters[ImportSolutionProperties.ISTEMPLATEMODE] as bool? : null;
                templateSuffix = extraParameters.ContainsKey(ImportSolutionProperties.TEMPLATESUFFIX) ? extraParameters[ImportSolutionProperties.TEMPLATESUFFIX].ToString() : string.Empty;

                // Pick up the data from the request,  if the request has the AsyncRibbonProcessing flag, pick up the value of it.
                asyncRibbonProcessing = extraParameters.ContainsKey(ImportSolutionProperties.ASYNCRIBBONPROCESSING) ? extraParameters[ImportSolutionProperties.ASYNCRIBBONPROCESSING] as bool? : null;
                // If the value is populated, and t
                if (asyncRibbonProcessing != null && asyncRibbonProcessing.HasValue)
                {
                    if (isConnectedToOnPrem)
                    {
                        // Not supported for OnPrem.
                        // reset the asyncRibbonProcess to Null.
                        this._logEntry.Log($"ImportSolution request contains {ImportSolutionProperties.ASYNCRIBBONPROCESSING} property.  This is not valid for OnPremise deployments and will be removed", TraceEventType.Warning);
                        asyncRibbonProcessing = null;
                    }
                    else
                    {
                        if (!Utilities.FeatureVersionMinimums.IsFeatureValidForEnviroment(_connectionSvc?.OrganizationVersion, Utilities.FeatureVersionMinimums.AllowAsyncRibbonProcessing))
                        {
                            // Not supported on this version of Dataverse
                            this._logEntry.Log($"ImportSolution request contains {ImportSolutionProperties.ASYNCRIBBONPROCESSING} property.  This request Dataverse version {Utilities.FeatureVersionMinimums.AllowAsyncRibbonProcessing.ToString()} or above. Current Dataverse version is {_connectionSvc?.OrganizationVersion}. This property will be removed", TraceEventType.Warning);
                            asyncRibbonProcessing = null;
                        }
                    }
                }

                if (componetsToProcess != null)
                {
                    if (isConnectedToOnPrem)
                    {
                        this._logEntry.Log($"ImportSolution request contains {ImportSolutionProperties.COMPONENTPARAMETERSPARAM} property.  This is not valid for OnPremise deployments and will be removed", TraceEventType.Warning);
                        componetsToProcess = null;
                    }
                    else
                    {
                        if (!Utilities.FeatureVersionMinimums.IsFeatureValidForEnviroment(_connectionSvc?.OrganizationVersion, Utilities.FeatureVersionMinimums.AllowComponetInfoProcessing))
                        {
                            // Not supported on this version of Dataverse
                            this._logEntry.Log($"ImportSolution request contains {ImportSolutionProperties.COMPONENTPARAMETERSPARAM} property. This request Dataverse version {Utilities.FeatureVersionMinimums.AllowComponetInfoProcessing.ToString()} or above. Current Dataverse version is {_connectionSvc?.OrganizationVersion}. This property will be removed", TraceEventType.Warning);
                            componetsToProcess = null;
                        }
                    }
                }

                if (isTemplateModeImport != null)
                {
                    if (isConnectedToOnPrem)
                    {
                        this._logEntry.Log($"ImportSolution request contains {ImportSolutionProperties.ISTEMPLATEMODE} property.  This is not valid for OnPremise deployments and will be removed", TraceEventType.Warning);
                        isTemplateModeImport = null;
                    }
                    else
                    {
                        if (!Utilities.FeatureVersionMinimums.IsFeatureValidForEnviroment(_connectionSvc?.OrganizationVersion, Utilities.FeatureVersionMinimums.AllowTemplateSolutionImport))
                        {
                            // Not supported on this version of Dataverse
                            this._logEntry.Log($"ImportSolution request contains {ImportSolutionProperties.ISTEMPLATEMODE} property. This request Dataverse version {Utilities.FeatureVersionMinimums.AllowTemplateSolutionImport.ToString()} or above. Current Dataverse version is {_connectionSvc?.OrganizationVersion}. This property will be removed", TraceEventType.Warning);
                            isTemplateModeImport = null;
                        }
                    }
                }
            }

            string solutionNameForLogging = string.IsNullOrWhiteSpace(solutionName) ? string.Empty : string.Concat(solutionName, " - ");

            // try to load the file from the file system
            if (File.Exists(solutionPath))
            {
                try
                {
                    importId = Guid.NewGuid();
                    byte[] fileData = File.ReadAllBytes(solutionPath);
                    ImportSolutionRequest SolutionImportRequest = new ImportSolutionRequest()
                    {
                        CustomizationFile = fileData,
                        PublishWorkflows = activatePlugIns,
                        ImportJobId = importId,
                        OverwriteUnmanagedCustomizations = overwriteUnManagedCustomizations
                    };

                    //If the desiredLayerOrder is null don't add it to the request. This ensures backward compatibility. It makes old packages work on old builds
                    if (desiredLayerOrder != null)
                    {
                        //If package contains the LayerDesiredOrder hint but the server doesn't support the new message, we want the package to fail
                        //The server will throw - "Unrecognized request parameter: LayerDesiredOrder" - That's the desired behavior
                        //The hint is only enforced on the first time a solution is added to the org. If we allow it to go, the import will succeed, but the desired state won't be achieved
                        SolutionImportRequest.LayerDesiredOrder = desiredLayerOrder;

                        string solutionsInHint = string.Join(",", desiredLayerOrder.Solutions.Select(n => n.Name).ToArray());

                        _logEntry.Log(string.Format(CultureInfo.InvariantCulture, "{0}DesiredLayerOrder clause present: Type: {1}, Solutions: {2}", solutionNameForLogging, desiredLayerOrder.Type, solutionsInHint), TraceEventType.Verbose);
                    }

                    if (asyncRibbonProcessing != null && asyncRibbonProcessing == true)
                    {
                        SolutionImportRequest.AsyncRibbonProcessing = true;
                        SolutionImportRequest.SkipQueueRibbonJob = true;
                        _logEntry.Log(string.Format(CultureInfo.InvariantCulture, "{0} AsyncRibbonProcessing: {1}", solutionNameForLogging, true), TraceEventType.Verbose);
                    }

                    if (componetsToProcess != null)
                    {
                        SolutionImportRequest.ComponentParameters = componetsToProcess;
                    }

                    if (convertToManaged != null)
                    {
                        SolutionImportRequest.ConvertToManaged = convertToManaged.Value;
                    }

                    if (isTemplateModeImport != null && isTemplateModeImport.Value)
                    {
                        SolutionImportRequest.Parameters[ImportSolutionProperties.ISTEMPLATEMODE] = isTemplateModeImport.Value;
                        SolutionImportRequest.Parameters[ImportSolutionProperties.TEMPLATESUFFIX] = templateSuffix;
                    }

                    if (IsBatchOperationsAvailable)
                    {
                        // Support for features added in UR12
                        SolutionImportRequest.SkipProductUpdateDependencies = skipDependancyOnProductUpdateCheckOnInstall;
                    }

                    if (importAsHoldingSolution)  // If Import as Holding is set..
                    {
                        // Check for Min version of Dataverse for support of Import as Holding solution.
                        if (Utilities.FeatureVersionMinimums.IsFeatureValidForEnviroment(_connectionSvc?.OrganizationVersion, Utilities.FeatureVersionMinimums.ImportHoldingSolution))
                        {
                            // Use Parameters to add the property here to support the underlying Xrm API on the incorrect version.
                            SolutionImportRequest.Parameters.Add("HoldingSolution", importAsHoldingSolution);
                        }
                    }

                    // Set IsInternalUpgrade flag on request only for upgrade scenario for V9 only.
                    if (isInternalUpgrade)
                    {
                        if (Utilities.FeatureVersionMinimums.IsFeatureValidForEnviroment(_connectionSvc?.OrganizationVersion, Utilities.FeatureVersionMinimums.InternalUpgradeSolution))
                        {
                            SolutionImportRequest.Parameters["IsInternalUpgrade"] = true;
                        }
                    }

                    if (useAsync)
                    {
                        // Assign Tracking ID
                        Guid requestTrackingId = Guid.NewGuid();
                        SolutionImportRequest.RequestId = requestTrackingId;

                        if (!isConnectedToOnPrem && Utilities.FeatureVersionMinimums.IsFeatureValidForEnviroment(ConnectedOrgVersion, Utilities.FeatureVersionMinimums.AllowImportSolutionAsyncV2))
                        {
                            // map import request to Async Model
                            ImportSolutionAsyncRequest asynImportRequest = new ImportSolutionAsyncRequest()
                            {
                                AsyncRibbonProcessing = SolutionImportRequest.AsyncRibbonProcessing,
                                ComponentParameters = SolutionImportRequest.ComponentParameters,
                                ConvertToManaged = SolutionImportRequest.ConvertToManaged,
                                CustomizationFile = SolutionImportRequest.CustomizationFile,
                                HoldingSolution = SolutionImportRequest.HoldingSolution,
                                LayerDesiredOrder = SolutionImportRequest.LayerDesiredOrder,
                                OverwriteUnmanagedCustomizations = SolutionImportRequest.OverwriteUnmanagedCustomizations,
                                Parameters = SolutionImportRequest.Parameters,
                                PublishWorkflows = SolutionImportRequest.PublishWorkflows,
                                RequestId = SolutionImportRequest.RequestId,
                                SkipProductUpdateDependencies = SolutionImportRequest.SkipProductUpdateDependencies,
                                SkipQueueRibbonJob = SolutionImportRequest.SkipQueueRibbonJob
                            };

                            // remove unsupported parameter from importsolutionasync request.
                            if (asynImportRequest.Parameters.ContainsKey("ImportJobId"))
                                asynImportRequest.Parameters.Remove("ImportJobId");

                            _logEntry.Log(string.Format(CultureInfo.InvariantCulture, "{1}Created Async ImportSolutionAsyncRequest : RequestID={0} ", requestTrackingId.ToString(), solutionNameForLogging), TraceEventType.Verbose);
                            ImportSolutionAsyncResponse asyncResp = (ImportSolutionAsyncResponse)Command_Execute(asynImportRequest, solutionNameForLogging + "Executing Request for ImportSolutionAsyncRequest : ");
                            if (asyncResp == null)
                                return Guid.Empty;
                            else
                                return asyncResp.AsyncOperationId;
                        }
                        else
                        {
                            // Creating Async Solution Import request.
                            ExecuteAsyncRequest req = new ExecuteAsyncRequest() { Request = SolutionImportRequest };
                            _logEntry.Log(string.Format(CultureInfo.InvariantCulture, "{1}Created Async ImportSolutionRequest : RequestID={0} ",
                                requestTrackingId.ToString(), solutionNameForLogging), TraceEventType.Verbose);
                            ExecuteAsyncResponse asyncResp = (ExecuteAsyncResponse)Command_Execute(req, solutionNameForLogging + "Executing Request for ImportSolutionToAsync : ");
                            if (asyncResp == null)
                                return Guid.Empty;
                            else
                                return asyncResp.AsyncJobId;
                        }
                    }
                    else
                    {
                        ImportSolutionResponse resp = (ImportSolutionResponse)Command_Execute(SolutionImportRequest, solutionNameForLogging + "Executing ImportSolutionRequest for ImportSolution");
                        if (resp == null)
                            return Guid.Empty;
                        else
                            return importId;
                    }
                }
                #region Exception handlers for files
                catch (UnauthorizedAccessException ex)
                {
                    this._logEntry.Log(string.Format(CultureInfo.InvariantCulture, "************ Exception on ImportSolution, Unauthorized Access to file: {0}", solutionPath), TraceEventType.Error, ex);
                    return Guid.Empty;
                }
                catch (ArgumentNullException ex)
                {
                    this._logEntry.Log("************ Exception on ImportSolutionToCds, File path not specified", TraceEventType.Error, ex);
                    return Guid.Empty;
                }
                catch (ArgumentException ex)
                {
                    this._logEntry.Log(string.Format(CultureInfo.InvariantCulture, "************ Exception on ImportSolution, File path is invalid: {0}", solutionPath), TraceEventType.Error, ex);
                    return Guid.Empty;
                }
                catch (PathTooLongException ex)
                {
                    this._logEntry.Log(string.Format(CultureInfo.InvariantCulture, "************ Exception on ImportSolution, File path is too long. Paths must be less than 248 characters, and file names must be less than 260 characters\n{0}", solutionPath), TraceEventType.Error, ex);
                    return Guid.Empty;
                }
                catch (DirectoryNotFoundException ex)
                {
                    this._logEntry.Log(string.Format(CultureInfo.InvariantCulture, "************ Exception on ImportSolution, File path is invalid: {0}", solutionPath), TraceEventType.Error, ex);
                    return Guid.Empty;
                }
                catch (FileNotFoundException ex)
                {
                    this._logEntry.Log(string.Format(CultureInfo.InvariantCulture, "************ Exception on ImportSolution, File Not Found: {0}", solutionPath), TraceEventType.Error, ex);
                    return Guid.Empty;
                }
                catch (NotSupportedException ex)
                {
                    this._logEntry.Log(string.Format(CultureInfo.InvariantCulture, "************ Exception on ImportSolution, File path or name is invalid: {0}", solutionPath), TraceEventType.Error, ex);
                    return Guid.Empty;
                }
                #endregion
            }
            else
            {
                this._logEntry.Log(string.Format(CultureInfo.InvariantCulture, "************ Exception on ImportSolution, File path specified in dataMapXml is not found: {0}", solutionPath), TraceEventType.Error);
                return Guid.Empty;
            }
        }

        /// <summary>
        /// Executes a Dataverse Create Request and returns the organization response object.
        /// Uses an Async pattern to allow for the thread to be backgrounded.
        /// </summary>
        /// <param name="req">Request to run</param>
        /// <param name="errorStringCheck">Formatted Error string</param>
        /// <param name="bypassPluginExecution">Adds the bypass plugin behavior to this request. Note: this will only apply if the caller has the prvBypassPlugins permission to bypass plugins.  If its attempted without the permission the request will fault.</param>
        /// <param name="cancellationToken">Propagates notification that operations should be canceled.</param>
        /// <returns>Result of create request or null.</returns>
        internal async Task<OrganizationResponse> Command_ExecuteAsync(OrganizationRequest req, string errorStringCheck, System.Threading.CancellationToken cancellationToken, bool bypassPluginExecution = false)
        {
            if (DataverseServiceAsync != null)
            {
                // if created based on Async Client.
                return await Command_ExecuteAsyncImpl(req, errorStringCheck, cancellationToken, bypassPluginExecution).ConfigureAwait(false);
            }
            else
            {
                // if not use task.run().
                return await Task.Run(() => Command_Execute(req, errorStringCheck, bypassPluginExecution), cancellationToken).ConfigureAwait(false);
            }

        }

        /// <summary>
        /// Executes a Dataverse Create Request and returns the organization response object.
        /// </summary>
        /// <param name="req">Request to run</param>
        /// <param name="errorStringCheck">Formatted Error string</param>
        /// <param name="bypassPluginExecution">Adds the bypass plugin behavior to this request. Note: this will only apply if the caller has the prvBypassPlugins permission to bypass plugins.  If its attempted without the permission the request will fault.</param>
        /// <param name="cancellationToken"></param>
        /// <returns>Result of create request or null.</returns>
        internal async Task<OrganizationResponse> Command_ExecuteAsyncImpl(OrganizationRequest req, string errorStringCheck, System.Threading.CancellationToken cancellationToken, bool bypassPluginExecution = false)
        {
            Guid requestTrackingId = Guid.NewGuid();
            OrganizationResponse resp = null;
            Stopwatch logDt = new Stopwatch();
            TimeSpan LockWait = TimeSpan.Zero;
            int retryCount = 0;
            bool retry = false;

            do
            {
                try
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    _retryPauseTimeRunning = _configuration.Value.RetryPauseTime;
                    retry = false;
                    if (!_disableConnectionLocking)
                        if (_lockObject == null)
                            _lockObject = new object();

                    if (_connectionSvc != null && _connectionSvc.AuthenticationTypeInUse == AuthenticationType.OAuth)
                        _connectionSvc.CalledbyExecuteRequest = true;
                    OrganizationResponse rsp = null;

                    // Check to see if a Tracking ID has allready been assigned,
                    if (!req.RequestId.HasValue || (req.RequestId.HasValue && req.RequestId.Value == Guid.Empty))
                    {
                        // Assign Tracking ID
                        req.RequestId = requestTrackingId;
                    }
                    else
                    {
                        // assign request Id to the tracking id.
                        requestTrackingId = req.RequestId.Value;
                    }

                    // if request should bypass plugin exec.
                    if (bypassPluginExecution && Utilities.FeatureVersionMinimums.IsFeatureValidForEnviroment(_connectionSvc?.OrganizationVersion, Utilities.FeatureVersionMinimums.AllowBypassCustomPlugin))
                        req.Parameters.Add(Utilities.RequestHeaders.BYPASSCUSTOMPLUGINEXECUTION, true);

                    _logEntry.Log(string.Format(CultureInfo.InvariantCulture, "Execute Command - {0}{1}: RequestID={2} {3}",
                        req.RequestName,
                        string.IsNullOrEmpty(errorStringCheck) ? "" : $" : {errorStringCheck} ",
                        requestTrackingId.ToString(),
                        SessionTrackingId.HasValue && SessionTrackingId.Value != Guid.Empty ? $"SessionID={SessionTrackingId.Value.ToString()} : " : ""
                        ), TraceEventType.Verbose);

                    logDt.Restart();
                    rsp = await DataverseServiceAsync.ExecuteAsync(req);

                    logDt.Stop();
                    _logEntry.Log(string.Format(CultureInfo.InvariantCulture, "Executed Command - {0}{2}: {5}RequestID={3} {4}: duration={1}",
                        req.RequestName,
                        logDt.Elapsed.ToString(),
                        string.IsNullOrEmpty(errorStringCheck) ? "" : $" : {errorStringCheck} ",
                        requestTrackingId.ToString(),
                        LockWait == TimeSpan.Zero ? string.Empty : string.Format(": LockWaitDuration={0} ", LockWait.ToString()),
                        SessionTrackingId.HasValue && SessionTrackingId.Value != Guid.Empty ? $"SessionID={SessionTrackingId.Value.ToString()} : " : ""
                        ), TraceEventType.Verbose);
                    resp = rsp;
                }
                catch (Exception ex)
                {
                    bool isThrottled = false;
                    retry = ShouldRetry(req, ex, retryCount, out isThrottled);
                    if (retry)
                    {
                        Utilities.RetryRequest(req, requestTrackingId, LockWait, logDt, _logEntry, SessionTrackingId, _disableConnectionLocking, _retryPauseTimeRunning, ex, errorStringCheck, ref retryCount, isThrottled);
                    }
                    else
                    {
                        _logEntry.LogRetry(retryCount, req, _retryPauseTimeRunning, true, isThrottled: isThrottled);
                        _logEntry.LogException(req, ex, errorStringCheck);
                        //keep it in end so that LastError could be a better message.
                        _logEntry.LogFailure(req, requestTrackingId, SessionTrackingId, _disableConnectionLocking, LockWait, logDt, ex, errorStringCheck, true);
                    }
                    resp = null;
                }
                finally
                {
                    logDt.Stop();
                }
            } while (retry);

            return resp;
        }

        /// <summary>
        /// Executes a Dataverse Create Request and returns the organization response object.
        /// </summary>
        /// <param name="req">Request to run</param>
        /// <param name="errorStringCheck">Formatted Error string</param>
        /// <param name="bypassPluginExecution">Adds the bypass plugin behavior to this request. Note: this will only apply if the caller has the prvBypassPlugins permission to bypass plugins.  If its attempted without the permission the request will fault.</param>
        /// <returns>Result of create request or null.</returns>
        internal OrganizationResponse Command_Execute(OrganizationRequest req, string errorStringCheck, bool bypassPluginExecution = false)
        {
            Guid requestTrackingId = Guid.NewGuid();
            OrganizationResponse resp = null;
            Stopwatch logDt = new Stopwatch();
            TimeSpan LockWait = TimeSpan.Zero;
            int retryCount = 0;
            bool retry = false;

            do
            {
                try
                {
                    _retryPauseTimeRunning = _configuration.Value.RetryPauseTime; // Set the default time for each loop.
                    retry = false;
                    if (!_disableConnectionLocking)
                        if (_lockObject == null)
                            _lockObject = new object();

                    if (_connectionSvc != null && _connectionSvc.AuthenticationTypeInUse == AuthenticationType.OAuth)
                        _connectionSvc.CalledbyExecuteRequest = true;
                    OrganizationResponse rsp = null;

                    // Check to see if a Tracking ID has allready been assigned,
                    if (!req.RequestId.HasValue || (req.RequestId.HasValue && req.RequestId.Value == Guid.Empty))
                    {
                        // Assign Tracking ID
                        req.RequestId = requestTrackingId;
                    }
                    else
                    {
                        // assign request Id to the tracking id.
                        requestTrackingId = req.RequestId.Value;
                    }

                    // if request should bypass plugin exec.
                    if (bypassPluginExecution && Utilities.FeatureVersionMinimums.IsFeatureValidForEnviroment(_connectionSvc?.OrganizationVersion, Utilities.FeatureVersionMinimums.AllowBypassCustomPlugin))
                        req.Parameters.Add(Utilities.RequestHeaders.BYPASSCUSTOMPLUGINEXECUTION, true);

                    _logEntry.Log(string.Format(CultureInfo.InvariantCulture, "Execute Command - {0}{1}: RequestID={2} {3}",
                        req.RequestName,
                        string.IsNullOrEmpty(errorStringCheck) ? "" : $" : {errorStringCheck} ",
                        requestTrackingId.ToString(),
                        SessionTrackingId.HasValue && SessionTrackingId.Value != Guid.Empty ? $"SessionID={SessionTrackingId.Value.ToString()} : " : ""
                        ), TraceEventType.Verbose);

                    logDt.Restart();
                    if (!_disableConnectionLocking) // Allow Developer to override Cross Thread Safeties
                        lock (_lockObject)
                        {
                            if (logDt.Elapsed > TimeSpan.FromMilliseconds(0000010))
                                LockWait = logDt.Elapsed;
                            logDt.Restart();
                            rsp = DataverseService.Execute(req);
                        }
                    else
                        rsp = DataverseService.Execute(req);

                    logDt.Stop();
                    _logEntry.Log(string.Format(CultureInfo.InvariantCulture, "Executed Command - {0}{2}: {5}RequestID={3} {4}: duration={1}",
                        req.RequestName,
                        logDt.Elapsed.ToString(),
                        string.IsNullOrEmpty(errorStringCheck) ? "" : $" : {errorStringCheck} ",
                        requestTrackingId.ToString(),
                        LockWait == TimeSpan.Zero ? string.Empty : string.Format(": LockWaitDuration={0} ", LockWait.ToString()),
                        SessionTrackingId.HasValue && SessionTrackingId.Value != Guid.Empty ? $"SessionID={SessionTrackingId.Value.ToString()} : " : ""
                        ), TraceEventType.Verbose);
                    resp = rsp;
                }
                catch (Exception ex)
                {
                    bool isThrottled = false;
                    retry = ShouldRetry(req, ex, retryCount, out isThrottled);
                    if (retry)
                    {
                        Utilities.RetryRequest(req, requestTrackingId, LockWait, logDt, _logEntry, SessionTrackingId, _disableConnectionLocking, _retryPauseTimeRunning, ex, errorStringCheck, ref retryCount, isThrottled);
                    }
                    else
                    {
                        _logEntry.LogRetry(retryCount, req, _retryPauseTimeRunning, true, isThrottled: isThrottled);
                        _logEntry.LogException(req, ex, errorStringCheck);
                        //keep it in end so that LastError could be a better message.
                        _logEntry.LogFailure(req, requestTrackingId, SessionTrackingId, _disableConnectionLocking, LockWait, logDt, ex, errorStringCheck, true);
                    }
                    resp = null;
                }
                finally
                {
                    logDt.Stop();
                }
            } while (retry);

            return resp;
        }

        /// <summary>
        /// retry request or not
        /// </summary>
        /// <param name="req">req</param>
        /// <param name="ex">exception</param>
        /// <param name="retryCount">retry count</param>
        /// <param name="isThrottlingRetry">when true, indicates that the retry was caused by a throttle tripping.</param>
        /// <returns></returns>
        private bool ShouldRetry(OrganizationRequest req, Exception ex, int retryCount, out bool isThrottlingRetry)
        {
            isThrottlingRetry = false;
            if (retryCount >= _configuration.Value.MaxRetryCount)
                return false;
            else if (((string.Equals(req.RequestName.ToLower(), "retrieve"))
                && ((Utilities.ShouldAutoRetryRetrieveByEntityName(((Microsoft.Xrm.Sdk.EntityReference)req.Parameters["Target"]).LogicalName))))
                || (string.Equals(req.RequestName.ToLower(), "retrievemultiple")
                && (
                        ((((RetrieveMultipleRequest)req).Query is FetchExpression) && Utilities.ShouldAutoRetryRetrieveByEntityName(((FetchExpression)((RetrieveMultipleRequest)req).Query).Query))
                    || ((((RetrieveMultipleRequest)req).Query is QueryExpression) && Utilities.ShouldAutoRetryRetrieveByEntityName(((QueryExpression)((RetrieveMultipleRequest)req).Query).EntityName))
                    )))
                return true;
            else if ((ex.HResult == -2147204784 || ex.HResult == -2146233087) && ex.Message.Contains("SQL"))
                return true;
            else if (ex.Message.ToLowerInvariant().Contains("(502) bad gateway"))
                return true;
            else if (ex.Message.ToLowerInvariant().Contains("(503) service unavailable"))
            {
                _retryPauseTimeRunning = _configuration.Value.RetryPauseTime;
                isThrottlingRetry = true;
                return true;
            }
            else if (ex is FaultException<OrganizationServiceFault>)
            {
                var OrgEx = (FaultException<OrganizationServiceFault>)ex;
                if (OrgEx.Detail.ErrorCode == ErrorCodes.ThrottlingBurstRequestLimitExceededError ||
                    OrgEx.Detail.ErrorCode == ErrorCodes.ThrottlingTimeExceededError ||
                    OrgEx.Detail.ErrorCode == ErrorCodes.ThrottlingConcurrencyLimitExceededError)
                {
                    // Error was raised by a instance throttle trigger.
                    if (OrgEx.Detail.ErrorCode == ErrorCodes.ThrottlingBurstRequestLimitExceededError)
                    {
                        // Use Retry-After delay when specified
                        _retryPauseTimeRunning = (TimeSpan)OrgEx.Detail.ErrorDetails["Retry-After"];
                    }
                    else
                    {
                        // else use exponential back off delay
                        _retryPauseTimeRunning = _configuration.Value.RetryPauseTime.Add(TimeSpan.FromSeconds(Math.Pow(2, retryCount)));
                    }
                    isThrottlingRetry = true;
                    return true;
                }
                else
                    return false;
            }
            else
                return false;
        }

        #endregion

        #endregion

        #region Support classes

        /// <summary>
        /// PickList data
        /// </summary>
        public sealed class PickListMetaElement
        {
            /// <summary>
            /// Current value of the PickList Item
            /// </summary>
            public string ActualValue { get; set; }
            /// <summary>
            /// Displayed Label
            /// </summary>
            public string PickListLabel { get; set; }
            /// <summary>
            /// Displayed value for the PickList
            /// </summary>
            public string DisplayValue { get; set; }
            /// <summary>
            /// Array of Potential Pick List Items.
            /// </summary>
            public List<PickListItem> Items { get; set; }

            /// <summary>
            /// Default Constructor
            /// </summary>
            public PickListMetaElement()
            {
                Items = new List<PickListItem>();
            }

            /// <summary>
            /// Constructs a PickList item with data.
            /// </summary>
            /// <param name="actualValue"></param>
            /// <param name="displayValue"></param>
            /// <param name="pickListLabel"></param>
            public PickListMetaElement(string actualValue, string displayValue, string pickListLabel)
            {
                Items = new List<PickListItem>();
                ActualValue = actualValue;
                PickListLabel = pickListLabel;
                DisplayValue = displayValue;
            }
        }

        /// <summary>
        /// PickList Item
        /// </summary>
        public sealed class PickListItem
        {
            /// <summary>
            /// Display label for the PickList Item
            /// </summary>
            public string DisplayLabel { get; set; }
            /// <summary>
            /// ID of the picklist item
            /// </summary>
            public int PickListItemId { get; set; }

            /// <summary>
            /// Default Constructor
            /// </summary>
            public PickListItem()
            {
            }

            /// <summary>
            /// Constructor with data.
            /// </summary>
            /// <param name="label"></param>
            /// <param name="id"></param>
            public PickListItem(string label, int id)
            {
                DisplayLabel = label;
                PickListItemId = id;
            }
        }

        /// <summary>
        /// Dataverse Filter class.
        /// </summary>
        public sealed class DataverseSearchFilter
        {
            /// <summary>
            /// List of Dataverse Filter conditions
            /// </summary>
            public List<DataverseFilterConditionItem> SearchConditions { get; set; }
            /// <summary>
            /// Dataverse Filter Operator
            /// </summary>
            public LogicalOperator FilterOperator { get; set; }

            /// <summary>
            /// Creates an empty Dataverse Search Filter.
            /// </summary>
            public DataverseSearchFilter()
            {
                SearchConditions = new List<DataverseFilterConditionItem>();
            }
        }

        /// <summary>
        /// Dataverse Filter item.
        /// </summary>
        public sealed class DataverseFilterConditionItem
        {
            /// <summary>
            /// Dataverse Field name to Filter on
            /// </summary>
            public string FieldName { get; set; }
            /// <summary>
            /// Value to use for the Filter
            /// </summary>
            public object FieldValue { get; set; }
            /// <summary>
            /// Dataverse Operator to apply
            /// </summary>
            public ConditionOperator FieldOperator { get; set; }

        }

        /// <summary>
        /// Describes an import request for Dataverse
        /// </summary>
        public sealed class ImportRequest
        {
            #region Vars
            // Import Items..
            /// <summary>
            /// Name of the Import Request.  this Name will appear in Dataverse
            /// </summary>
            public string ImportName { get; set; }
            /// <summary>
            /// Sets or gets the Import Mode.
            /// </summary>
            public ImportMode Mode { get; set; }

            // Import Map Items.
            /// <summary>
            /// ID of the DataMap to use
            /// </summary>
            public Guid DataMapFileId { get; set; }
            /// <summary>
            /// Name of the DataMap File to use
            /// ID or Name is required
            /// </summary>
            public string DataMapFileName { get; set; }

            /// <summary>
            /// if True, infers the map from the type of entity requested..
            /// </summary>
            public bool UseSystemMap { get; set; }

            /// <summary>
            /// List of files to import in this job,  there must be at least one.
            /// </summary>
            public List<ImportFileItem> Files { get; set; }


            #endregion

            /// <summary>
            /// Mode of the Import, Update or Create
            /// </summary>
            public enum ImportMode
            {
                /// <summary>
                /// Create a new Import
                /// </summary>
                Create = 0,
                /// <summary>
                /// Update to Imported Items
                /// </summary>
                Update = 1
            }

            /// <summary>
            /// Default constructor
            /// </summary>
            public ImportRequest()
            {
                Files = new List<ImportFileItem>();
            }

        }

        /// <summary>
        /// Describes an Individual Import Item.
        /// </summary>
        public class ImportFileItem
        {
            /// <summary>
            /// File Name of Individual file
            /// </summary>
            public string FileName { get; set; }
            /// <summary>
            /// Type of Import file.. XML or CSV
            /// </summary>
            public FileTypeCode FileType { get; set; }
            /// <summary>
            /// This is the CSV file you wish to import,
            /// </summary>
            public string FileContentToImport { get; set; }
            /// <summary>
            /// This enabled duplicate detection rules
            /// </summary>
            public bool EnableDuplicateDetection { get; set; }
            /// <summary>
            /// Name of the entity that Originated the data.
            /// </summary>
            public string SourceEntityName { get; set; }
            /// <summary>
            /// Name of the entity that Target Entity the data.
            /// </summary>
            public string TargetEntityName { get; set; }
            /// <summary>
            /// This is the delimiter for the Data,
            /// </summary>
            public DataDelimiterCode DataDelimiter { get; set; }
            /// <summary>
            /// this is the field separator
            /// </summary>
            public FieldDelimiterCode FieldDelimiter { get; set; }
            /// <summary>
            /// Is the first row of the CSV the RowHeader?
            /// </summary>
            public bool IsFirstRowHeader { get; set; }
            /// <summary>
            /// UserID or Team ID of the Record Owner ( from systemuser )
            /// </summary>
            public Guid RecordOwner { get; set; }
            /// <summary>
            /// Set true if the Record Owner is a Team
            /// </summary>
            public bool IsRecordOwnerATeam { get; set; }

            /// <summary>
            /// Key used to delimit data in the import file
            /// </summary>
            public enum DataDelimiterCode
            {
                /// <summary>
                /// Specifies "
                /// </summary>
                DoubleQuotes = 1,   // "
                /// <summary>
                /// Specifies no delimiter
                /// </summary>
                None = 2,           //
                /// <summary>
                /// Specifies '
                /// </summary>
                SingleQuote = 3     // '
            }

            /// <summary>
            /// Key used to delimit fields in the import file
            /// </summary>
            public enum FieldDelimiterCode
            {
                /// <summary>
                /// Specifies :
                /// </summary>
                Colon = 1,
                /// <summary>
                /// Specifies ,
                /// </summary>
                Comma = 2,
                /// <summary>
                /// Specifies '
                /// </summary>
                SingleQuote = 3
            }

            /// <summary>
            /// Type if file described in the FileContentToImport
            /// </summary>
            public enum FileTypeCode
            {
                /// <summary>
                /// CSV File Type
                /// </summary>
                CSV = 0,
                /// <summary>
                /// XML File type
                /// </summary>
                XML = 1
            }

        }

        /// <summary>
        /// Logical Search Pram to apply to over all search.
        /// </summary>
        public enum LogicalSearchOperator
        {
            /// <summary>
            /// Do not apply the Search Operator
            /// </summary>
            None = 0,
            /// <summary>
            /// Or Search
            /// </summary>
            Or = 1,
            /// <summary>
            /// And Search
            /// </summary>
            And = 2
        }

        /// <summary>
        /// Logical Search Pram to apply to over all search.
        /// </summary>
        public enum LogicalSortOrder
        {
            /// <summary>
            /// Sort in Ascending
            /// </summary>
            Ascending = 0,
            /// <summary>
            /// Sort in Descending
            /// </summary>
            Descending = 1,
        }

        /// <summary>
        /// Used with GetFormIdsForEntity Call
        /// </summary>
        public enum FormTypeId
        {
            /// <summary>
            /// Dashboard form
            /// </summary>
            Dashboard = 0,
            /// <summary>
            /// Appointment book, for service requests.
            /// </summary>
            AppointmentBook = 1,
            /// <summary>
            /// Main or default form
            /// </summary>
            Main = 2,
            //MiniCampaignBo = 3,  // Not used in 2011
            //Preview = 4,          // Not used in 2011
            /// <summary>
            /// Mobile default form
            /// </summary>
            Mobile = 5,
            /// <summary>
            /// User defined forms
            /// </summary>
            Other = 100
        }

        #endregion

        #region IOrganzation Service Proxy - Proxy object
        /// <summary>
        /// Issues an Associate Request to Dataverse.
        /// </summary>
        /// <param name="entityName">Entity Name to associate to</param>
        /// <param name="entityId">ID if Entity to associate to</param>
        /// <param name="relationship">Relationship Name</param>
        /// <param name="relatedEntities">Entities to associate</param>
        public void Associate(string entityName, Guid entityId, Relationship relationship, EntityReferenceCollection relatedEntities)
        {
            AssociateResponse resp = (AssociateResponse)ExecuteOrganizationRequestImpl(new AssociateRequest()
            {
                Target = new EntityReference(entityName, entityId),
                Relationship = relationship,
                RelatedEntities = relatedEntities
            }, "Associate To Dataverse via IOrganizationService");
            if (resp == null)
                throw LastException;
        }

        /// <summary>
        /// Issues a Create request to Dataverse
        /// </summary>
        /// <param name="entity">Entity to create</param>
        /// <returns>ID of newly created entity</returns>
        public Guid Create(Entity entity)
        {
            // Relay to Update request.
            CreateResponse resp = (CreateResponse)ExecuteOrganizationRequestImpl(
                new CreateRequest()
                {
                    Target = entity
                }
                , "Create To Dataverse via IOrganizationService"
                , useWebAPI: true);
            if (resp == null)
                throw LastException;

            return resp.id;
        }

        /// <summary>
        /// Issues a Delete request to Dataverse
        /// </summary>
        /// <param name="entityName">Entity name to delete</param>
        /// <param name="id">ID if entity to delete</param>
        public void Delete(string entityName, Guid id)
        {
            DeleteResponse resp = (DeleteResponse)ExecuteOrganizationRequestImpl(
                new DeleteRequest()
                {
                    Target = new EntityReference(entityName, id)
                }
                , "Delete Request to Dataverse via IOrganizationService"
                , useWebAPI: true);
            if (resp == null)
                throw LastException;
        }

        /// <summary>
        /// Issues a Disassociate Request to Dataverse.
        /// </summary>
        /// <param name="entityName">Entity Name to disassociate from</param>
        /// <param name="entityId">ID if Entity to disassociate from</param>
        /// <param name="relationship">Relationship Name</param>
        /// <param name="relatedEntities">Entities to disassociate</param>
        public void Disassociate(string entityName, Guid entityId, Relationship relationship, EntityReferenceCollection relatedEntities)
        {
            DisassociateResponse resp = (DisassociateResponse)ExecuteOrganizationRequestImpl(new DisassociateRequest()
            {
                Target = new EntityReference(entityName, entityId),
                Relationship = relationship,
                RelatedEntities = relatedEntities
            }, "Disassociate To Dataverse via IOrganizationService");
            if (resp == null)
                throw LastException;
        }

        /// <summary>
        /// Executes a general organization request
        /// </summary>
        /// <param name="request">Request object</param>
        /// <returns>Response object</returns>
        public OrganizationResponse Execute(OrganizationRequest request)
        {
            OrganizationResponse resp = ExecuteOrganizationRequestImpl(request, string.Format("Execute ({0}) request to Dataverse from IOrganizationService", request.RequestName), useWebAPI: true);
            if (resp == null)
                throw LastException;
            return resp;
        }

        /// <summary>
        /// Issues a Retrieve Request to Dataverse
        /// </summary>
        /// <param name="entityName">Entity name to request</param>
        /// <param name="id">ID of the entity to request</param>
        /// <param name="columnSet">ColumnSet to request</param>
        /// <returns>Entity object</returns>
        public Entity Retrieve(string entityName, Guid id, ColumnSet columnSet)
        {
            RetrieveResponse resp = (RetrieveResponse)ExecuteOrganizationRequestImpl(
                new RetrieveRequest()
                {
                    ColumnSet = columnSet,
                    Target = new EntityReference(entityName, id)
                }
                , "Retrieve Request to Dataverse via IOrganizationService");
            if (resp == null)
                throw LastException;

            return resp.Entity;
        }

        /// <summary>
        /// Issues a RetrieveMultiple Request to Dataverse
        /// </summary>
        /// <param name="query">Query to Request</param>
        /// <returns>EntityCollection Result</returns>
        public EntityCollection RetrieveMultiple(QueryBase query)
        {
            RetrieveMultipleResponse resp = (RetrieveMultipleResponse)ExecuteOrganizationRequestImpl(new RetrieveMultipleRequest() { Query = query }, "RetrieveMultiple to Dataverse via IOrganizationService");
            if (resp == null)
                throw LastException;

            return resp.EntityCollection;
        }

        /// <summary>
        /// Issues an update to Dataverse.
        /// </summary>
        /// <param name="entity">Entity to update into Dataverse</param>
        public void Update(Entity entity)
        {
            // Relay to Update request.
            UpdateResponse resp = (UpdateResponse)ExecuteOrganizationRequestImpl(
                new UpdateRequest()
                {
                    Target = entity
                }
                , "UpdateRequest To Dataverse via IOrganizationService"
                , useWebAPI: true);

            if (resp == null)
                throw LastException;
        }

        #endregion

        #region IOrganzationServiceAsync helper - Proxy object
        /// <summary>
        /// Associate an entity with a set of entities
        /// </summary>
        /// <param name="entityName"></param>
        /// <param name="entityId"></param>
        /// <param name="relationship"></param>
        /// <param name="relatedEntities"></param>
        public async Task AssociateAsync(string entityName, Guid entityId, Relationship relationship, EntityReferenceCollection relatedEntities)
        {
            await AssociateAsync(entityName, entityId, relationship, relatedEntities, CancellationToken.None).ConfigureAwait(false);
            return;
        }

        /// <summary>
        /// Create an entity and process any related entities
        /// </summary>
        /// <param name="entity">entity to create</param>
        /// <returns>The ID of the created record</returns>
        public async Task<Guid> CreateAsync(Entity entity)
        {
            return await CreateAsync(entity, CancellationToken.None).ConfigureAwait(false);
        }

        /// <summary>
        /// Delete instance of an entity
        /// </summary>
        /// <param name="entityName">Logical name of entity</param>
        /// <param name="id">Id of entity</param>
        public async Task DeleteAsync(string entityName, Guid id)
        {
            await DeleteAsync(entityName, id, CancellationToken.None).ConfigureAwait(false);
            return;
        }

        /// <summary>
        /// Disassociate an entity with a set of entities
        /// </summary>
        /// <param name="entityName"></param>
        /// <param name="entityId"></param>
        /// <param name="relationship"></param>
        /// <param name="relatedEntities"></param>
        public async Task DisassociateAsync(string entityName, Guid entityId, Relationship relationship, EntityReferenceCollection relatedEntities)
        {
            await DisassociateAsync(entityName, entityId, relationship, relatedEntities, CancellationToken.None).ConfigureAwait(false);
            return;
        }

        /// <summary>
        /// Perform an action in an organization specified by the request.
        /// </summary>
        /// <param name="request">Refer to SDK documentation for list of messages that can be used.</param>
        /// <returns>Results from processing the request</returns>
        public async Task<OrganizationResponse> ExecuteAsync(OrganizationRequest request)
        {
            return await ExecuteAsync(request, CancellationToken.None).ConfigureAwait(false);
        }

        /// <summary>
        /// Retrieves instance of an entity
        /// </summary>
        /// <param name="entityName">Logical name of entity</param>
        /// <param name="id">Id of entity</param>
        /// <param name="columnSet">Column Set collection to return with the request</param>
        /// <returns>Selected Entity</returns>
        public async Task<Entity> RetrieveAsync(string entityName, Guid id, ColumnSet columnSet)
        {
            return await RetrieveAsync(entityName, id, columnSet, CancellationToken.None).ConfigureAwait(false);
        }

        /// <summary>
        /// Retrieves a collection of entities
        /// </summary>
        /// <param name="query"></param>
        /// <returns>Returns an EntityCollection Object containing the results of the query</returns>
        public async Task<EntityCollection> RetrieveMultipleAsync(QueryBase query)
        {
            return await RetrieveMultipleAsync(query, CancellationToken.None).ConfigureAwait(false);
        }

        /// <summary>
        /// Updates an entity and process any related entities
        /// </summary>
        /// <param name="entity">entity to update</param>
        public async Task UpdateAsync(Entity entity)
        {
            await UpdateAsync(entity, CancellationToken.None).ConfigureAwait(false);
            return;
        }


        #endregion

        #region IOrganzationServiceAsync2 helper - Proxy object

        /// <summary>
        /// Associate an entity with a set of entities
        /// </summary>
        /// <param name="entityName"></param>
        /// <param name="entityId"></param>
        /// <param name="relationship"></param>
        /// <param name="relatedEntities"></param>
        /// <param name="cancellationToken">Propagates notification that operations should be canceled.</param>
        public async Task AssociateAsync(string entityName, Guid entityId, Relationship relationship, EntityReferenceCollection relatedEntities, CancellationToken cancellationToken)
        {
            AssociateResponse resp = (AssociateResponse)await ExecuteOrganizationRequestAsyncImpl(new AssociateRequest()
            {
                Target = new EntityReference(entityName, entityId),
                Relationship = relationship,
                RelatedEntities = relatedEntities
            }
            , cancellationToken
            , "Associate To Dataverse via IOrganizationService").ConfigureAwait(false);
            if (resp == null)
                throw LastException;
        }

        /// <summary>
        /// Create an entity and process any related entities
        /// </summary>
        /// <param name="entity">entity to create</param>
        /// <param name="cancellationToken">Propagates notification that operations should be canceled.</param>
        /// <returns>The ID of the created record</returns>
        public async Task<Guid> CreateAsync(Entity entity, CancellationToken cancellationToken)
        {
            // Relay to Update request.
            CreateResponse resp = (CreateResponse)await ExecuteOrganizationRequestAsyncImpl(
                new CreateRequest()
                {
                    Target = entity
                }
                , cancellationToken
                , "Create To Dataverse via IOrganizationService"
                , useWebAPI: true).ConfigureAwait(false);
            if (resp == null)
                throw LastException;

            return resp.id;
        }

        /// <summary>
        /// Create an entity and process any related entities
        /// </summary>
        /// <param name="entity">entity to create</param>
        /// <param name="cancellationToken">Propagates notification that operations should be canceled.</param>
        /// <returns>Returns the newly created record</returns>
        public Task<Entity> CreateAndReturnAsync(Entity entity, CancellationToken cancellationToken)
        {
            throw new NotImplementedException();
        }
        /// <summary>
        /// Create an entity and process any related entities
        /// </summary>
        /// <param name="entity">entity to create</param>
        /// <returns>Returns the newly created record</returns>
        public Task<Entity> CreateAndReturnAsync(Entity entity)
        {
            throw new NotImplementedException();
        }

        /// <summary>
        /// Delete instance of an entity
        /// </summary>
        /// <param name="entityName">Logical name of entity</param>
        /// <param name="id">Id of entity</param>
        /// <param name="cancellationToken">Propagates notification that operations should be canceled.</param>
        public async Task DeleteAsync(string entityName, Guid id, CancellationToken cancellationToken)
        {
            DeleteResponse resp = (DeleteResponse)await ExecuteOrganizationRequestAsyncImpl(
               new DeleteRequest()
               {
                   Target = new EntityReference(entityName, id)
               }
               , cancellationToken
               , "Delete Request to Dataverse via IOrganizationService"
               , useWebAPI: true).ConfigureAwait(false);
            if (resp == null)
                throw LastException;
        }

        /// <summary>
        /// Disassociate an entity with a set of entities
        /// </summary>
        /// <param name="entityName"></param>
        /// <param name="entityId"></param>
        /// <param name="relationship"></param>
        /// <param name="relatedEntities"></param>
        /// <param name="cancellationToken">Propagates notification that operations should be canceled.</param>
        public async Task DisassociateAsync(string entityName, Guid entityId, Relationship relationship, EntityReferenceCollection relatedEntities, CancellationToken cancellationToken)
        {
            DisassociateResponse resp = (DisassociateResponse)await ExecuteOrganizationRequestAsyncImpl(new DisassociateRequest()
            {
                Target = new EntityReference(entityName, entityId),
                Relationship = relationship,
                RelatedEntities = relatedEntities
            }
            , cancellationToken
            , "Disassociate To Dataverse via IOrganizationService").ConfigureAwait(false);
            if (resp == null)
                throw LastException;
        }

        /// <summary>
        /// Perform an action in an organization specified by the request.
        /// </summary>
        /// <param name="request">Refer to SDK documentation for list of messages that can be used.</param>
        /// <param name="cancellationToken">Propagates notification that operations should be canceled.</param>
        /// <returns>Results from processing the request</returns>
        public async Task<OrganizationResponse> ExecuteAsync(OrganizationRequest request, CancellationToken cancellationToken)
        {
            OrganizationResponse resp = await ExecuteOrganizationRequestAsyncImpl(request
                , cancellationToken
                , string.Format("Execute ({0}) request to Dataverse from IOrganizationService", request.RequestName)
                , useWebAPI: true).ConfigureAwait(false);
            if (resp == null)
                throw LastException;
            return resp;
        }

        /// <summary>
        /// Retrieves instance of an entity
        /// </summary>
        /// <param name="entityName">Logical name of entity</param>
        /// <param name="id">Id of entity</param>
        /// <param name="columnSet">Column Set collection to return with the request</param>
        /// <param name="cancellationToken">Propagates notification that operations should be canceled.</param>
        /// <returns>Selected Entity</returns>
        public async Task<Entity> RetrieveAsync(string entityName, Guid id, ColumnSet columnSet, CancellationToken cancellationToken)
        {
            RetrieveResponse resp = (RetrieveResponse)await ExecuteOrganizationRequestAsyncImpl(
            new RetrieveRequest()
            {
                ColumnSet = columnSet,
                Target = new EntityReference(entityName, id)
            }
            , cancellationToken
            , "Retrieve Request to Dataverse via IOrganizationService").ConfigureAwait(false);
            if (resp == null)
                throw LastException;

            return resp.Entity;
        }

        /// <summary>
        /// Retrieves a collection of entities
        /// </summary>
        /// <param name="query"></param>
        /// <param name="cancellationToken">Propagates notification that operations should be canceled.</param>
        /// <returns>Returns an EntityCollection Object containing the results of the query</returns>
        public async Task<EntityCollection> RetrieveMultipleAsync(QueryBase query, CancellationToken cancellationToken)
        {
            RetrieveMultipleResponse resp = (RetrieveMultipleResponse)await ExecuteOrganizationRequestAsyncImpl(new RetrieveMultipleRequest() { Query = query }, cancellationToken, "RetrieveMultiple to Dataverse via IOrganizationService").ConfigureAwait(false);
            if (resp == null)
                throw LastException;

            return resp.EntityCollection;
        }

        /// <summary>
        /// Updates an entity and process any related entities
        /// </summary>
        /// <param name="entity">entity to update</param>
        /// <param name="cancellationToken">Propagates notification that operations should be canceled.</param>
        public async Task UpdateAsync(Entity entity, CancellationToken cancellationToken)
        {
            // Relay to Update request.
            UpdateResponse resp = (UpdateResponse)await ExecuteOrganizationRequestAsyncImpl(
                new UpdateRequest()
                {
                    Target = entity
                }
                , cancellationToken
                , "UpdateRequest To Dataverse via IOrganizationService"
                , useWebAPI: true).ConfigureAwait(false);

            if (resp == null)
                throw LastException;
        }

        #endregion

        #region IDisposable Support
        private bool disposedValue = false; // To detect redundant calls

        private void Dispose(bool disposing)
        {
            if (!disposedValue)
            {
                if (disposing)
                {
                    //if (_CdsServiceClientTokenCache != null)
                    //    _CdsServiceClientTokenCache.Dispose();


                    if (_logEntry != null)
                    {
                        _logEntry.Dispose();
                    }

                    if (_connectionSvc != null)
                    {
                        try
                        {
                            if (_connectionSvc.WebClient != null)
                                _connectionSvc.WebClient.Dispose();
                        }
                        catch { }
                        _connectionSvc.Dispose();
                    }

                    _connectionSvc = null;

                }
                disposedValue = true;
            }
        }


        /// <summary>
        /// Disposed the resources used by the ServiceClient.
        /// </summary>
        public void Dispose()
        {
            Dispose(true);
        }
        #endregion
    }
}
