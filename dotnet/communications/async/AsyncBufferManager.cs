using System;
using System.Net.Sockets;
using System.Collections.Generic;

namespace GitHub.DavidHoyt.Utils.Communications.Async {
	///<summary>
	///	Provides a large buffer that can be partitioned to SocketAsyncEventArgs instances for use with 
	///	I/O operations and to prevent fragmenting the heap.
	///</summary>
	public class AsyncBufferManager {
		#region Variables
		private int total_managed_size;
		private byte[] buffer;
		private Stack<int> free_index_pool;
		private int current_index;
		private int chunk_size;
		#endregion

		#region Initialization
		public AsyncBufferManager(int ChunkSize, int TotalManagedSize) {
			if (ChunkSize > TotalManagedSize)
				throw new ArgumentOutOfRangeException("ChunkSize must be less than or equal to TotalManagedSize");
			//if (TotalManagedSize / 2 < ChunkSize)
			//	throw new ArgumentOutOfRangeException("ChunkSize cannot be greater than half of TotalManagedSize");

			this.current_index = 0;
			this.chunk_size = ChunkSize;
			this.total_managed_size = TotalManagedSize;
			this.free_index_pool = new Stack<int>(TotalManagedSize / ChunkSize + 1);
		}
		#endregion

		#region Public Methods
		///<summary>Allocates buffer space used by the buffer pool.</summary>
		public void Initialize() {
			//Create one large buffer that will be divied out to SocketAsyncEventArg objects.
			this.buffer = new byte[total_managed_size];
		}

		///<summary>Partitions a section of the pool for the provided SocketAsyncEventArgs object.</summary>
		///	<returns>True if the buffer was successfully partitioned and allocated. False otherwise.</returns>
		public bool PartitionBuffer(SocketAsyncEventArgs args) {
			if (free_index_pool.Count > 0) {
				args.SetBuffer(buffer, free_index_pool.Pop(), chunk_size);
			} else {
				if ((total_managed_size - chunk_size) < current_index)
					return false;
				args.SetBuffer(buffer, current_index, chunk_size);
				current_index += chunk_size;
			}
			return true;
		}

		///<summary>Removes the buffer from the SocketAsyncEventArgs object and recycles it by placing it back into the pool.</summary>
		public void DisposeBuffer(SocketAsyncEventArgs args) {
			if (args == null || args.Buffer == null || args.Buffer != buffer)
				return;

			free_index_pool.Push(args.Offset);
			args.SetBuffer(null, 0, 0);
		}
		#endregion
	}
}
