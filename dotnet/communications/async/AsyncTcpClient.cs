using System;
using System.Net;
using System.Security;
using System.Threading;
using System.Diagnostics;
using System.Net.Sockets;
using System.Collections.Generic;
using System.Runtime.CompilerServices;

namespace GitHub.DavidHoyt.Utils.Communications.Async {
	///<summary>
	///	Uses asynchronous (IO completion ports on Windows) socket calls for TCP communication 
	///	with a server. Also provides half-closed connection detection using periodic heartbeats 
	///	and idle detection.
	///</summary>
	public class AsyncTcpClient : IDisposable {
		#region Constants
		public const int DEFAULT_HEARTBEAT_INTERVAL = 500;
		public const int DEFAULT_IDLE_WAIT_TIME = 10 * 1000;
		public const int DEFAULT_RECONNECT_WAIT_TIME = 1 * 1000;
		public const int DEFAULT_CONNECT_WAIT_TIME = 5 * 1000;
		public const int DEFAULT_BUFFER_SIZE = 4096;
		public const int DEFAULT_CHUNK_SIZE = 1024;
		public const string DEFAULT_HOSTNAME = "localhost";
		#endregion

		#region Enums
		public enum ConnectionState : byte {
			  Unknown
			, Open
			, Closed
		}
		#endregion

		#region Variables
		private bool disposed = false;

		private bool please_stop = false;
		private bool is_connected = false;
		private ConnectionState previous_connection_state = ConnectionState.Closed;
		private ConnectionState current_connection_state = ConnectionState.Unknown;
		private readonly Object object_lock = new Object();

		private AsyncSocketInfo current_socket_info = null;
		private SocketAsyncEventArgs current_async_args = null;
		private Socket current_async_socket = null;

		private AsyncBufferManager buffer_manager = null;

		private int last_port = -1;
		private int last_connect_wait_time = DEFAULT_CONNECT_WAIT_TIME;
		private string last_host_name = string.Empty;
		private bool last_use_heartbeat = false;
		private int last_heartbeat_interval = DEFAULT_HEARTBEAT_INTERVAL;
		private bool last_automatically_reconnect = false;
		private int last_reconnect_wait_time = DEFAULT_RECONNECT_WAIT_TIME;
		private bool last_use_idle = false;
		private int last_idle_wait_time = DEFAULT_IDLE_WAIT_TIME;
		private int last_received_data_tick_count = -1;

		private object data;
		#endregion

		#region Constructors
		public AsyncTcpClient() {
			init(null);
		}

		public AsyncTcpClient(object Data) {
			init(Data);
		}

		private void init(object Data) {
			this.data = Data;
			OnInit(Data);

			int chunk_size = 0;
			int total_buffer_size = 0;
			OnBufferInit(data, out chunk_size, out total_buffer_size);
			if (total_buffer_size <= 0)
				total_buffer_size = DEFAULT_BUFFER_SIZE;
			if (chunk_size <= 0)
				chunk_size = DEFAULT_CHUNK_SIZE;

			this.buffer_manager = new AsyncBufferManager(chunk_size, total_buffer_size);
			this.buffer_manager.Initialize();
		}
		#endregion

		#region IDisposable Members
		public bool IsDisposed {
			get {
				return this.disposed;
			}
		}

		~AsyncTcpClient() {
			Dispose(false);
		}

		public void Dispose() {
			Dispose(true);
			GC.SuppressFinalize(this);
		}

		[MethodImpl(MethodImplOptions.Synchronized)]
		protected void Dispose(bool disposing) {
			if (this.disposed)
				return;
			this.disposed = true;

			if (disposing) {
				DisposeManagedResources();
				OnDisposeManagedResources();
			}
			DisposeUnmanagedResources();
			OnDisposeUnmanagedResources();
		}

		protected void DisposeManagedResources() {
			//Remove managed resources
			Disconnect();
		}

		protected void DisposeUnmanagedResources() {
			//Unmanaged resources
		}
		#endregion

		#region Properties
		public static bool IsSupported {
			get {
				try {
					using (SocketAsyncEventArgs e = new SocketAsyncEventArgs()) {
						return true;
					}
				} catch (NotSupportedException) {
					return false;
				}
			}
		}

		public ConnectionState CurrentConnectionState {
			get { 
				return current_connection_state; 
			}
		}

		public ConnectionState PreviousConnectionState {
			get { 
				return previous_connection_state; 
			}
		}

		public bool IsConnected {
			get {
				lock (object_lock) {
					return is_connected;
				}
			}
		}

		public string LastHostName {
			get {
				return this.last_host_name;
			}
		}

		public int LastPort {
			get {
				return this.last_port;
			}
		}

		public object Data {
			get {
				return this.data;
			}
			set {
				this.data = value;
			}
		}

		public object Lock {
			get { return object_lock; }
		}

		public int ConnectTimeout {
			get { return last_connect_wait_time; }
			set { last_connect_wait_time = (value >= 0 ? value : DEFAULT_CONNECT_WAIT_TIME); }
		}

		public int ReconnectWaitTime {
			get { return last_reconnect_wait_time; }
			set { last_reconnect_wait_time = (value >= 0 ? value : DEFAULT_RECONNECT_WAIT_TIME); }
		}

		public bool AutomaticallyReconnect {
			get { return last_automatically_reconnect; }
			set {
				bool previous_value = last_automatically_reconnect;
				last_automatically_reconnect = value;
				if (previous_value != value && !value)
					CancelReconnect();
			}
		}

		public int IdleTimeout {
			get { return last_idle_wait_time; }
			set { last_idle_wait_time = (value >= 0 ? value : DEFAULT_IDLE_WAIT_TIME); }
		}

		public bool IdleDetection {
			get { return last_use_idle; }
			set {
				bool previous_value = last_use_idle;
				last_use_idle = value;
				if (previous_value != value && !value)
					CancelIdleDetection();
			}
		}

		public int HeartbeatInterval {
			get { return last_heartbeat_interval; }
			set { last_heartbeat_interval = (value >= 0 ? value : DEFAULT_HEARTBEAT_INTERVAL); }
		}

		public bool UseHeartbeat {
			get { return last_use_heartbeat; }
			set {
				bool previous_value = last_use_heartbeat;
				last_use_heartbeat = value;
				if (previous_value != value && !value)
					CancelHeartbeat();
			}
		}

		protected SocketInitCallbackDelegate SocketInitCallback {
			get;
			set;
		}

		protected HeartbeatCallbackDelegate HeartbeatCallback {
			get;
			set;
		}

		protected IdleCallbackDelegate IdleCallback {
			get;
			set;
		}

		protected ConnectCallbackDelegate ConnectCallback {
			get;
			set;
		}

		protected UnsuccessfulConnectionCallbackDelegate UnsuccessfulConnectionCallback {
			get;
			set;
		}

		protected BufferInitCallbackDelegate BufferInitCallback {
			get;
			set;
		}

		protected ReadCallbackDelegate ReadCallback {
			get;
			set;
		}

		protected SendCallbackDelegate SendCallback {
			get;
			set;
		}

		protected DisconnectCallbackDelegate DisconnectCallback {
			get;
			set;
		}
		#endregion

		#region Callbacks
		protected delegate void SocketInitCallbackDelegate(Socket Socket);
		protected delegate void HeartbeatCallbackDelegate();
		protected delegate void IdleCallbackDelegate();
		protected delegate void ConnectCallbackDelegate();
		protected delegate void UnsuccessfulConnectionCallbackDelegate();
		protected delegate void BufferInitCallbackDelegate(out int ChunkSize, out int TotalBufferSize);
		protected delegate void ReadCallbackDelegate(byte[] Buffer, int Offset, int BytesRead, int BufferSize);
		protected delegate void SendCallbackDelegate(byte[] Buffer, int Offset, int BytesSent, int BufferSize);
		protected delegate void DisconnectCallbackDelegate();

		protected class Callbacks {
			public SocketInitCallbackDelegate SocketInit {
				get;
				set;
			}

			public BufferInitCallbackDelegate BufferInit {
				get;
				set;
			}

			public ConnectCallbackDelegate Connect {
				get;
				set;
			}

			public HeartbeatCallbackDelegate Heartbeat {
				get;
				set;
			}

			public IdleCallbackDelegate Idle {
				get;
				set;
			}

			public ReadCallbackDelegate Read {
				get;
				set;
			}

			public SendCallbackDelegate Send {
				get;
				set;
			}

			public UnsuccessfulConnectionCallbackDelegate UnsuccessfulConnection {
				get;
				set;
			}

			public DisconnectCallbackDelegate Disconnect {
				get;
				set;
			}
		}

		protected void RegisterCallbacks(Callbacks Callbacks) {
			if (Callbacks == null)
				return;

			RegisterCallbacks(
				  Callbacks.SocketInit
				, Callbacks.BufferInit
				, Callbacks.Connect
				, Callbacks.Heartbeat
				, Callbacks.Idle
				, Callbacks.Read
				, Callbacks.Send
				, Callbacks.UnsuccessfulConnection
				, Callbacks.Disconnect
			);
		}

		protected void RegisterCallbacks(
			  SocketInitCallbackDelegate SocketInit
			, BufferInitCallbackDelegate BufferInit
			, ConnectCallbackDelegate Connect
			, HeartbeatCallbackDelegate Heartbeat
			, IdleCallbackDelegate Idle 
			, ReadCallbackDelegate Read
			, SendCallbackDelegate Send
			, UnsuccessfulConnectionCallbackDelegate UnsuccessfulConnection
			, DisconnectCallbackDelegate Disconnect
		) {
			this.SocketInitCallback = SocketInit;
			this.BufferInitCallback = BufferInit;
			this.ConnectCallback = Connect;
			this.HeartbeatCallback = Heartbeat;
			this.IdleCallback = Idle;
			this.ReadCallback = Read;
			this.SendCallback = Send;
			this.UnsuccessfulConnectionCallback = UnsuccessfulConnection;
			this.DisconnectCallback = Disconnect;
		}
		#endregion

		#region Events
		public delegate void ConnectedDelegate(object Source, object Data);
		public delegate void HeartbeatDelegate(object Source, object Data);
		public delegate void IdleDelegate(object Source, object Data);
		public delegate void ReceivedDelegate(object Source, object Data, byte[] Buffer, int Offset, int BytesRead);
		public delegate void SentDelegate(object Source, object Data, byte[] Buffer, int Offset, int BytesSent);
		public delegate void DisconnectedDelegate(object Source, object Data);
		public delegate void UnsuccessfulConnectionDelegate(object Source, object Data);

		public event ConnectedDelegate Connected;
		protected void NotifyConnectionOpened() {
			if (Connected != null)
				Connected(this, Data);
		}

		public event HeartbeatDelegate Heartbeat;
		protected void NotifyHeartbeat() {
			if (Heartbeat != null)
				Heartbeat(this, Data);
		}

		public event IdleDelegate Idle;
		protected void NotifyIdle() {
			if (Idle != null)
				Idle(this, Data);
		}

		public event ReceivedDelegate Received;
		protected void NotifyReceived(byte[] Buffer, int Offset, int BytesRead) {
			if (Received != null)
				Received(this, Data, Buffer, Offset, BytesRead);
		}

		public event SentDelegate Sent;
		protected void NotifySent(byte[] Buffer, int Offset, int BytesSent) {
			if (Sent != null)
				Sent(this, Data, Buffer, Offset, BytesSent);
		}

		public event UnsuccessfulConnectionDelegate UnsuccessfulConnection;
		protected void NotifyUnsuccessfulConnection() {
			if (UnsuccessfulConnection != null)
				UnsuccessfulConnection(this, Data);
		}

		public event DisconnectedDelegate Disconnected;
		protected void NotifyConnectionClosed() {
			if (Disconnected != null)
				Disconnected(this, Data);
		}
		#endregion

		#region Virtual Methods
		protected virtual void OnInit(object Data) {
		}

		protected virtual void OnSocketInit(object Data, Socket Socket) {
			if (SocketInitCallback != null)
				SocketInitCallback(Socket);
		}

		protected virtual void OnConnect(object Data) {
			if (ConnectCallback != null)
				ConnectCallback();
		}

		protected virtual void OnUnsuccessfulConnection(object Data) {
			if (UnsuccessfulConnectionCallback != null)
				UnsuccessfulConnectionCallback();
		}

		protected virtual void OnHeartbeat(object Data) {
			if (HeartbeatCallback != null)
				HeartbeatCallback();
		}

		protected virtual void OnIdle(object Data) {
			if (IdleCallback != null)
				IdleCallback();
		}

		protected virtual void OnBufferInit(object Data, out int ChunkSize, out int TotalBufferSize) {
			if (BufferInitCallback != null) {
				BufferInitCallback(out ChunkSize, out TotalBufferSize);
			} else {
				ChunkSize = DEFAULT_CHUNK_SIZE;
				TotalBufferSize = DEFAULT_BUFFER_SIZE;
			}
		}

		protected virtual void OnRead(object Data, byte[] Buffer, int Offset, int BytesRead, int BufferSize) {
			if (ReadCallback != null)
				ReadCallback(Buffer, Offset, BytesRead, BufferSize);
		}

		protected virtual void OnSend(object Data, byte[] Buffer, int Offset, int BytesSent, int BufferSize) {
			if (SendCallback != null)
				SendCallback(Buffer, Offset, BytesSent, BufferSize);
		}

		protected virtual void OnDisconnect(object Data) {
			if (DisconnectCallback != null)
				DisconnectCallback();
		}

		protected virtual void OnDisposeManagedResources() {
		}

		protected virtual void OnDisposeUnmanagedResources() {
		}
		#endregion

		#region Helper Methods
		private static void removeWhere<T>(LinkedList<T> ll, Predicate<T> pred) {
			if (ll == null)
				throw new ArgumentNullException("ll");
			if (pred == null)
				throw new ArgumentNullException("pred");

			LinkedListNode<T> node = ll.First;
			LinkedListNode<T> next = null;

			while (node != null) {
				next = node.Next;
				if (pred(node.Value))
					ll.Remove(node);
				node = next;
			}
		}

		private static void forEach<T>(LinkedList<T> ll, Action<T> action) {
			if (ll == null)
				throw new ArgumentNullException("ll");
			if (action == null)
				throw new ArgumentNullException("action");

			LinkedListNode<T> node = ll.First;
			LinkedListNode<T> next = null;

			while (node != null) {
				next = node.Next;
				action(node.Value);
				node = next;
			}
		}

		private static void beginThreadAffinity(out bool AffinitySuccess) {
			try {
				Thread.BeginThreadAffinity();
				AffinitySuccess = true;
			} catch {
				AffinitySuccess = false;
			}
		}

		private static void endThreadAffinity() {
			try {
				Thread.EndThreadAffinity();
			} catch (SecurityException) {
			}
		}
		#endregion

		#region Public Methods
		public bool Connect(string HostName, int Port) {
			return Connect(HostName, Port, ConnectTimeout);
		}

		public bool Connect(string HostName, int Port, int ConnectWaitTimeInMilliseconds) {
			return Connect(HostName, Port, ConnectWaitTimeInMilliseconds, UseHeartbeat, HeartbeatInterval, AutomaticallyReconnect, ReconnectWaitTime);
		}

		public bool Connect(string HostName, int Port, int ConnectWaitTimeInMilliseconds, bool UseHeartbeat, int HeartbeatInterval) {
			return Connect(HostName, Port, ConnectWaitTimeInMilliseconds, UseHeartbeat, HeartbeatInterval, AutomaticallyReconnect, ReconnectWaitTime);
		}

		public bool Connect(string HostName, int Port, int ConnectWaitTimeInMilliseconds, bool UseHeartbeat, int HeartbeatInterval, bool AutomaticallyReconnect, int ReconnectWaitTimeInMilliseconds) {
			return Connect(HostName, Port, ConnectWaitTimeInMilliseconds, UseHeartbeat, HeartbeatInterval, AutomaticallyReconnect, ReconnectWaitTimeInMilliseconds, IdleDetection, IdleTimeout);
		}

		public bool Connect(string HostName, int Port, int ConnectWaitTimeInMilliseconds, bool UseHeartbeat, int HeartbeatInterval, bool AutomaticallyReconnect, int ReconnectWaitTimeInMilliseconds, bool UseIdle, int IdleWaitTimeInMilliseconds) {
			if (Port < 0)
				throw new ArgumentOutOfRangeException("Port");
			if (string.IsNullOrEmpty(HostName))
				throw new ArgumentOutOfRangeException("HostName");
			if (HeartbeatInterval < 0)
				HeartbeatInterval = DEFAULT_HEARTBEAT_INTERVAL;
			if (ReconnectWaitTimeInMilliseconds < 0)
				ReconnectWaitTimeInMilliseconds = DEFAULT_RECONNECT_WAIT_TIME;
			if (IdleWaitTimeInMilliseconds < 0)
				IdleWaitTimeInMilliseconds = DEFAULT_IDLE_WAIT_TIME;

			lock (object_lock) {
				if (is_connected)
					return false;

				return innerConnect(HostName, Port, ConnectWaitTimeInMilliseconds, UseHeartbeat, HeartbeatInterval, AutomaticallyReconnect, ReconnectWaitTimeInMilliseconds, UseIdle, IdleWaitTimeInMilliseconds);
			}
		}

		public bool Reconnect() {
			return Connect(last_host_name, last_port, last_connect_wait_time >= 0 ? last_connect_wait_time : DEFAULT_CONNECT_WAIT_TIME, last_use_heartbeat, last_heartbeat_interval, last_automatically_reconnect, last_reconnect_wait_time >= 0 ? last_reconnect_wait_time : DEFAULT_RECONNECT_WAIT_TIME, last_use_idle, last_idle_wait_time >= 0 ? last_idle_wait_time : DEFAULT_IDLE_WAIT_TIME);
		}

		public bool Reconnect(int ConnectWaitTimeInMilliseconds) {
			return Connect(last_host_name, last_port, ConnectWaitTimeInMilliseconds, last_use_heartbeat, last_heartbeat_interval, last_automatically_reconnect, last_reconnect_wait_time >= 0 ? last_reconnect_wait_time : DEFAULT_RECONNECT_WAIT_TIME, last_use_idle, last_idle_wait_time >= 0 ? last_idle_wait_time : DEFAULT_IDLE_WAIT_TIME);
		}

		public bool ScheduleReconnect() {
			return ScheduleReconnect(last_reconnect_wait_time >= 0 ? last_reconnect_wait_time : DEFAULT_RECONNECT_WAIT_TIME);
		}

		public bool ScheduleReconnect(int TimeToWaitInMilliseconds) {
			return ScheduleReconnect(last_connect_wait_time >= 0 ? last_connect_wait_time : DEFAULT_CONNECT_WAIT_TIME, TimeToWaitInMilliseconds);
		}

		public bool ScheduleReconnect(int ConnectWaitTimeInMilliseconds, int TimeToWaitInMilliseconds) {
			scheduleReconnect(this, ConnectWaitTimeInMilliseconds, TimeToWaitInMilliseconds, last_use_heartbeat, last_heartbeat_interval, last_automatically_reconnect, last_reconnect_wait_time >= 0 ? last_reconnect_wait_time : DEFAULT_RECONNECT_WAIT_TIME, last_use_idle, last_idle_wait_time >= 0 ? last_idle_wait_time : DEFAULT_IDLE_WAIT_TIME);
			return true;
		}

		public void CancelReconnect() {
			removeClientForReconnect(this);
		}

		public bool Disconnect() {
			lock (object_lock) {
				return innerDisconnect();
			}
		}

		public bool Send(byte[] Buffer, int Offset, int Size) {
			//lock (object_lock) {
				return innerSend(Buffer, Offset, Size);
			//}
		}

		public bool Write(byte[] Buffer, int Offset, int Size) {
			return Send(Buffer, Offset, Size);
		}

		public bool Write(byte[] Buffer) {
			if (Buffer == null || Buffer.Length <= 0)
				return true;
			return Send(Buffer, 0, Buffer.Length);
		}

		public bool Write(byte Value) {
			return Write(new byte[] { Value });
		}

		public bool Write(short Value) {
			return Write(BitConverter.GetBytes(Value));
		}

		public bool Write(int Value) {
			return Write(BitConverter.GetBytes(Value));
		}

		public bool Write(long Value) {
			return Write(BitConverter.GetBytes(Value));
		}

		public bool Write(char c) {
			return Write(System.Text.Encoding.UTF8.GetBytes(new char[] { c }));
		}

		public bool Write(string Text) {
			if (Text == null)
				return true;
			return Write(System.Text.Encoding.UTF8.GetBytes(Text));
		}

		public bool WriteLine(string Text) {
			return Write(Text + Environment.NewLine);
		}

		public bool ConnectionStateHasChanged() {
			lock (object_lock) {
				return current_connection_state != previous_connection_state;
			}
		}

		public void CancelIdleDetection() {
			removeClientForIdleDetection(this);
		}

		public void CancelHeartbeat() {
			removeClientForHeartbeat(this);
		}

		public static void KeepAlive(Socket Socket, uint KeepAliveIntervalInMilliseconds) {
			KeepAlive(Socket, true, KeepAliveIntervalInMilliseconds, KeepAliveIntervalInMilliseconds);
		}

		public static void KeepAlive(Socket Socket, bool Enable, uint KeepAliveIntervalInMilliseconds) {
			KeepAlive(Socket, Enable, KeepAliveIntervalInMilliseconds, KeepAliveIntervalInMilliseconds);
		}

		public static void KeepAlive(Socket Socket, bool Enable, uint KeepAliveTimeInMilliseconds, uint KeepAliveIntervalInMilliseconds) {
			if (Socket == null)
				throw new NullReferenceException("socket is null");
			
			byte[] SIO_KEEPALIVE_VALS = new byte[12];
			byte[] result = new byte[4];

			//Bytes 0-3 are enable: 1 = true, 0 = false
			//Bytes 4-7 are the time in milliseconds
			//Bytes 8-12 are the interval in milliseconds
			Array.Copy(BitConverter.GetBytes((Enable ? (uint)1 : (uint)0)), 0, SIO_KEEPALIVE_VALS, 0, 4);
			Array.Copy(BitConverter.GetBytes(KeepAliveTimeInMilliseconds), 0, SIO_KEEPALIVE_VALS, 4, 4);
			Array.Copy(BitConverter.GetBytes(KeepAliveIntervalInMilliseconds), 0, SIO_KEEPALIVE_VALS, 8, 4);
			Socket.IOControl(IOControlCode.KeepAliveValues, SIO_KEEPALIVE_VALS, result);
		}
		#endregion

		#region Connect/Send/Disconnect
		private bool innerConnect(string HostName, int Port, int ConnectWaitTimeInMilliseconds, bool UseHeartbeat, int HeartbeatInterval, bool AutomaticallyReconnect, int ReconnectWaitTimeInMilliseconds, bool UseIdle, int IdleWaitTimeInMilliseconds) {
			if (disposed)
				return false;

			if (is_connected)
				return true;

			please_stop = false;
			previous_connection_state = current_connection_state;

			last_connect_wait_time = ConnectWaitTimeInMilliseconds;
			last_host_name = HostName;
			last_port = Port;
			last_use_heartbeat = UseHeartbeat;
			last_heartbeat_interval = HeartbeatInterval;
			last_automatically_reconnect = AutomaticallyReconnect;
			last_reconnect_wait_time = ReconnectWaitTimeInMilliseconds;
			last_use_idle = UseIdle;
			last_idle_wait_time = IdleWaitTimeInMilliseconds;

			//Debug.WriteLine("ATTEMPTING CONNECT");

			connect(
				this,
				data,
				HostName,
				Port,
				ConnectWaitTimeInMilliseconds,
				(AsyncSocketInfo socket_info, SocketAsyncEventArgs args, Socket connect_socket) => {
					//Connected
					lock (object_lock) {
						is_connected = true;
						current_connection_state = ConnectionState.Open;

						removeClientForReconnect(this);

						current_async_args = args;
						current_socket_info = socket_info;
						current_async_socket = connect_socket;

						if (UseHeartbeat) {
							addClientForHeartbeat(
								this,
								socket_info,
								args,
								connect_socket,
								HeartbeatInterval,
								(AsyncSocketInfo heartbeat_socket_info, SocketAsyncEventArgs heartbeat_args, Socket heartbeat_socket) => {
									OnHeartbeat(data);
									NotifyHeartbeat();

									//Debug.WriteLine("HEARTBEAT");
								}
							);
						}

						//Notify subclasses that a connection has been established so they can 
						//perform any setup before reading from the socket begins.
						OnConnect(data);

						//Our receive buffer has been setup and we're almost ready to start 
						//processing bytes, but first let other, public interested parties 
						//know that a valid connection has been established.
						NotifyConnectionOpened();

						//Debug.WriteLine("CONNECTED");
						
						//We want to do this AFTER notifying interested parties that we've connected 
						//so they have plenty of time to do whatever processing they need before we 
						//perform idle detection.
						if (UseIdle) {
							//When doing idle detection, this will ensure that our last data recv'd time 
							//is reset to when the connection occurred.
							last_received_data_tick_count = Environment.TickCount & int.MaxValue;

							//Add our client to the list.
							addClientForIdleDetection(
								this,
								socket_info,
								args,
								connect_socket,
								IdleWaitTimeInMilliseconds,
								(AsyncSocketInfo idle_socket_info, SocketAsyncEventArgs idle_args, Socket idle_socket) => {
									OnIdle(data);
									NotifyIdle();

									//Debug.WriteLine("IDLE");
								}
							);
						}

						//Begin receiving data on the socket.
						scheduleReceive(socket_info, args, connect_socket);
					}
				},
				(AsyncSocketInfo socket_info, SocketAsyncEventArgs args, Socket recv_socket, byte[] buffer, int offset, int bytes_read) => {
					//Data was received on the socket. Forward it on.
					//If we no longer want to receive data, then return false to instruct our 
					//caller that we're now done.
					if (please_stop)
						return false;

					//Keep track of when data comes in. Used in idle detection.
					last_received_data_tick_count = Environment.TickCount & int.MaxValue;

					//Let subclasses know about the data.
					OnRead(data, buffer, offset, bytes_read, buffer.Length);
					NotifyReceived(buffer, offset, bytes_read);

					//Debug.WriteLine("DATA RECVD: " + bytes_read + " byte(s)");// + Convert.ToString(.ASCII.GetString(buffer, offset, bytes_read));
					//for (int i = 0; i < bytes_read; ++i)
					//	Debug.Write(buffer[i] + "/" + System.Text.Encoding.UTF8.GetString(buffer, offset + i, 1) + " ");
					//Debug.WriteLine("");

					return true;
				}, 
				(AsyncSocketInfo socket_info, SocketAsyncEventArgs args, Socket send_socket, byte[] buffer, int offset, int bytes_sent) => {
					//Debug.WriteLine("DATA SENT: " + bytes_sent + " byte(s)");
					//for (int i = 0; i < bytes_sent; ++i)
					//	Debug.Write(buffer[i] + "/" + System.Text.Encoding.UTF8.GetString(buffer, offset + i, 1) + " ");
					//Debug.WriteLine("");

					OnSend(data, buffer, offset, bytes_sent, buffer.Length);
					NotifySent(buffer, offset, bytes_sent);

					return true;
				}, 
				(AsyncSocketInfo socket_info, SocketAsyncEventArgs args, Socket disconnect_socket) => {
					//Disconnected.
					//This can be called even if there was no successful connect -- in which case 
					//it's intended to do some cleanup only.

					bool was_connected = is_connected;

					previous_connection_state = (is_connected ? ConnectionState.Open : current_connection_state != ConnectionState.Unknown ? ConnectionState.Closed : ConnectionState.Unknown);
					current_connection_state = ConnectionState.Closed;

					current_async_args = null;
					current_socket_info = null;
					current_async_socket = null;

					is_connected = false;

					//If the client isn't registered for idle detection, this 
					//won't do anything.
					removeClientForIdleDetection(this);

					//This client may not be registered for a heartbeat in which 
					//case this will do nothing.
					removeClientForHeartbeat(this);

					if (was_connected) {
						OnDisconnect(data);
						NotifyConnectionClosed();
						
						//Debug.WriteLine("DISCONNECTED");
					} else {
						OnUnsuccessfulConnection(data);
						NotifyUnsuccessfulConnection();

						//Debug.WriteLine("UNSUCCESSFUL CONNECTION");
					}

					if (args != null)
						args.Dispose();

					if (socket_info != null)
						socket_info.Cleanup();

					if (!please_stop && AutomaticallyReconnect)
						ScheduleReconnect(ReconnectWaitTimeInMilliseconds);
				}
			);

			return false;
		}

		private bool innerSend(byte[] buffer, int offset, int size) {
			return innerSend(current_socket_info, current_async_args, current_async_socket, buffer, offset, size);
		}

		private bool innerSend(AsyncSocketInfo socket_info, SocketAsyncEventArgs args, Socket socket, byte[] buffer, int offset, int size) {
			if (please_stop || !is_connected || socket_info == null || args == null || socket == null)
				return false;
			
			using (SocketAsyncEventArgs send_args = new SocketAsyncEventArgs()) {
				send_args.UserToken = socket_info;
				send_args.SetBuffer(buffer, offset, size);
				send_args.Completed += socket_info.SendCompleted;

				if (!socket.SendAsync(send_args))
					socket_info.SendCompleted(this, send_args);
			}
			
			return true;
		}

		private bool innerDisconnect() {
			return innerDisconnect(current_socket_info, current_async_socket);
		}

		private bool innerDisconnect(AsyncSocketInfo socket_info, Socket socket) {
			if (please_stop)
				return true;

			//User requested an explicit disconnect. Don't attempt to 
			//reconnect unless explicitly told to do so.
			removeClientForReconnect(this);

			//This client may not be registered for idle detection in which 
			//case this will do nothing. Yes this appears twice.
			removeClientForIdleDetection(this);

			//This client may not be registered for a heartbeat in which 
			//case this will do nothing. Yes this appears twice.
			removeClientForHeartbeat(this);

			if (!is_connected || socket_info == null || socket == null)
				return true;

			please_stop = true;

			SocketAsyncEventArgs dis_args = new SocketAsyncEventArgs();
			dis_args.Completed += socket_info.DisconnectCompleted;
			dis_args.UserToken = socket_info;

			if (!socket.DisconnectAsync(dis_args))
				socket_info.DisconnectCompleted(this, dis_args);

			return true;
		}
		#endregion

		#region Miscellaneous (Reconnect/Heartbeat/Idle/Socket)
		#region Reconnect
		//This timer allows us to use one thread to check for pending reconnect requests and then run the 
		//request once their deadline has been reached.

		#region Constants
		//Describes how often we'll check to see if any of our scheduled reconnects is ready to go.
		private const int RECONNECT_CHECK_RESOLUTION = 100 /* 100 milliseconds */;
		#endregion

		#region Variables
		private static readonly Object reconnect_lock = new Object();
		private static readonly LinkedList<ReconnectData> reconnect_clients = new LinkedList<ReconnectData>();
		private static bool reconnect_thread_please_stop = false;
		private static readonly ManualResetEvent reconnect_thread_wait = new ManualResetEvent(false);
		#endregion

		#region Classes
		private class ReconnectData {
			public AsyncTcpClient Client;
			public long EndTime;
			public int ConnectWaitTime;
			public int TimeToWait;
			public bool UseHeartbeat;
			public int HeartbeatInterval;
			public bool AutomaticallyReconnect;
			public int ReconnectWaitTime;
			public bool UseIdle;
			public int IdleWaitTime;

			public ReconnectData(AsyncTcpClient Client, int ConnectWaitTime, int TimeToWait, bool UseHeartbeat, int HeartbeatInterval, bool AutomaticallyReconnect, int ReconnectWaitTime, bool UseIdle, int IdleWaitTime) {
				this.Client = Client;
				this.ConnectWaitTime = ConnectWaitTime;
				this.TimeToWait = TimeToWait;
				this.UseHeartbeat = UseHeartbeat;
				this.HeartbeatInterval = HeartbeatInterval;
				this.AutomaticallyReconnect = AutomaticallyReconnect;
				this.ReconnectWaitTime = ReconnectWaitTime;
				this.UseIdle = UseIdle;
				this.IdleWaitTime = IdleWaitTime;
				this.EndTime = -1L;
			}
		}
		#endregion

		private static void scheduleReconnect(AsyncTcpClient Client, int ConnectWaitTime, int TimeToWaitInMilliseconds, bool UseHeartbeat, int HeartbeatInterval, bool AutomaticallyReconnect, int ReconnectWaitTime, bool UseIdle, int IdleWaitTime) {
			scheduleReconnect(new ReconnectData(Client, ConnectWaitTime, TimeToWaitInMilliseconds, UseHeartbeat, HeartbeatInterval, AutomaticallyReconnect, ReconnectWaitTime, UseIdle, IdleWaitTime));
		}

		private static void scheduleReconnect(ReconnectData Data) {
			if (Data.Client == null)
				return;

			lock (reconnect_lock) {
				bool wasEmpty = (reconnect_clients.Count <= 0);

				reconnect_clients.AddLast(Data);

				//If this is the first client we're adding, then we want to start a thread that will periodically issue a 
				//heartbeat and detect clients that may have forcefully disconnected.
				if (wasEmpty) {
					ManualResetEvent reconnect_thread_started = new ManualResetEvent(false);
					reconnect_thread_started.Reset();

					reconnect_thread_please_stop = false;
					reconnect_thread_wait.Reset();
					
					Thread reconnect_thread = new Thread((object param) => {
						Stopwatch stopwatch = null;
						bool affinitySuccess = false;

						try {
							//Setting thread affinity is important because using QueryPerformanceCounter() on 
							//Windows can actually go *back* in time on some types of hardware because threads 
							//are scheduled on different processors/cores at different times (although these are 
							//typically indicative of a hardware bug/problem).
							beginThreadAffinity(out affinitySuccess);

							stopwatch = new Stopwatch();
							stopwatch.Reset();

							//Notify creating thread that this thread has started.
							reconnect_thread_started.Set();

							stopwatch.Start();

							do {
								if (reconnect_thread_please_stop)
									break;

								lock (reconnect_lock) {
									//Loop through our clients and see if any are scheduled to be reconnected now.
									//They'll be removed from the linked list automatically.
									removeWhere(
										reconnect_clients, 
										(ReconnectData data) => {
											//Has the end time been set?
											//Remember that other threads could be adding new objects at any time. So it's 
											//possible (and very likely) that the number of clients will increase from one 
											//loop to the next.
											if (data.EndTime < 0L) {
												data.EndTime = (long)data.TimeToWait + stopwatch.ElapsedMilliseconds;
											}

											//Check to see if our stopwatch has moved beyond this client's scheduled end time.
											//If so, then reconnect and return a flag indicating that you'd like this one to 
											//be removed from the list.
											if (data.EndTime <= stopwatch.ElapsedMilliseconds) {
												reconnect(data.Client, data.ConnectWaitTime, data.TimeToWait, data.UseHeartbeat, data.HeartbeatInterval, data.AutomaticallyReconnect, data.ReconnectWaitTime, data.UseIdle, data.IdleWaitTime);
												return true;
											}


											//If the stopwatch wrapped around for some reason, then go ahead and try him 
											//anyway.
											if (data.EndTime < 0 || stopwatch.ElapsedMilliseconds < 0) {
												reconnect(data.Client, data.ConnectWaitTime, data.TimeToWait, data.UseHeartbeat, data.HeartbeatInterval, data.AutomaticallyReconnect, data.ReconnectWaitTime, data.UseIdle, data.IdleWaitTime);
												return true;
											}

											return false;
										}
									);
									
									//If there's no more work to do, then get out of this thread.
									//It will be restarted later on.
									if (reconnect_clients.Count <= 0)
										break;
								}
							} while (!reconnect_thread_wait.WaitOne(RECONNECT_CHECK_RESOLUTION));
						} catch {
						} finally {
							if (stopwatch != null)
								stopwatch.Stop();

							reconnect_clients.Clear();

							if (affinitySuccess)
								endThreadAffinity();
						}
					});
					reconnect_thread.Name = "AsyncTcpClient: Reconnect";
					reconnect_thread.IsBackground = true;
					reconnect_thread.Priority = ThreadPriority.BelowNormal;
					reconnect_thread.Start();

					//Wait for the reconnect thread to notify us that it has started up.
					reconnect_thread_started.WaitOne();
					reconnect_thread_started.Close();
				}
			}
		}

		private static void removeClientForReconnect(AsyncTcpClient Client) {
			if (Client == null)
				return;

			//Locate client and remove it.
			//lock (reconnect_lock) {
				bool found = false;
				removeWhere(
					reconnect_clients,
					(ReconnectData Potential) => {
						if (Potential.Client == Client) {
							found = true;
							return true;
						}
						return false;
					}
				);
				if (found)
					reconnectClientRemoved();
			//}
		}

		private static void removeClientForReconnect(ReconnectData Data) {
			if (Data.Client == null)
				return;

			removeWhere(
				reconnect_clients,
				(ReconnectData Potential) => {
					return (Potential == Data || Potential.Client == Data.Client);
				}
			);

			reconnectClientRemoved();
		}

		private static void reconnectClientRemoved() {
			if (reconnect_clients.Count > 0)
				return;

			//Only stop if this is the last client.

			//Ask the reconnect thread to stop nicely.
			reconnect_thread_please_stop = true;
			reconnect_thread_wait.Set();

			//Wait until the thread signals that it's stopped processing.
			//reconnectThreadStopped.WaitOne();
		}

		private static void reconnect(AsyncTcpClient Client, int ConnectWaitTime, int TimeToWait, bool UseHeartbeat, int HeartbeatInterval, bool AutomaticallyReconnect, int ReconnectWaitTime, bool UseIdle, int IdleWaitTime) {
			if (Client == null)
				return;

			lock (Client.object_lock) {
				try {
					Client.innerConnect(Client.last_host_name, Client.last_port, ConnectWaitTime, UseHeartbeat, HeartbeatInterval, AutomaticallyReconnect, ReconnectWaitTime, UseIdle, IdleWaitTime);
				} catch {
				}
			}
		}
		#endregion

		#region Heartbeat
		#region Constants
		private const int HEARTBEAT_CHECK_RESOLUTION = 100 /* 100 milliseconds */;
		protected static readonly byte[] EMPTY_BYTE = new byte[1];
		protected const int WSAEWOULDBLOCK = 10035;
		#endregion

		#region Variables
		private static readonly Object heartbeat_lock = new Object();
		private static readonly LinkedList<HeartbeatData> heartbeat_clients = new LinkedList<HeartbeatData>();
		private static bool heartbeat_thread_please_stop = false;
		private static readonly ManualResetEvent heartbeat_thread_wait = new ManualResetEvent(false);
		#endregion

		#region Delegates
		private delegate void InnerHeartbeatCallbackDelegate(AsyncSocketInfo socket_info, SocketAsyncEventArgs args, Socket socket);
		#endregion

		#region Classes
		private class HeartbeatData {
			public static readonly HeartbeatData Empty = new HeartbeatData(null, null, null, null, 0, null);

			public AsyncTcpClient Client;
			public AsyncSocketInfo Info;
			public SocketAsyncEventArgs Args;
			public Socket Socket;
			public int Interval;
			public long EndTime;
			public InnerHeartbeatCallbackDelegate Callback;

			public HeartbeatData(AsyncTcpClient Client, AsyncSocketInfo Info, SocketAsyncEventArgs Args, Socket Socket, int Interval, InnerHeartbeatCallbackDelegate Callback) {
				this.Args = Args;
				this.Info = Info;
				this.Client = Client;
				this.Socket = Socket;
				this.Interval = Interval;
				this.Callback = Callback;
				this.EndTime = -1L;
			}
		}
		#endregion

		private static void addClientForHeartbeat(AsyncTcpClient Client, AsyncSocketInfo Info, SocketAsyncEventArgs Args, Socket Socket, int Interval, InnerHeartbeatCallbackDelegate Callback) {
			if (Client == null)
				return;
			addClientForHeartbeat(new HeartbeatData(Client, Info, Args, Socket, Interval, Callback));
		}

		private static void addClientForHeartbeat(HeartbeatData Data) {
			if (Data.Client == null)
				return;

			lock (heartbeat_lock) {
				bool was_empty = (heartbeat_clients.Count <= 0);

				heartbeat_clients.AddLast(Data);

				//If this is the first client we're adding, then we want to start a thread that will periodically issue a 
				//heartbeat and detect clients that may have forcefully disconnected.
				if (was_empty) {
					ManualResetEvent heartbeat_thread_started = new ManualResetEvent(false);
					heartbeat_thread_started.Reset();

					heartbeat_thread_please_stop = false;
					heartbeat_thread_wait.Reset();

					Thread heartbeat_thread = new Thread((object param) => {
						bool affinitySuccess = false;

						try {
							//Setting thread affinity is important because using QueryPerformanceCounter() on 
							//Windows can actually go *back* in time on some types of hardware because threads 
							//are scheduled on different processors/cores at different times (although these are 
							//typically indicative of a hardware bug/problem).
							beginThreadAffinity(out affinitySuccess);

							//Notify creating thread that this thread has started.
							heartbeat_thread_started.Set();

							do {
								if (heartbeat_thread_please_stop)
									break;

								lock (heartbeat_lock) {
									//Loop through our clients and see if any are scheduled to run a heartbeat.
									//They'll be removed from the linked list automatically.
									removeWhere(
										heartbeat_clients,
										(HeartbeatData data) => {
											//Has the end time been set?
											//Remember that other threads could be adding new objects at any time. So it's 
											//possible (and very likely) that the number of clients will increase from one 
											//loop to the next.
											if (data.EndTime < 0L) {
												//Schedule the next time this needs to run
												data.EndTime = (long)data.Interval + Environment.TickCount;
											}

											//Check to see if our stopwatch has moved beyond this client's scheduled end time.
											//If so, then return a flag indicating that you'd like this one to 
											//be removed from the list.
											if (data.EndTime <= Environment.TickCount) {
												heartbeat(data);

												//Schedule the next time this needs to run
												data.EndTime = (long)data.Interval + Environment.TickCount;
												return false;
											}


											//If the tick count wrapped around for some reason, then go ahead and try him 
											//anyway.
											if (data.EndTime < 0 || Environment.TickCount < 0) {
												heartbeat(data);

												//Schedule the next time this needs to run
												data.EndTime = (long)data.Interval + Environment.TickCount;
												return false;
											}

											return false;
										}
									);

									//If there's no more work to do, then get out of this thread.
									//It will be restarted later on.
									if (heartbeat_clients.Count <= 0)
										break;
								}
							} while (!heartbeat_thread_wait.WaitOne(HEARTBEAT_CHECK_RESOLUTION));
						} catch {
						} finally {
							heartbeat_clients.Clear();

							if (affinitySuccess)
								endThreadAffinity();
						}
					});
					heartbeat_thread.Name = "AsyncTcpClient: Heartbeat";
					heartbeat_thread.IsBackground = true;
					heartbeat_thread.Priority = ThreadPriority.AboveNormal;
					heartbeat_thread.Start();

					//Wait for the reconnect thread to notify us that it has started up.
					heartbeat_thread_started.WaitOne();
					heartbeat_thread_started.Close();
				}
			}
		}

		private static void removeClientForHeartbeat(AsyncTcpClient Client) {
			if (Client == null)
				return;

			//Locate client and remove it.
			//lock (heartbeat_lock) {
				bool found = false;
				removeWhere(
					heartbeat_clients,
					(HeartbeatData Potential) => {
						if (Potential.Client == Client) {
							found = true;
							return true;
						}
						return false;
					}
				);
				if (found)
					heartbeatClientRemoved();
			//}
		}

		private static void removeClientForHeartbeat(HeartbeatData Data) {
			if (Data.Client == null)
				return;

			removeWhere(
				heartbeat_clients,
				(HeartbeatData Potential) => {
					return (Potential == Data || Potential.Client == Data.Client || Potential.Socket == Data.Socket);
				}
			);

			heartbeatClientRemoved();
		}

		private static void heartbeatClientRemoved() {
			if (heartbeat_clients.Count > 0)
				return;

			//Only stop if this is the last client.

			//Ask the heartbeat thread to stop nicely.
			heartbeat_thread_please_stop = true;
			heartbeat_thread_wait.Set();

			//Wait until the thread signals that it's stopped processing.
			//heartbeatThreadStopped.WaitOne();
		}

		private static void heartbeat(HeartbeatData HeartbeatClient) {
			if (HeartbeatClient == null || HeartbeatClient.Client == null)
				return;

			HeartbeatClient.Callback(HeartbeatClient.Info, HeartbeatClient.Args, HeartbeatClient.Socket);
		}
		#endregion

		#region Idle
		#region Constants
		private const int IDLE_CHECK_RESOLUTION = 100 /* 100 milliseconds */;
		#endregion

		#region Variables
		private static readonly Object idle_lock = new Object();
		private static readonly LinkedList<IdleData> idle_clients = new LinkedList<IdleData>();
		private static bool idle_thread_please_stop = false;
		private static readonly ManualResetEvent idle_thread_wait = new ManualResetEvent(false);
		#endregion

		#region Delegates
		private delegate void InnerIdleCallbackDelegate(AsyncSocketInfo socket_info, SocketAsyncEventArgs args, Socket socket);
		#endregion

		#region Classes
		private class IdleData {
			public static readonly IdleData Empty = new IdleData(null, null, null, null, 0, null);

			public AsyncTcpClient Client;
			public AsyncSocketInfo Info;
			public SocketAsyncEventArgs Args;
			public Socket Socket;
			public int Interval;
			public long EndTime;
			public InnerIdleCallbackDelegate Callback;

			public IdleData(AsyncTcpClient Client, AsyncSocketInfo Info, SocketAsyncEventArgs Args, Socket Socket, int Interval, InnerIdleCallbackDelegate Callback) {
				this.Args = Args;
				this.Info = Info;
				this.Client = Client;
				this.Socket = Socket;
				this.Interval = Interval;
				this.Callback = Callback;
				this.EndTime = -1L;
			}
		}
		#endregion

		private static void addClientForIdleDetection(AsyncTcpClient Client, AsyncSocketInfo Info, SocketAsyncEventArgs Args, Socket Socket, int Interval, InnerIdleCallbackDelegate Callback) {
			if (Client == null)
				return;
			addClientForIdleDetection(new IdleData(Client, Info, Args, Socket, Interval, Callback));
		}

		private static void addClientForIdleDetection(IdleData Data) {
			if (Data.Client == null)
				return;

			lock (idle_lock) {
				bool wasEmpty = (idle_clients.Count <= 0);

				idle_clients.AddLast(Data);

				//If this is the first client we're adding, then we want to start a thread that will periodically issue a 
				//heartbeat and detect clients that may have forcefully disconnected.
				if (wasEmpty) {
					ManualResetEvent idle_thread_started = new ManualResetEvent(false);
					idle_thread_started.Reset();

					idle_thread_please_stop = false;
					idle_thread_wait.Reset();

					Thread idle_thread = new Thread((object param) => {
						int last_tick_count = -1;
						int tick_count = -1;

						try {
							//This thread does not care about thread affinity.

							//Notify creating thread that this thread has started.
							idle_thread_started.Set();

							do {
								if (idle_thread_please_stop)
									break;

								lock (idle_lock) {
									//The tick count will cycle around roughly every 24.9 days.
									//When that happens we need to be sure we aren't detecting things as idle when 
									//they're really not. And then we'll need to update each client so 
									//it's now in sync with the cycle. We do that by resetting it's tick count to 
									//be negative before the cycle.
									//
									//Anding it with int.MaxValue ensures that it will always be between 0 and 
									//int.MaxValue (it takes off the sign bit).
									tick_count = Environment.TickCount & int.MaxValue;
									if (last_tick_count < 0)
										last_tick_count = tick_count;

									forEach(
										idle_clients,
										(IdleData data) => {
											//Detect a cycle.
											//If the last recv'd data was from before the cycle, then update it 
											//to be negative the interval before the current tick_count.
											if (tick_count < last_tick_count && tick_count < data.Client.last_received_data_tick_count)
												data.Client.last_received_data_tick_count = tick_count - Math.Abs(last_tick_count - data.Client.last_received_data_tick_count);

											//Debug.WriteLine("tick_count: " + tick_count + ", last_received_data_tick_count: " + data.Client.last_received_data_tick_count);

											//See if we've exceeded the max idle interval. If so, then it's time to fire 
											//off the callback and notify interested parties.
											if (Math.Abs(tick_count - data.Client.last_received_data_tick_count) >= data.Interval) {
												idle(data);
												data.Client.last_received_data_tick_count = tick_count;
											}
										}
									);

									last_tick_count = tick_count;

									//If there's no more work to do, then get out of this thread.
									//It will be restarted later on.
									if (idle_clients.Count <= 0)
										break;
								}
							} while (!idle_thread_wait.WaitOne(IDLE_CHECK_RESOLUTION));
						} catch {
						} finally {
							idle_clients.Clear();
						}
					});
					idle_thread.Name = "AsyncTcpClient: Idle";
					idle_thread.IsBackground = true;
					idle_thread.Priority = ThreadPriority.BelowNormal;
					idle_thread.Start();

					//Wait for the idle thread to notify us that it has started up.
					idle_thread_started.WaitOne();
					idle_thread_started.Close();
				}
			}
		}

		private static void removeClientForIdleDetection(AsyncTcpClient Client) {
			if (Client == null)
				return;

			//Locate client and remove it.
			//lock (idle_lock) {
				bool found = false;
				removeWhere(
					idle_clients,
					(IdleData Potential) => {
						if (Potential.Client == Client) {
							found = true;
							return true;
						}
						return false;
					}
				);
				if (found)
					idleClientRemoved();
			//}
		}

		private static void removeClientForIdleDetection(IdleData Data) {
			if (Data.Client == null)
				return;

			removeWhere(
				idle_clients,
				(IdleData Potential) => {
					return (Potential == Data || Potential.Client == Data.Client || Potential.Socket == Data.Socket);
				}
			);

			idleClientRemoved();
		}

		private static void idleClientRemoved() {
			if (idle_clients.Count > 0)
				return;

			//Only stop if this is the last client.

			//Ask the idle thread to stop nicely.
			idle_thread_please_stop = true;
			idle_thread_wait.Set();

			//Wait until the thread signals that it's stopped processing.
			//idle_thread_stopped.WaitOne();
		}

		private static void idle(IdleData IdleClient) {
			if (IdleClient == null || IdleClient.Client == null)
				return;

			IdleClient.Callback(IdleClient.Info, IdleClient.Args, IdleClient.Socket);
		}
		#endregion

		#region Socket
		#region Delegates
		private delegate void InnerConnectCallbackDelegate(AsyncSocketInfo socket_info, SocketAsyncEventArgs args, Socket socket);
		private delegate void InnerDisconnectCallbackDelegate(AsyncSocketInfo socket_info, SocketAsyncEventArgs args, Socket socket);
		private delegate bool InnerReceiveCallbackDelegate(AsyncSocketInfo socket_info, SocketAsyncEventArgs args, Socket socket, byte[] buffer, int offset, int bytes_read);
		private delegate bool InnerSendCallbackDelegate(AsyncSocketInfo socket_info, SocketAsyncEventArgs args, Socket socket, byte[] buffer, int offset, int bytes_sent);
		#endregion

		#region Classes
		private class AsyncSocketInfo {
			public EventHandler<SocketAsyncEventArgs> DemultiplexOperation;
			public EventHandler<SocketAsyncEventArgs> ConnectCompleted;
			public EventHandler<SocketAsyncEventArgs> ReceiveCompleted;
			public EventHandler<SocketAsyncEventArgs> SendCompleted;
			public EventHandler<SocketAsyncEventArgs> DisconnectCompleted;
			public object Data;
			public Socket Socket;
			public AsyncTcpClient Client;

			public void Cleanup() {
			}
		}
		#endregion

		private static Socket setupSocket(AsyncTcpClient Client, object Data, Socket Socket) {
			//Socket.NoDelay = true;
			//Socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.KeepAlive, 1);
			Client.OnSocketInit(Data, Socket);
			return Socket;
		}

		//[DebuggerStepThrough]
		private static Socket connect(AsyncTcpClient Client, object Data, string HostName, int Port, int ConnectWaitTimeInMilliseconds, InnerConnectCallbackDelegate ConnectCallback, InnerReceiveCallbackDelegate ReceiveCallback, InnerSendCallbackDelegate SendCallback, InnerDisconnectCallbackDelegate DisconnectCallback) {
			#region DNS query and locate potential endpoints
			Socket ret = null;
			object ret_lock = new object();
			IPHostEntry host_entry = Dns.GetHostEntry(HostName);
			int endpoint_count = host_entry.AddressList.Length;
			Semaphore connect_sem = new Semaphore(0, endpoint_count);
			Socket[] sockets = new Socket[endpoint_count];
			#endregion

			//Attempt to connect to every returned end point.
			for (int i = 0; i < endpoint_count; ++i) {
				IPEndPoint ipe = new IPEndPoint(host_entry.AddressList[i], Port);
				Socket socket = sockets[i] = setupSocket(Client, Data, new Socket(ipe.AddressFamily, SocketType.Stream, ProtocolType.Tcp));

				AsyncSocketInfo async_socket_info = new AsyncSocketInfo() {
					Data = Data,
					Client = Client,
					Socket = socket, 

					DemultiplexOperation = (object sender, SocketAsyncEventArgs e) => {
						#region Demultiplex
						AsyncSocketInfo socket_info = e.UserToken as AsyncSocketInfo;

						switch (e.LastOperation) {
							case SocketAsyncOperation.Receive:
								socket_info.ReceiveCompleted(sender, e);
								break;
							case SocketAsyncOperation.Send:
								socket_info.SendCompleted(sender, e);
								break;
							case SocketAsyncOperation.Connect:
								socket_info.ConnectCompleted(sender, e);
								break;
							case SocketAsyncOperation.Disconnect:
								socket_info.DisconnectCompleted(sender, e);
								break;
						}
						#endregion
					}, 
					ConnectCompleted = (object sender, SocketAsyncEventArgs e) => {
						#region Connect
						AsyncSocketInfo socket_info = e.UserToken as AsyncSocketInfo;
						bool connected = false;
						bool final_attempt = false;

						//Remove the connect delegate so it's not called again.
						//The "Completed" event will be called for any successful I/O completion port event.
						//That could be a successful connect, receive, send, or disconnect. We want to 
						//ensure that our connect callback is called only once and then never again for 
						//this connection, so we remove it from the event callbacks.
						e.Completed -= socket_info.ConnectCompleted;
						
						try {
							//We have a valid connection. But it's possible that we now have multiple 
							//valid connections. Choose the first successful one and then in our caller 
							//we'll take care to shutdown and close any sockets that connected but aren't 
							//being used.
							if (e.SocketError == SocketError.Success && socket.Connected) {
								//Ensure that only one successful connection attempt updates the 
								//valid returned socket.
								
								lock (ret_lock) {
									if (ret == null) {
										ret = socket;
										connected = true;
									}
								}
							}
						} catch(SocketException) {
						} finally {
							//If this is the last endpoint attempt, this will release the main thread who is waiting
							//to see if any of the attempts were successful.
							try {
								final_attempt = (connect_sem.Release() <= 0);
							} catch {
								//It's possible the semaphore has been closed due to the connect timeout.
							}
						}

						//At this point, we've selected one successfully connected end point. The caller 
						//should be able to proceed merrily on its way and we're now executing within an 
						//I/O completion port worker thread. Let's let our users know where we're at.
						if (connected) {
							//The buffer for send/receive is pulled from our buffer manager.
							socket_info.Client.buffer_manager.PartitionBuffer(e);

							e.Completed += socket_info.ReceiveCompleted;

							if (ConnectCallback != null)
								ConnectCallback(socket_info, e, socket);
						} else {
							//if (final_attempt)
							//	socket_info.DisconnectCompleted(sender, e);
						}
						#endregion
					},
					ReceiveCompleted = (object sender, SocketAsyncEventArgs e) => {
						#region Receive
						AsyncSocketInfo socket_info = e.UserToken as AsyncSocketInfo;
						Socket recv_socket = socket_info.Socket;

						receive_try_again:
						if (e.SocketError != SocketError.Success || e.BytesTransferred <= 0) {
							//Release our used buffer.
							socket_info.Client.buffer_manager.DisposeBuffer(e);

							SocketAsyncEventArgs dis_args = new SocketAsyncEventArgs();
							dis_args.Completed += socket_info.DisconnectCompleted;
							dis_args.UserToken = socket_info;

							try {
								if (!recv_socket.DisconnectAsync(dis_args))
									socket_info.DisconnectCompleted(sender, dis_args);
							} catch (ObjectDisposedException) {
								//Ignore in this case -- the socket was disconnected or disposed of 
								//elsewhere already.
							}

							return;
						}

						if (ReceiveCallback != null)
							if (!ReceiveCallback(socket_info, e, recv_socket, e.Buffer, e.Offset, e.BytesTransferred))
								return;

						try {
							if (!recv_socket.ReceiveAsync(e))
								goto receive_try_again;
						} catch {
							e.SocketError = SocketError.SocketError;
							goto receive_try_again;
						}
						#endregion
					},
					SendCompleted = (object sender, SocketAsyncEventArgs e) => {
						#region Send
						AsyncSocketInfo socket_info = e.UserToken as AsyncSocketInfo;
						Socket send_socket = socket_info.Socket;

						//send_try_again:
						if (e.SocketError != SocketError.Success || e.BytesTransferred <= 0) {
							//Release our used buffer.
							socket_info.Client.buffer_manager.DisposeBuffer(e);

							SocketAsyncEventArgs dis_args = new SocketAsyncEventArgs();
							dis_args.Completed += socket_info.DisconnectCompleted;
							dis_args.UserToken = socket_info;

							try {
								if (!send_socket.DisconnectAsync(dis_args))
									socket_info.DisconnectCompleted(sender, dis_args);
							} catch (ObjectDisposedException) {
								//Ignore in this case -- the socket was disconnected or disposed of 
								//elsewhere already.
							}

							return;
						}

						if (SendCallback != null)
							if (!SendCallback(socket_info, e, send_socket, e.Buffer, e.Offset, e.BytesTransferred))
								return;
						#endregion
					},
					DisconnectCompleted = (object sender, SocketAsyncEventArgs e) => {
						#region Disconnect
						AsyncSocketInfo socket_info = e.UserToken as AsyncSocketInfo;
						Socket dis_socket = socket_info.Socket;

						try {
							if (dis_socket != null)
								dis_socket.Shutdown(SocketShutdown.Both);
						} catch(SocketException) {
						} catch(ObjectDisposedException) {
						} finally {
							try {
								dis_socket.Close();
							} catch {
							}
						}

						//Release our used buffer.
						socket_info.Client.buffer_manager.DisposeBuffer(e);

						if (DisconnectCallback != null)
							DisconnectCallback(socket_info, e, dis_socket);

						e.Dispose();

						socket_info.Cleanup();
						#endregion
					}
				};

				SocketAsyncEventArgs connect_args = new SocketAsyncEventArgs();
				connect_args.UserToken = async_socket_info;
				connect_args.RemoteEndPoint = ipe;
				connect_args.DisconnectReuseSocket = false;
				connect_args.Completed += async_socket_info.ConnectCompleted;

				//If this is false, then the call completed synchronously.
				if (!socket.ConnectAsync(connect_args))
					async_socket_info.ConnectCompleted(Client, connect_args);
			}

			#region Cleanup
			//Wait for all connection attempts to report back.
			if (endpoint_count <= 0 || !connect_sem.WaitOne(ConnectWaitTimeInMilliseconds))
				ret = null;

			//Clean up unmanaged resources
			connect_sem.Close();

			bool any_connected = (endpoint_count > 0 && ret != null);

			//Shutdown any open sockets that didn't work
			foreach (Socket s in sockets) {
				if (s == ret || s == null)
					continue;
				try {
					s.Shutdown(SocketShutdown.Both);
				} catch {
				} finally {
					try {
						s.Close();
					} catch {
					}
				}
			}
			#endregion

			if (!any_connected)
				if (DisconnectCallback != null)
					DisconnectCallback(null, null, null);

			return ret;
		}

		//[DebuggerStepThrough]
		private void scheduleReceive(AsyncSocketInfo socket_info, SocketAsyncEventArgs args, Socket socket) {
			try {
				while (!socket.ReceiveAsync(args))
					socket_info.ReceiveCompleted(this, args);
			} catch (NullReferenceException) {
				args.SocketError = SocketError.SocketError;
				socket_info.ReceiveCompleted(this, args);
			}
		}
		#endregion
		#endregion
	}
}
