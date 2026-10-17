# Loxone.Communicator

This project vendors the MIT-licensed LxCommunicator.NET 0.9.5 source from
[Loxone/LxCommunicator.NET](https://github.com/Loxone/LxCommunicator.NET),
upstream commit `34eb9ca`, so protocol fixes can be maintained and tested with
the gateway. Original license: [LICENSE](LICENSE).

Local changes:

- Consume Loxone's zero-payload message headers without reading bytes from the
  next WebSocket frame.
- Implement the protocol `keepalive` request and await its `0x06` response.
- Surface background listener failures and WebSocket close details.
