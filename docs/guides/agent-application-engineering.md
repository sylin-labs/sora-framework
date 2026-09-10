---
type: GUIDE
domain: framework
title: "Engineer a Koan application with an agent"
audience: [developers, architects, ai-agents]
status: current
last_updated: 2026-09-10
framework_version: v1.0.0
validation:
  date_last_tested: 2026-07-23
  status: verified
  scope: greenfield and brownfield application reasoning workflow
---

# Engineer a Koan application with an agent

Give an agent the business outcome, the application repository, and this page. The useful result is
not “more Koan”: it is the smallest application expression whose guarantee Koan can honestly own.

## Say five things before editing

1. **Application intent:** one business sentence, without framework nouns.
2. **Public expression:** packages, C#, configuration, context, and runtime actions the app performs.
3. **Guarantee and correction:** what becomes true, and what safe failure teaches the operator.
4. **Complete action surface:** every required user/operator action; say when there are no others.
5. **Owner:** the one application or Koan capability responsible for the behavior.

Example:

> A submitted review runs after the request and remains inspectable. Reference
> `Sylin.Koan.Jobs`; make `Review : Entity<Review>, IKoanJob<Review>`; submit with
> `await review.Job.Submit()`. Jobs guarantees ledger acceptance and at-least-once execution;
> durable restart survival additionally requires a durable Data provider. No Jobs registration
> is required. Jobs owns execution; the application owns idempotent business behavior.

That paragraph is a better implementation plan than a list of framework internals.

## Greenfield: grow from the business model

1. Start from the [quickstart](../getting-started/quickstart.md).
2. Model business state as an `Entity<T>` and use its standard verbs.
3. Add a package only when a capability is needed; the reference makes it available to `AddKoan()`.
4. Read the owning [capability pillar](../index.md), then its provider companion when external
   infrastructure is involved.
5. Add an HTTP, Jobs, Events, Transport, AI, media, or MCP projection only when the business intent
   asks for it.
6. Prove the focused behavior, composed host, and corrective failure.

Do not begin with repositories, provider registrations, service locators, mirrored agent models, or
manual endpoint inventories. Introduce an escape hatch only after naming the missing guarantee.

## Brownfield: preserve contracts, replace mechanisms

Begin with the [existing-app adoption path](../getting-started/adopt-existing-app.md). Inventory each
bespoke mechanism and classify it:

| Decision | Meaning |
|---|---|
| Keep | It is application policy or an integration Koan does not own. |
| Absorb | A current Koan capability already owns the same guarantee. |
| Rebuild | The intent remains, but the implementation should use Koan's public expression. |
| Delete | It duplicates composition, persistence, routing, lifecycle, or diagnostics with no unique policy. |

Preserve observable contracts first: routes, payloads, authentication, database names, queues,
buckets, model endpoints, environment keys, and deployment topology. Replace one mechanism at a time,
prove parity at its real boundary, then remove the duplicate.

If Koan cannot express the requirement cleanly, write a tiny anonymous reproduction and report:
desired intent, closest public expression, missing guarantee, expected correction, and focused proof.
Do not hide the gap behind application infrastructure.

## Proof proportional to the claim

- **Behavior:** the owning unit or integration test proves the changed business guarantee.
- **Composition:** boot the real `AddKoan()` host with the references and configuration the app uses.
- **Correction:** prove missing infrastructure, unsupported intent, or unsafe configuration fails
  with a useful next action.

Use a real provider boundary when claiming provider behavior. Do not run the entire framework to
prove one application capability.

## Design authority and derived state

Separate permission to read a row, eligibility for a public listing, and disclosure of its fields.
Those are business decisions even when they share an Access declaration. A hidden row is not a
missing identity when deciding whether a write may insert. Exercise the real HTTP or MCP body and
stored result; a policy-predicate test alone cannot expose serializer or transport bypasses.

Use the Entity document semantics when restoring a typed update. HTTP writable-member admission
still applies before restoration; persistence's ability to hydrate private state is not permission
for a caller to write it. Preserve string enums, collection contents and dictionary keys across the
actual transport and provider path instead of adding an application serializer for each surface.
See [Entity lifecycle](../reference/data/entity-lifecycle.md) and
[typed PATCH](patch-capabilities-howto.md) for the owning contracts.

For delayed work, retain the business identity and reload the authority needed when forming the
result. A queued hint or previously rendered payload is not current permission. A destination
revision checked by `ReplaceIf` protects that destination; it does not make a separate source read
atomic with the write. Reconciliation can repair current state but cannot replay intermediate events
that were never retained. Keep those distinctions in the business contract.
Use [Jobs](../reference/jobs/index.md) for durable execution and its captured work route;
keep current business authority in the application action.

Prefer removing derived state when a fresh read supplies the required behavior at an acceptable
cost. If caching remains useful, state expiry and invalidation ordering separately. Ordinary Cache
owns storage, TTL and keyed coalescing; the application owns which source and audience the value
represents. A successful removal is not a claim that previously returned bytes can be revoked.
Read the [Cache contract](../reference/data/cache.md) before selecting a consistency boundary.

An interrupted business operation often needs a small checkpoint on its existing Entity, not a new
workflow engine. A retry must still honor current business exclusions and ownership before using a
reserved identity. Preserve normal lifecycle effects and prove them at the successful insert boundary.

## Retrieval order

Read only as far as needed:

1. [`llms.txt`](../../llms.txt) for the intent-to-document map;
2. the owning capability page;
3. its package README for installation and limits;
4. a graduated [sample](../../samples/README.md);
5. source and focused tests when the public contract is still ambiguous.

ADRs and initiative artifacts explain why the framework evolved; they are not the first application
API reference. The generated [product surface](../reference/product-surface.md) is the support and
package authority.

## Handoff

Leave the next engineer a short table: intent, owning capability, proof, remaining limit. For a
migration, also name the superseded owner and what was removed. Record framework gaps separately
from application work. Never copy private names, paths, URLs, data, or
recognizable workflows into public framework artifacts; reduce them to anonymous reproductions.
