---
type: ANALYSIS
domain: framework
title: "OpenGraph controller ownership"
audience: [maintainers, ai-agents]
status: archived
last_updated: 2026-09-09
framework_version: v1.0.0
validation:
  status: reviewed
  scope: historical dogfeeding evidence classification; original execution claims retained
---

# OpenGraph controller ownership

**Task:** Preserve controller status and authorization during HTML navigation.
**Application intent:** A moderated resource must return 404 before rendering any social metadata.
**Public expression:** Reference OpenGraph, configure ShellPath, declare SocialCards in AddKoan, and use an attribute-routed controller with IOpenGraphCardRenderer after the business visibility check.
**Guarantee/correction:** Automatic rendering runs after authorization and passes controller endpoints through. A denied or missing resource retains its HTTP result.
**Complete intent surface:** No new options or application middleware are required.
**Public concepts:** Existing controllers own routes; the existing renderer owns encoding and shell injection.
**Docs read:** README defines declaration and custom rendering; TECHNICAL locates the early short-circuit; principles and CLAUDE require controllers-only route ownership; release playbook requires the normal dev/main publication boundary; engineering README and toc locate current guidance.
**Code read:** ApplicationBuilderExtensions short-circuits before controller execution; OpenGraphPipelineContributor currently runs BeforeRouting; IOpenGraphCardRenderer already supports custom endpoints; OpenGraphModule registers the contributor; existing filter, ordering and renderer specs provide focused proof.
**Reusing:** KoanWebPipelineStage.AfterAuthorization, ControllerActionDescriptor, IOpenGraphCardRenderer, existing host fixture. Constants/options/DTO search found no new required types.
**Creating new:** Regression cases in MiddlewareFilterTests and OpenGraphCardRendererSpec. No production types or identifiers.
**Coalescence:** Keep renderer and projection lifecycle; rebuild the OpenGraph pipeline boundary. The Web.OpenGraph owner must stop bypassing controller-owned responses. Delete early routing behavior and its obsolete test expectation.
**Ergonomics:** Existing controller and renderer APIs suffice; no additional switches or wiring.
**Constraints satisfied:** Controller routes, Entity data, existing options, no provider changes, no new global state, focused real AddKoan proof.
**Risks:** Custom controller routes previously served automatically will now execute their declared actions, intentionally. SPA fallback remains automatic. Release requires maintainer authorization; no workstation publication.

Standing authorization: user authorized framework dogfeeding and bug fixes during application migration.
