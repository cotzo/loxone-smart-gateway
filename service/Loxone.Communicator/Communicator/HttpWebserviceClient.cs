using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Loxone.Communicator {
	/// <summary>
	/// Client to handle httpWebservices to loxone miniserver. Use <see cref="HttpWebserviceClient"/> for communicating via http or derive from it to create your own httpClient.
	/// </summary>
	public class HttpWebserviceClient : WebserviceClient {
		/// <summary>
		/// The httpClient used for sending the messages
		/// </summary>
		private HttpClient HttpClient { get; set; }

		/// <summary>
		/// Provides info required for the authentication on the miniserver
		/// </summary>
		/// <param name="handler">The tokenHandler that should be used</param>
		public override Task Authenticate(TokenHandler handler) {
			TokenHandler = handler;
			return Task.CompletedTask;
		}

		private CancellationTokenSource CancellationTokenSource;
		private readonly bool UseTls;
		private readonly string CertificateAuthorityPem;

		/// <summary>
		/// Sends a webservice to the miniserver
		/// </summary>
		/// <param name="request">The Request that should be sent</param>
		/// <returns>The Response the miniserver returns</returns>
		public override async Task<WebserviceResponse> SendWebservice(WebserviceRequest request) {
			WebserviceRequest encRequest = await GetEncryptedRequest(request);
			Uri url = BuildWebserviceUri(IP, Port, UseTls, encRequest.Command, encRequest.Queries.ToString());
			CancellationTokenSource?.Dispose();
			CancellationTokenSource = new CancellationTokenSource(request.Timeout);
			HttpResponseMessage httpResponse = await HttpClient?.GetAsync(url.OriginalString, CancellationTokenSource.Token);
			byte[] responseContent = await httpResponse?.Content.ReadAsByteArrayAsync();
			CancellationTokenSource?.Dispose();
			if (httpResponse.IsSuccessStatusCode && request.Encryption == EncryptionType.RequestAndResponse) {//decypt response if needed
				responseContent = Encoding.UTF8.GetBytes(Cryptography.AesDecrypt(Encoding.UTF8.GetString(responseContent), Session));
			}
			WebserviceResponse response = new WebserviceResponse(null, responseContent, (int)httpResponse.StatusCode);
			encRequest.TryValidateResponse(response);
			return response;
		}

		/// <summary>
		/// Creates a clone of a request and encrypts it
		/// </summary>
		/// <param name="request">The request that should be encrypted</param>
		/// <returns>the encrypted clone of the given request</returns>
		private async Task<WebserviceRequest> GetEncryptedRequest(WebserviceRequest request) {
			if (request == null) {
				return null;
			}
			WebserviceRequest encRequest = (WebserviceRequest)request.Clone();
			if (request.NeedAuthentication && TokenHandler != null) {//add authentication if needed
				if (TokenHandler.Token == null) {
					await TokenHandler.RequestNewToken();
				}
				encRequest.Queries.Add("autht", await TokenHandler.GetTokenHash());
				encRequest.Queries.Add("user", TokenHandler.Username);
				encRequest.NeedAuthentication = false;
				if (encRequest.Encryption == EncryptionType.None) {
					encRequest.Encryption = EncryptionType.Request;
				}
			}
			switch (encRequest.Encryption) {
				case EncryptionType.Request:
					encRequest.Command = "jdev/sys/enc/";
					break;
				case EncryptionType.RequestAndResponse:
					encRequest.Command = "jdev/sys/fenc/";
					break;
				case EncryptionType.None:
				default:
					return encRequest;
			}
			string query = encRequest.Queries.HasKeys() ? $"?{encRequest.Queries.ToString()}" : "";
			encRequest.Command += Uri.EscapeDataString(Cryptography.AesEncrypt($"salt/{Session.Salt}/{request.Command}{query}", Session));
			encRequest.Queries.Clear();
			encRequest.Queries.Add("sk", await Session.GetSessionKey());
			encRequest.Encryption = EncryptionType.None;
			return encRequest;
		}

		/// <summary>
		/// Creates a new instance of the httpWebserviceClient.
		/// </summary>
		/// <param name="ip">IP adress of the miniserver</param>
		/// <param name="port">Port of the miniserver</param>
		/// <param name="permissions">Permissions of the connecting user</param>
		/// <param name="deviceUuid">Uuid of the connecting device</param>
		/// <param name="deviceInfo">Info of the connecting device</param>
		public HttpWebserviceClient(string ip, int port, int permissions, string deviceUuid, string deviceInfo, bool useTls = false, string certificateAuthorityPem = null) {
			IP = ip;
			Port = port;
			UseTls = useTls;
			CertificateAuthorityPem = certificateAuthorityPem;
			HttpClient = CreateHttpClient(useTls, certificateAuthorityPem);
			Session = new Session(this, permissions, deviceUuid, deviceInfo);
		}

		/// <summary>
		/// Creates a new instance of the httpWebserviceClient.
		/// </summary>
		/// <param name="ip">IP adress of the miniserver</param>
		/// <param name="port">Port of the miniserver</param>
		/// <param name="session">Session object containing info used for connection</param>
		public HttpWebserviceClient(string ip, int port, Session session, bool useTls = false, string certificateAuthorityPem = null) {
			IP = ip;
			Port = port;
			UseTls = useTls;
			CertificateAuthorityPem = certificateAuthorityPem;
			HttpClient = CreateHttpClient(useTls, certificateAuthorityPem);
			Session = session;
		}

		private static HttpClient CreateHttpClient(bool useTls, string certificateAuthorityPem) {
			if (!useTls) return new HttpClient();
			var handler = new HttpClientHandler {
				ServerCertificateCustomValidationCallback = (_, certificate, _, errors) =>
					LoxoneCertificateValidation.Validate(certificate, errors, certificateAuthorityPem)
			};
			return new HttpClient(handler);
		}

		internal static Uri BuildWebserviceUri(string host, int port, bool useTls, string path, string query) => new UriBuilder() {
			Scheme = useTls ? "https" : "http",
			Host = host,
			Port = port,
			Path = path,
			Query = query
		}.Uri;

		/// <summary>
		/// Disposes the WebserviceClient
		/// </summary>
		public override void Dispose() {
			base.Dispose();
			HttpClient?.Dispose();
			CancellationTokenSource?.Dispose();
		}

	}
}
