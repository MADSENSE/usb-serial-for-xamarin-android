/* Copyright 2011-2013 Google Inc.
 * Copyright 2013 mike wakerly <opensource@hoho.com>
 * Copyright 2015 Yasuyuki Hamada <yasuyuki_hamada@agri-info-design.com>
 *
 * This library is free software; you can redistribute it and/or
 * modify it under the terms of the GNU Lesser General Public
 * License as published by the Free Software Foundation; either
 * version 2.1 of the License, or (at your option) any later version.
 *
 * This library is distributed in the hope that it will be useful,
 * but WITHOUT ANY WARRANTY; without even the implied warranty of
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the GNU
 * Lesser General Public License for more details.
 *
 * You should have received a copy of the GNU Lesser General Public
 * License along with this library; if not, write to the Free Software
 * Foundation, Inc., 51 Franklin Street, Fifth Floor, Boston, MA  02110-1301,
 * USA.
 *
 * Project home page: https://github.com/ysykhmd/usb-serial-for-xamarin-android
 * 
 * This project is based on usb-serial-for-android and ported for Xamarin.Android.
 * Original project home page: https://github.com/mik3y/usb-serial-for-android
 */

using System;
using System.Threading;

using Android.Hardware.Usb;

namespace Aid.UsbSerial
{
    public abstract class UsbSerialPort
    {
        // ReSharper disable InconsistentNaming
        public const int DEFAULT_INTERNAL_READ_BUFFER_SIZE = 16 * 1024;
        public const int DEFAULT_TEMP_READ_BUFFER_SIZE = 16 * 1024;
        public const int DEFAULT_READ_BUFFER_SIZE = 16 * 1024;
        public const int DEFAULT_WRITE_BUFFER_SIZE = 16 * 1024;
        // ReSharper restore InconsistentNaming

        public const int DefaultBaudrate = 9600;
		public const int DefaultDataBits = 8;
		public const Parity DefaultParity = Parity.None;
		public const StopBits DefaultStopBits = StopBits.One;

        // ReSharper disable once InconsistentNaming
        protected int _portNumber;

        // non-null when open()
		protected UsbDeviceConnection Connection { get; set; }

        protected object InternalReadBufferLock = new object();
        protected object ReadBufferLock = new object();
        protected object WriteBufferLock = new object();

        /** Internal read buffer.  Guarded by {@link #ReadBufferLock}. */
        protected byte[] InternalReadBuffer;
        protected byte[] TempReadBuffer;
        protected byte[] ReadBuffer;
        protected int ReadBufferWriteCursor;
        protected int ReadBufferReadCursor;

        /** Internal write buffer.  Guarded by {@link #WriteBufferLock}. */
        protected byte[] WriteBuffer;

        // ReSharper disable once InconsistentNaming
		private int mDataBits;

		private volatile bool _continueUpdating;
		public bool IsOpened { get; protected set; }
		public int Baudrate { get; set; }
		public int DataBits {
			get { return mDataBits; }
			set {
				if (value < 5 || 8 < value)
					throw new ArgumentOutOfRangeException ();
				mDataBits = value;
			}
		}
		public Parity Parity { get; set; }
		public StopBits StopBits { get; set; }

        public event EventHandler<DataReceivedEventArgs> DataReceived;


        protected UsbSerialPort(UsbManager manager, UsbDevice device, int portNumber)
        {
			Baudrate = DefaultBaudrate;
			DataBits = DefaultDataBits;
			Parity = DefaultParity;
			StopBits = DefaultStopBits;

            UsbManager = manager;
			UsbDevice = device;
            _portNumber = portNumber;

            InternalReadBuffer = new byte[DEFAULT_INTERNAL_READ_BUFFER_SIZE];
            TempReadBuffer = new byte[DEFAULT_TEMP_READ_BUFFER_SIZE];
            ReadBuffer = new byte[DEFAULT_READ_BUFFER_SIZE];
            ReadBufferReadCursor = 0;
            ReadBufferWriteCursor = 0;
            WriteBuffer = new byte[DEFAULT_WRITE_BUFFER_SIZE];
        }

        public override string ToString()
        {
            return
                $"<{GetType().Name} device_name={UsbDevice.DeviceName} device_id={UsbDevice.DeviceId} port_number={_portNumber}>";
        }

        public UsbManager UsbManager { get; }

        /**
         * Returns the currently-bound USB device.
         *
         * @return the device
         */
        public UsbDevice UsbDevice { get; }

        /**
         * Sets the size of the internal buffer used to exchange data with the USB
         * stack for read operations.  Most users should not need to change this.
         *
         * @param bufferSize the size in bytes
         */
        public void SetReadBufferSize(int bufferSize)
        {
            if (bufferSize == InternalReadBuffer.Length)
            {
                return;
            }
            lock (InternalReadBufferLock)
            {
                InternalReadBuffer = new byte[bufferSize];
            }
        }

        /**
         * Sets the size of the internal buffer used to exchange data with the USB
         * stack for write operations.  Most users should not need to change this.
         *
         * @param bufferSize the size in bytes
         */
        public void SetWriteBufferSize(int bufferSize)
        {
            lock (WriteBufferLock)
            {
                if (bufferSize == WriteBuffer.Length)
                {
                    return;
                }
                WriteBuffer = new byte[bufferSize];
            }
        }

        // Members of IUsbSerialPort

        public int PortNumber => _portNumber;

        /**
         * Returns the device serial number
         *  @return serial number
         */
        public string Serial => Connection.Serial;


        public abstract void Open ();

		public abstract void Close ();


		protected void CreateConnection()
		{
			if (UsbManager != null && UsbDevice != null) {
				lock (ReadBufferLock) {
					lock (WriteBufferLock) {
						Connection = UsbManager.OpenDevice (UsbDevice);
					}
				}
			}
		}


		protected void CloseConnection()
		{
			if (Connection != null) {
				lock (ReadBufferLock) {
					lock (WriteBufferLock) {
						Connection.Close();
						Connection = null;
					}
				}
			}
		}


		protected void StartUpdating()
		{
			ThreadPool.QueueUserWorkItem (o => DoTasks ());
		}


		protected void StopUpdating()
		{
			_continueUpdating = false;
		}


		private void DoTasks()
        {
			_continueUpdating = true;
			while (_continueUpdating)
            {
                var rxlen = ReadInternal(TempReadBuffer, 0);
                if (rxlen > 0)
                {
                    lock (ReadBufferLock)
                    {
                        for (int i = 0; i < rxlen; i++)
                        {
                            ReadBuffer[ReadBufferWriteCursor] = TempReadBuffer[i];
                            ReadBufferWriteCursor = (ReadBufferWriteCursor + 1) % ReadBuffer.Length;
                            if (ReadBufferWriteCursor == ReadBufferReadCursor)
                            {
                                ReadBufferReadCursor = (ReadBufferReadCursor + 1) % ReadBuffer.Length;
                            }
                        }
                    }
                    DataReceived?.Invoke(this, new DataReceivedEventArgs(this));
                }
            }
        }

        public int Read(byte[] dest, int startIndex)
        {
            var len = 0;
            lock (ReadBufferLock)
            {
                var pos = startIndex;
                while ((ReadBufferReadCursor != ReadBufferWriteCursor) && (pos < dest.Length))
                {
                    dest[pos] = ReadBuffer[ReadBufferReadCursor];
                    len++;
                    pos++;
                    ReadBufferReadCursor = (ReadBufferReadCursor + 1) % ReadBuffer.Length;
                }
            }
            return len;
        }

		public void ResetParameters()
		{
			SetParameters(Baudrate, DataBits, StopBits, Parity);
		}

        protected abstract int ReadInternal(byte[] dest, int timeoutMillis);

        public abstract int Write(byte[] src, int timeoutMillis);

		protected abstract void SetParameters(int baudRate, int dataBits, StopBits stopBits, Parity parity);

        // ReSharper disable once InconsistentNaming
        public abstract bool CD { get; }

        public abstract bool Cts { get; }

        public abstract bool Dsr { get; }

        public abstract bool Dtr { get; set; }

        // ReSharper disable once InconsistentNaming
        public abstract bool RI { get; }

        public abstract bool Rts { get; set; }

        public virtual bool PurgeHwBuffers(bool flushReadBuffers, bool flushWriteBuffers)
        {
            return !flushReadBuffers && !flushWriteBuffers;
        }
    }
}
