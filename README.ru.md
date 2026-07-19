# VK4DotNet

[English README](README.md)

VK4DotNet — асинхронная библиотека .NET 10 для VK API. Версия 1.0 читает личные и многопользовательские диалоги, поддерживает фото во входящих и исходящих сообщениях и публикует посты с фото на собственную стену либо в управляемое сообщество.

Архитектура и модели основаны на GPL-проекте [VK4ME/client](https://github.com/VK4ME/client) и библиотеке J2VK, но адаптированы к официальной схеме VK API 5.199. Чужие `client_id`, `client_secret`, refresh token и User-Agent официального Android-клиента из исторического исходника не переносились.

> [!IMPORTANT]
> В актуальном официальном списке VK ID scopes отсутствует `messages`. Для личных диалогов нужен совместимый внешний user-токен либо отдельный opt-in пакет `VK4DotNet.LegacyAuth`. Устаревший password flow может быть недоступен для конкретного приложения или аккаунта и способен перестать работать без предупреждения.

## Установка

Пакеты `VK4DotNet` и `VK4DotNet.LegacyAuth` находятся в [GitHub Releases](https://github.com/t3chn0pr13st/VK4DotNet/releases), а не в NuGet.org:

```sh
dotnet nuget add source /путь/к/пакетам --name VK4DotNetLocal
dotnet add package VK4DotNet --version 1.0.0
dotnet add package VK4DotNet.LegacyAuth --version 1.0.0
```

## Чтение диалогов и сообщений

```csharp
using VK4DotNet;

using var vk = new VkClient(Environment.GetEnvironmentVariable("VK_USER_TOKEN")!);
var dialogs = await vk.Messages.GetConversationsAsync(
    new VkGetConversationsRequest(Count: 20), cancellationToken);

await foreach (var message in vk.Messages.EnumerateHistoryAsync(
    new VkGetHistoryRequest(dialogs.Items[0].Conversation.Peer.Id, Count: 100),
    cancellationToken))
{
    foreach (var photo in message.Attachments.OfType<VkPhotoAttachment>())
        Console.WriteLine(photo.Photo.GetBestSize(1080)?.Url);
}
```

## Отправка сообщения с фото

```csharp
await using var image = File.OpenRead("photo.jpg");
await vk.Messages.SendAsync(new VkSendMessageRequest
{
    PeerId = 123456,
    Message = "Привет!",
    Photos = [new VkUploadFile(image, "photo.jpg", "image/jpeg")]
}, cancellationToken);
```

До десяти фото последовательно проходят полный workflow VK: upload server → multipart upload → save photo → send. По умолчанию библиотека не закрывает переданный поток.

## Публикация в сообщество

```csharp
await using var image = File.OpenRead("release.png");
await vk.Wall.PublishAsync(new VkPublishPostRequest
{
    Target = VkWallTarget.Community(123456),
    Message = "VK4DotNet 1.0 опубликован",
    Photos = [new VkUploadFile(image, "release.png", "image/png")]
}, cancellationToken);
```

Для своей стены используйте `VkWallTarget.Self`. Загрузка фото на стену и `wall.post` требуют user-токен даже при публикации в управляемую группу.

## VK ID и LegacyAuth

`VkIdAuthClient.CreateAuthorizationSession()` генерирует PKCE URL и state. Браузер и callback принадлежат приложению-хосту; `ExchangeCodeAsync()` проверяет state и обменивает `code` + `device_id` на токены. `VkIdTokenProvider` обновляет токен и передаёт новые значения callback-функции хоста, но ничего не сохраняет самостоятельно.

`LegacyVkPasswordAuthenticator` требует реквизиты только вашего VK-приложения и возвращает `LegacyVkAuthSuccess`, `LegacyVkAuthChallenge` или `LegacyVkAuthFailure`. CAPTCHA, TOTP и SMS передаются повторным `LegacyVkAuthRequest`. Пароль нельзя логировать или сохранять.

Полные примеры находятся в [README.md](README.md) и `samples/VK4DotNet.Console`.

## Сборка

```sh
dotnet restore VK4DotNet.slnx
dotnet build VK4DotNet.slnx -c Release --no-restore
dotnet test VK4DotNet.slnx -c Release --no-build
dotnet pack VK4DotNet.slnx -c Release --no-build -o artifacts/packages
```

Опциональный read-only интеграционный тест включается переменной `VK4DOTNET_LIVE_USER_TOKEN`.

## Лицензия

Проект и оба NuGet-пакета распространяются по **GPL-3.0-only**. При распространении связанного приложения необходимо выполнить требования GPL, включая предоставление соответствующего исходного кода. Подробности: [LICENSE](LICENSE) и [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md).
