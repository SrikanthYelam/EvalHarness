# Acme Engineering Runbook

This runbook covers how the platform team handles incidents, ships releases and maintains the production databases. It is the source of truth during an incident; if it disagrees with a wiki page or a chat message, follow the runbook and fix the other source afterwards.

## Incident Response

### Severity Levels

Every incident is assigned a severity by the first responder. The severity can be raised or lowered later, but the clock for the initial response starts when the alert fires.

| Severity | Definition | First response | Update cadence | Who is paged |
| --- | --- | --- | --- | --- |
| SEV1 | Full outage or data loss affecting most customers | 5 minutes | every 15 minutes | On-call engineer, engineering manager, VP Engineering |
| SEV2 | Major feature unavailable or severe degradation for some customers | 15 minutes | every 30 minutes | On-call engineer, engineering manager |
| SEV3 | Minor feature broken, workaround available | 4 business hours | daily | On-call engineer |
| SEV4 | Cosmetic issue or internal tooling problem | next business day | none | Ticket only |

A SEV1 incident requires a written post-incident review within 5 business days. A SEV2 review is optional but recommended when the root cause is unclear.

### Communication

The incident commander opens a dedicated chat channel named after the date and a short slug, for example `inc-2025-03-14-checkout`. The commander posts status updates there and on the public status page. Only the commander speaks for the company: everyone else keeps the channel for technical discussion and does not message customers directly.

Customer-facing updates must avoid speculation about root cause. They state what customers see, what the team is doing, and when the next update will come. If a SEV1 lasts longer than one hour, the support lead drafts an email to affected enterprise accounts.

## Deployments

### Release Process

Production releases ship every Tuesday and Thursday at 10:00 UTC. Nothing ships on Friday, and nothing ships during the last two business days of a quarter unless the VP Engineering approves it. A release needs one approving review, a green pipeline and a completed change ticket.

Services are deployed with a canary first. The pipeline sends 5 percent of traffic to the new version for 20 minutes, then 50 percent for another 20 minutes, and promotes to 100 percent only if the error rate stays below 1 percent and the p95 latency stays under 400 milliseconds. If either threshold is crossed, the pipeline rolls back automatically and pages the author of the release.

### Rolling Back

If a release has to be reverted by hand, use the deploy tool rather than reverting the commit, because reverting the commit triggers a full pipeline run and delays the fix. The rollback takes about two minutes. Run the following from the bastion host with your own credentials:

```bash
# 1. Confirm which revision is live and which one you are returning to
deploy history --service "$SERVICE" --env production --limit 5

# 2. Roll back to the previous revision
deploy rollback --service "$SERVICE" --env production --to previous

# 3. Verify the rollback finished and the error rate has dropped
deploy status --service "$SERVICE" --env production --wait
```

After a manual rollback, open a ticket tagged `rollback` so that the author can investigate before the change is released again. Never roll back a release that included a database migration without asking the database on-call first, because the previous version may not be able to read the migrated schema.

## Service Ownership

Each service has one owning team that is paged first. Cross-team escalation goes through the owning team's on-call, not through direct messages to individual engineers.

| Service | Owning team | Language | Tier | Datastore |
| --- | --- | --- | --- | --- |
| api-gateway | Platform | Go | 1 | none |
| auth | Identity | Go | 1 | PostgreSQL |
| billing | Payments | Java | 1 | PostgreSQL |
| checkout | Payments | Java | 1 | PostgreSQL |
| search | Discovery | Rust | 2 | OpenSearch |
| recommendations | Discovery | Python | 2 | Redis |
| notifications | Engagement | Node.js | 2 | DynamoDB |
| email-sender | Engagement | Node.js | 3 | DynamoDB |
| reporting | Data | Python | 3 | Snowflake |
| admin-console | Platform | TypeScript | 3 | PostgreSQL |
| media-processor | Content | Rust | 2 | S3 |
| feature-flags | Platform | Go | 1 | Redis |

Tier 1 services have a 99.95 percent monthly availability target, tier 2 services 99.9 percent, and tier 3 services 99.5 percent.

## Database Maintenance

### Backups

PostgreSQL databases take a full backup every night at 02:00 UTC and stream write-ahead logs continuously, which allows point-in-time recovery to any second within the last 14 days. Full backups are kept for 35 days and then moved to cold storage for one year. Backups are restored into a scratch environment every month, and the restore test is considered failed if it takes longer than four hours.

### Schema Migrations

Migrations must be backward compatible with the previous release, because the canary runs two versions of the service against the same database. Add columns as nullable, backfill in batches, and only then add constraints in a later release. Dropping a column or table takes two releases: the first stops reading it, the second removes it. Any migration expected to lock a table for more than 30 seconds must be scheduled with the database on-call and run outside the Tuesday and Thursday release windows.
