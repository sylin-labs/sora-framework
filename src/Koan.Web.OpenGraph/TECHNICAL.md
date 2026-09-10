# Sylin.Koan.Web.OpenGraph — technical contract

## Composition ownership

`OpenGraphModule` binds `Koan:Web:OpenGraph`, creates one `SocialCardRegistry` instance in the application's
`IServiceCollection`, registers the renderer/shell cache, and contributes `OpenGraphPipelineContributor` at
`AfterAuthorization`. There is no manual activation path.

`SocialCards.For<T>` requires `KoanCompositionScope`, adds its registration to that host's registry, and registers
after-upsert/after-remove handlers through the host-owned Entity lifecycle plan. Application resolver/selector closures
therefore release with the host and cannot leak into a later composition.

## Request flow

1. pass through unless the request is an HTML `GET` outside `/api` and known asset extensions; a
   request routed to a controller action always passes through, whatever status it produces;
2. load the configured shell through `ShellCache`;
3. match declarations in registration order and extract the route token;
4. read `SocialCardSnapshot` by Entity-type/token identity;
5. on a miss, resolve the Entity, project the card, and best-effort persist the snapshot;
6. encode and truncate configured/entity values; and
7. replace the marker or insert before `</head>`, then short-circuit the navigation.

Upsert warming projects the Entity already in hand; remove evicts its snapshot. Snapshot persistence and optional
`[Cacheable]` acceleration use Data/Cache semantics and inherit their active segmentation.

## Options

`Koan:Web:OpenGraph` owns enablement, `ShellPath`, `PlaceholderMarker`, site/default metadata, Twitter/title/canonical
toggles, locale, and title/description limits. The module reports effective enablement, shell posture, and declaration
count at startup.

## Runtime and hot path

Route declarations and lifecycle plans are compiled once per host. The request hot path iterates an immutable
copy-on-write registration list, performs one snapshot lookup, and one shell string replacement. `ShellCache` reloads
when the shell file's timestamp changes.

## Unsupported

No routing/fallback ownership, SSR/prerender, user-agent policy, distributed shell cache, CDN invalidation, oEmbed,
JSON-LD, sitemap generation, or guarantee that external crawlers honor emitted metadata is claimed.

Automatic injection runs at the post-authorization Web pipeline boundary and only for navigations no
controller route claimed. Controller endpoints retain their response and authorization semantics: a
controller result, including its errors and 404s, passes through untouched. Controllers may call
`IOpenGraphCardRenderer.RenderShellAsync(Request, ct)` after their business visibility checks; SPA
fallback navigation remains automatic. Automatic injection is appropriate for content the application
permits to be public. Card resolvers are application-supplied, and a cached card bypasses resolution;
cards do not revalidate row or member Access. Check visibility before calling the renderer for governed
content, and keep the card's fields appropriate for the audience receiving it.
