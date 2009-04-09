// System.Net.Sockets.Socket.cs
//
// Authors:
//	Phillip Pearson (pp@myelin.co.nz)
//	Dick Porter <dick@ximian.com>
//	Gonzalo Paniagua Javier (gonzalo@ximian.com)
//	Sridhar Kulkarni (sridharkulkarni@gmail.com)
//	Brian Nickel (brian.nickel@gmail.com)
//
// Copyright (C) 2001, 2002 Phillip Pearson and Ximian, Inc.
//    http://www.myelin.co.nz
// (c) 2004-2006 Novell, Inc. (http://www.novell.com)
//

//
// Permission is hereby granted, free of charge, to any person obtaining
// a copy of this software and associated documentation files (the
// "Software"), to deal in the Software without restriction, including
// without limitation the rights to use, copy, modify, merge, publish,
// distribute, sublicense, and/or sell copies of the Software, and to
// permit persons to whom the Software is furnished to do so, subject to
// the following conditions:
// 
// The above copyright notice and this permission notice shall be
// included in all copies or substantial portions of the Software.
// 
// THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND,
// EXPRESS OR IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF
// MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND
// NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS OR COPYRIGHT HOLDERS BE
// LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION
// OF CONTRACT, TORT OR OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION
// WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE SOFTWARE.
//

using System;
using System.Net;
using System.Collections;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using System.Reflection;
using System.IO;
using System.Net.Configuration;
using System.Text;

#if NET_2_0
using System.Collections.Generic;
using System.Net.NetworkInformation;
#if !NET_2_1
using System.Timers;
#endif
#endif

namespace System.Net.Sockets {

	public partial class Socket : IDisposable {

		/*
		 *	These two fields are looked up by name by the runtime, don't change
		 *  their name without also updating the runtime code.
		 */
		private static int ipv4Supported = -1, ipv6Supported = -1;

		static Socket ()
		{
			// initialize ipv4Supported and ipv6Supported
			CheckProtocolSupport ();
		}

		internal static void CheckProtocolSupport ()
		{
			if(ipv4Supported == -1) {
				try {
					Socket tmp = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
					tmp.Close();

					ipv4Supported = 1;
				} catch {
					ipv4Supported = 0;
				}
			}

			if (ipv6Supported == -1) {
#if !NET_2_1
#if NET_2_0 && CONFIGURATION_DEP
				SettingsSection config;
				config = (SettingsSection) System.Configuration.ConfigurationManager.GetSection ("system.net/settings");
				if (config != null)
					ipv6Supported = config.Ipv6.Enabled ? -1 : 0;
#else
				NetConfig config = System.Configuration.ConfigurationSettings.GetConfig("system.net/settings") as NetConfig;
				if (config != null)
					ipv6Supported = config.ipv6Enabled ? -1 : 0;
#endif
#endif
				if (ipv6Supported != 0) {
					try {
						Socket tmp = new Socket(AddressFamily.InterNetworkV6, SocketType.Stream, ProtocolType.Tcp);
						tmp.Close();

						ipv6Supported = 1;
					} catch {
						ipv6Supported = 0;
					}
				}
			}
		}

		public static bool SupportsIPv4 {
			get {
				CheckProtocolSupport();
				return ipv4Supported == 1;
			}
		}

#if NET_2_0
		[ObsoleteAttribute ("Use OSSupportsIPv6 instead")]
#endif
		public static bool SupportsIPv6 {
			get {
				CheckProtocolSupport();
				return ipv6Supported == 1;
			}
		}
#if NET_2_1
		public static bool OSSupportsIPv4 {
			get {
				NetworkInterface[] nics = NetworkInterface.GetAllNetworkInterfaces ();
				
				foreach (NetworkInterface adapter in nics) {
					if (adapter.Supports (NetworkInterfaceComponent.IPv4))
						return true;
				}
				return false;
			}
		}
#endif
#if NET_2_0
		public static bool OSSupportsIPv6 {
			get {
				NetworkInterface[] nics = NetworkInterface.GetAllNetworkInterfaces ();
				
				foreach (NetworkInterface adapter in nics) {
					if (adapter.Supports (NetworkInterfaceComponent.IPv6))
						return true;
				}
				return false;
			}
		}
#endif

		/* the field "socket" is looked up by name by the runtime */
		private IntPtr socket;
		private AddressFamily address_family;
		private SocketType socket_type;
		private ProtocolType protocol_type;
		internal bool blocking=true;
		Thread blocking_thread;
#if NET_2_0
		private bool isbound;
#endif
		/* When true, the socket was connected at the time of
		 * the last IO operation
		 */
		private bool connected;
		/* true if we called Close_internal */
		private bool closed;
		internal bool disposed;

		/*
		 * This EndPoint is used when creating new endpoints. Because
		 * there are many types of EndPoints possible,
		 * seed_endpoint.Create(addr) is used for creating new ones.
		 * As such, this value is set on Bind, SentTo, ReceiveFrom,
		 * Connect, etc.
 		 */
		internal EndPoint seed_endpoint = null;

#if !TARGET_JVM
		// Creates a new system socket, returning the handle
		[MethodImplAttribute(MethodImplOptions.InternalCall)]
		private extern IntPtr Socket_internal(AddressFamily family,
						      SocketType type,
						      ProtocolType proto,
						      out int error);
#endif		
		
		public Socket(AddressFamily family, SocketType type, ProtocolType proto)
		{
			address_family=family;
			socket_type=type;
			protocol_type=proto;
			
			int error;
			
			socket = Socket_internal (family, type, proto, out error);
			if (error != 0)
				throw new SocketException (error);
#if !NET_2_1
			SocketDefaults ();
#endif
		}

		~Socket ()
		{
			Dispose (false);
		}


		public AddressFamily AddressFamily {
			get { return address_family; }
		}

#if !TARGET_JVM
		[MethodImplAttribute(MethodImplOptions.InternalCall)]
		private extern static void Blocking_internal(IntPtr socket,
							     bool block,
							     out int error);
#endif
		public bool Blocking {
			get {
				return(blocking);
			}
			set {
				if (disposed && closed)
					throw new ObjectDisposedException (GetType ().ToString ());

				int error;
				
				Blocking_internal (socket, value, out error);

				if (error != 0)
					throw new SocketException (error);
				
				blocking=value;
			}
		}

		public bool Connected {
			get { return connected; }
			internal set { connected = value; }
		}

		public ProtocolType ProtocolType {
			get { return protocol_type; }
		}
#if NET_2_0
		public bool NoDelay {
			get {
				if (disposed && closed)
					throw new ObjectDisposedException (GetType ().ToString ());

				ThrowIfUpd ();

				return (int)(GetSocketOption (
					SocketOptionLevel.Tcp,
					SocketOptionName.NoDelay)) != 0;
			}

			set {
				if (disposed && closed)
					throw new ObjectDisposedException (GetType ().ToString ());

				ThrowIfUpd ();

				SetSocketOption (
					SocketOptionLevel.Tcp,
					SocketOptionName.NoDelay, value ? 1 : 0);
			}
		}

		public int ReceiveBufferSize {
			get {
				if (disposed && closed) {
					throw new ObjectDisposedException (GetType ().ToString ());
				}
				return((int)GetSocketOption (SocketOptionLevel.Socket, SocketOptionName.ReceiveBuffer));
			}
			set {
				if (disposed && closed) {
					throw new ObjectDisposedException (GetType ().ToString ());
				}
				if (value < 0) {
					throw new ArgumentOutOfRangeException ("value", "The value specified for a set operation is less than zero");
				}
				
				SetSocketOption (SocketOptionLevel.Socket, SocketOptionName.ReceiveBuffer, value);
			}
		}

		public int SendBufferSize {
			get {
				if (disposed && closed) {
					throw new ObjectDisposedException (GetType ().ToString ());
				}
				return((int)GetSocketOption (SocketOptionLevel.Socket, SocketOptionName.SendBuffer));
			}
			set {
				if (disposed && closed) {
					throw new ObjectDisposedException (GetType ().ToString ());
				}
				if (value < 0) {
					throw new ArgumentOutOfRangeException ("value", "The value specified for a set operation is less than zero");
				}
				
				SetSocketOption (SocketOptionLevel.Socket,
						 SocketOptionName.SendBuffer,
						 value);
			}
		}

		public short Ttl {
			get {
				if (disposed && closed) {
					throw new ObjectDisposedException (GetType ().ToString ());
				}
				
				short ttl_val;
				
				if (address_family == AddressFamily.InterNetwork) {
					ttl_val = (short)((int)GetSocketOption (SocketOptionLevel.IP, SocketOptionName.IpTimeToLive));
				} else if (address_family == AddressFamily.InterNetworkV6) {
					ttl_val = (short)((int)GetSocketOption (SocketOptionLevel.IPv6, SocketOptionName.HopLimit));
				} else {
					throw new NotSupportedException ("This property is only valid for InterNetwork and InterNetworkV6 sockets");
				}
				
				return(ttl_val);
			}
			set {
				if (disposed && closed) {
					throw new ObjectDisposedException (GetType ().ToString ());
				}
				
				if (address_family == AddressFamily.InterNetwork) {
					SetSocketOption (SocketOptionLevel.IP, SocketOptionName.IpTimeToLive, value);
				} else if (address_family == AddressFamily.InterNetworkV6) {
					SetSocketOption (SocketOptionLevel.IPv6, SocketOptionName.HopLimit, value);
				} else {
					throw new NotSupportedException ("This property is only valid for InterNetwork and InterNetworkV6 sockets");
				}
			}
		}
#endif
		// Returns the remote endpoint details in addr and port
		[MethodImplAttribute(MethodImplOptions.InternalCall)]
		private extern static SocketAddress RemoteEndPoint_internal(IntPtr socket, out int error);

		public EndPoint RemoteEndPoint {
			get {
				if (disposed && closed)
					throw new ObjectDisposedException (GetType ().ToString ());
				
				/*
				 * If the seed EndPoint is null, Connect, Bind,
				 * etc has not yet been called. MS returns null
				 * in this case.
				 */
				if (seed_endpoint == null)
					return null;
				
				SocketAddress sa;
				int error;
				
				sa=RemoteEndPoint_internal(socket, out error);

				if (error != 0)
					throw new SocketException (error);

				return seed_endpoint.Create (sa);
			}
		}

		protected virtual void Dispose (bool explicitDisposing)
		{
			if (disposed)
				return;

			disposed = true;
			connected = false;
			if ((int) socket != -1) {
				int error;
				closed = true;
				IntPtr x = socket;
				socket = (IntPtr) (-1);
				Close_internal (x, out error);
#if !NET_2_1
				if (blocking_thread != null) {
					blocking_thread.Abort ();
					blocking_thread = null;
				}
#endif
				if (error != 0)
					throw new SocketException (error);
			}
		}

#if NET_2_1
		public void Dispose ()
#else
		void IDisposable.Dispose ()
#endif
		{
			Dispose (true);
			GC.SuppressFinalize (this);
		}

		// Closes the socket
		[MethodImplAttribute(MethodImplOptions.InternalCall)]
		private extern static void Close_internal(IntPtr socket, out int error);

		public void Close ()
		{
			((IDisposable) this).Dispose ();
		}

		// Connects to the remote address
		[MethodImplAttribute(MethodImplOptions.InternalCall)]
		private extern static void Connect_internal(IntPtr sock,
							    SocketAddress sa,
							    out int error);

		public void Connect (EndPoint remote_end)
		{
			if (disposed && closed)
				throw new ObjectDisposedException (GetType ().ToString ());

			if (remote_end == null)
				throw new ArgumentNullException("remote_end");

			if (remote_end is IPEndPoint) {
				IPEndPoint ep = (IPEndPoint) remote_end;
				if (ep.Address.Equals (IPAddress.Any) || ep.Address.Equals (IPAddress.IPv6Any))
					throw new SocketException ((int) SocketError.AddressNotAvailable);
			}

#if NET_2_1
			// Check for SL2.0b1 restrictions
			// - tcp only
			// - SiteOfOrigin
			// - port 4502->4532 + default
			if (protocol_type != ProtocolType.Tcp)
				throw new SocketException ((int) SocketError.AccessDenied);
			//FIXME: replace 80 by Application.Curent.Host.Source.Port
			if (remote_end is IPEndPoint)
				if (((remote_end as IPEndPoint).Port < 4502 || (remote_end as IPEndPoint).Port > 4530) && (remote_end as IPEndPoint).Port != 80)
					throw new SocketException ((int) SocketError.AccessDenied);
			else //unsuported endpoint type
				throw new SocketException ((int) SocketError.AccessDenied);
			//FIXME: check for Application.Curent.Host.Source.DnsSafeHost
#elif NET_2_0
			/* TODO: check this for the 1.1 profile too */
			if (islistening)
				throw new InvalidOperationException ();
#endif

			SocketAddress serial = remote_end.Serialize ();
			int error = 0;

			blocking_thread = Thread.CurrentThread;
			try {
				Connect_internal (socket, serial, out error);
			} catch (ThreadAbortException) {
				if (disposed) {
#if !NET_2_1 //2.1 profile does not contains Thread.ResetAbort
					Thread.ResetAbort ();
#endif
					error = (int) SocketError.Interrupted;
				}
			} finally {
				blocking_thread = null;
			}

			if (error != 0)
				throw new SocketException (error);

			connected=true;

#if NET_2_0
			isbound = true;
#endif
			
			seed_endpoint = remote_end;
		}

#if NET_2_0
		public bool ReceiveAsync (SocketAsyncEventArgs e)
		{
			// NO check is made whether e != null in MS.NET (NRE is thrown in such case)
			//
			// LAME SPEC: the ArgumentException is never thrown, instead an NRE is
			// thrown when e.Buffer and e.BufferList are null (works fine when one is
			// set to a valid object)
			if (disposed && closed)
				throw new ObjectDisposedException (GetType ().ToString ());

			// We do not support recv into multiple buffers yet
			if (e.BufferList != null)
				throw new NotSupportedException ("Mono doesn't support using BufferList at this point.");
			
			e.DoOperation (SocketAsyncOperation.Receive, this);

			// We always return true for now
			return true;
		}

		public bool SendAsync (SocketAsyncEventArgs e)
		{
			// NO check is made whether e != null in MS.NET (NRE is thrown in such case)
			
			if (disposed && closed)
				throw new ObjectDisposedException (GetType ().ToString ());
			if (e.Buffer == null && e.BufferList == null)
				throw new ArgumentException ("Either e.Buffer or e.BufferList must be valid buffers.");

			e.DoOperation (SocketAsyncOperation.Send, this);

			// We always return true for now
			return true;
		}
#endif

		[MethodImplAttribute(MethodImplOptions.InternalCall)]
		extern static bool Poll_internal (IntPtr socket, SelectMode mode, int timeout, out int error);

		/* This overload is needed as the async Connect method
		 * also needs to check the socket error status, but
		 * getsockopt(..., SO_ERROR) clears the error.
		 */
		internal bool Poll (int time_us, SelectMode mode, out int socket_error)
		{
			if (disposed && closed)
				throw new ObjectDisposedException (GetType ().ToString ());

			if (mode != SelectMode.SelectRead &&
			    mode != SelectMode.SelectWrite &&
			    mode != SelectMode.SelectError)
				throw new NotSupportedException ("'mode' parameter is not valid.");

			int error;
			bool result = Poll_internal (socket, mode, time_us, out error);
			if (error != 0)
				throw new SocketException (error);

			socket_error = (int)GetSocketOption (SocketOptionLevel.Socket, SocketOptionName.Error);
			
			if (mode == SelectMode.SelectWrite && result) {
				/* Update the connected state; for
				 * non-blocking Connect()s this is
				 * when we can find out that the
				 * connect succeeded.
				 */
				if (socket_error == 0) {
					connected = true;
				}
			}
			
			return result;
		}

		[MethodImplAttribute(MethodImplOptions.InternalCall)]
		private extern static int Receive_internal(IntPtr sock,
							   byte[] buffer,
							   int offset,
							   int count,
							   SocketFlags flags,
							   out int error);

		internal int Receive_nochecks (byte [] buf, int offset, int size, SocketFlags flags, out SocketError error)
		{
			int nativeError;
			int ret = Receive_internal (socket, buf, offset, size, flags, out nativeError);
			error = (SocketError) nativeError;
			if (error != SocketError.Success && error != SocketError.WouldBlock && error != SocketError.InProgress)
				connected = false;
			else
				connected = true;
			
			return ret;
		}

		[MethodImplAttribute(MethodImplOptions.InternalCall)]
		private extern static void GetSocketOption_obj_internal(IntPtr socket,
			SocketOptionLevel level, SocketOptionName name, out object obj_val,
			out int error);

		[MethodImplAttribute(MethodImplOptions.InternalCall)]
		private extern static int Send_internal(IntPtr sock,
							byte[] buf, int offset,
							int count,
							SocketFlags flags,
							out int error);

		internal int Send_nochecks (byte [] buf, int offset, int size, SocketFlags flags, out SocketError error)
		{
			if (size == 0) {
				error = SocketError.Success;
				return 0;
			}

			int nativeError;

			int ret = Send_internal (socket, buf, offset, size, flags, out nativeError);

			error = (SocketError)nativeError;

			if (error != SocketError.Success && error != SocketError.WouldBlock && error != SocketError.InProgress)
				connected = false;
			else
				connected = true;

			return ret;
		}

		public object GetSocketOption (SocketOptionLevel level, SocketOptionName name)
		{
			if (disposed && closed)
				throw new ObjectDisposedException (GetType ().ToString ());

			object obj_val;
			int error;
			
			GetSocketOption_obj_internal(socket, level, name, out obj_val,
				out error);
			if (error != 0)
				throw new SocketException (error);
			
			if (name == SocketOptionName.Linger) {
				return((LingerOption)obj_val);
			} else if (name==SocketOptionName.AddMembership ||
				   name==SocketOptionName.DropMembership) {
				return((MulticastOption)obj_val);
			} else if (obj_val is int) {
				return((int)obj_val);
			} else {
				return(obj_val);
			}
		}

		[MethodImplAttribute (MethodImplOptions.InternalCall)]
		private extern static void Shutdown_internal (IntPtr socket, SocketShutdown how, out int error);
		
		public void Shutdown (SocketShutdown how)
		{
			if (disposed && closed)
				throw new ObjectDisposedException (GetType ().ToString ());

			int error;
			
			Shutdown_internal (socket, how, out error);

			if (error != 0)
				throw new SocketException (error);
		}

		[MethodImplAttribute(MethodImplOptions.InternalCall)]
		private extern static void SetSocketOption_internal (IntPtr socket, SocketOptionLevel level,
								     SocketOptionName name, object obj_val,
								     byte [] byte_val, int int_val,
								     out int error);

		public void SetSocketOption (SocketOptionLevel level, SocketOptionName name, int opt_value)
		{
			if (disposed && closed)
				throw new ObjectDisposedException (GetType ().ToString ());

			int error;
			
			SetSocketOption_internal(socket, level, name, null,
						 null, opt_value, out error);

			if (error != 0)
				throw new SocketException (error);
		}

		private void ThrowIfUpd ()
		{
#if !NET_2_1
			if (protocol_type == ProtocolType.Udp)
				throw new SocketException ((int)SocketError.ProtocolOption);
#endif
		}
	}
}

