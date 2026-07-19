# Third-party notices

VK4DotNet is a clean .NET 10 translation and adaptation of selected networking, request, response, conversation, message, attachment, and photo-upload concepts from:

- **VK4ME/client**, commit `c007cf12bee10b9b3e2da53e08f89f4f6ae5322a`, GNU General Public License version 3.
- **VK4ME/j2vk**, commit `650f919705c078b6f149d452caac00f580f5d99a`, GNU Lesser General Public License version 2.1. LGPL 2.1 section 3 permits applying a newer GNU GPL to a copy of the library; the adapted VK4DotNet code is distributed under GPL-3.0-only.

The original authors identified by the upstream repositories include Mathew Tkachuk (`curoviyxru`), Roman Lahin (`rmn20`), Shinovon, and other VK4ME contributors. Copyright remains with the respective authors.

The first VK4DotNet translation was made on 2026-07-19. It substantially changes the original Java ME implementation: asynchronous `HttpClient` transport, immutable C# models, VK API 5.199 contracts, PKCE authorization, explicit dependency injection, modern exception handling, and .NET packaging were added. The original J2ME UI, emoji resources, audio code, proxy code, JSON.org implementation, embedded application credentials, refresh token, and official-client User-Agent were not copied.

The full GPL-3.0 text is in [`LICENSE`](LICENSE). The LGPL-2.1 text applying to the J2VK source is preserved in [`licenses/J2VK-LGPL-2.1.txt`](licenses/J2VK-LGPL-2.1.txt).

Official VK method and object contracts were checked against **VKCOM/vk-api-schema**, VK API version 5.199, licensed under the MIT License. VK ID behavior and scopes were checked against the official **VKCOM/vk-php-sdk**, also MIT licensed. No generated source from either repository is included.
