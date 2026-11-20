using System;
using System.Collections.Generic;
using System.Net.Sockets;
using System.Threading;
using FlexRouter.Hardware.HardwareEvents;
using FlexRouter.Hardware.Helpers;

namespace FlexRouter.Hardware.Arcc
{
    /// <summary>
    /// ARCC-устройство, подключаемое по сети (TCP) вместо COM-порта.
    /// Протокольное ядро то же (ArccHardwareDataProcessor), транспорт - TcpClient/NetworkStream.
    /// У платы должен быть включён TCP-сервер ARCC (порт 4001, "transport":{"tcp":true}).
    /// Обрыв линка не роняет потоки: устройство само реконнектится с backoff (1с -> 5с -> 15с).
    /// </summary>
    public class ArccNetDevice : IHardwareDevice
    {
        /// <summary>
        /// Значение-метка для потоков, установленная в true говорит о том, что пора завершаться
        /// </summary>
        private volatile bool _quit;
        /// <summary>
        /// Нить для разгребания очереди исходящих сообщений (периодической отсылки данных на лампы, индикаторы, ...)
        /// </summary>
        private readonly Thread _sendDataThread;
        /// <summary>
        /// Нить приёма данных и восстановления соединения
        /// </summary>
        private readonly Thread _receiveThread;
        /// <summary>
        /// Хост (ip-адрес или имя) платы ARCC
        /// </summary>
        private readonly string _host;
        /// <summary>
        /// TCP-порт платы ARCC
        /// </summary>
        private readonly int _port;
        /// <summary>
        /// id материнской платы
        /// </summary>
        public string MotherboardId { get; private set; }
        /// <summary>
        /// TCP-соединение с платой (null, если линк не поднят)
        /// </summary>
        private TcpClient _client;
        private NetworkStream _stream;
        /// <summary>
        /// Линк поднят (TCP-сессия установлена)
        /// </summary>
        private volatile bool _linkUp;
        /// <summary>
        /// Текущая задержка перед попыткой реконнекта, мс
        /// </summary>
        private int _reconnectDelayMs;
        private const int ReconnectDelaysMs = 1000;
        private const int MaxReconnectDelayMs = 15000;
        /// <summary>
        /// Таймаут на установку TCP-соединения, мс
        /// </summary>
        private const int ConnectTimeoutMs = 3000;
        /// <summary>
        /// Таймаут блокирующего чтения из сети, мс (чтобы поток мог завершаться)
        /// </summary>
        private const int ReadTimeoutMs = 500;
        /// <summary>
        /// Очередь входящих событий (кнопки, энкодеры, ...)
        /// </summary>
        private readonly Queue<ControlEventBase> _incomingEvents = new Queue<ControlEventBase>();
        /// <summary>
        /// Очередь исходящих событий (индикаторы, лампы, ...)
        /// </summary>
        private readonly Queue<ControlEventBase> _outgoingEvents = new Queue<ControlEventBase>();
        /// <summary>
        /// Ограничение очереди исходящих событий при лежащем линке (чтобы не копить мусор)
        /// </summary>
        private const int MaxOutgoingQueue = 512;
        /// <summary>
        /// Класс, преобразующий массив данных от устройства в события, понятные роутеру и наоборот
        /// </summary>
        private readonly ArccHardwareDataProcessor _arccHardwareDataProcessor;
        /// <summary>
        /// Конструктор
        /// </summary>
        /// <param name="motherboardId">Идентификатор устройства для использования в роутере</param>
        /// <param name="host">Хост платы (ip или имя)</param>
        /// <param name="port">TCP-порт платы</param>
        public ArccNetDevice(string motherboardId, string host, int port)
        {
            MotherboardId = motherboardId;
            _arccHardwareDataProcessor = new ArccHardwareDataProcessor(MotherboardId);
            _host = host;
            _port = port;
            _sendDataThread = new Thread(SendDataLoop) { IsBackground = true };
            _receiveThread = new Thread(ReceiveLoop) { IsBackground = true };
        }
        /// <summary>
        /// Получить входящие события (нажатие кнопки, вращение энкодера и т.д.)
        /// </summary>
        /// <returns>Массив событий</returns>
        public ControlEventBase[] GetIncomingEvents()
        {
            if (_incomingEvents.Count == 0)
                return null;
            lock (_incomingEvents)
            {
                var copy = _incomingEvents.ToArray();
                _incomingEvents.Clear();
                return copy;
            }
        }

        public void PostOutgoingEvents(ControlEventBase[] outgoingEvents)
        {
            lock (_outgoingEvents)
            {
                foreach (var ev in outgoingEvents)
                {
                    if (ev.Hardware.MotherBoardId != MotherboardId)
                        continue;
                    if (_outgoingEvents.Count >= MaxOutgoingQueue)
                        _outgoingEvents.Clear();
                    _outgoingEvents.Enqueue(ev);
                }
            }
        }
        public void PostOutgoingEvent(ControlEventBase outgoingEvent)
        {
            lock (_outgoingEvents)
            {
                if (_outgoingEvents.Count >= MaxOutgoingQueue)
                    _outgoingEvents.Clear();
                _outgoingEvents.Enqueue(outgoingEvent);
            }
        }
        /// <summary>
        /// Начать работу с устройством. При недоступности платы возвращает true в любом случае -
        /// нить приёма продолжит попытки соединения в фоне (реконнект без рестарта роутера)
        /// </summary>
        /// <returns>true - фоновые нити запущены</returns>
        public bool Connect()
        {
            if (_receiveThread.IsAlive || _sendDataThread.IsAlive)
                return true;
            _quit = false;
            _reconnectDelayMs = ReconnectDelaysMs;
            _sendDataThread.Start();
            _receiveThread.Start();
            return true;
        }
        /// <summary>
        /// Разорвать соединение с устройством и остановить фоновые нити
        /// </summary>
        public void Disconnect()
        {
            _quit = true;
            CloseLink();
            if (_sendDataThread.IsAlive)
                _sendDataThread.Join(2000);
            if (_receiveThread.IsAlive)
                _receiveThread.Join(2000);
        }
        /// <summary>
        /// Установить TCP-соединение с платой
        /// </summary>
        private bool ConnectTcp()
        {
            var client = new TcpClient { NoDelay = true };
            try
            {
                var async = client.BeginConnect(_host, _port, null, null);
                if (!async.AsyncWaitHandle.WaitOne(ConnectTimeoutMs) || !client.Connected)
                {
                    client.Close();
                    return false;
                }
                client.EndConnect(async);
                client.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.KeepAlive, true);
                _client = client;
                _stream = client.GetStream();
                _stream.ReadTimeout = ReadTimeoutMs;
                _linkUp = true;
                _reconnectDelayMs = ReconnectDelaysMs;
                System.Diagnostics.Debug.Print("ARCC NET " + _host + ":" + _port + " connected");
                return true;
            }
            catch (Exception)
            {
                try { client.Close(); } catch (Exception) { }
                return false;
            }
        }
        /// <summary>
        /// Закрыть TCP-соединение, пометить линк разорванным
        /// </summary>
        private void CloseLink()
        {
            _linkUp = false;
            _stream = null;
            if (_client != null)
            {
                try { _client.Close(); } catch (Exception) { }
                _client = null;
            }
        }
        /// <summary>
        /// Циклическая функция нити приёма: держит соединение, читает данные от платы
        /// </summary>
        private void ReceiveLoop()
        {
            while (!_quit)
            {
                if (!_linkUp)
                {
                    if (ConnectTcp())
                        continue;
                    // Плата недоступна - ждём и пробуем снова (backoff 1с -> 5с -> 15с)
                    Thread.Sleep(_reconnectDelayMs);
                    _reconnectDelayMs = _reconnectDelayMs >= MaxReconnectDelayMs
                        ? MaxReconnectDelayMs
                        : (_reconnectDelayMs < ReconnectDelaysMs * 5 ? ReconnectDelaysMs * 5 : MaxReconnectDelayMs);
                    continue;
                }
                try
                {
                    var buffer = new byte[1024];
                    int bytesRead;
                    try
                    {
                        bytesRead = _stream.Read(buffer, 0, buffer.Length);
                    }
                    catch (Exception ex)
                    {
                        if (_quit)
                            return;
                        // Таймаут чтения - НЕ обрыв линка: плата молчит между событиями,
                        // это штатная ситуация (иначе - бесконечный реконнект каждые 500 мс)
                        if (ex is System.IO.IOException ioEx &&
                            ioEx.InnerException is SocketException se &&
                            se.SocketErrorCode == SocketError.TimedOut)
                            continue;
                        if (ex is System.IO.IOException || ex is SocketException || ex is ObjectDisposedException)
                        {
                            System.Diagnostics.Debug.Print("ARCC NET RX link down on " + _host + ":" + _port + ": " + ex.Message);
                            CloseLink();
                            continue;
                        }
                        throw;
                    }
                    if (bytesRead == 0)
                    {
                        // Плата закрыла соединение
                        System.Diagnostics.Debug.Print("ARCC NET link closed by peer " + _host + ":" + _port);
                        CloseLink();
                        continue;
                    }
                    lock (_incomingEvents)
                    {
                        var data = new byte[bytesRead];
                        Array.Copy(buffer, data, bytesRead);
                        var events = _arccHardwareDataProcessor.ProcessDataFromSerialPort(data);
                        foreach (var ev in events)
                            _incomingEvents.Enqueue(ev);
                    }
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.Print("ARCC NET RX error on " + _host + ":" + _port + ": " + ex.Message);
                    CloseLink();
                }
            }
        }
        /// <summary>
        /// Циклическая функция отправки данных устройству
        /// </summary>
        private void SendDataLoop()
        {
            while (true)
            {
                if (_quit)
                    return;

                if (_outgoingEvents.Count == 0 || !_linkUp)
                {
                    Thread.Sleep(10);
                    continue;
                }
                try
                {
                    SendData();
                }
                catch (Exception ex)
                {
                    // Ошибка сети (плата отвалилась) - не роняем поток, ждём реконнекта
                    System.Diagnostics.Debug.Print("ARCC NET TX error on " + _host + ":" + _port + ": " + ex.Message);
                    CloseLink();
                    Thread.Sleep(100);
                }
            }
        }
        /// <summary>
        /// Отправить накопившиеся события железу
        /// </summary>
        private void SendData()
        {
            lock (_outgoingEvents)
            {
                while (_outgoingEvents.Count != 0 && _linkUp)
                {
                    var ev = _outgoingEvents.Dequeue();
                    var outDataBuffer = _arccHardwareDataProcessor.ConvertEventToByteArrayForHardware(ev);
                    if (outDataBuffer != null)
                        _stream.Write(outDataBuffer, 0, outDataBuffer.Length);
                    Thread.Sleep(2);
                }
            }
        }
        /// <summary>
        /// Дамп всех клавиш
        /// </summary>
        /// <param name="allHardwareInUse">массив содержит упоминание всех модулей, которые нужно сдампить</param>
        public void Dump(ControlProcessorHardware[] allHardwareInUse)
        {
            // Если не сделать эту задержку, то дамп может не произойти. Почему - не понятно.
            Thread.Sleep(1000);
            var dumpEvents = new ControlEventBase[1];
            var dumpEvent = new DumpEvent { Hardware = allHardwareInUse[0] };
            dumpEvents[0] = dumpEvent;
            PostOutgoingEvents(dumpEvents);
            System.Diagnostics.Debug.Print("Dump: " + MotherboardId);
        }
    }
}
