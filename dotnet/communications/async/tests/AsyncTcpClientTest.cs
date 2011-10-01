using System;
using System.Text;
using System.Net.Sockets;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace GitHub.DavidHoyt.Utils.Communications.Async.Test {
	/// <summary>
	/// Summary description for UnitTest1
	/// </summary>
	[TestClass]
	public class AsyncTcpClientTest {
		public AsyncTcpClientTest() {
			//
			// TODO: Add constructor logic here
			//
		}

		private TestContext testContextInstance;

		/// <summary>
		///Gets or sets the test context which provides
		///information about and functionality for the current test run.
		///</summary>
		public TestContext TestContext {
			get {
				return testContextInstance;
			}
			set {
				testContextInstance = value;
			}
		}

		#region Additional test attributes
		//
		// You can use the following additional attributes as you write your tests:
		//
		// Use ClassInitialize to run code before running the first test in the class
		// [ClassInitialize()]
		// public static void MyClassInitialize(TestContext testContext) { }
		//
		// Use ClassCleanup to run code after all tests in a class have run
		// [ClassCleanup()]
		// public static void MyClassCleanup() { }
		//
		// Use TestInitialize to run code before running each test 
		// [TestInitialize()]
		// public void MyTestInitialize() { }
		//
		// Use TestCleanup to run code after each test has run
		// [TestCleanup()]
		// public void MyTestCleanup() { }
		//
		#endregion

		[TestMethod]
		public void TestMethod1() {
			//
			// TODO: Add test logic here
			//
		}
	}

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
}
