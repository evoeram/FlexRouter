# FlexRouter

Приложение-«роутер» для подключения домашнего кокпита (физических блоков кнопок, энкодеров,
индикаторов и ламп) к полётному симулятору. FlexRouter читает состояние органов управления,
преобразует его в переменные симулятора и, наоборот, выводит состояние симулятора на
индикаторы и лампы.

Основная цель проекта — полноразмерные пульты Ту-154 (симулятор PT Tu-154M под
FS2004, модуль `NN_pnk_154m_*.GAU`), но программа не привязана к конкретному самолёту:
всё поведение описывается профилем.

Версия: **0.731** (см. `changes.txt`)

## Возможности

- **Профили** — XML-файлы (`.ap`), описывающие панели, переменные, описатели доступа и
  процессоры органов управления. Профиль можно создавать и редактировать в интерфейсе
  приложения (WPF).
- **Обработка переменных симулятора** несколькими способами:
  - *Memory patch* — прямое чтение/запись памяти процесса симулятора (размер, смещение и
    модуль указываются для каждой переменной);
  - *FSUIPC* — через общедоступный FSUIPC;
  - *Click* — имитация кликов по окнам FS2004;
  - *Fake* — фиктивные переменные (для тестов и цепочек без симулятора).
- **Дескрипторы доступа** — «общий язык» между переменной и органом управления:
  - *Value* — набор дискретных значений;
  - *Range* — число с минимумом/максимумом/шагом;
  - *BinaryOutput* — двоидный вывод (вкл/выкл);
  - *Indicator* — вывод на 7-сегментный индикатор (в т.ч. с текстом).
- **Процессоры органов управления**: кнопки, кнопки «+/-», эмулятор кнопки с
  переключением, энкодеры/оси, индикаторы, лампы, шаговые моторы, светодиодные матрицы.
- **Формулы** — встроенный вычислитель выражений: питание панелей (`PowerFormula`),
  преобразования значений переменных, формулы индикаторов, коммутация.
- **Map-файлы** — утилита обновления смещений и имён переменных по map-файлу линкера
  модуля `.GAU` (когда симулятор перекомпилирован).
- **Локализация** — русский и английский интерфейсы (`!Configutaion\Languages`).
- **Логи** — необработанные исключения записываются в каталог `Logs` рядом с exe.
- **Юнит-тесты** — MSTest (калькулятор формул, работа с профилем и железом ARCC).

## Поддерживаемое железо

| Устройство | Подключение | Что даёт |
|---|---|---|
| **ARCC** | USB (чип FTDI, определяется по ChipID, обмен через COM-порт) | блоки: до 168 кнопок, 14 энкодеров, 8 осей, 28 двоичных выводов |
| **F3 / L3** | USB (API `f3io.dll`, HID) | индикаторы (7-сегментные), лампы и т.п. |
| **Клавиатура** | DirectInput (SlimDX) | кнопки по нажатию клавиш |
| **Джойстик** | DirectInput (SlimDX) | оси/кнопки джойстиков |

Идентификатор устройства в назначении имеет вид `<Платформа>:<ID>|<Тип модуля>|<Номер>`
(например, `Arcc:1A2B3C|Indicator|1`).

## Структура проекта

```
FlexRouter.sln                решение (FlexRouter + FlexRouterUnitTests)
Core.cs                       «сердце» роутера: поток, цикл Work/Pause/Stop, дамп железа
ProfileItems/                 профиль: панели, хранилища переменных/дескрипторов/процессоров
AccessDescriptors/            дескрипторы доступа (Value, Range, BinaryOutput, Indicator,
                              RangeUnion) и хранилище формул (FormulaKeeper)
ControlProcessors/            процессоры органов управления (Button, Encoder, Axis, Lamp,
                              Indicator, SteppingMotor, LedMatrixIndicator, ...)
VariableWorkerLayer/          работа с переменными: MemoryPatch, Fsuipc, Click, Fake,
                              конвертация типов (MemoryVariableSize)
Hardware/                     устройства: Arcc (COM-порт, FTDI), F3 (f3io.dll),
                              Keyboard/Joystick (SlimDX), события железа
CalculatorRelated/            токенизатор/парсер/вычислитель формул
EditorsUI/                    WPF-редакторы (панели, переменные, дескрипторы, назначения)
ManageMapFiles/               обновление смещений/имён переменных по map-файлу
Localizers/                   локализация (Phrases, LanguageManager)
MessagesToMainForm/           шина сообщений UI (Messenger)
Problems/                     механизм «проблем» профиля с подсказками в интерфейсе
FlexRouterUnitTests/          юнит-тесты (MSTest)
!Configutaion/               Languages, Profiles — конфигурация, которую нужно
                              скопировать рядом с FlexRouter.exe (см. «Запуск»)
Addons/                       внешние сборки: SlimDX, FTChipIDNet, FTD2XX_NET
~docs/                        пользовательская документация (.docx)
changes.txt                   история изменений
```

## Сборка

Требования:

- Windows;
- Visual Studio 2013 или новее (проект в «старом» формате csproj, WPF), либо
  VS Build Tools;
- **.NET Framework 4.5** — рантайм и *targeting pack* (reference assemblies).

Зависимости (уже лежат в `Addons\`):

- `SlimDX.dll` 4.0.13.43 (DirectInput/Direct3D);
- `FTChipIDNet.dll`, `FTD2XX_NET.dll` + нативные `FTChipID.dll`, `ftd2xx.dll`
  (определение ARCC по FTDI-чипу);
- `f3io.dll` — **в репозитории нет**, ставится из SDK платы F3/L3 (нужна только для работы
  с платой F3; для сборки достаточно описанных P/Invoke-объявлений).

Через Visual Studio: открыть `FlexRouter.sln` → *Build*.

Из командной строки (MSBuild из VS Build Tools 2022):

```bat
msbuild FlexRouter.sln /p:Configuration=Release
```

Платформу не указывайте: в `.sln` уже прописана привязка `Release|x86` для приложения и
`Release|Any CPU` для тестов.

> **Примечание.** Если .NET 4.5 targeting pack не установлен (ошибка MSB3644),
> установите .NET Framework 4.5 Developer Pack либо передайте reference assemblies из
> NuGet-пакета `Microsoft.NETFramework.ReferenceAssemblies.net45`:
>
> ```bat
> msbuild FlexRouter.sln /p:Configuration=Release ^
>   /p:TargetFrameworkRootPath="<путь>\net45ra\build\"
> ```

Результат: `bin\Release\FlexRouter.exe` (x86).

## Запуск

1. Скопируйте содержимое папки `!Configutaion` (`Languages`, `Profiles`) в каталог с
   `FlexRouter.exe`.
2. Запустите `FlexRouter.exe`, выберите профиль и язык. Роутер стартует автоматически при
   загрузке/сохранении профиля, его состояние (Рабочий / Приостановлен / Остановлен) видно в
   поле «Состояние роутера».
3. Настройки приложения хранятся в `FlexRouter.exe.ini` рядом с исполняемым файлом
   (`ApplicationSettings`), часть ключей — в `app.config`
   (`userSettings`: `DefaultProfile`, `DefaultLanguage`, `F3IndicatorSegmentsToPin`, ...).
4. Логи необработанных исключений — в каталоге `Logs` рядом с exe.

Переменные профиля указывают `ModuleName` (имя модуля `.GAU`) и смещение; при смене версии
симулятора смещения/имена обновляются через *ManageMapFiles* по map-файлу.

## Тесты

Юнит-тесты — MSTest, проект `FlexRouterUnitTests`. Сборка вместе с решением. Запуск из
командной строки (важен `/Platform:x86` — приложение собрано под x86):

```bat
vstest.console.exe FlexRouterUnitTests\bin\Release\FlexRouterUnitTests.dll /Platform:x86
```

(`vstest.console.exe` лежит в `Common7\IDE\CommonExtensions\Microsoft\TestWindow` вашего
Visual Studio.)

## Документация

- `~docs\FlexRouter. Руководство пользователя 0.730.docx` — руководство пользователя;
- `~docs\Что такое переменные и как их искать.docx` — как находить переменные симулятора
  (ArtMoney, map-файлы) и добавлять их в профиль;
- `changes.txt` — история изменений (кодировка Windows-1251).

## Лицензия

Лицензионный файл в репозитории отсутствует. Проект является форком
[diminovei/FlexRouter](https://github.com/diminovei/FlexRouter).
