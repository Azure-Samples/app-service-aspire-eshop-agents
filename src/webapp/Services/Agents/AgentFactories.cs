using Azure.AI.Agents.Persistent;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using dotnetfashionassistant.Config;

namespace dotnetfashionassistant.Services.Agents
{
    /// <summary>
    /// Base factory class for creating AI agents in the fashion store application.
    /// This provides common functionality and makes agent creation consistent.
    /// 
    /// HANDS-ON DEMO GUIDE:
    /// - All agent factories inherit from this base class
    /// - Common configuration and error handling is centralized here
    /// - Easy to extend for new agent types
    /// </summary>
    public abstract class BaseAgentFactory
    {
        protected readonly PersistentAgentsClient _agentsClient;
        protected readonly IConfiguration _configuration;
        protected readonly ILogger _logger;
        protected readonly string _modelDeploymentName;

        protected BaseAgentFactory(
            PersistentAgentsClient agentsClient,
            IConfiguration configuration,
            ILogger logger)
        {
            _agentsClient = agentsClient;
            _configuration = configuration;
            _logger = logger;
            _modelDeploymentName = _configuration["AI_MODEL_DEPLOYMENT_NAME"] ?? 
                                  Environment.GetEnvironmentVariable("AI_MODEL_DEPLOYMENT_NAME") ?? 
                                  "gpt-4o";
        }

        /// <summary>
        /// Abstract method that each agent factory must implement to create their specific agent type.
        /// </summary>
        public abstract Task<PersistentAgent?> CreateAgentAsync();

        /// <summary>
        /// Helper method to safely create any agent with error handling.
        /// </summary>
        protected async Task<PersistentAgent?> CreateAgentWithErrorHandlingAsync(
            string name, 
            string description, 
            string instructions, 
            IEnumerable<ToolDefinition>? tools = null)
        {
            try
            {
                var response = await _agentsClient.Administration.CreateAgentAsync(
                    model: _modelDeploymentName,
                    name: name,
                    description: description,
                    instructions: instructions,
                    tools: tools);

                _logger.LogInformation("Successfully created agent: {AgentName} (ID: {AgentId})", name, response.Value.Id);
                return response.Value;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to create agent: {AgentName}", name);
                return null;
            }
        }
    }

    /// <summary>
    /// Factory for creating the main orchestrator agent.
    /// The orchestrator coordinates all other agents but has no tools itself.
    /// 
    /// DEMO NOTE: This is the "brain" of the multi-agent system!
    /// </summary>
    public class OrchestratorAgentFactory : BaseAgentFactory
    {
        public OrchestratorAgentFactory(
            PersistentAgentsClient agentsClient,
            IConfiguration configuration,
            ILogger<OrchestratorAgentFactory> logger)
            : base(agentsClient, configuration, logger)
        {
        }

        /// <summary>
        /// Creates the orchestrator agent with connected agent tools.
        /// This agent will coordinate all specialist agents.
        /// </summary>
        public async Task<PersistentAgent?> CreateOrchestratorWithConnectedAgentsAsync(
            PersistentAgent cartManagerAgent,
            PersistentAgent fashionAdvisorAgent,
            PersistentAgent contentModeratorAgent)
        {
            try
            {
                // Create connected agent tool definitions
                var connectedAgentTools = new List<ToolDefinition>
                {
                    new ConnectedAgentToolDefinition(new ConnectedAgentDetails(
                        cartManagerAgent.Id,
                        "cart_manager",
                        AgentDefinitions.CartManager.ConnectedAgentDescription)),
                    
                    new ConnectedAgentToolDefinition(new ConnectedAgentDetails(
                        fashionAdvisorAgent.Id,
                        "fashion_advisor", 
                        AgentDefinitions.FashionAdvisor.ConnectedAgentDescription)),
                    
                    new ConnectedAgentToolDefinition(new ConnectedAgentDetails(
                        contentModeratorAgent.Id,
                        "content_moderator",
                        AgentDefinitions.ContentModerator.ConnectedAgentDescription))
                };

                var response = await _agentsClient.Administration.CreateAgentAsync(
                    model: _modelDeploymentName,
                    name: AgentDefinitions.Orchestrator.Name,
                    description: AgentDefinitions.Orchestrator.Description,
                    instructions: AgentDefinitions.Orchestrator.Instructions,
                    tools: connectedAgentTools);

                _logger.LogInformation("Successfully created orchestrator agent with {ConnectedAgentCount} connected agents", 
                    connectedAgentTools.Count);
                return response.Value;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to create orchestrator agent");
                return null;
            }
        }

        public override async Task<PersistentAgent?> CreateAgentAsync()
        {
            // This method creates the orchestrator without connected agents
            // Use CreateOrchestratorWithConnectedAgentsAsync for the full setup
            return await CreateAgentWithErrorHandlingAsync(
                AgentDefinitions.Orchestrator.Name,
                AgentDefinitions.Orchestrator.Description,
                AgentDefinitions.Orchestrator.Instructions);
        }
    }

    /// <summary>
    /// Factory for creating the cart manager agent.
    /// This agent handles shopping cart operations using OpenAPI tools.
    /// 
    /// DEMO NOTE: This agent shows how to integrate with external APIs!
    /// </summary>
    public class CartManagerAgentFactory : BaseAgentFactory
    {
        public CartManagerAgentFactory(
            PersistentAgentsClient agentsClient,
            IConfiguration configuration,
            ILogger<CartManagerAgentFactory> logger)
            : base(agentsClient, configuration, logger)
        {
        }

        public override async Task<PersistentAgent?> CreateAgentAsync()
        {
            // MUST HAVE OpenAPI tool - fail if it can't be created
            var openApiTool = await CreateOpenApiToolAsync();
            if (openApiTool == null)
            {
                var errorMsg = "🛒 CART AGENT CREATION FAILED - OpenAPI tool could not be created. Check logs above for specific error.";
                _logger.LogError(errorMsg);
                throw new InvalidOperationException(errorMsg);
            }

            var tools = new[] { openApiTool };

            var agent = await CreateAgentWithErrorHandlingAsync(
                AgentDefinitions.CartManager.Name,
                AgentDefinitions.CartManager.Description,
                AgentDefinitions.CartManager.Instructions,
                tools);

            if (agent == null)
            {
                var errorMsg = "🛒 CART AGENT CREATION FAILED - Agent creation returned null";
                _logger.LogError(errorMsg);
                throw new InvalidOperationException(errorMsg);
            }

            _logger.LogInformation("🛒 CART AGENT CREATED SUCCESSFULLY with OpenAPI tools");
            return agent;
        }

        /// <summary>
        /// Creates the OpenAPI tool for cart operations.
        /// MUST succeed or throw exception with detailed error.
        /// </summary>
        private async Task<OpenApiToolDefinition?> CreateOpenApiToolAsync()
        {
            // Step 1: Get server URL
            var serverUrl = GetServerUrl();
            _logger.LogInformation("🛒 STEP 1 - Server URL: {ServerUrl}", serverUrl);

            // Step 2: Get swagger specification
            var swaggerJson = await GetUpdatedSwaggerSpecificationAsync(serverUrl);
            if (string.IsNullOrEmpty(swaggerJson))
            {
                var errorMsg = "🛒 STEP 2 FAILED - Could not load or update swagger specification";
                _logger.LogError(errorMsg);
                throw new InvalidOperationException(errorMsg);
            }
            _logger.LogInformation("🛒 STEP 2 SUCCESS - Swagger spec loaded, length: {Length}", swaggerJson.Length);

            // Step 3: Test API connectivity
            await TestCartApiAsync(serverUrl);

            // Step 4: Create OpenAPI tool
            try
            {
                var tool = new OpenApiToolDefinition(
                    name: "fashion_store_api",
                    description: "API for managing fashion store inventory and shopping cart operations",
                    spec: BinaryData.FromString(swaggerJson),
                    openApiAuthentication: new OpenApiAnonymousAuthDetails());

                _logger.LogInformation("🛒 STEP 4 SUCCESS - OpenAPI tool created successfully");
                return tool;
            }
            catch (Exception ex)
            {
                var errorMsg = $"🛒 STEP 4 FAILED - OpenAPI tool creation failed: {ex.Message}";
                _logger.LogError(ex, errorMsg);
                throw new InvalidOperationException(errorMsg, ex);
            }
        }

        /// <summary>
        /// Creates a minimal OpenAPI specification focused on cart operations as a fallback.
        /// </summary>
        private string CreateMinimalCartApiSpec(string serverUrl)
        {
            var minimalSpec = $$"""
            {
                "openapi": "3.0.4",
                "info": {
                    "title": "Fashion Store Cart API",
                    "description": "Basic cart operations for the fashion store",
                    "version": "v1"
                },
                "servers": [
                    {
                        "url": "{{serverUrl}}"
                    }
                ],
                "paths": {
                    "/api/Cart": {
                        "get": {
                            "operationId": "getCart",
                            "summary": "Get cart contents",
                            "responses": {
                                "200": {
                                    "description": "Cart contents",
                                    "content": {
                                        "application/json": {
                                            "schema": {
                                                "type": "object",
                                                "properties": {
                                                    "items": {
                                                        "type": "array",
                                                        "items": {
                                                            "type": "object"
                                                        }
                                                    },
                                                    "totalCost": {
                                                        "type": "number"
                                                    }
                                                }
                                            }
                                        }
                                    }
                                }
                            }
                        }
                    }
                }
            }
            """;

            _logger.LogInformation("Created minimal cart API specification as fallback");
            return minimalSpec;
        }

        /// <summary>
        /// Tests the cart API to ensure it's accessible.
        /// MUST succeed or throw exception.
        /// </summary>
        private async Task TestCartApiAsync(string serverUrl)
        {
            using var httpClient = new HttpClient();
            httpClient.Timeout = TimeSpan.FromSeconds(10);
            
            var cartUrl = $"{serverUrl}/api/Cart";
            _logger.LogInformation("🛒 CART DEBUG - Testing cart API at: {CartUrl}", cartUrl);
            
            var response = await httpClient.GetAsync(cartUrl);
            var responseContent = await response.Content.ReadAsStringAsync();
            
            if (response.IsSuccessStatusCode)
            {
                _logger.LogInformation("🛒 CART DEBUG - Cart API test successful: {StatusCode}, Response: {Response}", 
                    response.StatusCode, responseContent);
            }
            else
            {
                var errorMsg = $"🛒 CART API TEST FAILED - {response.StatusCode} - {response.ReasonPhrase}, Response: {responseContent}";
                _logger.LogError(errorMsg);
                throw new HttpRequestException(errorMsg);
            }
        }

        /// <summary>
        /// Gets the server URL for the OpenAPI specification.
        /// Tries multiple sources to find the correct URL.
        /// </summary>
        private string GetServerUrl()
        {
            // Log all potential URL sources for debugging
            var websiteHostname = _configuration["WEBSITE_HOSTNAME"] ?? Environment.GetEnvironmentVariable("WEBSITE_HOSTNAME");
            var configServerUrl = _configuration["ServerUrl"];
            var siteName = Environment.GetEnvironmentVariable("WEBSITE_SITE_NAME");
            
            _logger.LogInformation("🛒 CART DEBUG - URL Detection: WEBSITE_HOSTNAME: {WebsiteHostname}, ServerUrl: {ServerUrl}, WEBSITE_SITE_NAME: {SiteName}", 
                websiteHostname, configServerUrl, siteName);

            // Try to get from various configuration sources in order of preference
            var serverUrl = websiteHostname ?? 
                           configServerUrl ??
                           "localhost:5000"; // fallback for local development

            // For Azure App Service, we can also check for the common hostname pattern
            if (serverUrl == "localhost:5000" && !string.IsNullOrEmpty(siteName))
            {
                serverUrl = $"{siteName}.azurewebsites.net";
                _logger.LogInformation("🛒 CART DEBUG - Detected Azure App Service, using site name: {SiteName}", siteName);
            }

            // If we still don't have a proper URL, try to detect from the current request context
            if (serverUrl == "localhost:5000")
            {
                // Try to get the current app's URL from common Azure App Service environment variables
                var defaultHostname = Environment.GetEnvironmentVariable("WEBSITE_DEFAULT_HOSTNAME");
                if (!string.IsNullOrEmpty(defaultHostname))
                {
                    serverUrl = defaultHostname;
                    _logger.LogInformation("🛒 CART DEBUG - Using WEBSITE_DEFAULT_HOSTNAME: {DefaultHostname}", defaultHostname);
                }
            }

            // Ensure proper format
            if (!serverUrl.StartsWith("http"))
            {
                if (serverUrl.Contains("localhost"))
                {
                    serverUrl = $"http://{serverUrl}";
                }
                else
                {
                    serverUrl = $"https://{serverUrl}";
                }
            }

            _logger.LogInformation("🛒 CART DEBUG - Final server URL for OpenAPI tool: {ServerUrl}", serverUrl);
            return serverUrl;
        }

        /// <summary>
        /// Reads the swagger.json file and updates the server URL dynamically.
        /// MUST succeed or throw exception.
        /// </summary>
        private async Task<string> GetUpdatedSwaggerSpecificationAsync(string serverUrl)
        {
            var swaggerPath = Path.Combine(AppContext.BaseDirectory, "swagger.json");
            _logger.LogInformation("🛒 Looking for swagger.json at: {SwaggerPath}", swaggerPath);
            
            if (!File.Exists(swaggerPath))
            {
                // Try alternative locations
                var alternativePath = Path.Combine(Directory.GetCurrentDirectory(), "swagger.json");
                _logger.LogInformation("🛒 Trying alternative path: {AlternativePath}", alternativePath);
                
                if (File.Exists(alternativePath))
                {
                    swaggerPath = alternativePath;
                    _logger.LogInformation("🛒 Found swagger.json at alternative location");
                }
                else
                {
                    var errorMsg = $"🛒 SWAGGER FILE NOT FOUND - Checked: {swaggerPath} and {alternativePath}";
                    _logger.LogError(errorMsg);
                    throw new FileNotFoundException(errorMsg);
                }
            }

            var swaggerContent = await File.ReadAllTextAsync(swaggerPath);
            _logger.LogInformation("🛒 Read swagger.json file, length: {Length} characters", swaggerContent.Length);
            
            // Log the original content to see what we're working with
            var originalUrlMatch = swaggerContent.Contains("<APP-SERVICE-URL>");
            _logger.LogInformation("🛒 Swagger contains placeholder <APP-SERVICE-URL>: {HasPlaceholder}", originalUrlMatch);
            
            if (!originalUrlMatch)
            {
                _logger.LogWarning("🛒 Swagger file does not contain expected placeholder <APP-SERVICE-URL>");
            }
            
            // Replace the placeholder server URL with the actual server URL
            var updatedContent = swaggerContent.Replace("<APP-SERVICE-URL>", serverUrl);
            
            // Verify the replacement worked
            var replacementSuccessful = !updatedContent.Contains("<APP-SERVICE-URL>");
            _logger.LogInformation("🛒 URL replacement successful: {ReplacementSuccessful}, final server URL: {ServerUrl}", 
                replacementSuccessful, serverUrl);
            
            if (!replacementSuccessful && originalUrlMatch)
            {
                var errorMsg = "🛒 URL REPLACEMENT FAILED - Placeholder still exists after replacement";
                _logger.LogError(errorMsg);
                throw new InvalidOperationException(errorMsg);
            }
            
            return updatedContent;
        }
    }

    /// <summary>
    /// Factory for creating the fashion advisor agent.
    /// This agent provides fashion advice and has no tools - knowledge only.
    /// 
    /// DEMO NOTE: This agent shows pure AI reasoning without external tools!
    /// </summary>
    public class FashionAdvisorAgentFactory : BaseAgentFactory
    {
        public FashionAdvisorAgentFactory(
            PersistentAgentsClient agentsClient,
            IConfiguration configuration,
            ILogger<FashionAdvisorAgentFactory> logger)
            : base(agentsClient, configuration, logger)
        {
        }

        public override async Task<PersistentAgent?> CreateAgentAsync()
        {
            return await CreateAgentWithErrorHandlingAsync(
                AgentDefinitions.FashionAdvisor.Name,
                AgentDefinitions.FashionAdvisor.Description,
                AgentDefinitions.FashionAdvisor.Instructions);
        }
    }

    /// <summary>
    /// Factory for creating the content moderator agent.
    /// This agent validates content appropriateness and relevance.
    /// 
    /// DEMO NOTE: This agent shows how to implement safety and content filtering!
    /// </summary>
    public class ContentModeratorAgentFactory : BaseAgentFactory
    {
        public ContentModeratorAgentFactory(
            PersistentAgentsClient agentsClient,
            IConfiguration configuration,
            ILogger<ContentModeratorAgentFactory> logger)
            : base(agentsClient, configuration, logger)
        {
        }

        public override async Task<PersistentAgent?> CreateAgentAsync()
        {
            return await CreateAgentWithErrorHandlingAsync(
                AgentDefinitions.ContentModerator.Name,
                AgentDefinitions.ContentModerator.Description,
                AgentDefinitions.ContentModerator.Instructions);
        }
    }
}