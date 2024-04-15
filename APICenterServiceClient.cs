public class APICenterServiceClient
{
  private HttpClient _client;

  public HttpClient Client { get => _client; }

  public APICenterServiceClient(HttpClient httpClient)
  {
    _client = httpClient;
  }
}