<p align="center">
  <img src="private/header.svg" alt="Filee, private build: собран из исходников, без проверок обновлений и без автоматических подключений к серверам" width="100%">
</p>
<p align="center">
  <img src="private/stats.svg" alt="0 трекеров · 0 автоподключений · 30 файлов движков с SHA-256 · 247 форматов" width="100%">
</p>

<p align="center">
  <a href="https://github.com/flexeykinDev/Filee/releases/tag/private-1.12.0"><b>Скачать установщик</b></a> ·
  <a href="https://github.com/KnifeLemon/Filee"><b>Оригинальный Filee</b></a> ·
  <a href="#build">Собрать самому</a>
</p>

Это неофициальная сборка [Filee](https://github.com/KnifeLemon/Filee), бесплатного конвертера файлов от
[KnifeLemon](https://github.com/KnifeLemon). Зажимаете <kbd>Ctrl</kbd>, тянете файл, и у курсора открывается
кольцо форматов. Код тот же, что в оригинале, плюс один коммит, который отключает автоматическое обращение к серверам.

<a id="diff"></a>
<img src="private/s-diff.svg" alt="01 · Чем отличается от официальной сборки" width="100%">

- **Нет проверки обновлений.** Оригинал раз в 6 часов спрашивает `api.github.com` о новой версии. Здесь все запросы
  этой службы отклоняются ещё до подключения, а таймер не запускается.
- **Нет самообновления.** Оригинал умеет сам скачать и запустить официальный установщик, который заменил бы эту
  сборку на чужую. Здесь это отключено.
- Галочка «Проверять обновления» выключена по умолчанию.
- Всё остальное взято из `main` оригинала без изменений: [`0f43adb`](https://github.com/KnifeLemon/Filee/commit/0f43adb)
  и коммит [`e3c49a9`](https://github.com/flexeykinDev/Filee/commit/e3c49a9) (12 добавленных строк в двух файлах).

<a id="network"></a>
<img src="private/s-net.svg" alt="02 · Куда программа ходит в сеть" width="100%">

Трекеров, аналитики и уникальных идентификаторов в коде нет ни в оригинале, ни здесь. В сеть сборка выходит только
когда вы сами что-то нажали:

| Действие | Куда |
|---|---|
| «Установить» у движка (Настройки → Движки) | официальные сайты движков, см. ниже |
| Перетащить картинку или ссылку прямо из браузера | адрес, который вы перетащили |
| Ссылки GitHub и «Помочь» | открываются в вашем браузере |

<a id="engines"></a>
<img src="private/s-engines.svg" alt="03 · Откуда качаются движки" width="100%">

Картинки, PDF, документы Office, архивы, шрифты и 3D работают без движков. Движки нужны для видео, аудио и редких
форматов. Каждый файл закреплён в [`engines.json`](../src/Filee.Engines/Infrastructure/engines.json) по SHA-256:
файл с другим хешем Filee выбросит и не установит.

| Движок | Источник |
|---|---|
| FFmpeg (видео, аудио) | [gyan.dev](https://github.com/GyanD/codexffmpeg), на эти сборки ссылается ffmpeg.org |
| LibreOffice | download.documentfoundation.org |
| Pandoc | [jgm/pandoc](https://github.com/jgm/pandoc) |
| calibre | download.calibre-ebook.com |
| 7-Zip | [ip7z/7zip](https://github.com/ip7z/7zip) |
| rhwp (HWP) | [edwardkim/rhwp](https://github.com/edwardkim/rhwp) |
| Ghostscript | conda-forge, пакет сообщества, не Artifex |

<a id="install"></a>
<img src="private/s-install.svg" alt="04 · Установка" width="100%">

1. Скачайте `Filee-1.12.0-win-Setup.exe` со [страницы релиза](https://github.com/flexeykinDev/Filee/releases/tag/private-1.12.0)
   и сверьте хеш:
   ```powershell
   Get-FileHash .\Filee-1.12.0-win-Setup.exe   # 02FA3518CE3858DE37461D1BC80E019209D5F728EB8163BE4BE27C415287336F
   ```
2. Установщик не подписан, поэтому SmartScreen предупредит: «Подробнее» → «Выполнить в любом случае».
3. Не соглашайтесь на обновление из официального Filee: оно заменит эту сборку.

**Как открыть кольцо форматов:**

| Способ | Что сделать |
|---|---|
| Перетаскивание | В Проводнике тяните файл левой кнопкой и зажмите <kbd>Ctrl</kbd>. Кнопку отпускайте на нужном формате |
| Клавиатура | Выделите файл в Проводнике и нажмите <kbd>Ctrl</kbd> + <kbd>Alt</kbd> + <kbd>Space</kbd> |
| Меню | ПКМ по файлу → «Конвертировать с помощью Filee» |
| Окно | Перетащите файл в зону на главной странице Filee |

Жесты по умолчанию работают над Проводником и рабочим столом. Чтобы они работали везде, откройте
«Горячие клавиши» и выберите область «Везде».

<a id="build"></a>
<img src="private/s-build.svg" alt="05 · Собрать самому" width="100%">

Нужны [.NET 10 SDK](https://dotnet.microsoft.com/download) и [PowerShell 7](https://github.com/PowerShell/PowerShell).

```bash
git clone -b private-build https://github.com/flexeykinDev/Filee.git
cd Filee
pwsh build/fetch-fonts.ps1
pwsh build/build-installer.ps1
```

Установщик появится в `Releases/`. Чтобы подтянуть новую версию оригинала, перенесите ветку на его `main`:

```bash
git fetch https://github.com/KnifeLemon/Filee.git main
git rebase FETCH_HEAD
```

---

<sub>MIT, © Myeonggu Kim (KnifeLemon) и участники проекта Filee. Эта сборка не связана с автором и не
поддерживается им. Ошибки самого Filee сообщайте [в оригинальный репозиторий](https://github.com/KnifeLemon/Filee/issues).</sub>
