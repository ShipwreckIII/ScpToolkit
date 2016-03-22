﻿using System;
using System.Collections.Generic;
using System.Net.NetworkInformation;
using System.Threading;
using ScpControl.ScpCore;
using ScpControl.Shared.Core;

namespace ScpControl.Bluetooth
{
    /// <summary>
    ///     Supported HID input update rates.
    /// </summary>
    public enum Ds4UpdateRate : byte
    {
        Fastest = 0x80, // 1000 Hz
        Fast = 0xD0, // 66 Hz
        Slow = 0xA0, // 31 Hz
        Slowest = 0xB0 // 20 Hz
    }

    /// <summary>
    ///     Represents a DualShock 4 controller connected via Bluetooth.
    /// </summary>
    public class BthDs4 : BthDevice
    {
        #region Private fields

        private const int R = 9; // Led Offsets
        private const int G = 10; // Led Offsets
        private const int B = 11; // Led Offsets
        private byte _mBrightness = GlobalConfiguration.Instance.Brightness;
        private bool _mFlash;

        #endregion

        #region Public properties

        /// <summary>
        ///     Supported HID input update rates.
        /// </summary>
        public static Dictionary<Ds4UpdateRate, string> UpdateRates
        {
            get
            {
                return new Dictionary<Ds4UpdateRate, string>
                {
                    {Ds4UpdateRate.Fastest, "1000 Hz"},
                    {Ds4UpdateRate.Fast, "66 Hz"},
                    {Ds4UpdateRate.Slow, "31 Hz"},
                    {Ds4UpdateRate.Slowest, "20 Hz"}
                };
            }
        }

        /// <summary>
        ///     Product name of the Sony DualShock 4 controller.
        /// </summary>
        public static string GenuineProductName
        {
            get { return "Wireless Controller"; }
        }

        #endregion

        #region Private methods

        private void SetLightBarColor(DsPadId value)
        {
            if (GlobalConfiguration.Instance.IsLightBarDisabled)
            {
                _hidReport[R] = _hidReport[G] = _hidReport[B] = _hidReport[12] = _hidReport[13] = 0x00;
            }
            else
            {
                switch (value)
                {
                    case DsPadId.One: // Blue
                        _hidReport[R] = 0x00;
                        _hidReport[G] = 0x00;
                        _hidReport[B] = _mBrightness;
                        break;
                    case DsPadId.Two: // Green
                        _hidReport[R] = 0x00;
                        _hidReport[G] = _mBrightness;
                        _hidReport[B] = 0x00;
                        break;
                    case DsPadId.Three: // Yellow
                        _hidReport[R] = _mBrightness;
                        _hidReport[G] = _mBrightness;
                        _hidReport[B] = 0x00;
                        break;
                    case DsPadId.Four: // Cyan
                        _hidReport[R] = 0x00;
                        _hidReport[G] = _mBrightness;
                        _hidReport[B] = _mBrightness;
                        break;
                    case DsPadId.None: // Red
                        _hidReport[R] = _mBrightness;
                        _hidReport[G] = 0x00;
                        _hidReport[B] = 0x00;
                        break;
                }
            }

            m_Queued = 1;
        }

        #endregion

        #region Public methods

        public override bool Start()
        {
            CanStartHid = false;
            State = DsState.Connected;

            _hidReport[2] = (byte) GlobalConfiguration.Instance.Ds4InputUpdateDelay;

            m_Last = DateTime.Now;
            Rumble(0, 0);

            return base.Start();
        }

        /// <summary>
        ///     Interprets a HID report sent by a DualShock 4 device.
        /// </summary>
        /// <param name="report">The HID report as byte array.</param>
        public override void ParseHidReport(byte[] report)
        {
            m_Packet++;

            var inputReport = NewHidReport();

            Battery = (DsBattery) ((byte) ((report[41] + 2)/2));

            inputReport.PacketCounter = m_Packet;

            var buttons = (report[16] << 0) | (report[17] << 8) | (report[18] << 16);
            var trigger = false;

            //++ Convert HAT to DPAD
            report[16] &= 0xF0;

            switch ((uint) buttons & 0xF)
            {
                case 0:
                    report[16] |= (byte) Ds4Button.Up.Offset;
                    break;
                case 1:
                    report[16] |= (byte) (Ds4Button.Up.Offset | Ds4Button.Right.Offset);
                    break;
                case 2:
                    report[16] |= (byte) Ds4Button.Right.Offset;
                    break;
                case 3:
                    report[16] |= (byte) (Ds4Button.Right.Offset | Ds4Button.Down.Offset);
                    break;
                case 4:
                    report[16] |= (byte) Ds4Button.Down.Offset;
                    break;
                case 5:
                    report[16] |= (byte) (Ds4Button.Down.Offset | Ds4Button.Left.Offset);
                    break;
                case 6:
                    report[16] |= (byte) Ds4Button.Left.Offset;
                    break;
                case 7:
                    report[16] |= (byte) (Ds4Button.Left.Offset | Ds4Button.Up.Offset);
                    break;
            }
            //--

            // copy controller data to report packet
            Buffer.BlockCopy(report, 11, inputReport.RawBytes, 8, 76);

            // set report ID
            inputReport.RawBytes[8] = report[9];

            // Quick Disconnect
            if (inputReport[Ds4Button.L1].IsPressed
                && inputReport[Ds4Button.R1].IsPressed
                && inputReport[Ds4Button.Ps].IsPressed)
            {
                trigger = true;
                // unset PS button
                inputReport.Unset(Ds4Button.Ps);
            }

            if (inputReport.IsPadActive)
            {
                m_IsIdle = false;
            }
            else if (!m_IsIdle)
            {
                m_IsIdle = true;
                m_Idle = DateTime.Now;
            }

            if (trigger && !m_IsDisconnect)
            {
                m_IsDisconnect = true;
                m_Disconnect = DateTime.Now;
            }
            else if (!trigger && m_IsDisconnect)
            {
                m_IsDisconnect = false;
            }

            OnHidReportReceived(inputReport);
        }

        /// <summary>
        ///     Send Rumble request to controller.
        /// </summary>
        /// <param name="large">Larg motor.</param>
        /// <param name="small">Small motor.</param>
        /// <returns>Always true.</returns>
        public override bool Rumble(byte large, byte small)
        {
            lock (_hidReport)
            {
                if (GlobalConfiguration.Instance.DisableRumble)
                {
                    _hidReport[7] = 0;
                    _hidReport[8] = 0;
                }
                else
                {
                    _hidReport[7] = small;
                    _hidReport[8] = large;
                }

                if (!m_Blocked)
                {
                    m_Last = DateTime.Now;
                    m_Blocked = true;
                    BluetoothDevice.HID_Command(HciHandle.Bytes, Get_SCID(L2CAP.PSM.HID_Command), _hidReport);
                }
                else
                {
                    m_Queued = 1;
                }
            }

            return true;
        }

        public override bool InitHidReport(byte[] report)
        {
            var retVal = false;

            if (m_Init < _hidInitReport.Length)
            {
                BluetoothDevice.HID_Command(HciHandle.Bytes, Get_SCID(L2CAP.PSM.HID_Service), _hidInitReport[m_Init++]);
            }
            else if (m_Init == _hidInitReport.Length)
            {
                m_Init++;
                retVal = true;
            }

            return retVal;
        }

        #endregion

        #region Protected methods

        protected override void Process(DateTime now)
        {
            if (!Monitor.TryEnter(_hidReport) || State != DsState.Connected) return;

            try
            {
                #region Light bar manipulation

                if (!GlobalConfiguration.Instance.IsLightBarDisabled)
                {
                    if (Battery < DsBattery.Medium)
                    {
                        if (!_mFlash)
                        {
                            _hidReport[12] = _hidReport[13] = 0x40;

                            _mFlash = true;
                            m_Queued = 1;
                        }
                    }
                    else
                    {
                        if (_mFlash)
                        {
                            _hidReport[12] = _hidReport[13] = 0x00;

                            _mFlash = false;
                            m_Queued = 1;
                        }
                    }
                }

                if (GlobalConfiguration.Instance.Brightness != _mBrightness)
                {
                    _mBrightness = GlobalConfiguration.Instance.Brightness;
                }

                if (XInputSlot.HasValue)
                {
                    SetLightBarColor((DsPadId) XInputSlot);
                }

                #endregion

                if ((now - m_Last).TotalMilliseconds >= 500)
                {
                    if (_hidReport[7] > 0x00 || _hidReport[8] > 0x00)
                    {
                        m_Queued = 1;
                    }
                }

                if (!m_Blocked && m_Queued > 0)
                {
                    m_Last = now;
                    m_Blocked = true;
                    m_Queued--;

                    BluetoothDevice.HID_Command(HciHandle.Bytes, Get_SCID(L2CAP.PSM.HID_Command), _hidReport);
                }
            }
            finally
            {
                Monitor.Exit(_hidReport);
            }
        }

        #endregion

        #region HID Reports

        private readonly byte[][] _hidInitReport =
        {
            new byte[]
            {
                0x07, 0x00, 0x01, 0x02, 0x9B, 0x02, 0x90, 0x36, 0x06, 0x51, 0x35, 0x98, 0x09, 0x00, 0x00, 0x0A, 0x00,
                0x00,
                0x00, 0x00, 0x09, 0x00, 0x01, 0x35, 0x03, 0x19, 0x10, 0x00, 0x09, 0x00, 0x04, 0x35, 0x0D, 0x35, 0x06,
                0x19, 0x01, 0x00, 0x09, 0x00, 0x01, 0x35, 0x03, 0x19, 0x00, 0x01, 0x09, 0x00, 0x05, 0x35, 0x03, 0x19,
                0x10, 0x02, 0x09, 0x00, 0x06, 0x35, 0x09, 0x09, 0x65, 0x6E, 0x09, 0x00, 0x6A, 0x09, 0x01, 0x00, 0x09,
                0x01, 0x00, 0x25, 0x12, 0x53, 0x65, 0x72, 0x76, 0x69, 0x63, 0x65, 0x20, 0x44, 0x69, 0x73, 0x63, 0x6F,
                0x76, 0x65, 0x72, 0x79, 0x00, 0x09, 0x01, 0x01, 0x25, 0x25, 0x50, 0x75, 0x62, 0x6C, 0x69, 0x73, 0x68,
                0x65, 0x73, 0x20, 0x73, 0x65, 0x72, 0x76, 0x69, 0x63, 0x65, 0x73, 0x20, 0x74, 0x6F, 0x20, 0x72, 0x65,
                0x6D, 0x6F, 0x74, 0x65, 0x20, 0x64, 0x65, 0x76, 0x69, 0x63, 0x65, 0x73, 0x00, 0x09, 0x01, 0x02, 0x25,
                0x0A, 0x4D, 0x69, 0x63, 0x72, 0x6F, 0x73, 0x6F, 0x66, 0x74, 0x00, 0x09, 0x02, 0x00, 0x35, 0x03, 0x09,
                0x01, 0x00, 0x09, 0x02, 0x01, 0x0A, 0x00, 0x00, 0x00, 0x0D, 0x35, 0x95, 0x09, 0x00, 0x00, 0x0A, 0x00,
                0x01, 0x00, 0x00, 0x09, 0x00, 0x01, 0x35, 0x03, 0x19, 0x12, 0x00, 0x09, 0x00, 0x04, 0x35, 0x0D, 0x35,
                0x06, 0x19, 0x01, 0x00, 0x09, 0x00, 0x01, 0x35, 0x03, 0x19, 0x00, 0x01, 0x09, 0x00, 0x05, 0x35, 0x03,
                0x19, 0x10, 0x02, 0x09, 0x00, 0x06, 0x35, 0x09, 0x09, 0x65, 0x6E, 0x09, 0x00, 0x6A, 0x09, 0x01, 0x00,
                0x09, 0x01, 0x00, 0x25, 0x18, 0x44, 0x65, 0x76, 0x69, 0x63, 0x65, 0x20, 0x49, 0x44, 0x20, 0x53, 0x65,
                0x72, 0x76, 0x69, 0x63, 0x65, 0x20, 0x52, 0x65, 0x63, 0x6F, 0x72, 0x64, 0x09, 0x01, 0x01, 0x25, 0x18,
                0x44, 0x65, 0x76, 0x69, 0x63, 0x65, 0x20, 0x49, 0x44, 0x20, 0x53, 0x65, 0x72, 0x76, 0x69, 0x63, 0x65,
                0x20, 0x52, 0x65, 0x63, 0x6F, 0x72, 0x64, 0x09, 0x02, 0x00, 0x09, 0x01, 0x03, 0x09, 0x02, 0x01, 0x09,
                0x00, 0x06, 0x09, 0x02, 0x02, 0x09, 0x00, 0x01, 0x09, 0x02, 0x03, 0x09, 0x08, 0x00, 0x09, 0x02, 0x04,
                0x28, 0x01, 0x09, 0x02, 0x05, 0x09, 0x00, 0x01, 0x35, 0x9D, 0x09, 0x00, 0x00, 0x0A, 0x00, 0x01, 0x00,
                0x01, 0x09, 0x00, 0x01, 0x35, 0x03, 0x19, 0x11, 0x15, 0x09, 0x00, 0x04, 0x35, 0x1B, 0x35, 0x06, 0x19,
                0x01, 0x00, 0x09, 0x00, 0x0F, 0x35, 0x11, 0x19, 0x00, 0x0F, 0x09, 0x01, 0x00, 0x35, 0x09, 0x09, 0x08,
                0x00, 0x09, 0x86, 0xDD, 0x09, 0x08, 0x06, 0x09, 0x00, 0x05, 0x35, 0x03, 0x19, 0x10, 0x02, 0x09, 0x00,
                0x06, 0x35, 0x09, 0x09, 0x65, 0x6E, 0x09, 0x00, 0x6A, 0x09, 0x01, 0x00, 0x09, 0x00, 0x09, 0x35, 0x08,
                0x35, 0x06, 0x19, 0x11, 0x15, 0x09, 0x01, 0x00, 0x09, 0x01, 0x00, 0x25, 0x1D, 0x50, 0x65, 0x72, 0x73,
                0x6F, 0x6E, 0x61, 0x6C, 0x20, 0x41, 0x64, 0x20, 0x48, 0x6F, 0x63, 0x20, 0x55, 0x73, 0x65, 0x72, 0x20,
                0x53, 0x65, 0x72, 0x76, 0x69, 0x63, 0x65, 0x00, 0x09, 0x01, 0x01, 0x25, 0x1D, 0x50, 0x65, 0x72, 0x73,
                0x6F, 0x6E, 0x61, 0x6C, 0x20, 0x41, 0x64, 0x20, 0x48, 0x6F, 0x63, 0x20, 0x55, 0x73, 0x65, 0x72, 0x20,
                0x53, 0x65, 0x72, 0x76, 0x69, 0x63, 0x65, 0x00, 0x09, 0x03, 0x0A, 0x09, 0x00, 0x00, 0x35, 0x5A, 0x09,
                0x00, 0x00, 0x0A, 0x00, 0x01, 0x00, 0x02, 0x09, 0x00, 0x01, 0x35, 0x03, 0x19, 0x11, 0x0A, 0x09, 0x00,
                0x04, 0x35, 0x10, 0x35, 0x06, 0x19, 0x01, 0x00, 0x09, 0x00, 0x19, 0x35, 0x06, 0x19, 0x00, 0x19, 0x09,
                0x01, 0x00, 0x09, 0x00, 0x05, 0x35, 0x03, 0x19, 0x10, 0x02, 0x09, 0x00, 0x06, 0x35, 0x09, 0x09, 0x65,
                0x6E, 0x09, 0x00, 0x6A, 0x09, 0x01, 0x00, 0x09, 0x00, 0x09, 0x35, 0x08, 0x35, 0x06, 0x19, 0x11, 0x0D,
                0x09, 0x01, 0x02, 0x09, 0x01, 0x00, 0x25, 0x0D, 0x41, 0x75, 0x64, 0x69, 0x6F, 0x20, 0x53, 0x6F, 0x75,
                0x72, 0x63, 0x65, 0x00, 0x35, 0x40, 0x09, 0x00, 0x00, 0x0A, 0x00, 0x01, 0x00, 0x03, 0x09, 0x00, 0x01,
                0x35, 0x03, 0x19, 0x11, 0x0C, 0x09, 0x00, 0x04, 0x35, 0x10, 0x35, 0x06, 0x19, 0x01, 0x00, 0x09, 0x00,
                0x17, 0x35, 0x06, 0x19, 0x00, 0x17, 0x09, 0x01, 0x02, 0x09, 0x00, 0x05, 0x35, 0x03, 0x19, 0x10, 0x02,
                0x09, 0x00, 0x09, 0x35, 0x08, 0x35, 0x06, 0x19, 0x11, 0x0E, 0x09, 0x01, 0x03, 0x09, 0x03, 0x11, 0x09,
                0x00, 0x01, 0x35, 0x73, 0x09, 0x00, 0x00, 0x0A, 0x00, 0x01, 0x00, 0x05, 0x09, 0x00, 0x01, 0x35, 0x03,
                0x19, 0x11, 0x05, 0x09, 0x00, 0x04, 0x35, 0x11, 0x35, 0x03, 0x19, 0x01, 0x00, 0x35, 0x05, 0x19, 0x08,
                0xA0, 0x8B, 0x95, 0x08, 0x80, 0xFA, 0xFF, 0xFF
            },
            new byte[]
            {
                0x07, 0x00, 0x02, 0x02, 0x9B, 0x02, 0x90, 0x00, 0x03, 0x08, 0x01, 0x35, 0x03, 0x19, 0x00, 0x08, 0x09,
                0x00,
                0x05, 0x35, 0x03, 0x19, 0x10, 0x02, 0x09, 0x00, 0x06, 0x35, 0x09, 0x09, 0x65, 0x6E, 0x09, 0x00, 0x6A,
                0x09, 0x01, 0x00, 0x09, 0x00, 0x09, 0x35, 0x08, 0x35, 0x06, 0x19, 0x11, 0x05, 0x09, 0x01, 0x02, 0x09,
                0x01, 0x00, 0x25, 0x12, 0x50, 0x49, 0x4D, 0x20, 0x49, 0x74, 0x65, 0x6D, 0x20, 0x54, 0x72, 0x61, 0x6E,
                0x73, 0x66, 0x65, 0x72, 0x00, 0x09, 0x02, 0x00, 0x09, 0xD6, 0xE1, 0x09, 0x03, 0x03, 0x35, 0x08, 0x08,
                0x01, 0x08, 0x02, 0x08, 0x04, 0x08, 0xFF, 0x35, 0x62, 0x09, 0x00, 0x00, 0x0A, 0x00, 0x01, 0x00, 0x06,
                0x09, 0x00, 0x01, 0x35, 0x03, 0x19, 0x11, 0x06, 0x09, 0x00, 0x04, 0x35, 0x11, 0x35, 0x03, 0x19, 0x01,
                0x00, 0x35, 0x05, 0x19, 0x00, 0x03, 0x08, 0x02, 0x35, 0x03, 0x19, 0x00, 0x08, 0x09, 0x00, 0x05, 0x35,
                0x03, 0x19, 0x10, 0x02, 0x09, 0x00, 0x06, 0x35, 0x09, 0x09, 0x65, 0x6E, 0x09, 0x00, 0x6A, 0x09, 0x01,
                0x00, 0x09, 0x00, 0x09, 0x35, 0x08, 0x35, 0x06, 0x19, 0x11, 0x06, 0x09, 0x01, 0x02, 0x09, 0x01, 0x00,
                0x25, 0x0E, 0x46, 0x69, 0x6C, 0x65, 0x20, 0x54, 0x72, 0x61, 0x6E, 0x73, 0x66, 0x65, 0x72, 0x00, 0x09,
                0x02, 0x00, 0x09, 0xD6, 0xE3, 0x35, 0x46, 0x09, 0x00, 0x00, 0x0A, 0x00, 0x01, 0x00, 0x07, 0x09, 0x00,
                0x01, 0x35, 0x03, 0x19, 0x11, 0x0E, 0x09, 0x00, 0x04, 0x35, 0x10, 0x35, 0x06, 0x19, 0x01, 0x00, 0x09,
                0x00, 0x17, 0x35, 0x06, 0x19, 0x00, 0x17, 0x09, 0x01, 0x03, 0x09, 0x00, 0x05, 0x35, 0x03, 0x19, 0x10,
                0x02, 0x09, 0x00, 0x09, 0x35, 0x08, 0x35, 0x06, 0x19, 0x11, 0x0E, 0x09, 0x01, 0x03, 0x09, 0x01, 0x00,
                0x25, 0x01, 0x00, 0x09, 0x03, 0x11, 0x09, 0x00, 0x01, 0x35, 0x5A, 0x09, 0x00, 0x00, 0x0A, 0x00, 0x01,
                0x00, 0x08, 0x09, 0x00, 0x01, 0x35, 0x03, 0x19, 0x11, 0x0B, 0x09, 0x00, 0x04, 0x35, 0x10, 0x35, 0x06,
                0x19, 0x01, 0x00, 0x09, 0x00, 0x19, 0x35, 0x06, 0x19, 0x00, 0x19, 0x09, 0x01, 0x00, 0x09, 0x00, 0x05,
                0x35, 0x03, 0x19, 0x10, 0x02, 0x09, 0x00, 0x06, 0x35, 0x09, 0x09, 0x65, 0x6E, 0x09, 0x00, 0x6A, 0x09,
                0x01, 0x00, 0x09, 0x00, 0x09, 0x35, 0x08, 0x35, 0x06, 0x19, 0x11, 0x0D, 0x09, 0x01, 0x02, 0x09, 0x01,
                0x00, 0x25, 0x0D, 0x53, 0x74, 0x65, 0x72, 0x65, 0x6F, 0x20, 0x41, 0x75, 0x64, 0x69, 0x6F, 0x00, 0x35,
                0x6F, 0x09, 0x00, 0x00, 0x0A, 0x00, 0x01, 0x00, 0x09, 0x09, 0x00, 0x01, 0x35, 0x03, 0x19, 0x11, 0x2F,
                0x09, 0x00, 0x04, 0x35, 0x11, 0x35, 0x03, 0x19, 0x01, 0x00, 0x35, 0x05, 0x19, 0x00, 0x03, 0x08, 0x03,
                0x35, 0x03, 0x19, 0x00, 0x08, 0x09, 0x00, 0x05, 0x35, 0x03, 0x19, 0x10, 0x02, 0x09, 0x00, 0x06, 0x35,
                0x09, 0x09, 0x65, 0x6E, 0x09, 0x00, 0x6A, 0x09, 0x01, 0x00, 0x09, 0x00, 0x09, 0x35, 0x08, 0x35, 0x06,
                0x19, 0x11, 0x30, 0x09, 0x01, 0x01, 0x09, 0x01, 0x00, 0x25, 0x1C, 0x42, 0x6C, 0x75, 0x65, 0x74, 0x6F,
                0x6F, 0x74, 0x68, 0x20, 0x50, 0x68, 0x6F, 0x6E, 0x65, 0x20, 0x42, 0x6F, 0x6F, 0x6B, 0x20, 0x41, 0x63,
                0x63, 0x65, 0x73, 0x73, 0x00, 0x09, 0x03, 0x14, 0x08, 0x01, 0x36, 0x01, 0x4B, 0x09, 0x00, 0x00, 0x0A,
                0x00, 0x01, 0x00, 0x0A, 0x09, 0x00, 0x01, 0x35, 0x03, 0x19, 0x11, 0x24, 0x09, 0x00, 0x04, 0x35, 0x0D,
                0x35, 0x06, 0x19, 0x01, 0x00, 0x09, 0x00, 0x11, 0x35, 0x03, 0x19, 0x00, 0x11, 0x09, 0x00, 0x05, 0x35,
                0x03, 0x19, 0x10, 0x02, 0x09, 0x00, 0x06, 0x35, 0x09, 0x09, 0x65, 0x6E, 0x09, 0x00, 0x6A, 0x09, 0x01,
                0x00, 0x09, 0x00, 0x09, 0x35, 0x08, 0x35, 0x06, 0x19, 0x11, 0x24, 0x09, 0x01, 0x00, 0x09, 0x00, 0x0D,
                0x35, 0x0F, 0x35, 0x0D, 0x35, 0x06, 0x19, 0x01, 0x00, 0x09, 0x00, 0x13, 0x35, 0x03, 0x19, 0x00, 0x11,
                0x09, 0x01, 0x00, 0x25, 0x0B, 0x48, 0x49, 0x44, 0x20, 0x44, 0x65, 0x76, 0x69, 0x63, 0x65, 0x00, 0x09,
                0x02, 0x00, 0x09, 0x01, 0x40, 0x09, 0x02, 0x01, 0x09, 0x01, 0x11, 0x09, 0x02, 0x02, 0x08, 0x40, 0x09,
                0x02, 0x03, 0x08, 0x21, 0x09, 0x02, 0x04, 0x28, 0x00, 0x09, 0x02, 0x05, 0x28, 0x01, 0x09, 0x02, 0x06,
                0x35, 0x9B, 0x35, 0x99, 0x08, 0x22, 0x25, 0x95, 0x05, 0x01, 0x09, 0x06, 0xA1, 0x01, 0x05, 0x07, 0x85,
                0x01, 0x19, 0xE0, 0x29, 0xE7, 0x15, 0x00, 0x25, 0x01, 0x75, 0x01, 0x95, 0x08, 0x81, 0x02, 0x95, 0x01,
                0x75, 0x08, 0x81, 0x01, 0x95, 0x05, 0x75, 0x01, 0x05, 0x08, 0x19, 0x01, 0x29, 0x05, 0x91, 0x02, 0x08,
                0xA0, 0x8B, 0x95, 0x08, 0x80, 0xFA, 0xFF, 0xFF
            },
            new byte[]
            {
                0x07, 0x00, 0x03, 0x01, 0x37, 0x01, 0x34, 0x95, 0x01, 0x75, 0x03, 0x91, 0x01, 0x95, 0x06, 0x75, 0x08,
                0x15,
                0x00, 0x26, 0xA4, 0x00, 0x05, 0x07, 0x19, 0x00, 0x29, 0xA4, 0x81, 0x00, 0xC0, 0x05, 0x01, 0x09, 0x02,
                0xA1, 0x01, 0x09, 0x01, 0xA1, 0x00, 0x85, 0x02, 0x05, 0x09, 0x19, 0x01, 0x29, 0x03, 0x15, 0x00, 0x25,
                0x01, 0x95, 0x03, 0x75, 0x01, 0x81, 0x02, 0x95, 0x01, 0x75, 0x05, 0x81, 0x03, 0x05, 0x01, 0x09, 0x30,
                0x09, 0x31, 0x09, 0x38, 0x15, 0x81, 0x25, 0x7F, 0x75, 0x08, 0x95, 0x03, 0x81, 0x06, 0xC0, 0xC0, 0x05,
                0x0C, 0x09, 0x01, 0xA1, 0x01, 0x85, 0x7F, 0x06, 0x00, 0xFF, 0x75, 0x08, 0x95, 0x03, 0x15, 0x00, 0x26,
                0xFF, 0x00, 0x1A, 0x00, 0xFC, 0x2A, 0x02, 0xFC, 0xB1, 0x02, 0xC0, 0x09, 0x02, 0x07, 0x35, 0x08, 0x35,
                0x06, 0x09, 0x03, 0x09, 0x09, 0x01, 0x00, 0x09, 0x02, 0x08, 0x28, 0x00, 0x09, 0x02, 0x0B, 0x09, 0x01,
                0x00, 0x09, 0x02, 0x0D, 0x28, 0x00, 0x09, 0x02, 0x0E, 0x28, 0x00, 0x35, 0x4C, 0x09, 0x00, 0x00, 0x0A,
                0x00, 0x01, 0x00, 0x0B, 0x09, 0x00, 0x01, 0x35, 0x06, 0x19, 0x11, 0x12, 0x19, 0x12, 0x03, 0x09, 0x00,
                0x04, 0x35, 0x0C, 0x35, 0x03, 0x19, 0x01, 0x00, 0x35, 0x05, 0x19, 0x00, 0x03, 0x08, 0x04, 0x09, 0x00,
                0x05, 0x35, 0x03, 0x19, 0x10, 0x02, 0x09, 0x00, 0x09, 0x35, 0x08, 0x35, 0x06, 0x19, 0x11, 0x08, 0x09,
                0x01, 0x00, 0x09, 0x01, 0x00, 0x25, 0x0E, 0x41, 0x75, 0x64, 0x69, 0x6F, 0x20, 0x47, 0x61, 0x74, 0x65,
                0x77, 0x61, 0x79, 0x00, 0x35, 0x57, 0x09, 0x00, 0x00, 0x0A, 0x00, 0x01, 0x00, 0x0C, 0x09, 0x00, 0x01,
                0x35, 0x06, 0x19, 0x11, 0x1F, 0x19, 0x12, 0x03, 0x09, 0x00, 0x04, 0x35, 0x0C, 0x35, 0x03, 0x19, 0x01,
                0x00, 0x35, 0x05, 0x19, 0x00, 0x03, 0x08, 0x05, 0x09, 0x00, 0x05, 0x35, 0x03, 0x19, 0x10, 0x02, 0x09,
                0x00, 0x09, 0x35, 0x08, 0x35, 0x06, 0x19, 0x11, 0x1E, 0x09, 0x01, 0x06, 0x09, 0x01, 0x00, 0x25, 0x0E,
                0x41, 0x75, 0x64, 0x69, 0x6F, 0x20, 0x47, 0x61, 0x74, 0x65, 0x77, 0x61, 0x79, 0x00, 0x09, 0x03, 0x01,
                0x08, 0x01, 0x09, 0x03, 0x11, 0x09, 0x00, 0x29, 0x00
            }
        };

        /// <summary>
        ///     The HID-Report used by the DS4 to receive commands
        /// </summary>
        /// <remarks>
        ///     <see href="https://github.com/ValveSoftware/SteamOS/issues/274#issuecomment-125632543">
        ///         Byte 3 sets the update rate
        ///         (0x80 equals 1000Hz)
        ///     </see>
        /// </remarks>
        private readonly byte[] _hidReport =
        {
            0x52, 0x11,
            0x80, 0x00, 0xFF, 0x00, 0x00, 0x00, 0x00, 0xFF, 0xFF, 0xFF,
            0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
            0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
            0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
            0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
            0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
            0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
            0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00
        };

        #endregion

        #region Ctors

        public BthDs4()
        {
        }

        public BthDs4(IBthDevice device, PhysicalAddress master, byte lsb, byte msb)
            : base(device, master, lsb, msb)
        {
        }

        #endregion
    }
}
