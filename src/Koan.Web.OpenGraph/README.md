# Sylin.Koan.Web.OpenGraph

Project Entity-owned social metadata into a SPA shell for link previews and HTML navigations—without SSR, user-agent
sniffing, or an application middleware call.

```bash
dotnet add package Sylin.Koan.Web.OpenGraph
```

## Smallest meaningful result

Configure the shell, then declare the route-to-Entity projection while Koan composes the application:

```json
{
  "Koan": {
    "Web": {
      "OpenGraph": {
        "ShellPath": "wwwroot/index.html",
        "SiteName": "My Site"
      }
    }
  }
}
```

```csharp
builder.Services.AddKoan(() =>
    SocialCards.For<Article>("/articles/{id}", id => Article.Get(id))
        .Title(article => article.Title)
        .Description(article => article.Summary)
        .Url(article => $"/articles/{article.Id}")
        .Type("article"));
```

The package contributes its Web pipeline behavior automatically. A matching HTML navigation receives encoded title,
canonical, OpenGraph, and Twitter metadata. Unknown entities and unmatched routes receive configured defaults; asset,
API, non-HTML, disabled, and unavailable-shell requests pass through.

Each declaration and its Entity lifecycle warming plan belong to the composing host. Resolvers should use Entity
statics and selectors should remain pure over the Entity value; application services are not captured.

## Image projection

`CardImage.Recipe("share-card", mediaId)` projects `/media/{id}/share-card`, `CardImage.Raw(mediaId)` projects the
original, and `CardImage.Url(...)` preserves an external URL. This package composes URLs only; Media owns rendering.

## Boundaries

- The application owns its SPA fallback and shell file.
- `SocialCards.For<T>` must run inside `AddKoan(() => ...)` or a `KoanModule.Register` method.
- One host may declare one card per Entity type; separate hosts are isolated.
- Snapshots use the Entity data path and optional Cache capability; no CDN purge or distributed shell cache is
  claimed.
- No SSR, prerender, headless browser, oEmbed, JSON-LD, sitemap, or routing ownership is provided.

See the [OpenGraph guide](../../docs/guides/opengraph-howto.md) and [technical contract](TECHNICAL.md).

Controller endpoints retain their response and authorization semantics. Automatic shell injection passes them through. Controllers may call `IOpenGraphCardRenderer.RenderShellAsync(Request, ct)` after their business visibility checks; SPA fallback navigation remains automatic.
