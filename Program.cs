using System.Diagnostics;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Azure.Core;
using Azure.Identity;
using Microsoft.OpenApi.Models;
using Microsoft.OpenApi.Readers;
using Microsoft.Extensions.Configuration;

var jsonSerializerOptions = new JsonSerializerOptions
{
  PropertyNameCaseInsensitive = true,
  PropertyNamingPolicy = JsonNamingPolicy.CamelCase
};

// configurable via plugin settings
var builder = new ConfigurationBuilder()
  .SetBasePath(Directory.GetCurrentDirectory())
  .AddJsonFile("appsettings.json", optional: true, reloadOnChange: true);

IConfigurationRoot configuration = builder.Build();

var subscriptionId = configuration["subscriptionId"];
var resourceGroupName = configuration["resourceGroupName"];
var serviceName = configuration["serviceName"];
var workspaceName = configuration["workspaceName"];
var createApicEntryForNewApis = bool.Parse(configuration?["createApicEntryForNewApis"] ?? "true");

var credential = new ChainedTokenCredential(
  new VisualStudioCredential(),
  new VisualStudioCodeCredential(),
  new AzureCliCredential(),
  new AzurePowerShellCredential(),
  new AzureDeveloperCliCredential()
);
string[] scopes = ["https://management.azure.com/.default"];

// check if the user is signed-in. Stop if they're not
try
{
  await credential.GetTokenAsync(new TokenRequestContext(scopes), CancellationToken.None);
}
catch (AuthenticationFailedException ex)
{
  Console.WriteLine($"Sign in to Azure before using this plugin. Original error: {ex.Message}");
  return;
}

var authenticationHandler = new AuthenticationDelegatingHandler(credential, scopes)
{
  InnerHandler = new HttpClientHandler()
};
var httpClient = new HttpClient(authenticationHandler);

// requests recorded by Dev Proxy
var interceptedRequests = new List<Request>
{
  new Request
  {
    Url = $"https://jsonplaceholder.typicode.com/posts"
  },
  new Request
  {
    Url = $"https://jsonplaceholder.typicode.com/posts/1/"
  },
  new Request
  {
    Url = $"https://jsonplaceholder.typicode.com/users"
  },
  new Request
  {
    Url = $"https://jsonplaceholder.typicode.com/users/1/"
  },
  new Request
  {
    Url = $"https://jsonplaceholder.typicode.com/posts",
    Method = "POST"
  }
};
var newApis = new List<Tuple<string, string>>();
// url > definition for easy lookup
var apiDefinitions = new Dictionary<string, ApiDefinition>();

async Task LoadApiDefinitions(string apiName, IDictionary<string, ApiDefinition> apiDefinitions)
{
  var deployments = await LoadApiDeployments(apiName);
  if (deployments == null || !deployments.Value.Any())
  {
    Console.WriteLine($"No deployments found for API {apiName}");
    return;
  }

  foreach (var deployment in deployments.Value)
  {
    Debug.Assert(deployment?.Properties?.Server is not null);
    Debug.Assert(deployment?.Properties?.DefinitionId is not null);

    if (!deployment.Properties.Server.RuntimeUri.Any())
    {
      Console.WriteLine($"No runtime URIs found for deployment {deployment.Name}");
      continue;
    }

    foreach (var runtimeUri in deployment.Properties.Server.RuntimeUri)
    {
      apiDefinitions.Add(runtimeUri, new ApiDefinition
      {
        Id = deployment.Properties.DefinitionId
      });
    }
  }
}

async Task<Collection<ApiDeployment>?> LoadApiDeployments(string apiName)
{
  var res = await httpClient.GetStringAsync($"https://management.azure.com/subscriptions/{subscriptionId}/resourceGroups/{resourceGroupName}/providers/Microsoft.ApiCenter/services/{serviceName}/workspaces/{workspaceName}/apis/{apiName}/deployments?api-version=2024-03-01");
  return JsonSerializer.Deserialize<Collection<ApiDeployment>>(res, jsonSerializerOptions);
}

async Task EnsureApiDefinition(ApiDefinition apiDefinition)
{
  if (apiDefinition.Properties is not null)
  {
    return;
  }

  var res = await httpClient.GetStringAsync($"https://management.azure.com/subscriptions/{subscriptionId}/resourceGroups/{resourceGroupName}/providers/Microsoft.ApiCenter/services/{serviceName}{apiDefinition.Id}?api-version=2024-03-01");
  var definition = JsonSerializer.Deserialize<ApiDefinition>(res, jsonSerializerOptions);
  if (definition is null)
  {
    return;
  }

  apiDefinition.Properties = definition.Properties;
  if (apiDefinition.Properties?.Specification?.Name != "openapi")
  {
    return;
  }

  var definitionRes = await httpClient.PostAsync($"https://management.azure.com/subscriptions/{subscriptionId}/resourceGroups/{resourceGroupName}/providers/Microsoft.ApiCenter/services/{serviceName}{apiDefinition.Id}/exportSpecification?api-version=2024-03-01", null);
  var exportResult = await definitionRes.Content.ReadFromJsonAsync<ApiSpecExportResult>();
  if (exportResult is null)
  {
    return;
  }

  if (exportResult.Format != ApiSpecExportResultFormat.Inline)
  {
    return;
  }

  try
  {
    apiDefinition.Definition = new OpenApiStringReader().Read(exportResult.Value, out var diagnostic);
  }
  catch (Exception ex)
  {
    Console.WriteLine(ex.Message);
    return;
  }
}

string MaxLength(string input, int maxLength)
{
  return input.Length <= maxLength ? input : input.Substring(0, maxLength);
}

var res = await httpClient.GetStringAsync($"https://management.azure.com/subscriptions/{subscriptionId}/resourceGroups/{resourceGroupName}/providers/Microsoft.ApiCenter/services/{serviceName}/workspaces/{workspaceName}/apis?api-version=2024-03-01");
var apis = JsonSerializer.Deserialize<Collection<Api>>(res, jsonSerializerOptions);
if (apis == null || !apis.Value.Any())
{
  Console.WriteLine("No APIs found in API Center");
  return;
}

foreach (var api in apis.Value)
{
  Debug.Assert(api.Name is not null);

  await LoadApiDefinitions(api.Name, apiDefinitions);
}

foreach (var request in interceptedRequests)
{
  var apiDefinition = apiDefinitions.FirstOrDefault(x => request.Url.Contains(x.Key)).Value;
  if (apiDefinition.Id is null)
  {
    newApis.Add(new(request.Method, request.Url));
    continue;
  }

  await EnsureApiDefinition(apiDefinition);

  if (apiDefinition.Definition is null)
  {
    newApis.Add(new(request.Method, request.Url));
    continue;
  }

  OpenApiPathItem? pathItem = null;
  foreach (var path in apiDefinition.Definition.Paths)
  {
    var urlPath = path.Key;

    // check if path contains parameters. If it does,
    // replace them with regex
    if (urlPath.Contains('{'))
    {
      foreach (var parameter in path.Value.Parameters)
      {
        urlPath = urlPath.Replace($"{{{parameter.Name}}}", $"([^/]+)");
      }

      var regex = new Regex(urlPath);
      if (regex.IsMatch(request.Url))
      {
        pathItem = path.Value;
        break;
      }
    }
    else
    {
      if (request.Url.Contains(urlPath, StringComparison.OrdinalIgnoreCase))
      {
        pathItem = path.Value;
        break;
      }
    }
  }

  if (pathItem is null)
  {
    newApis.Add(new(request.Method, request.Url));
    continue;
  }

  var operation = pathItem.Operations.FirstOrDefault(x => x.Key.ToString().Equals(request.Method, StringComparison.OrdinalIgnoreCase)).Value;
  if (operation is null)
  {
    newApis.Add(new(request.Method, request.Url));
    continue;
  }
}

if (!newApis.Any())
{
  Console.WriteLine("No new APIs found");
  return;
}

var apisPerHost = newApis.GroupBy(x => new Uri(x.Item2).Host);

var newApisMessageChunks = new List<string>(["New APIs discovered by Dev Proxy", ""]);
foreach (var apiPerHost in apisPerHost)
{
  newApisMessageChunks.Add($"{apiPerHost.Key}:");
  newApisMessageChunks.AddRange(apiPerHost.Select(a => $"  {a.Item1} {a.Item2}"));
}

Console.WriteLine(string.Join(Environment.NewLine, newApisMessageChunks));
if (!createApicEntryForNewApis)
{
  return;
}

Console.WriteLine();
Console.WriteLine("Creating new API entries in API Center...");

foreach (var apiPerHost in apisPerHost)
{
  var host = apiPerHost.Key;
  // trim to 50 chars which is max length for API name
  var apiName = MaxLength($"new-{host.Replace(".", "-")}-{DateTimeOffset.UtcNow.ToUnixTimeSeconds()}", 50);
  Console.WriteLine(apiName);

  var title = $"New APIs: {host}";
  var description = new List<string>(["New APIs discovered by Dev Proxy", ""]);
  description.AddRange(apiPerHost.Select(a => $"  {a.Item1} {a.Item2}").ToArray());
  var payload = new
  {
    properties = new
    {
      title,
      description = string.Join(Environment.NewLine, description),
      kind = "REST",
      type = "rest"
    }
  };
  var content = new StringContent(JsonSerializer.Serialize(payload, jsonSerializerOptions), Encoding.UTF8, "application/json");
  var createRes = await httpClient.PutAsync($"https://management.azure.com/subscriptions/{subscriptionId}/resourceGroups/{resourceGroupName}/providers/Microsoft.ApiCenter/services/{serviceName}/workspaces/{workspaceName}/apis/{apiName}?api-version=2024-03-01", content);
  if (createRes.IsSuccessStatusCode)
  {
    Console.WriteLine("API created successfully");
  }
  else
  {
    Console.WriteLine("Failed to create API");
  }
  var createResContent = await createRes.Content.ReadAsStringAsync();
  Console.WriteLine(createResContent);
}