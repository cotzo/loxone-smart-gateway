namespace loxone.smart.gateway;

public class HttpClientHandlerInsecure : HttpClientHandler
{
    public HttpClientHandlerInsecure()
    {
        ServerCertificateCustomValidationCallback = (_, _, _, _) => true;
    }
}
