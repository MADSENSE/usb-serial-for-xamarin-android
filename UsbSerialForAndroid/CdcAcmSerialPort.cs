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
using System.IO;

using Android.OS;
using Android.Util;
using Android.Hardware.Usb;

namespace Aid.UsbSerial
{
    /**
     * USB CDC/ACM serial driver implementation.
     *
     * @author mike wakerly (opensource@hoho.com)
     * @see <a
     *      href="http://www.usb.org/developers/devclass_docs/usbcdc11.pdf">Universal
     *      Serial Bus Class Definitions for Communication Devices, v1.1</a>
     */
	internal class CdcAcmSerialPort : UsbSerialPort
    {
        private const string Tag = "CdcAcmSerialPort";

        // ReSharper disable InconsistentNaming
        // ReSharper disable UnusedMember.Local
        private const int USB_RECIP_INTERFACE = 0x01;
	    private const int USB_RT_ACM = UsbConstants.UsbTypeClass | USB_RECIP_INTERFACE;
	    private const UsbAddressing a = UsbAddressing.DirMask;
        private const int SET_LINE_CODING = 0x20;  // USB CDC 1.1 section 6.2
	    private const int GET_LINE_CODING = 0x21;
	    private const int SET_CONTROL_LINE_STATE = 0x22;
        private const int SEND_BREAK = 0x23;
        // ReSharper restore InconsistentNaming
        // ReSharper restore UnusedMember.Local

        private readonly bool _enableAsyncReads = Build.VERSION.SdkInt >= BuildVersionCodes.JellyBeanMr1;
        private UsbInterface _controlInterface;
        private UsbInterface _dataInterface;

        private UsbEndpoint _controlEndpoint;
        private UsbEndpoint _readEndpoint;
        private UsbEndpoint _writeEndpoint;

        private bool _rts;
        private bool _dtr;

		public CdcAcmSerialPort(UsbManager usbManager, UsbDevice usbDevice, int portNumber)
            : base(usbManager, usbDevice, portNumber)
        {
            
        }

        public override void Open()
        {
			if (IsOpened) {
				return;
			}

			var openedSuccessfully = false;
            try
            {
				CreateConnection();

                Log.Debug(Tag, "claiming interfaces, count=" + UsbDevice.InterfaceCount);
                _controlInterface = UsbDevice.GetInterface(0);
                Log.Debug(Tag, "Control iface=" + _controlInterface);
                // class should be USB_CLASS_COMM

                if (!Connection.ClaimInterface(_controlInterface, true))
                {
                    throw new IOException("Could not claim control interface.");
                }
                _controlEndpoint = _controlInterface.GetEndpoint(0);
                Log.Debug(Tag, "Control endpoint direction: " + _controlEndpoint.Direction);

                Log.Debug(Tag, "Claiming data interface.");
                _dataInterface = UsbDevice.GetInterface(1);
                Log.Debug(Tag, "data iface=" + _dataInterface);
                // class should be USB_CLASS_CDC_DATA

                if (!Connection.ClaimInterface(_dataInterface, true))
                {
                    throw new IOException("Could not claim data interface.");
                }
                _readEndpoint = _dataInterface.GetEndpoint(1);
                Log.Debug(Tag, "Read endpoint direction: " + _readEndpoint.Direction);
                _writeEndpoint = _dataInterface.GetEndpoint(0);
                Log.Debug(Tag, "Write endpoint direction: " + _writeEndpoint.Direction);
                Log.Debug(Tag, _enableAsyncReads ? "Async reads enabled" : "Async reads disabled.");
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

	    // ReSharper disable once UnusedMethodReturnValue.Local
        private int SendAcmControlMessage(int request, int value, byte[] buf)
        {
            return Connection.ControlTransfer((UsbAddressing)USB_RT_ACM, request, value, 0, buf, buf?.Length ?? 0, 5000);
        }

        public override void Close()
        {
			StopUpdating ();
			CloseConnection ();
			IsOpened = false;
        }

        protected override int ReadInternal(byte[] dest, int timeoutMillis)
        {
            int numBytesRead;
            lock (InternalReadBufferLock)
            {
                var readAmt = Math.Min(dest.Length, InternalReadBuffer.Length);
                numBytesRead = Connection.BulkTransfer(_readEndpoint, InternalReadBuffer, readAmt,
                        timeoutMillis);
                if (numBytesRead < 0)
                {
                    // This sucks: we get -1 on timeout, not 0 as preferred.
                    // We *should* use UsbRequest, except it has a bug/api oversight
                    // where there is no way to determine the number of bytes read
                    // in response :\ -- http://b.android.com/28023
                    if (timeoutMillis == int.MaxValue)
                    {
                        // Hack: Special case "~infinite timeout" as an error.
                        return -1;
                    }
                    return 0;
                }
                Array.Copy(InternalReadBuffer, 0, dest, 0, numBytesRead);
            }
            return numBytesRead;
        }

        public override int Write(byte[] src, int timeoutMillis)
        {
            // TODO(mikey): Nearly identical to FtdiSerial write. Refactor.
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

                    amtWritten = Connection.BulkTransfer(_writeEndpoint, writeBuffer, writeLength,
                            timeoutMillis);
                }
                if (amtWritten <= 0)
                {
                    throw new IOException("Error writing " + writeLength
                            + " bytes at offset " + offset + " length=" + src.Length);
                }

                Log.Debug(Tag, "Wrote amt=" + amtWritten + " attempted=" + writeLength);
                offset += amtWritten;
            }
            return offset;
        }

        protected override void SetParameters(int baudRate, int dataBits, StopBits stopBits, Parity parity)
        {
            byte stopBitsByte;
            switch (stopBits)
            {
                case StopBits.One:
                    stopBitsByte = 0;
                    break;
                case StopBits.OnePointFive:
                    stopBitsByte = 1;
                    break;
                case StopBits.Two:
                    stopBitsByte = 2;
                    break;
                default: throw new ArgumentException("Bad value for stopBits: " + stopBits);
            }

            byte parityBitesByte;
            switch (parity)
            {
                case Parity.None:
                    parityBitesByte = 0;
                    break;
                case Parity.Odd:
                    parityBitesByte = 1;
                    break;
                case Parity.Even:
                    parityBitesByte = 2;
                    break;
                case Parity.Mark:
                    parityBitesByte = 3;
                    break;
                case Parity.Space:
                    parityBitesByte = 4;
                    break;
                default: throw new ArgumentException("Bad value for parity: " + parity);
            }

            byte[] msg = {
                (byte) ( baudRate & 0xff),
                (byte) ((baudRate >> 8 ) & 0xff),
                (byte) ((baudRate >> 16) & 0xff),
                (byte) ((baudRate >> 24) & 0xff),
                stopBitsByte,
                parityBitesByte,
                (byte) dataBits};
            SendAcmControlMessage(SET_LINE_CODING, 0, msg);
        }

        public override bool CD => false; // TODO

	    public override bool Cts => false; // TODO

        public override bool Dsr => false; // TODO

        public override bool Dtr
        {
            get
            {
                return _dtr;
            }
            set
            {
                _dtr = value;
                SetDtrRts();
            }
        }

        public override bool RI => false; // TODO

        public override bool Rts
        {
            get
            {
                return _rts;
            }
            set
            {
                _rts = value;
                SetDtrRts();
            }
        }

        private void SetDtrRts()
        {
            var value = (_rts ? 0x2 : 0) | (_dtr ? 0x1 : 0);
            SendAcmControlMessage(SET_CONTROL_LINE_STATE, value, null);
        }
    }
}

