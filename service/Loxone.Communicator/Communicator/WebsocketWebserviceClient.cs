using Loxone.Communicator.Events;
using Newtonsoft.Json;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Loxone.Communicator {
	/// <summary>
	/// Client to handle websocketWebservices to loxone miniserver. Use <see cref="WebsocketWebserviceClient"/> for communicating via websocket or derive from it to create your own websocketClient.
	/// </summary>
	public class WebsocketWebserviceClient : WebserviceClient {
		/// <summary>
		/// The httpCLient used for checking if the miniserver is available and getting the public key.
		/// </summary>
		public HttpWebserviceClient HttpClient { get; private set; }

		/// <summary>
		/// The websocket the webservices will be sent with.
		/// </summary>
		private ClientWebSocket WebSocket;
		private readonly bool UseTls;
		private readonly string CertificateAuthorityPem;

		/// <summary>
		/// A Listener to catch every incoming message from the miniserver
		/// </summary>
		private Task Listener;
		private readonly TaskCompletionSource<Exception> ConnectionFailureSource = new TaskCompletionSource<Exception>(TaskCreationOptions.RunContinuationsAsynchronously);
		public Task<Exception> ConnectionFailure => ConnectionFailureSource.Task;

		/// <summary>
		/// Event, fired when a not expected Message is received.
		/// Contains the message in the eventArgs
		/// </summary>
		private event EventHandler<MessageReceivedEventArgs> OnReceiveMessge;

		/// <summary>
		/// Event, fired when an eventTable is received and parsed.
		/// Contains the eventTable in the eventArgs
		/// </summary>
		public event EventHandler<EventStatesParsedEventArgs> OnReceiveEventTable;

		/// <summary>
		/// Event, fired when the connection is authenticated or a new token is received.
		/// Contains the tokenHandler with the used token in the eventArgs.
		/// </summary>
		public event EventHandler<ConnectionAuthenticatedEventArgs> OnAuthenticated;

		/// <summary>Raised after a keepalive reply (message type 0x06) is received.</summary>
		public event EventHandler OnReceiveKeepAlive;

		/// <summary>Raised when the background websocket listener fails.</summary>
		public event Action<Exception> OnConnectionError;

		/// <summary>
		/// The cancellationTokenSource used for cancelling the listener and receiving messages
		/// </summary>
		private readonly CancellationTokenSource TokenSource = new CancellationTokenSource();

		/// <summary>
		/// List of all sent requests that wait for a response
		/// </summary>
		private readonly List<WebserviceRequest> Requests = new List<WebserviceRequest>();
		private readonly SemaphoreSlim KeepAliveLock = new SemaphoreSlim(1, 1);
		private TaskCompletionSource<bool> PendingKeepAlive;

		/// <summary>
		/// Initialises a new instance of the websocketWebserviceClient.
		/// </summary>
		/// <param name="ip">The ip adress of the miniserver</param>
		/// <param name="port">The port of the miniserver</param>
		/// <param name="permissions">The permissions the user should have on the server</param>
		/// <param name="deviceUuid">The uuid of the current device</param>
		/// <param name="deviceInfo">A short info of the current device</param>
		public WebsocketWebserviceClient(string ip, int port, int permissions, string deviceUuid, string deviceInfo, bool useTls = false, string certificateAuthorityPem = null) {
			IP = ip;
			Port = port;
			UseTls = useTls;
			CertificateAuthorityPem = certificateAuthorityPem;
			Session = new Session(null, permissions, deviceUuid, deviceInfo);
			HttpClient = new HttpWebserviceClient(IP, Port, Session, UseTls, CertificateAuthorityPem);
			Session.Client = HttpClient;
		}

		internal static Uri BuildWebsocketUri(string host, int port, bool useTls) =>
			new UriBuilder(useTls ? "wss" : "ws", host, port, "/ws/rfc6455").Uri;

		/// <summary>
		/// Establish an authenticated connection to the miniserver. Fires the OnAuthenticated event when successfull.
		/// After the event fired, the connection to the miniserver can be used.
		/// </summary>
		/// <param name="handler">The tokenhandler that should be used</param>
		public override async Task Authenticate(TokenHandler handler) {
			if (await MiniserverReachable()) {
				WebSocket = new ClientWebSocket();
				WebSocket.Options.AddSubProtocol("remotecontrol");
				if (UseTls)
					WebSocket.Options.RemoteCertificateValidationCallback = (_, certificate, _, errors) =>
						LoxoneCertificateValidation.Validate(certificate, errors, CertificateAuthorityPem);
				await WebSocket.ConnectAsync(BuildWebsocketUri(IP, Port, UseTls), CancellationToken.None);
				BeginListening();
				string key = await Session.GetSessionKey();
				string keyExchangeResponse = (await SendWebservice(new WebserviceRequest<string>($"jdev/sys/keyexchange/{key}", EncryptionType.None))).Value;
				TokenHandler = handler;
				if (TokenHandler?.Token != null) {
					string hash = await TokenHandler?.GetTokenHash();
					string response = (await SendWebservice(new WebserviceRequest<string>($"authwithtoken/{hash}/{TokenHandler.Username}", EncryptionType.RequestAndResponse))).Value;
					AuthResponse authResponse = JsonConvert.DeserializeObject<AuthResponse>(response);
					if (authResponse.ValidUntil != default && authResponse.TokenRights != default) {
						OnAuthenticated.Invoke(this, new ConnectionAuthenticatedEventArgs(TokenHandler));
						return;
					}
				}
				if (await TokenHandler.RequestNewToken()) {
					OnAuthenticated.Invoke(this, new ConnectionAuthenticatedEventArgs(TokenHandler));
					return;
				}
				await HttpClient?.Authenticate(new TokenHandler(HttpClient, handler.Username, handler.Token, false));
			}
		}

		/// <summary>
		/// Checks if the miniserver is reachable
		/// </summary>
		/// <returns>Wheter the miniserver is reachable or not</returns>
		public async Task<bool> MiniserverReachable() {
			try {
				string response = (await HttpClient?.SendWebservice(new WebserviceRequest<string>($"jdev/cfg/api", EncryptionType.None) { NeedAuthentication = false })).Value;

				if (response != null && response != "") {
					return true;
				}
				else {
					return false;
				}
			}
			catch (Exception) {
				return false;
			}
		}

		/// <summary>
		/// The listener starts to wait for messsages from the miniserver
		/// </summary>
		private void BeginListening() {
			if (Listener != null) {
				TokenSource.Cancel();
				Listener = null;
			}
			var listenerToken = TokenSource.Token;
			Listener = Task.Run(async () => {
				try {
					while (WebSocket.State == WebSocketState.Open) {
						WebserviceResponse response = await ReceiveWebsocketMessage(1024, listenerToken);
						if (response.Header.Type == MessageType.KeepAlive) {
							PendingKeepAlive?.TrySetResult(true);
							OnReceiveKeepAlive?.Invoke(this, EventArgs.Empty);
						}
						else if (response.Header.Type == MessageType.OutOfService) {
							throw new WebserviceException("Miniserver reported that it is out of service.");
						}
						else if (!HandleWebserviceResponse(response) && !ParseEventTable(response.Content, response.Header.Type)) {
							OnReceiveMessge?.Invoke(WebSocket, new MessageReceivedEventArgs(response));
						}
						await Task.Delay(10, listenerToken);
					}
					if (!listenerToken.IsCancellationRequested)
						throw new WebserviceException($"Miniserver websocket stopped in state {WebSocket.State} (status {(int?)WebSocket.CloseStatus}: {WebSocket.CloseStatusDescription ?? "no description"}).");
				}
				catch (Exception) when (listenerToken.IsCancellationRequested) {
				}
				catch (Exception exception) {
					ConnectionFailureSource.TrySetResult(exception);
					OnConnectionError?.Invoke(exception);
				}
			}, listenerToken);
		}

		/// <summary>
		/// Sends Loxone's protocol-level keepalive command and waits for its empty 0x06 header reply.
		/// </summary>
		public async Task KeepAliveAsync(TimeSpan timeout, CancellationToken cancellationToken = default) {
			await KeepAliveLock.WaitAsync(cancellationToken);
			try {
				if (WebSocket == null || WebSocket.State != WebSocketState.Open)
					throw new WebserviceException("The Miniserver websocket is not open.");

				var acknowledgement = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
				PendingKeepAlive = acknowledgement;
				byte[] command = Encoding.UTF8.GetBytes("keepalive");
				await WebSocket.SendAsync(new ArraySegment<byte>(command), WebSocketMessageType.Text, true, cancellationToken);
				await acknowledgement.Task.WaitAsync(timeout, cancellationToken);
			}
			finally {
				PendingKeepAlive = null;
				KeepAliveLock.Release();
			}
		}

		/// <summary>
		/// Sends a webservice to the miniserver and waits until its receives an answer
		/// </summary>
		/// <param name="request">The Request that should be sent</param>
		/// <returns>The Response the miniserver returns</returns>
		public override async Task<WebserviceResponse> SendWebservice(WebserviceRequest request) {
			switch (request?.Encryption) {
				case EncryptionType.Request:
					request.Command = Uri.EscapeDataString(Cryptography.AesEncrypt($"salt/{Session.Salt}/{request.Command}", Session));
					request.Command = $"jdev/sys/enc/{request.Command}";
					request.Encryption = EncryptionType.None;
					return await SendWebservice(request);

				case EncryptionType.RequestAndResponse:
					string command = Uri.EscapeDataString(Cryptography.AesEncrypt($"salt/{Session.Salt}/{request.Command}", Session));
					command = $"jdev/sys/fenc/{command}";
					WebserviceRequest encryptedRequest = new WebserviceRequest(command, EncryptionType.None);
					WebserviceResponse encrypedResponse = await SendWebservice(encryptedRequest);
					if (encrypedResponse == null) {
						request.TryValidateResponse(new WebserviceResponse(null, null, (int?)WebSocket?.CloseStatus));
					}
					else {
						request.TryValidateResponse(new WebserviceResponse(encrypedResponse.Header, Encoding.UTF8.GetBytes(Cryptography.AesDecrypt(encrypedResponse.GetAsStringContent(), Session)), (int?)WebSocket?.CloseStatus));
					}
					return request.WaitForResponse();

				default:
				case EncryptionType.None:
					if (WebSocket == null || WebSocket.State != WebSocketState.Open) {
						return null;
					}
					lock (Requests) {
						Requests.Add(request);
					}
					byte[] input = Encoding.UTF8.GetBytes(request.Command);
					await WebSocket.SendAsync(new ArraySegment<byte>(input, 0, input.Length), WebSocketMessageType.Text, true, CancellationToken.None);
					return request.WaitForResponse();
			}
		}

		/// <summary>
		/// Receives a webservice from the Miniservers
		/// </summary>
		/// <param name="bufferSize">The size of the buffer that should be used</param>
		/// <param name="token">the cancellationToken to cancel receiving messages</param>
		/// <returns>The received webserviceResponse</returns>
		private async Task<WebserviceResponse> ReceiveWebsocketMessage(uint bufferSize, CancellationToken token) {
			byte[] data;
			MessageHeader header;
			do {
				data = await InternalReceiveWebsocketMessage(bufferSize, token);
				if (!MessageHeader.TryParse(data, out header)) {
					throw new WebserviceException("Received incomplete Data: \n" + Encoding.UTF8.GetString(data));
				}
			} while (header == null || header.Estimated);
			if (!HasPayload(header))
				return new WebserviceResponse(header, Array.Empty<byte>(), (int?)WebSocket?.CloseStatus);

			data = await InternalReceiveWebsocketMessage(header.Length, token);
			return new WebserviceResponse(header, data, (int?)WebSocket?.CloseStatus);
		}

		internal static bool HasPayload(MessageHeader header) =>
			header.Type != MessageType.KeepAlive && header.Length > 0;

		/// <summary>
		/// Internally receives messages from the websocket
		/// </summary>
		/// <param name="bufferSize">The bufferSize that should be used</param>
		/// <param name="token">The cancellationToken for cancelling receiving the response</param>
		/// <returns></returns>
		private async Task<byte[]> InternalReceiveWebsocketMessage(uint bufferSize, CancellationToken token) {
			WebSocketReceiveResult result;
			byte[] buffer = new byte[bufferSize <= 0 ? 1024 : bufferSize];
			int offset = 0;
			using (MemoryStream stream = new MemoryStream()) {
				do {
					result = await WebSocket?.ReceiveAsync(new ArraySegment<byte>(buffer, offset, Math.Max(0, buffer.Length - offset)), token);
					if (result.MessageType == WebSocketMessageType.Close)
						throw new WebSocketException($"Miniserver closed the websocket (status {(int?)result.CloseStatus}: {result.CloseStatusDescription ?? "no description"}).");
					offset += result.Count;
					if (offset >= buffer.Length) {
						await stream.WriteAsync(buffer, 0, buffer.Length, token);
						offset = 0;
					}
				} while (result != null && !result.EndOfMessage);
				await stream.WriteAsync(buffer, 0, offset, token);
				return stream.ToArray();
			}
		}

		/// <summary>
		/// Handles the assignment of the responses to the right requests.
		/// </summary>
		/// <param name="response">The response that should be handled</param>
		/// <returns>Whether or not the reponse could be assigned to a request</returns>
		private bool HandleWebserviceResponse(WebserviceResponse response) {
			foreach (WebserviceRequest request in Enumerable.Reverse(Requests)) {
				if (request.TryValidateResponse(response)) {
					lock (Requests) {
						Requests.Remove(request);
					}
					return true;
				}
			}
			return false;
		}

		/// <summary>
		/// Parses a received message into an eventTable.
		/// Fires an onReceiveEventTable event if successful
		/// </summary>
		/// <param name="content">The message that should be parsed</param>
		/// <param name="type">The expected type of the eventTable</param>
		/// <returns>Whether or not parsing the eventTable was successful</returns>
		private bool ParseEventTable(byte[] content, MessageType type) {
			List<EventState> eventStates = new List<EventState>();
			using (BinaryReader reader = new BinaryReader(new MemoryStream(content))) {
				try {
					do {
						EventState state = null;
						switch (type) {
							case MessageType.EventTableValueStates:
								state = ValueState.Parse(reader);
								break;

							case MessageType.EventTableTextStates:
								state = TextState.Parse(reader);
								break;

							case MessageType.EventTableDaytimerStates:
								state = DaytimerState.Parse(reader);
								break;

							case MessageType.EventTableWeatherStates:
								state = WeatherState.Parse(reader);
								break;

							default:
								return false;
						}
						eventStates.Add(state);
					} while (reader.BaseStream.Length - reader.BaseStream.Position > 0);
				}
				catch {
					return false;
				}
			}
			if (OnReceiveEventTable != null) {
				OnReceiveEventTable.Invoke(this, new EventStatesParsedEventArgs(type, eventStates));
				return true;
			}
			else {
				return false;
			}
		}

		/// <summary>
		/// Disposes the WebserviceClient
		/// </summary>
		public override void Dispose() {
			base.Dispose();
			TokenSource?.Cancel();
			TokenSource?.Dispose();
			HttpClient?.Dispose();
			WebSocket?.Dispose();
			Requests.Clear();
		}
	}
}
