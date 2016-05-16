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

using Android.Hardware.Usb;

namespace Aid.UsbSerial
{
	public sealed class UsbSerialDevice
    {
        public UsbManager UsbManager { get; }

        public UsbDevice UsbDevice { get; }

        public UsbSerialPort[] Ports { get; }

	    // ReSharper disable once InconsistentNaming
		public UsbSerialDeviceID ID { get; private set; }

		public UsbSerialDeviceInfo Info { get; }
        
        public UsbSerialDevice(UsbManager usbManager, UsbDevice usbDevice, UsbSerialDeviceID id, UsbSerialDeviceInfo info)
		{
            UsbManager = usbManager;
            UsbDevice = usbDevice;
            ID = id;
			Info = info;

			var cInfo = Info.DriverType.GetConstructor(new[] { typeof(UsbManager), typeof(UsbDevice), typeof(int) });
			if (cInfo == null) {
				throw new InvalidProgramException ();
			}

			Ports = new UsbSerialPort[info.NumberOfPorts];
			for (var i = 0; i < Info.NumberOfPorts; i++) {
                Ports[i] = (UsbSerialPort)cInfo.Invoke(new object[] { UsbManager, UsbDevice, i });
			}
		}

        public void CloseAllPorts()
        {
            foreach(var port in Ports)
            {
                port.Close();
            }
        }
    }
}