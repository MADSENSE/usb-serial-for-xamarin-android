/* Copyright 2015 Yasuyuki Hamada <yasuyuki_hamada@agri-info-design.com>
 * 
 *  This library is free software; you can redistribute it and/or
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
 * 
 * +++
 * 
 * Original Java source code is ported to usb-serial-for-android
 * by Felix Hädicke <felixhaedicke@web.de>
 *
 * Based on the pyprolific driver written
 * by Emmanuel Blot <emmanuel.blot@free.fr>
 * See https://github.com/eblot/pyftdi
 */

using System;
using System.IO;
using System.Threading;

using Android.Util;
using Android.Hardware.Usb;

namespace Aid.UsbSerial
{
	public class ProlificSerialPort : UsbSerialPort
    {
        // ReSharper disable UnusedMember.Local
        // ReSharper disable InconsistentNaming

        private const string TAG = "ProlificSerialPort";

        private const int USB_READ_TIMEOUT_MILLIS = 1000;
        private const int USB_WRITE_TIMEOUT_MILLIS = 5000;

        private const int USB_RECIP_INTERFACE = 0x01;

        private const int ProlificVendorREAD_REQUEST = 0x01;
        private const int ProlificVendorWRITE_REQUEST = 0x01;

        private const int ProlificVendorOUT_REQTYPE = (int)UsbAddressing.Out | UsbConstants.UsbTypeVendor;

        private const int ProlificVendorIN_REQTYPE = (int)UsbAddressing.In | UsbConstants.UsbTypeVendor;

		private const int ProlificCTRL_OUT_REQTYPE = (int)UsbAddressing.Out | UsbConstants.UsbTypeClass | USB_RECIP_INTERFACE;

        private const int WRITE_ENDPOINT = 0x02;
        private const int READ_ENDPOINT = 0x83;
        private const int INTERRUPT_ENDPOINT = 0x81;

        private const int FLUSH_RX_REQUEST = 0x08;
        private const int FLUSH_TX_REQUEST = 0x09;

        private const int SET_LINE_REQUEST = 0x20;
        private const int SET_CONTROL_REQUEST = 0x22;

        private const int CONTROL_DTR = 0x01;
        private const int CONTROL_RTS = 0x02;

        private const int STATUS_FLAG_CD = 0x01;
        private const int STATUS_FLAG_DSR = 0x02;
        private const int STATUS_FLAG_RI = 0x08;
        private const int STATUS_FLAG_CTS = 0x80;

        private const int STATUS_BUFFER_SIZE = 10;
        private const int STATUS_BYTE_IDX = 8;

        private const int DEVICE_TYPE_HX = 0;
        private const int DEVICE_TYPE_0 = 1;
        private const int DEVICE_TYPE_1 = 2;

        // ReSharper restore InconsistentNaming
        // ReSharper restore UnusedMember.Local

        private int _deviceType = DEVICE_TYPE_HX;

        private UsbEndpoint _readEndpoint;
        private UsbEndpoint _writeEndpoint;
        private UsbEndpoint _interruptEndpoint;

        private int _controlLinesValue;

        private int? _baudRate, _dataBits;
        private StopBits? _stopBits;
        private Parity? _parity;

        private int _status;
        private Thread _readStatusThread;
        private readonly object _readStatusThreadLock = new object();
        private bool _stopReadStatusThread;
        private IOException _readStatusException;

        public ProlificSerialPort(UsbManager manager, UsbDevice device, int portNumber)
            : base(manager, device, portNumber)
        {
        }

        private void InControlTransfer(int requestType, int request, int value, int index, int length)
        {
            var buffer = new byte[length];
            var result = Connection.ControlTransfer((UsbAddressing)requestType, request, value, index, buffer, length, USB_READ_TIMEOUT_MILLIS);
            if (result != length)
            {
                throw new IOException($"ControlTransfer with value 0x{value:x} failed: {result}");
            }
        }

        private void OutControlTransfer(int requestType, int request,
                int value, int index, byte[] data)
        {
            var length = data?.Length ?? 0;
            var result = Connection.ControlTransfer((UsbAddressing)requestType, request, value, index, data, length, USB_WRITE_TIMEOUT_MILLIS);
            if (result != length)
            {
				throw new IOException($"ControlTransfer with value 0x{value:x} failed: {result}");
            }
        }

        private void VendorIn(int value, int index, int length)
        {
            InControlTransfer(ProlificVendorIN_REQTYPE, ProlificVendorREAD_REQUEST, value, index, length);
        }

	    private void VendorOut(int value, int index, byte[] data)
        {
            OutControlTransfer(ProlificVendorOUT_REQTYPE, ProlificVendorWRITE_REQUEST, value, index, data);
        }

        private void ResetDevice()
        {
            PurgeHwBuffers(true, true);
        }

        private void CtrlOut(int request, int value, int index, byte[] data)
        {
            OutControlTransfer(ProlificCTRL_OUT_REQTYPE, request, value, index, data);
        }

        private void DoBlackMagic()
        {
            VendorIn(0x8484, 0, 1);
            VendorOut(0x0404, 0, null);
            VendorIn(0x8484, 0, 1);
            VendorIn(0x8383, 0, 1);
            VendorIn(0x8484, 0, 1);
            VendorOut(0x0404, 1, null);
            VendorIn(0x8484, 0, 1);
            VendorIn(0x8383, 0, 1);
            VendorOut(0, 1, null);
            VendorOut(1, 0, null);
            VendorOut(2, (_deviceType == DEVICE_TYPE_HX) ? 0x44 : 0x24, null);
        }

        private void SetControlLines(int newControlLinesValue)
        {
			CtrlOut(SET_CONTROL_REQUEST, newControlLinesValue, 0, null);
            _controlLinesValue = newControlLinesValue;
        }

        private void ReadStatusThreadFunction()
        {
            try
            {
                while (!_stopReadStatusThread)
                {
                    var buffer = new byte[STATUS_BUFFER_SIZE];
                    var readBytesCount = Connection.BulkTransfer(_interruptEndpoint,
                            buffer,
                            STATUS_BUFFER_SIZE,
                            500);
                    if (readBytesCount > 0)
                    {
                        if (readBytesCount == STATUS_BUFFER_SIZE)
                        {
                            _status = buffer[STATUS_BYTE_IDX] & 0xff;
                        }
                        else
                        {
                            throw new IOException(
                                $"Invalid CTS / DSR / CD / RI status buffer received, expected {STATUS_BUFFER_SIZE} bytes, but received {readBytesCount} bytes.");
                        }
                    }
                }
            }
            catch (IOException e)
            {
                _readStatusException = e;
            }
        }

        private int Status
        {
            get
            {
                if ((_readStatusThread == null) && (_readStatusException == null))
                {
                    lock (_readStatusThreadLock)
                    {
                        if (_readStatusThread == null)
                        {
                            var buffer = new byte[STATUS_BUFFER_SIZE];
                            var readBytes = Connection.BulkTransfer(_interruptEndpoint, buffer, STATUS_BUFFER_SIZE, 100);
                            if (readBytes != STATUS_BUFFER_SIZE)
                            {
                                Log.Warn(TAG, "Could not read initial CTS / DSR / CD / RI status");
                            }
                            else
                            {
                                _status = buffer[STATUS_BYTE_IDX] & 0xff;
                            }
                            _readStatusThread = new Thread(ReadStatusThreadFunction);
                            _readStatusThread.Start();
                        }
                    }
                }

                /* throw and clear an exception which occured in the status read thread */
                var readStatusException = _readStatusException;
                if (readStatusException != null)
                {
                    _readStatusException = null;
                    throw readStatusException;
                }

                return _status;
            }
        }

        private bool TestStatusFlag(int flag)
        {
            return (Status & flag) == flag;
        }

		public override void Open()
        {
            var openedSuccessfully = false;
            try
            {
				CreateConnection();

				var usbInterface = UsbDevice.GetInterface(0);
				if (!Connection.ClaimInterface(usbInterface, true))
				{
					throw new IOException("Error claiming Prolific interface 0");
				}

                for (var i = 0; i < usbInterface.EndpointCount; ++i)
                {
                    var currentEndpoint = usbInterface.GetEndpoint(i);

                    switch ((int)currentEndpoint.Address)
                    {
                        case READ_ENDPOINT:
                            _readEndpoint = currentEndpoint;
                            break;

                        case WRITE_ENDPOINT:
                            _writeEndpoint = currentEndpoint;
                            break;

                        case INTERRUPT_ENDPOINT:
                            _interruptEndpoint = currentEndpoint;
                            break;
                    }
                }

                if (UsbDevice.DeviceClass == UsbClass.Comm)
                {
                    _deviceType = DEVICE_TYPE_0;
                }
                else
                {
                    try
                    {
                        byte[] rawDescriptors = Connection.GetRawDescriptors();
                        byte maxPacketSize0 = rawDescriptors[7];
                        if (maxPacketSize0 == 64)
                        {
                            _deviceType = DEVICE_TYPE_HX;
                        }
                        else if ((UsbDevice.DeviceClass == UsbClass.PerInterface) || (UsbDevice.DeviceClass == UsbClass.VendorSpec))
                        {
                            _deviceType = DEVICE_TYPE_1;
                        }
                        else
                        {
                            Log.Warn(TAG, "Could not detect PL2303 subtype, "
                                + "Assuming that it is a HX device");
                            _deviceType = DEVICE_TYPE_HX;
                        }
                    }
                    catch (Exception e)
                    {
                        Log.Error(TAG, "An unexpected exception occured while trying "
                                + "to detect PL2303 subtype", e);
                    }
                }

                SetControlLines(_controlLinesValue);
                ResetDevice();
                DoBlackMagic();
				ResetParameters();

				openedSuccessfully = true;
            }
			finally {
				if (openedSuccessfully) {
					IsOpened = true;
					StartUpdating ();
				} else {
					CloseConnection();
				}
			}
        }

        public override void Close()
		{
			StopUpdating ();

			try {
				_stopReadStatusThread = true;
				lock (_readStatusThreadLock) {
					if (_readStatusThread != null) {
						try {
							_readStatusThread.Join ();
						} catch (Exception e) {
							Log.Warn (TAG, "An error occured while waiting for status read thread", e);
						}
					}
				}
				if (Connection != null) {
					ResetDevice ();
				}
			}
			catch (Exception)
			{
			    // ignored
			}
			finally {
				try
				{
				    Connection?.ReleaseInterface (UsbDevice.GetInterface (0));
				}
				finally {
					CloseConnection ();
					IsOpened = false;
				}
			}
		}

        protected override int ReadInternal(byte[] dest, int timeoutMillis)
        {
		if (Connection == null)
			return 0;
		
            lock (InternalReadBufferLock)
            {
                var readAmt = Math.Min(dest.Length, InternalReadBuffer.Length);
                var numBytesRead = Connection.BulkTransfer(_readEndpoint, InternalReadBuffer, readAmt, timeoutMillis);
                if (numBytesRead < 0)
                {
                    return 0;
                }
                Array.Copy(InternalReadBuffer, 0, dest, 0, numBytesRead);
                return numBytesRead;
            }
        }

        public override int Write(byte[] src, int timeoutMillis)
        {
            var offset = 0;

            while (offset < src.Length)
            {
                int writeLength;
                int amtWritten;

                lock (WriteBufferLock)
                {
                    byte[] writeBuffer;

                    writeLength = Math.Min(src.Length - offset, WriteBuffer.Length);
                    if (offset == 0)
                    {
                        writeBuffer = src;
                    }
                    else
                    {
                        // bulkTransfer does not support offsets, make a copy.
                        Array.Copy(src, offset, WriteBuffer, 0, writeLength);
                        writeBuffer = WriteBuffer;
                    }

                    amtWritten = Connection.BulkTransfer(_writeEndpoint,
                            writeBuffer, writeLength, timeoutMillis);
                }

                if (amtWritten <= 0)
                {
                    throw new IOException("Error writing " + writeLength
                            + " bytes at offset " + offset + " length="
                            + src.Length);
                }

                offset += amtWritten;
            }
            return offset;
        }

        protected override void SetParameters(int baudRate, int dataBits, StopBits stopBits, Parity parity)
        {
            if ((_baudRate == baudRate) && (_dataBits == dataBits)
                    && (_stopBits == stopBits) && (_parity == parity))
            {
                // Make sure no action is performed if there is nothing to change
                return;
            }

            var lineRequestData = new byte[7];

            lineRequestData[0] = (byte)(baudRate & 0xff);
            lineRequestData[1] = (byte)((baudRate >> 8) & 0xff);
            lineRequestData[2] = (byte)((baudRate >> 16) & 0xff);
            lineRequestData[3] = (byte)((baudRate >> 24) & 0xff);

            switch (stopBits)
            {
                case StopBits.One:
                    lineRequestData[4] = 0;
                    break;

                case StopBits.OnePointFive:
                    lineRequestData[4] = 1;
                    break;

                case StopBits.Two:
                    lineRequestData[4] = 2;
                    break;

                default:
                    throw new ArgumentException("Unknown stopBits value: " + stopBits);
            }

            switch (parity)
            {
                case Parity.None:
                    lineRequestData[5] = 0;
                    break;

                case Parity.Odd:
                    lineRequestData[5] = 1;
                    break;

                case Parity.Mark:
                    lineRequestData[5] = 3;
                    break;

                case Parity.Space:
                    lineRequestData[5] = 4;
                    break;

                default:
                    throw new ArgumentException("Unknown parity value: " + parity);
            }

            lineRequestData[6] = (byte)dataBits;

            CtrlOut(SET_LINE_REQUEST, 0, 0, lineRequestData);

            ResetDevice();

            _baudRate = baudRate;
            _dataBits = dataBits;
            _stopBits = stopBits;
            _parity = parity;
        }

        public override bool CD => TestStatusFlag(STATUS_FLAG_CD);

	    public override bool Cts => TestStatusFlag(STATUS_FLAG_CTS);

	    public override bool Dsr => TestStatusFlag(STATUS_FLAG_DSR);

	    public override bool Dtr
        {
            get
            {
                return (_controlLinesValue & CONTROL_DTR) == CONTROL_DTR;
            }
            set
            {
                int newControlLinesValue;
                if (value)
                {
                    newControlLinesValue = _controlLinesValue | CONTROL_DTR;
                }
                else
                {
                    newControlLinesValue = _controlLinesValue & ~CONTROL_DTR;
                }
                SetControlLines(newControlLinesValue);
            }
        }

        public override bool RI => TestStatusFlag(STATUS_FLAG_RI);

	    public override bool Rts
        {
            get
            {
                return (_controlLinesValue & CONTROL_RTS) == CONTROL_RTS;
            }
            set
            {
                int newControlLinesValue;
                if (value)
                {
                    newControlLinesValue = _controlLinesValue | CONTROL_RTS;
                }
                else
                {
                    newControlLinesValue = _controlLinesValue & ~CONTROL_RTS;
                }
                SetControlLines(newControlLinesValue);
            }
        }

        public override bool PurgeHwBuffers(bool purgeReadBuffers, bool purgeWriteBuffers)
        {
            if (purgeReadBuffers)
            {
                VendorOut(FLUSH_RX_REQUEST, 0, null);
            }

            if (purgeWriteBuffers)
            {
                VendorOut(FLUSH_TX_REQUEST, 0, null);
            }

            return purgeReadBuffers || purgeWriteBuffers;
        }
    }
}
