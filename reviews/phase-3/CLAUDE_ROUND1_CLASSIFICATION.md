# Phase 3 — classification of round 1, and request for round 2

> Round 1: `CODEX_VERIFICATION_raw.md`, verdict **REJECT** (2 P1, 1 P2). Each finding was checked
> against the code before it was classified. Codex found no defect in commit ordering, poison-message
> classification, the state stream, or replay refusal, and judged the recorded intermittent acceptable
> as recorded.

| ID | Sev | Classification | What changed |
|---|---|---|---|
| P3-COD-001 | P1 | **ACCEPTED** | `TopicBootstrap` now compares the replica count of every partition against the register's RF for the deployment, and refuses a mismatch. Tested by checking a locally created topic against the production-like register (RF 3); before, it was reported `Unchanged`. |
| P3-COD-002 | P1 | **ACCEPTED** | Lag, record age and DLQ-by-reason are measured in `ContractConsumer`, per partition, and tested on a topic of their own: lag 7 after 3 of 10, record age from the record's timestamp (a record stamped a minute ago reads as a minute old), DLQ counts by reason. Export stays Phase 8, stated in `KAFKA_EVENT_BACKBONE.md` §17 with the mapping, as Phase 2 did for the gateway. `under_replicated_partitions` is a broker metric and is left to Phase 8 explicitly. |
| P3-COD-003 | P2 | **ACCEPTED** | `AllowAutoCreateTopics = false` on both gateway producers. No new test: the test broker refuses auto-creation globally, so a test could not tell the setting from the broker; the negative case that does exist is `TopicBootstrapTests.NoTopicIsAutoCreatedByAProducerOrConsumer`. |

## Found while fixing

| ID | Sev | Finding |
|---|---|---|
| CLD-P3-001 | P2 | **Topic creation raced its own metadata.** The first version of the RF test changed the order the bootstrap tests ran in, and exposed that `GetMetadata` right after a creation can omit the new topic — the bootstrap then tried to create it again and failed with `TopicAlreadyExists`. Two gateways starting together would hit the same thing. "Already exists" is now neither success nor failure: the topic is re-described and verified like any other existing one; any other creation error still propagates. |
| CLD-P3-002 | P3 | The first RF test depended on test order — it created the whole register and made "the first run creates 14" see 0. It now uses a topic of its own. |
| CLD-P3-003 | P2 | **The recorded OPC UA intermittent recurred** in full runs, now with messages: "initial telemetry did not happen within 15 s", and all twenty clients silent at once. The load test's assertion now names each silent machine's connect/reconnect/read-failure/poll counts and last failure. **Mitigation, not a proven fix:** five Kafka test classes share one broker instead of starting one each (six JVMs → two); three full runs followed without a failure. `TEST_SPECIFICATIONS.md` records it as still open. |

## Round 2 — verify

1. The three fixes, and whether any is incomplete.
2. The creation-race handling: does treating `TopicAlreadyExists` as "verify it" hide a real
   conflict, and is the refetch sound?
3. The metrics: is lag computed against the right high watermark, is record age per partition what
   §12 asks for, and do the tests prove it?
4. Anything from round 1's "looks correct" list that the new code disturbs — including whether
   sharing one broker across five test classes weakens any test's isolation (CLD-P3-003).
5. Anything else.
