using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.Graph;
using Microsoft.Graph.Models;
using Microsoft.Identity.Client;
using System.Linq;
using System.Text;
using System.Threading;
using Microsoft.Kiota.Abstractions.Authentication;
using Microsoft.Kiota.Abstractions;
using System.IO;

namespace AzureADAppRegistrationTool
{
    class Program
    {
        // Well-known client ID for Azure CLI (allows public client authentication)
        private static readonly string ClientId = "04b07795-8ddb-461a-bbee-02f9e1bf7b46";
        private static readonly string[] Scopes = { "https://graph.microsoft.com/.default" };
        private static readonly string Authority = "https://login.microsoftonline.com/common";

        static async Task Main(string[] args)
        {
            Console.WriteLine("=== Azure AD Application Registration Tool ===");
            Console.WriteLine("This tool will help you create Azure AD app registrations with required permissions.");
            Console.WriteLine();

            try
            {
                // Step 1: Interactive login
                Console.WriteLine("Step 1: Authenticating with Azure AD...");
                var authResult = await AuthenticateAsync();
                Console.WriteLine($"Successfully authenticated as: {authResult.Account.Username}");
                Console.WriteLine();

                // Step 2: Initialize Graph client
                var graphClient = CreateGraphClient(authResult.AccessToken);

                // Step 3: Get application details from user
                var appDetails = GetApplicationDetails();

                // Step 4: Create the application
                Console.WriteLine("Step 4: Creating Azure AD application...");
                var application = await CreateApplicationAsync(graphClient, appDetails);
                Console.WriteLine($"Application created successfully!");
                Console.WriteLine($"Application ID: {application.AppId}");
                Console.WriteLine($"Object ID: {application.Id}");
                Console.WriteLine();

                // Step 5: Create client secret
                Console.WriteLine("Step 5: Creating client secret...");
                var secret = await CreateClientSecretAsync(graphClient, application.Id, appDetails.SecretValidityDays);
                Console.WriteLine($"Client Secret created (expires: {secret.EndDateTime})");
                Console.WriteLine($"Secret Value: {secret.SecretText}");
                Console.WriteLine("⚠️  IMPORTANT: Save this secret value now - it won't be shown again!");
                Console.WriteLine();

                // Step 6: Add required API permissions
                Console.WriteLine("Step 6: Adding required API permissions...");
                await AddRequiredPermissionsAsync(graphClient, application.Id);
                Console.WriteLine("Required permissions added successfully!");
                Console.WriteLine();

                // Step 7: Generate admin consent URL
                Console.WriteLine("Step 7: Generating admin consent URL...");
                var consentUrl = GenerateAdminConsentUrl(application.AppId, authResult.TenantId);
                Console.WriteLine($"Admin Consent URL: {consentUrl}");
                Console.WriteLine();

                // Step 8: Open consent URL
                Console.Write("Would you like to open the admin consent URL in your browser? (y/n): ");
                var openBrowser = Console.ReadLine()?.ToLower().StartsWith("y") ?? false;

                if (openBrowser)
                {
                    try
                    {
                        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(consentUrl) { UseShellExecute = true });
                        Console.WriteLine("Admin consent URL opened in browser.");
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"Could not open browser: {ex.Message}");
                        Console.WriteLine("Please manually navigate to the URL above.");
                    }
                }

                // Step 9: Summary
                Console.WriteLine();
                Console.WriteLine("=== Registration Summary ===");
                Console.WriteLine($"Application Name: {appDetails.Name}");
                Console.WriteLine($"Application ID: {application.AppId}");
                Console.WriteLine($"Object ID: {application.Id}");
                Console.WriteLine($"Client Secret: {secret.SecretText}");
                Console.WriteLine($"Secret Expiry: {secret.EndDateTime}");
                Console.WriteLine($"Tenant ID: {authResult.TenantId}");
                Console.WriteLine();
                Console.WriteLine("⚠️  Remember to complete admin consent using the URL provided above!");
                Console.WriteLine("✅ Application registration completed successfully!");

                var summary = $@"
=== Registration Summary ===
Application Name: {appDetails.Name}
Application ID:   {application.AppId}
Object ID:        {application.Id}
Client Secret:    {secret.SecretText}
Secret Expiry:    {secret.EndDateTime}
Tenant ID:        {authResult.TenantId}

⚠️  Remember to complete admin consent using the URL provided above!
✅ Application registration completed successfully!
";
                var fileName = $"{appDetails.Name}.txt";
                try
                {
                    File.WriteAllText(fileName, summary);
                    Console.WriteLine($"📄 Summary written to file: {fileName}");
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"❌ Failed to write summary to file: {ex.Message}");
                }

            }
            catch (Exception ex)
            {
                Console.WriteLine($"❌ Error: {ex.Message}");
                if (ex.InnerException != null)
                {
                    Console.WriteLine($"Details: {ex.InnerException.Message}");
                }
            }

            Console.WriteLine();
            Console.WriteLine("Press any key to exit...");
            Console.ReadKey();
        }

        private static async Task<AuthenticationResult> AuthenticateAsync()
        {
            var app = PublicClientApplicationBuilder
                .Create(ClientId)
                .WithAuthority(Authority)
                .WithRedirectUri("http://localhost")
                .Build();

            try
            {
                // Try to get token silently first
                var accounts = await app.GetAccountsAsync();
                if (accounts.Any())
                {
                    try
                    {
                        return await app.AcquireTokenSilent(Scopes, accounts.FirstOrDefault()).ExecuteAsync();
                    }
                    catch (MsalUiRequiredException)
                    {
                        // Fall through to interactive authentication
                    }
                }

                // Interactive authentication
                return await app.AcquireTokenInteractive(Scopes)
                    .WithPrompt(Microsoft.Identity.Client.Prompt.SelectAccount)
                    .ExecuteAsync();
            }
            catch (Exception ex)
            {
                throw new Exception($"Authentication failed: {ex.Message}", ex);
            }
        }

        private static GraphServiceClient CreateGraphClient(string accessToken)
        {
            var authProvider = new AccessTokenAuthenticationProvider(accessToken);
            return new GraphServiceClient(authProvider);
        }

        private static ApplicationDetails GetApplicationDetails()
        {
            var details = new ApplicationDetails();

            // Application Name (default: Easy2PatchProd)
            Console.Write("Enter application name (default: Easy2PatchProd): ");
            var nameInput = Console.ReadLine();
            details.Name = string.IsNullOrWhiteSpace(nameInput)
                ? "Easy2PatchProd"
                : nameInput.Trim();

            // Redirect URI (mandatory and must match pattern)
            while (true)
            {
                Console.Write("Enter redirect URI (e.g., https://e2p.domain.com/#/auth/azuread/): ");
                var redirectInput = Console.ReadLine();

                if (!string.IsNullOrWhiteSpace(redirectInput))
                {
                    if (Uri.TryCreate(redirectInput, UriKind.Absolute, out Uri uriResult)
                        && redirectInput.Contains("/#/auth/azuread/"))
                    {
                        details.RedirectUri = redirectInput.Trim();
                        break;
                    }

                    Console.WriteLine("❌ Invalid format. Example: https://e2p.domain.com/#/auth/azuread/");
                }
                else
                {
                    Console.WriteLine("❌ Redirect URI is required.");
                }
            }

            // Secret Validity Days (default: 730)
            Console.Write("Enter client secret validity in days (default: 730): ");
            var validityInput = Console.ReadLine();
            if (int.TryParse(validityInput, out int validity) && validity > 0)
            {
                details.SecretValidityDays = validity;
            }
            else
            {
                details.SecretValidityDays = 730;
            }

            return details;
        }


        private static async Task<Application> CreateApplicationAsync(GraphServiceClient graphClient, ApplicationDetails details)
        {
            var application = new Application
            {
                DisplayName = details.Name,
                SignInAudience = "AzureADMyOrg",
                RequiredResourceAccess = new List<RequiredResourceAccess>()
            };

            // Add redirect URI if provided
            if (!string.IsNullOrWhiteSpace(details.RedirectUri))
            {
                application.Web = new WebApplication
                {
                    RedirectUris = new List<string> { details.RedirectUri }
                };
            }

            return await graphClient.Applications.PostAsync(application);
        }

        private static async Task<PasswordCredential> CreateClientSecretAsync(GraphServiceClient graphClient, string applicationId, int validityDays)
        {
            var addPasswordPostRequestBody = new Microsoft.Graph.Applications.Item.AddPassword.AddPasswordPostRequestBody
            {
                PasswordCredential = new PasswordCredential
                {
                    DisplayName = $"Auto-generated secret - {DateTime.Now:yyyy-MM-dd}",
                    EndDateTime = DateTimeOffset.Now.AddDays(validityDays)
                }
            };

            return await graphClient.Applications[applicationId].AddPassword.PostAsync(addPasswordPostRequestBody);
        }

        private static async Task AddRequiredPermissionsAsync(GraphServiceClient graphClient, string applicationId)
        {
            var requiredResourceAccess = new List<RequiredResourceAccess>();

            // Microsoft Graph permissions
            var graphPermissions = new RequiredResourceAccess
            {
                ResourceAppId = "00000003-0000-0000-c000-000000000000", // Microsoft Graph
                ResourceAccess = new List<ResourceAccess>
                {
                    // Application permissions (Role type)
                    new ResourceAccess { Id = Guid.Parse("9a5d68dd-52b0-4cc2-bd40-abcf44ac3a30"), Type = "Role" }, // Application.Read.All
                    new ResourceAccess { Id = Guid.Parse("1bfefb4e-e0b5-418b-a88f-73c46d2cc8e9"), Type = "Role" }, // Application.ReadWrite.All
                    
                    // Device permissions
                    new ResourceAccess { Id = Guid.Parse("7438b122-aefc-4978-80ed-43db9fcc7715"), Type = "Role" }, // Device.Read.All
                    
                    // DeviceManagement permissions
                    new ResourceAccess { Id = Guid.Parse("78145de6-330d-4800-a6ce-494ff2d33d07"), Type = "Role" }, // DeviceManagementApps.ReadWrite.All
                    new ResourceAccess { Id = Guid.Parse("dc377aa6-52d8-4e23-b271-2a7ae04cedf3"), Type = "Role" }, // DeviceManagementConfiguration.Read.All
                    new ResourceAccess { Id = Guid.Parse("2f51be20-0bb4-4fed-bf7b-db946066c75e"), Type = "Role" }, // DeviceManagementManagedDevices.Read.All
                    new ResourceAccess { Id = Guid.Parse("58ca0d9a-1575-47e1-a3cb-007ef2e4583b"), Type = "Role" }, // DeviceManagementRBAC.Read.All
                    new ResourceAccess { Id = Guid.Parse("06a5fe6d-c49d-46a7-b082-56b1b14103c7"), Type = "Role" }, // DeviceManagementServiceConfig.Read.All
                    
                    // User permissions - Application (Role)
                    new ResourceAccess { Id = Guid.Parse("df021288-bdef-4463-88db-98f22de89214"), Type = "Role" }, // User.Read.All (Application)
                    
                    // Group permissions - Application (Role)
                    new ResourceAccess { Id = Guid.Parse("98830695-27a2-44f7-8c18-0c3ebc9698f6"), Type = "Role" }, // GroupMember.Read.All (Application)
                    new ResourceAccess { Id = Guid.Parse("5b567255-7703-4780-807c-7be8301ae99b"), Type = "Role" }, // Group.Read.All (Application)
                    
                    // Delegated permissions (Scope type)
                    new ResourceAccess { Id = Guid.Parse("e1fe6dd8-ba31-4d61-89e7-88639da4683d"), Type = "Scope" }, // User.Read (Delegated)
                    new ResourceAccess { Id = Guid.Parse("a154be20-db9c-4678-8ab7-66f6cc099a59"), Type = "Scope" }, // User.Read.All (Delegated)
                    new ResourceAccess { Id = Guid.Parse("5f8c59db-677d-491f-a6b8-5f174b11ec1d"), Type = "Scope" }, // Group.Read.All (Delegated)
                }
            };

            // Windows Defender ATP permissions
            var defenderPermissions = new RequiredResourceAccess
            {
                ResourceAppId = "fc780465-2017-40d4-a0c5-307022471b92", // WindowsDefenderATP
                ResourceAccess = new List<ResourceAccess>
                {
                    new ResourceAccess { Id = Guid.Parse("71fe6b80-7034-4028-9ed8-0f316df9c3ff"), Type = "Role" }, // Alert.Read.All
                    new ResourceAccess { Id = Guid.Parse("47bf842d-354b-49ef-b741-3a6dd815bc13"), Type = "Role" }, // Ip.Read.All
                    new ResourceAccess { Id = Guid.Parse("ea8291d3-4b9a-44b5-bc3a-6cea3026dc79"), Type = "Role" }, // Machine.Read.All
                    new ResourceAccess { Id = Guid.Parse("aa027352-232b-4ed4-b963-a705fc4d6d2c"), Type = "Role" }, // Machine.ReadWrite.All
                    new ResourceAccess { Id = Guid.Parse("a86d9824-b2b6-45f8-b042-16bc4922ed4e"), Type = "Role" }, // Machine.Scan
                    new ResourceAccess { Id = Guid.Parse("6a33eedf-ba73-4e5a-821b-f057ef63853a"), Type = "Role" }, // RemediationTasks.Read.All
                    new ResourceAccess { Id = Guid.Parse("02b005dd-f804-43b4-8fc7-078460413f74"), Type = "Role" }, // Score.Read.All - FIXED
                    new ResourceAccess { Id = Guid.Parse("e870c0c1-c1a2-41ca-948e-a33912d2d3f0"), Type = "Role" }, // SecurityBaselinesAssessment.Read.All
                    new ResourceAccess { Id = Guid.Parse("227f2ea0-c2c2-4428-b7af-9ff40f1a720e"), Type = "Role" }, // SecurityConfiguration.Read.All
                    new ResourceAccess { Id = Guid.Parse("6443965c-7dd2-4cfd-b38f-bb7772bee163"), Type = "Role" }, // SecurityRecommendation.Read.All
                    new ResourceAccess { Id = Guid.Parse("37f71c98-d198-41ae-964d-2c49aab74926"), Type = "Role" }, // Software.Read.All
                    new ResourceAccess { Id = Guid.Parse("a833834a-4cf1-4732-8acf-bbcfa13fb610"), Type = "Role" }, // User.Read.All
                    new ResourceAccess { Id = Guid.Parse("41269fc5-d04d-4bfd-bce7-43a51cea049a"), Type = "Role" }, // Vulnerability.Read.All
                }
            };

            requiredResourceAccess.Add(graphPermissions);
            requiredResourceAccess.Add(defenderPermissions);

            // Update the application with required permissions
            var application = new Application
            {
                RequiredResourceAccess = requiredResourceAccess
            };

            await graphClient.Applications[applicationId].PatchAsync(application);
        }

        private static string GenerateAdminConsentUrl(string appId, string tenantId)
        {
            var baseUrl = "https://login.microsoftonline.com";
            var consentUrl = $"{baseUrl}/{tenantId}/adminconsent?client_id={appId}";
            return consentUrl;
        }
    }

    public class ApplicationDetails
    {
        public string Name { get; set; }
        public string RedirectUri { get; set; }
        public int SecretValidityDays { get; set; } = 730;
    }

    // Custom authentication provider for access token
    public class AccessTokenAuthenticationProvider : IAuthenticationProvider
    {
        private readonly string _accessToken;

        public AccessTokenAuthenticationProvider(string accessToken)
        {
            _accessToken = accessToken;
        }

        public Task AuthenticateRequestAsync(RequestInformation request, Dictionary<string, object> additionalAuthenticationContext = null, CancellationToken cancellationToken = default)
        {
            request.Headers.Add("Authorization", $"Bearer {_accessToken}");
            return Task.CompletedTask;
        }
    }
}
