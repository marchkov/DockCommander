# DockCommander — актуальный план

Обновлено 26 ноября 2026. Версия: 0.3.0-alpha.1.
Локальный файловый менеджер принят. SFTP, FTP, FTPS и WebDAV работают в панелях. Встроенный редактор F4 входит в эту Alpha.
Поиск файлов и размер каталога — последние функции файлового менеджера перед первой Alpha. Дальше, до проверки в реальной работе, допускаются только исправления надёжности.
Подсветка синтаксиса, вкладки и языковые серверы в этот этап не входят.

Это текущий план. `CODEX_TASK.md` сохраняет исходные требования MVP.

## Уже реализовано

### Локальный файловый менеджер

- Две независимые панели, темы, собственные заголовки и диалоги с Enter/Esc.
- Файловые операции, конфликты, F3, встроенный редактор F4, новый файл Shift+F4, внешний редактор из меню, переименование и создание каталогов.
- Маски выделения, Alt+F1/F2, компактные locations Linux и меню переполнения.
- Поиск файлов по имени: Ctrl+F и Alt+F7, от текущего каталога, маски `*` и `?`. Содержимое файлов не ищется.
- Space считает размер текущего каталога и показывает его в колонке Size. Ctrl+Space по-прежнему ставит отметку.
- Внутреннее перетаскивание между панелями, приём файлов из системного проводника
  и отдача локальных путей в другие проводники. Сброс внутри одной панели не принимается.
- Windows: Shift+правая кнопка открывает классическое Shell menu для файлов,
  выделения или фона папки. Во время непустой очереди оно блокируется.
- Очередь копирования/перемещения, прогресс, cancellation и conflict handling.
- Корзина Windows и Linux/GIO, отдельное подтверждение permanent delete.
- Безопасное staged-copy поведение и проверки ссылок/reparse points.
- Windows/Linux packaging, проверка Microsoft.NETCore.App 10 x64, Linux menu entry и .deb.
- GitHub Actions проверяет Windows и Ubuntu до сборки/release.

### Remote UI и профили

- У каждой панели собственный переключатель `Local | Remote`.
- Remote остаётся частью обычного `FilePane`, отдельного FTP/SFTP-клиента нет.
- Есть поиск соединений, `Add connection`, редактирование профилей и список соединений.
- Профиль хранит протокол, host, port, username, initial path и authentication mode.
- Пароли, passphrase и private-key данные не записываются открытым текстом в `settings.json` или `connections.json`.
- Windows использует DPAPI CurrentUser. Linux использует отдельный AES key с правами пользователя.
- Host key SFTP и TLS certificate FTP/FTPS подтверждаются для конкретного connection profile
  и спрашиваются снова при изменении fingerprint.

### Cross-provider transfers

- `FileOperationService` умеет копировать между разными экземплярами `IFileSystemProvider`
  через `OpenReadAsync` / `WriteFileAsync`.
- Один и тот же provider instance сохраняет быстрый нативный путь через `TransferFileAsync`
  и `TryMoveDirectoryAsync`.
- Cross-provider move выполняется как copy, затем delete source.
- Очередь, progress, cancellation и conflict handling не содержат protocol-specific веток.
- Панель может менять provider без отдельного окна.

### Сетевые провайдеры

Рабочими являются:

- SFTP
- FTP
- FTPS
- WebDAV

Поддерживается:

- list
- read
- write
- create directory
- rename
- delete
- Local → Remote
- Remote → Local
- Remote A → Remote B
- F3 для remote text files
- F4 редактирует текст через `OpenReadAsync` / `WriteFileAsync`, без временного локального файла и без отдельной ветки протокола
- password authentication
- SFTP private key authentication
- anonymous FTP
- explicit FTPS
- implicit FTPS на port 990
- WebDAV по HTTPS, а на порту 80 по обычному HTTP
- WebDAV Basic и Digest
- проверка сертификата WebDAV тем же запомненным trust, что и FTPS
- один WebDAV provider и пресеты адреса: Nextcloud, ownCloud, Synology, QNAP, Generic

Пресет не является отдельным provider. Он только собирает endpoint. Nextcloud и ownCloud добавляют
`/remote.php/dav/files/<user>/`, в том числе под каталогом установки. ownCloud при отсутствии этого пути
пробует `/remote.php/webdav/`. Synology, QNAP и Generic используют введённый Server URL.
Старый профиль без пресета по-прежнему открывается через host, port и initial path.
Открытая remote session сохраняется при переключении панели в Local и обратно.
Вторая панель может подключиться к той же живой session, а несколько соединений переключаются по именам сверху.

## Приоритет 1 — стабилизация Remote

Перед добавлением следующего протокола проверить Remote на реальных серверах, а не только через unit/headless tests.

### SFTP

Проверить:

- password authentication;
- private key без passphrase и с passphrase;
- неверный пароль/ключ и повторный login prompt;
- первый host-key trust;
- повторное подключение с тем же fingerprint;
- изменение fingerprint;
- disconnect во время list/read/write;
- cancellation больших upload/download;
- большие файлы и каталоги;
- конфликт имён;
- rename/delete/create directory;
- Local ↔ SFTP и SFTP ↔ SFTP между двумя панелями.

### FTP / FTPS

Проверить:

- anonymous FTP;
- authenticated FTP;
- passive data connection;
- explicit FTPS;
- implicit FTPS :990;
- trusted и self-signed certificates;
- изменение certificate fingerprint;
- disconnect и timeout во время transfer;
- rename/delete/create directory;
- Local ↔ FTP/FTPS и transfer между двумя remote providers.

### Shared session

Проверить:

- одна панель подключается, вторая подхватывает session;
- обе панели могут одновременно просматривать разные каталоги одной session;
- переключение Remote → Local → Remote не теряет session;
- закрытие приложения корректно освобождает session;
- ошибка одной панели не оставляет вторую в неконсистентном состоянии.

## Приоритет 2 — проверка WebDAV на реальном сервере

Nextcloud в корне домена проверен: каталог, переименование, удаление, копирование в обе стороны, отмена передачи, F3, F4
и вторая панель на той же сессии. Ещё не проверены Nextcloud в подкаталоге, ownCloud с современным и классическим путём,
Synology, QNAP и Generic.

## Приоритет 3 — Remote lifecycle и UX

В интерфейсе сессии:

- `Disconnect` закрывает живое соединение и оставляет имя в состоянии Disconnected. Профиль, путь и сохранённый вход остаются.
- `Reconnect` подключается снова. Пока идёт попытка, состояние Reconnecting. Неудача возвращает Disconnected.
- Если сервер закрыл соединение, сессия сама переходит в Disconnected.
- `Forget sign-in` удаляет только сохранённый пароль или ключ. Следующее подключение спрашивает их снова.
- `Close` убирает имя сессии. Удаление профиля по-прежнему удаляет и его секрет, и запомненное доверие.
- Несколько соединений остаются независимыми: отключение одного не трогает остальные.
- Файл секрета на Linux создаётся с правами только владельца. Копия секрета между Windows и Linux не расшифровывается.

На живом сервере ещё стоит один раз оборвать связь во время списка или передачи и убедиться, что имя переходит в Disconnected.

## Архитектурный принцип

Remote не является отдельным приложением внутри DockCommander.

Основная модель:

**каждая панель — независимый файловый provider.**

Панели могут быть:

- Local + Local
- Local + Remote
- Remote + Local
- Remote A + Remote B
- две панели одной shared remote session

Обычные действия DockCommander — F5, F6, selection, queue, progress, cancel и conflicts —
должны максимально одинаково работать между всеми поддерживаемыми provider.

Отдельный filesystem-provider поверх SSH shell не планируется: файловый доступ через SSH использует SFTP.

## Позже, по отдельному решению

- PowerShell Remoting / Windows remote provider.
- Перетаскивание внутри одной панели.
- Закладки пользователя и дополнительные удобства навигации.
- Архивы как возможный provider для навигации внутри архива.
- Динамическая загрузка внешних provider plugins после стабилизации внутренних контрактов.
- Подсветка синтаксиса и другие возможности редактора уровня IDE.

Отдельный встроенный архиватор, image viewer и другие специализированные просмотрщики сейчас не планируются.
Архивирование и подобные операции по возможности передаются ОС и установленным приложениям,
включая системное Shell menu.
