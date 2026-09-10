# Shared approval foundation

Use this example package to give a purchasing application and an expense application the same
approval policy. It brings Koan's application, SQLite, and MCP capabilities as dependencies.
`Example.Approvals.Foundation` is a demonstration package built into a local feed, not a public
Koan package identifier.

Derive your business Entity from `ApprovalRequest<TEntity>`, its controller from
`ApprovalController<TEntity>`, and its module from `ApprovalPolicyModule<TEntity>`. Call
`base.Register(services)` before declaring additional Entity lifecycle rules in your module.
Keep the host's bootstrap as `builder.Services.AddKoan()`.

The policy owns pending/approved state, positive amounts, the approval limit, immutable approved
common fields, and retention of approved decisions. Consumers own their additional fields and
post-approval actions. See [TECHNICAL.md](TECHNICAL.md) for the persistence boundary and limits.

The current policy permits approvals through USD 500. New over-limit requests can be submitted
but cannot be approved. Updating the foundation changes that decision in both consumers. Old
approved records stay readable and may finish consumer-owned ordering or reimbursement.

Run the workspace's `verify.ps1` for the local package update and rollback experiment. Versions
come from NBGV history in its isolated fixture. The package's docs are included with its binaries
so consuming developers and coding agents receive the matching extension instructions.
The referenced capabilities bring the ordinary-foundation identity repair in published Core
1.0.38 through their dependency floors. No local Koan build is required.

This sample supplies business policy, not production authentication, authorization, tenant
isolation, payment processing, or procurement integrations. Its local development MCP exposure
is for exercising the shared Entity boundary.

When working in this sample workspace, coding agents should read [AGENTS.md](../AGENTS.md)
for shared-policy and consumer ownership.
