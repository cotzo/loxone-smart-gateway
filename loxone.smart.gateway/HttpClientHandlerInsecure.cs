namespace loxone.smart.gateway.Apis;

public class HttpClientHandlerInsecure : HttpClientHandler
{
    public HttpClientHandlerInsecure()
    {
        ServerCertificateCustomValidationCallback = (_, _, _, _) => true;
    }
}
