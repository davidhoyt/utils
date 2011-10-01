Usage examples:

```csharp
///<summary>
/// Example client that writes "Hello World" every second to a 
///	server and expects to receive data (anything) at least once 
///	every 10 seconds.
///</summary>
public class HelloWorldClient : AsyncTcpClient {
	private const string HELLO_WORLD = "Hello World";
		
	protected override void OnInit(object Data) {
		UseHeartbeat = true;
		HeartbeatInterval = 1 * 1000;

		AutomaticallyReconnect = true;
		ReconnectWaitTime = DEFAULT_RECONNECT_WAIT_TIME;

		IdleDetection = true;
		IdleTimeout = 10 * 1000;

		RegisterCallbacks(new Callbacks() {
			SocketInit = (Socket Socket) => {
				Socket.NoDelay = true;
			},

			BufferInit = (out int ChunkSize, out int TotalBufferSize) => {
				ChunkSize = HELLO_WORLD.Length;
				TotalBufferSize = HELLO_WORLD.Length;
			},

			Connect = () => {
				Write(HELLO_WORLD);
			},

			Read = (byte[] Buffer, int Offset, int BytesRead, int BufferSize) => {
				Console.WriteLine(Encoding.ASCII.GetString(Buffer, Offset, BytesRead));
			},

			Heartbeat = () => {
				//Send "Hello World" every second to the server.
				Write(HELLO_WORLD);
			},

			Idle = () => {
				//If no data is received on the socket for 10 seconds, 
				//then disconnect and try again.
				Disconnect();
				ScheduleReconnect();
			}
		});
	}
}
```