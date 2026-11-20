using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Ports;
using System.Linq;
using System.Management;
using System.Text.RegularExpressions;
using System.Threading;
using FlexRouter.Hardware.HardwareEvents;
using FlexRouter.Hardware.Helpers;
using FlexRouter.Helpers;
using FlexRouter.Problems;
using FTD2XX_NET;

namespace FlexRouter.Hardware.Arcc
{
    internal class ArccDevicesManager : DeviceManagerBase
    {
        private volatile bool _quit;
        /// <summary>
        /// Префикс, который в паре с идентификатором формирует уникальный идентификатор устройства
        /// </summary>
        private const string DevicePrefix = "Arcc";

        public override void PostOutgoingEvents(ControlEventBase[] outgoingEvents)
        {
            foreach (var d in Devices)
            {
                d.Value.PostOutgoingEvents(outgoingEvents);
            }
        }

        public override void PostOutgoingEvent(ControlEventBase outgoingEvent)
        {
            if (Devices.ContainsKey(outgoingEvent.Hardware.MotherBoardId))
                Devices[outgoingEvent.Hardware.MotherBoardId].PostOutgoingEvent(outgoingEvent);
       }

        public override Capacity GetCapacity(ControlProcessorHardware cph, DeviceSubType deviceSubType)
        {
            if (deviceSubType == DeviceSubType.Motherboard)
            {
                if (cph.ModuleType == HardwareModuleType.Axis
                    || cph.ModuleType == HardwareModuleType.BinaryOutput
                    || cph.ModuleType == HardwareModuleType.Button
                    || cph.ModuleType == HardwareModuleType.Encoder
                    || cph.ModuleType == HardwareModuleType.Indicator)
                    return new Capacity {Names = GetConnectedDevices().ToArray()};

                return new Capacity {DeviceSubtypeIsNotSuitableForCurrentHardware = true};
            }

            // В ARCC id любой платы задаётся байтом. 0-255
            if (deviceSubType == DeviceSubType.ExtensionBoard)
            {
                return new Capacity {Names = Enumerable.Range(0, 255).ToArray().Select(x=>x.ToString(CultureInfo.InvariantCulture)).ToArray()};
            }

            // ARCC не имеет подразделений типа "Block"
            if (deviceSubType == DeviceSubType.Block)
                return new Capacity { DeviceSubtypeIsNotSuitableForCurrentHardware = true };

            switch (cph.ModuleType)
            {
                case HardwareModuleType.Axis:
                    return new Capacity {Names = Enumerable.Range(1, 8).ToArray().Select(x=>x.ToString(CultureInfo.InvariantCulture)).ToArray()};
                case HardwareModuleType.BinaryOutput:
                    return new Capacity { Names = Enumerable.Range(1, 29).ToArray().Select(x => x.ToString(CultureInfo.InvariantCulture)).ToArray() };
                case HardwareModuleType.Button:
                    return new Capacity { Names = Enumerable.Range(1, 168).ToArray().Select(x => x.ToString(CultureInfo.InvariantCulture)).ToArray() };
                case HardwareModuleType.Encoder:
                    return new Capacity { Names = Enumerable.Range(1, 14).ToArray().Select(x => x.ToString(CultureInfo.InvariantCulture)).ToArray() };
                default:
                    return new Capacity { DeviceSubtypeIsNotSuitableForCurrentHardware = true };
            }
        }

        /// <summary>
        /// USB VID/PID устройств, которые определяются как ARCC-совместимые железки.
        /// VID_303A - Espressif ESP32-S3/C6 (нативный USB-CDC, TinyUSB).
        /// VID_1A86&PID_55D3 - мост WCH CH343 (стоит на девкитах ESP32-S3).
        /// </summary>
        private static readonly Regex[] CompatibleUsbIds = new[]
        {
            new Regex(@"VID_303A", RegexOptions.IgnoreCase),
            new Regex(@"VID_1A86&PID_55D3", RegexOptions.IgnoreCase),
        };

        private bool _usbEnabled = true;
        private bool _netEnabled = true;

        public override bool Connect()
        {
            var foundDevices = new Dictionary<string, ArccDevice>();
            if (_usbEnabled)
            {
                FindFtdiArccDevices(foundDevices);
                // Поиск ARCC-совместимых устройств на нативном USB / CH343 (ESP32 и т.п.)
                foreach (var usbDevice in FindCompatibleUsbSerialDevices())
                {
                    if (foundDevices.ContainsKey(usbDevice.Key))
                        continue;
                    var device = new ArccDevice(usbDevice.Key, usbDevice.Value);
                    foundDevices.Add(usbDevice.Key, device);
                }
                foreach (var arccDevice in foundDevices)
                {
                    if (Devices.ContainsKey(arccDevice.Key))
                        continue;
                    if (arccDevice.Value.Connect())
                        Devices.Add(arccDevice.Key, arccDevice.Value);
                }
            }
            // ARCC-устройства в сети (TCP): список адресов задаётся в файле ArccNetDevices.txt рядом с FlexRouter.exe
            // Формат файла: одна строка - один адрес "host:port" (например 192.168.4.1:4001),
            // строки, начинающиеся с #, - комментарии. НОТ: пробелы вокруг адреса допускаются.
            ConnectNetDevices();
            return true;
        }

        /// <summary>
        /// Включить/выключить USB-устройства ARCC (галочка в Settings).
        /// При выключении подключенные USB-устройства отключаются, при включении - ищутся заново
        /// </summary>
        public void SetUsbEnabled(bool enabled)
        {
            if (_usbEnabled == enabled)
                return;
            _usbEnabled = enabled;
            if (!enabled)
            {
                DisconnectByIdPrefix(DevicePrefix + ":USB_");
                return;
            }
            var foundDevices = new Dictionary<string, ArccDevice>();
            FindFtdiArccDevices(foundDevices);
            foreach (var usbDevice in FindCompatibleUsbSerialDevices())
            {
                if (foundDevices.ContainsKey(usbDevice.Key))
                    continue;
                foundDevices.Add(usbDevice.Key, new ArccDevice(usbDevice.Key, usbDevice.Value));
            }
            foreach (var arccDevice in foundDevices)
            {
                if (Devices.ContainsKey(arccDevice.Key))
                    continue;
                if (arccDevice.Value.Connect())
                    Devices.Add(arccDevice.Key, arccDevice.Value);
            }
        }

        /// <summary>
        /// Включить/выключить сетевые устройства ARCC (галочка в Settings).
        /// При выключении подключенные TCP-устройства отключаются, при включении - соединяются заново
        /// </summary>
        public void SetNetEnabled(bool enabled)
        {
            if (_netEnabled == enabled)
                return;
            _netEnabled = enabled;
            if (enabled)
                ConnectNetDevices();
            else
                DisconnectByIdPrefix(DevicePrefix + ":NET_");
        }

        /// <summary>
        /// Отключить и удалить из списка устройства с идентификатором, начинающимся с префикса
        /// </summary>
        private void DisconnectByIdPrefix(string prefix)
        {
            var keys = Devices.Keys.Where(k => k.StartsWith(prefix)).ToList();
            foreach (var key in keys)
            {
                Devices[key].Disconnect();
                Devices.Remove(key);
            }
        }

        /// <summary>
        /// Найти классические ARCC-материнские платы на FTDI и добавить их в foundDevices
        /// </summary>
        private void FindFtdiArccDevices(Dictionary<string, ArccDevice> foundDevices)
        {
            try
            {
                var myFtdiDevice = new FTDI();
                uint ftdiDeviceCount = 0;

                var ftStatus = myFtdiDevice.GetNumberOfDevices(ref ftdiDeviceCount);
                if (ftStatus != FTDI.FT_STATUS.FT_OK)
                    return;
                var ftdiDeviceList = new FTDI.FT_DEVICE_INFO_NODE[ftdiDeviceCount];
                ftStatus = myFtdiDevice.GetDeviceList(ftdiDeviceList);
                if (ftStatus != FTDI.FT_STATUS.FT_OK)
                    return;

                for (var i = 0; i < ftdiDeviceList.Length; i++)
                {
                    const string descriptionPattern = "ARCC";
                    if (!ftdiDeviceList[i].Description.StartsWith(descriptionPattern))
                        continue;
                    ftStatus = myFtdiDevice.OpenByIndex((uint)i);
                    if (ftStatus != FTDI.FT_STATUS.FT_OK)
                        continue;

                    string comPort;
                    var chipId = 0;

                    ftStatus = myFtdiDevice.GetCOMPort(out comPort);
                    if (ftStatus != FTDI.FT_STATUS.FT_OK)
                        continue;

                    myFtdiDevice.Close();
                    FTChipID.ChipID.GetDeviceChipID(i, ref chipId);
                    var id = DevicePrefix + ":" + chipId.ToString("X");
                    if (foundDevices.ContainsKey(id))
                        continue;
                    var device = new ArccDevice(id, comPort);
                    foundDevices.Add(id, device);
                }
            }
            catch (Exception)
            {
            }
        }

        /// <summary>
        /// Подключиться к ARCC-устройствам по сети (TCP). Адреса берутся из файла ArccNetDevices.txt
        /// рядом с FlexRouter.exe. Устройства регистрируются всегда: при недоступности платы
        /// ArccNetDevice сам продолжает попытки соединения в фоне.
        /// Идентификатор устройства: Arcc:NET_&lt;host&gt;_&lt;port&gt;
        /// </summary>
        private void ConnectNetDevices()
        {
            if (!_netEnabled)
                return;
            foreach (var address in FindNetDeviceAddresses())
            {
                var parts = address.Split(':');
                if (parts.Length != 2)
                    continue;
                var host = parts[0].Trim();
                int port;
                if (!int.TryParse(parts[1].Trim(), out port) || port <= 0 || port > 65535)
                {
                    Debug.Print("ARCC NET: bad address in ArccNetDevices.txt: " + address);
                    continue;
                }
                var id = DevicePrefix + ":NET_" + host + "_" + port;
                if (Devices.ContainsKey(id))
                    continue;
                var device = new ArccNetDevice(id, host, port);
                device.Connect();
                Devices.Add(id, device);
                Debug.Print("ARCC NET device added: " + id);
            }
        }

        /// <summary>
        /// Прочитать список адресов сетевых ARCC-устройств из ArccNetDevices.txt рядом с FlexRouter.exe
        /// </summary>
        private static IEnumerable<string> FindNetDeviceAddresses()
        {
            var result = new List<string>();
            try
            {
                var exePath = System.Reflection.Assembly.GetEntryAssembly().Location;
                if (string.IsNullOrEmpty(exePath))
                    return result;
                var fileName = Path.Combine(Path.GetDirectoryName(exePath), "ArccNetDevices.txt");
                if (!File.Exists(fileName))
                    return result;
                foreach (var line in File.ReadAllLines(fileName))
                {
                    var trimmed = line.Trim();
                    if (trimmed.Length == 0 || trimmed.StartsWith("#"))
                        continue;
                    result.Add(trimmed);
                }
            }
            catch (Exception ex)
            {
                Debug.Print("ARCC NET: failed to read ArccNetDevices.txt: " + ex.Message);
            }
            return result;
        }

        /// <summary>
        /// Найти все USB-COM устройства, чей USB DeviceID совпадает с одним из CompatibleUsbIds.
        /// Ключ - стабильный идентификатор устройства (из PNPDeviceID), значение - имя COM-порта
        /// </summary>
        private static Dictionary<string, string> FindCompatibleUsbSerialDevices()
        {
            var result = new Dictionary<string, string>();
            try
            {
                var existingPorts = SerialPort.GetPortNames();
                if (existingPorts.Length == 0)
                    return result;
                using (var searcher = new ManagementObjectSearcher(
                    "SELECT Name, DeviceID, PNPDeviceID FROM Win32_PnPEntity WHERE PNPClass='Ports'"))
                using (var collection = searcher.Get())
                {
                    foreach (var o in collection)
                    {
                        using (var entity = (ManagementObject)o)
                        {
                            var pnpDeviceId = Convert.ToString(entity["PNPDeviceID"]);
                            var deviceName = Convert.ToString(entity["DeviceID"]);
                            if (string.IsNullOrEmpty(pnpDeviceId) || string.IsNullOrEmpty(deviceName))
                                continue;
                            if (!CompatibleUsbIds.Any(r => r.IsMatch(pnpDeviceId)))
                                continue;
                            // Имя порта берём из Name вида "... (COM5)"
                            var name = Convert.ToString(entity["Name"]);
                            var match = Regex.Match(name ?? "", @"\((COM\d+)\)\s*$", RegexOptions.IgnoreCase);
                            if (!match.Success)
                                continue;
                            var comPort = match.Groups[1].Value;
                            if (!existingPorts.Contains(comPort, StringComparer.OrdinalIgnoreCase))
                                continue;
                            // Убираем &MI_XX (номер интерфейса композитного устройства) - он может меняться
                            var stableId = Regex.Replace(pnpDeviceId, @"&MI_\d+", "").Replace('\\', '_');
                            var id = DevicePrefix + ":USB_" + stableId;
                            if (!result.ContainsKey(id))
                                result.Add(id, comPort);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.Print("ARCC USB scan failed: " + ex.Message);
            }
            return result;
        }
        #region Методы доработаны для реализации контролируемого дампа железа Arcc
        /// <summary>
        /// Отключить все устройства
        /// </summary>
        public override void Disconnect()
        {
            // Дождаться выхода из дампа и только после этого разорвать соединение
            if (_dumpThread!=null && _dumpThread.IsAlive)
            {
                _quit = true;
                _dumpThread.Join();
            }
            foreach (var device in Devices)
                device.Value.Disconnect();
            Devices.Clear();
            ClearCaches();
        }
        /// <summary>
        /// Получить входящие события (нажатие кнопки, вращение энкодера и т.д.)
        /// </summary>
        /// <returns>Массив событий</returns>
        public override ControlEventBase[] GetIncomingEvents()
        {
            var ie = new List<ControlEventBase>();
            for (var i = 0; i < Devices.Count; i++)
            {
                var incomingEvents = Devices.ElementAt(i).Value.GetIncomingEvents();
                if (incomingEvents == null)
                    continue;
                ie.AddRange(incomingEvents);
                if(_inDump)
                    RememberDumpEvents(incomingEvents);
            }
            return ie.Count == 0 ? null : ie.ToArray();
        }
        #endregion
        #region Dump
        /// <summary>
        /// Метод заполняет массив сдампленных кнопок. Получая собитие от модуля, которму отправлена команда Dump,
        /// устанавливает в true соответствующий элемент массива
        /// </summary>
        /// <param name="events">список новых событий</param>
        private void RememberDumpEvents(IEnumerable<ControlEventBase> events)
        {
            lock (_dumpSyncRoot)
            {
                foreach (var ev in events)
                {
                    if (ev.Hardware.MotherBoardId != HardwareInDump.MotherBoardId || ev.Hardware.ModuleType != HardwareInDump.ModuleType || ev.Hardware.ModuleId != HardwareInDump.ModuleId)
                        continue;
                    if (_moduleTypeInDump == ModuleTypeInDump.BinaryInput)
                        _binaryInputDumpState[ev.Hardware.ControlId-1] = true;
                    if (_moduleTypeInDump == ModuleTypeInDump.Button)
                        _buttonDumpState[ev.Hardware.ControlId-1] = true;
                }
            }
        }

        private volatile bool _inDump;
        private readonly object _dumpSyncRoot = new object();
        private Thread _dumpThread;
        public volatile ControlProcessorHardware HardwareInDump = null;
        private ControlProcessorHardware[] _allHardwareInUse;
        private enum ModuleTypeInDump
        {
            None,
            Button,
            BinaryInput
        }

        private void ClearCaches()
        {
            _buttonDumpState = new bool[168];
            _binaryInputDumpState = new bool[28];
            _moduleTypeInDump = ModuleTypeInDump.None;
        }
        private bool[] _buttonDumpState = new bool[168];
        private bool[] _binaryInputDumpState = new bool[28];
        private ModuleTypeInDump _moduleTypeInDump = ModuleTypeInDump.None;

        public override void Dump(ControlProcessorHardware[] allHardwareInUse)
        {
            if (_dumpThread != null && _dumpThread.IsAlive)
                return;
            _allHardwareInUse = allHardwareInUse;
            _dumpThread = new Thread(DumpLoop){IsBackground = true};
            _dumpThread.Start();
        }
        /// <summary>
        /// Циклическая функция отправки команд дампа по одной и ожидания дампа всех кнопок
        /// </summary>
        private void DumpLoop()
        {
            Thread.Sleep(2000);
            _inDump = true;
            var hardwareArray = new ControlProcessorHardware[1];
            var dumpQueueHashes = new List<string>();
            foreach (var controlProcessorHardware in _allHardwareInUse)
            {
                // Если железо не Arcc
                if(!Devices.ContainsKey(controlProcessorHardware.MotherBoardId))
                    continue;
                
                // Если такая пара "материнская плата + модуль" уже обрабатывались
                var hash = controlProcessorHardware.MotherBoardId + "|" + controlProcessorHardware.ModuleId + "|" + controlProcessorHardware.ModuleType;
                if(dumpQueueHashes.Contains(hash))
                    continue;
                System.Diagnostics.Debug.Print("Dumping: " + controlProcessorHardware.MotherBoardId);//+ hash);
                lock (_dumpSyncRoot)
                {
                    // Очищаем массивы сработавших событий
                    for (var i = 0; i < _binaryInputDumpState.Length; i++)
                    {
                        _binaryInputDumpState[i] = false;
                    }
                    for (var i = 0; i < _buttonDumpState.Length; i++)
                    {
                        _buttonDumpState[i] = false;
                    }
                    // Отправляем пару "материнская плата + модуль" дампиться
                    HardwareInDump = controlProcessorHardware;
                    hardwareArray[0] = controlProcessorHardware;
                    _moduleTypeInDump = controlProcessorHardware.ModuleId > 255 ? ModuleTypeInDump.BinaryInput : ModuleTypeInDump.Button;
                }
                Devices[controlProcessorHardware.MotherBoardId].Dump(hardwareArray);
                var lastUpdateTime = DateTime.Now;
                var stateCount = 0;

                //var m = string.Format("{0} module {1}", HardwareInDump.MotherBoardId, _moduleTypeInDump);
                //Problems.AddOrUpdateProblem(m, "Дамп ", ProblemHideOnFixOptions.HideItemAndDescription, false);
                // Ждём дампа всех кнопок модуля или перерыва в поставке данных и переходим к дампу следующего модуля
                while (true)
                {
                    if (_quit)
                        return;
                    lock (_dumpSyncRoot)
                    {
                        var buttonsDumpedCount = 0;
                        if (_moduleTypeInDump == ModuleTypeInDump.Button)
                        {
                            // Если все кнопки сдамплены переходим к следующему модулю
                            if (_buttonDumpState.All(b => b))
                            {
                                System.Diagnostics.Debug.Print("Dumped successfully");
                                var moduleType = _moduleTypeInDump.ToString();
                                var message = string.Format("{0} module {1}", HardwareInDump.MotherBoardId, moduleType);
                                Problems.Problems.AddOrUpdateProblem(message, "", ProblemHideOnFixOptions.HideItemAndDescription, true);
                                break;
                            }
                            buttonsDumpedCount = _buttonDumpState.Count(b => b);
                        }
                        if (_moduleTypeInDump == ModuleTypeInDump.BinaryInput)
                        {
                            // Если все кнопки сдамплены переходим к следующему модулю
                            if (_binaryInputDumpState.All(b => b))
                            {
                                System.Diagnostics.Debug.Print("Dumped successfully");

                                var moduleType = _moduleTypeInDump.ToString();
                                var message = string.Format("{0} module {1}", HardwareInDump.MotherBoardId, moduleType);
                                Problems.Problems.AddOrUpdateProblem(message, "", ProblemHideOnFixOptions.HideItemAndDescription, true);
                                break;
                            }
                            buttonsDumpedCount = _binaryInputDumpState.Count(b => b);
                        }
                        // Если прошло X миллисекунд и ни одной новой кнопки не сдамплено, значит дамп окончен
                        if (buttonsDumpedCount == stateCount)
                        {
                            if (DateTime.Now - lastUpdateTime > new TimeSpan(0, 0, 0, 0, 1000))
                            {
                                var moduleType = _moduleTypeInDump.ToString();
                                var buttonsArray = _moduleTypeInDump == ModuleTypeInDump.Button
                                    ? _buttonDumpState
                                    : _binaryInputDumpState;
                                var undumpedButtonsCount = _moduleTypeInDump == ModuleTypeInDump.Button
                                    ? _buttonDumpState.Length - buttonsDumpedCount
                                    : _binaryInputDumpState.Length - buttonsDumpedCount;
                                var buttonsList = string.Empty;
                                for (var i = 0; i < buttonsArray.Length; i++)
                                {
                                    if (buttonsArray[i])
                                        continue;
                                    if (buttonsList != string.Empty)
                                        buttonsList += ", ";
                                    buttonsList += (i + 1).ToString(CultureInfo.InvariantCulture);
                                }

                                var description = string.Format("Can't dump {0} keys: {1}", undumpedButtonsCount, buttonsList);
                                var message = string.Format("{0} module {1}", HardwareInDump.MotherBoardId, moduleType);
                                Problems.Problems.AddOrUpdateProblem(message, description, ProblemHideOnFixOptions.HideItemAndDescription, false);
                                System.Diagnostics.Debug.Print("Dump failed. Dumped: " + buttonsDumpedCount);
                                break;
                            }
                        }
                        else
                        {
                            lastUpdateTime = DateTime.Now;
                            stateCount = buttonsDumpedCount;
                        }
                    }
                    Thread.Sleep(100);
                }
            }
            _inDump = false;
        }
        #endregion
    }
}
